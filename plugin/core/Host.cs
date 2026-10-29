using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NavisworksMcp
{
    public static class Entry
    {
        static Host host;
        public static void Start(string corePath) => host = new Host(corePath);
        public static void Stop() => host?.Dispose();
    }

    sealed class Job
    {
        public string Id;
        public string Label;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
        public Reply Reply;
        public volatile bool Cancelled;
    }

    /// Owns the process-wide state: UI marshalling, the pipe server, running jobs and handles.
    sealed class Host : IDisposable
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "navisworks-mcp");
        public readonly int Pid = Process.GetCurrentProcess().Id;
        public readonly string Epoch = Guid.NewGuid().ToString("N").Substring(0, 8);
        public readonly string CorePath;
        public readonly Handles Handles = new Handles();
        readonly Control ui = new Control();
        readonly ConcurrentDictionary<string, Job> jobs = new ConcurrentDictionary<string, Job>();
        readonly CancellationTokenSource stopping = new CancellationTokenSource();
        readonly string instanceFile;
        int jobSeq;

        public Host(string corePath)
        {
            CorePath = corePath;
            ui.CreateControl();
            Watch(Autodesk.Navisworks.Api.Application.ActiveDocument);
            Autodesk.Navisworks.Api.Application.ActiveDocumentChanged += (s, e) => Watch(Autodesk.Navisworks.Api.Application.ActiveDocument);
            Directory.CreateDirectory(Path.Combine(Dir, "instances"));
            new Thread(Listen) { IsBackground = true, Name = "navisworks-mcp" }.Start();
            // The catalog file only lets the server list tools before it connects; every hello carries the catalog,
            // so losing a write race with another starting Navisworks must not stop this one.
            WriteAtomic(Path.Combine(Dir, "catalog.json"), Catalog.Json.ToString(Formatting.None), required: false);
            instanceFile = Path.Combine(Dir, "instances", Pid + ".json");
            WriteAtomic(instanceFile, new JObject { ["pid"] = Pid, ["epoch"] = Epoch, ["pipe"] = PipeName, ["started"] = DateTime.UtcNow.ToString("o") }.ToString(Formatting.None), required: true);
        }

        string PipeName => "navisworks-mcp-" + Pid;

        void Watch(Autodesk.Navisworks.Api.Document doc)
        {
            Interlocked.Increment(ref Handles.Generation);
            if (doc != null) doc.Models.CollectionChanged += (s, e) => Interlocked.Increment(ref Handles.Generation);
        }

        static void WriteAtomic(string path, string text, bool required)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, text);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(50);
                }
                catch (IOException) when (!required)
                {
                    File.Delete(tmp);
                    return;
                }
            }
        }

        void Listen()
        {
            // An exception escaping a background thread terminates Navisworks, so nothing here may throw.
            while (!stopping.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    pipe.WaitForConnection();
                    var connected = pipe;
                    new Thread(() => Serve(connected)) { IsBackground = true, Name = "navisworks-mcp-client" }.Start();
                }
                catch (Exception e)
                {
                    pipe?.Dispose();
                    Log("pipe listener: " + e.Message);
                    Thread.Sleep(500);
                }
            }
        }

        static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(Dir, "plugin.log"), $"{DateTime.Now:o} pid {Process.GetCurrentProcess().Id} {text}\n"); }
            catch (IOException) { }
        }

        void Serve(NamedPipeServerStream pipe)
        {
            var utf8 = new UTF8Encoding(false);
            try
            {
                using (pipe)
                using (var reader = new StreamReader(pipe, utf8))
                using (var writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" })
                    Serve(reader, writer);
            }
            catch (Exception e) when (!(e is IOException || e is ObjectDisposedException))
            {
                Log("connection dropped: " + e);
            }
            catch (Exception) { }
        }

        void Serve(StreamReader reader, StreamWriter writer)
        {
            var gate = new object();
            Action<JObject> send = msg =>
            {
                try { lock (gate) writer.WriteLine(msg.ToString(Formatting.None)); }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException) { }
            };
            var byRequest = new ConcurrentDictionary<long, Job>();
            send(new JObject { ["hello"] = new JObject { ["pid"] = Pid, ["epoch"] = Epoch, ["catalog"] = Catalog.Json } });
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                JObject msg;
                long id;
                try
                {
                    msg = JObject.Parse(line);
                    if (msg["cancel"] != null)
                    {
                        if (byRequest.TryGetValue((long)msg["cancel"], out var victim)) victim.Cancelled = true;
                        continue;
                    }
                    id = (long)msg["id"];
                    if (msg["tool"]?.Type != JTokenType.String) throw new FormatException("tool must be a string");
                }
                catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is FormatException || e is InvalidCastException || e is ArgumentException)
                {
                    Log("bad request line: " + e.Message);
                    continue;
                }
                var tool = (string)msg["tool"];
                var args = msg["args"] as JObject ?? new JObject();
                var wait = msg["wait"]?.Type == JTokenType.Integer ? (int)msg["wait"] : 25000;
                // Registered before any work is scheduled, so a cancel that follows right behind still finds it.
                var job = Catalog.RunsOnPipeThread(tool) ? null : new Job { Id = "j" + Interlocked.Increment(ref jobSeq), Label = tool };
                if (job != null) byRequest[id] = job;
                Task.Run(() =>
                {
                    var reply = job == null ? Catalog.Run(new Ctx(this, null), tool, args) : Handle(job, tool, args, wait);
                    byRequest.TryRemove(id, out _);
                    send(reply.ToWire(id));
                });
            }
        }

        Reply Handle(Job job, string tool, JObject args, int waitMs)
        {
            jobs[job.Id] = job;
            try
            {
                ui.BeginInvoke(new Action(() =>
                {
                    job.Reply = job.Cancelled ? Reply.Error("cancelled", "the call was cancelled before it started", "repeat the call if you still need it") : Catalog.Run(new Ctx(this, job), tool, args);
                    job.Done.Set();
                }));
            }
            catch (Exception e) when (e is InvalidOperationException || e is ObjectDisposedException)
            {
                jobs.TryRemove(job.Id, out _);
                return Reply.Error("closing", "Navisworks is shutting down", "start Navisworks again, then call status");
            }
            if (job.Done.Wait(waitMs)) { jobs.TryRemove(job.Id, out _); return job.Reply; }
            return Reply.Running(job);
        }

        /// Called from the pipe thread by the `job` tool. A finished job is handed out once, then forgotten.
        public Reply Await(string id, int waitMs, bool cancel)
        {
            if (!jobs.TryGetValue(id, out var job))
                return Reply.Error("not_found", "no running job " + id + " (finished jobs are reported once)", "call status to see running jobs");
            if (cancel) job.Cancelled = true;
            if (!job.Done.Wait(waitMs)) return Reply.Running(job);
            jobs.TryRemove(id, out _);
            return job.Reply;
        }

        public Job[] RunningJobs() => new System.Collections.Generic.List<Job>(jobs.Values).ToArray();

        public void Dispose()
        {
            stopping.Cancel();
            try { File.Delete(instanceFile); } catch (IOException) { }
        }
    }
}
