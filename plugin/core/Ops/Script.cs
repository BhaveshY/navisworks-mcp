using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Navisworks.Api;
using Microsoft.CSharp;

namespace NavisworksMcp
{
    /// What a script sees. Public because the compiled script assembly links against it.
    public sealed class Nw
    {
        readonly Ctx c;
        internal Nw(Ctx c) { this.c = c; }
        public Document Doc => c.Doc;
        public bool Cancelled => c.Job != null && c.Job.Cancelled;
        public void CheckCancelled() => c.CheckCancelled();
        public readonly StringBuilder Out = new StringBuilder();
        public ModelItemCollection Items(string target) => Target.Parse(c, target).Items(c);
        public string Handle(IEnumerable<ModelItem> items) => c.Keep(new Handle { Kind = Kind.Items, Rows = items.Cast<object>().ToList(), Label = "script" }).Name;
        public double Mm(double documentLength) => Units.ToMm(c.Doc, documentLength);
    }

    static class ScriptOps
    {
        const string Header = "using System; using System.Linq; using System.Collections.Generic; using Autodesk.Navisworks.Api; using NavisworksMcp;\npublic static class McpScript { public static object Run(Nw nw) {\n";
        static readonly ConcurrentDictionary<string, MethodInfo> Compiled = new ConcurrentDictionary<string, MethodInfo>();

        public static readonly Op[] All =
        {
            Catalog.Read("script", "script", "code:str; check:bool=false", Run,
                "C# 5 method body; return a value or write to nw.Out. Runs as one undo step. check:true only compiles. Loops must check nw.Cancelled or call nw.CheckCancelled(); cancellation is cooperative and cannot interrupt a blocked native call",
                "{code:\"var walls = nw.Items(\\\"?Item.Type = IfcWall\\\"); return nw.Handle(walls.Where(w => nw.Mm(w.BoundingBox().Size.Z) > 2900));\"}").Anywhere(),
        };

        static MethodInfo Compile(Ctx c, string code)
        {
            var key = Convert.ToBase64String(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(code)));
            if (Compiled.TryGetValue(key, out var cached)) return cached;
            var navisworks = AppDomain.CurrentDomain.BaseDirectory;
            var options = new CompilerParameters { GenerateInMemory = true, TreatWarningsAsErrors = false };
            options.ReferencedAssemblies.AddRange(new[] { "System.dll", "System.Core.dll", "System.Data.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Xml.dll", c.Host.CorePath });
            options.ReferencedAssemblies.AddRange(new[] { "Autodesk.Navisworks.Api", "Autodesk.Navisworks.Clash", "Autodesk.Navisworks.Timeliner", "Autodesk.Navisworks.Takeoff", "Autodesk.Navisworks.ComApi", "Autodesk.Navisworks.Interop.ComApi" }
                .Select(n => Path.Combine(navisworks, n + ".dll")).ToArray());
            using (var provider = new CSharpCodeProvider())
            {
                var result = provider.CompileAssemblyFromSource(options, Header + code + "\n;return null; } }");
                var errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToList();
                if (errors.Count > 0)
                    throw new NwError("script", "compile failed:\n" + string.Join("\n", errors.Take(10).Select(e => $"line {e.Line - 2}: {e.ErrorText}")),
                        "fix the code; the dialect is C# 5 (no string interpolation, no ?., no nameof)");
                return Compiled[key] = result.CompiledAssembly.GetType("McpScript").GetMethod("Run");
            }
        }

        static void Run(Ctx c, Args a, Out o)
        {
            var run = Compile(c, a.Str("code"));
            if (a.Bool("check")) { o.Line("compiled ok"); return; }
            var nw = new Nw(c);
            object value;
            var undoBefore = c.Doc.NextUndo;
            using (var tx = c.Doc.BeginTransaction("MCP script"))
            {
                try { value = run.Invoke(null, new object[] { nw }); }
                catch (TargetInvocationException e)
                {
                    var inner = e.InnerException;
                    if (inner is NwError error) throw error;
                    throw new NwError("script", $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("McpScript"))?.Trim()}", "fix the code and run it again; changes made before the error stay applied unless you undo");
                }
                tx.Commit();
            }
            if (nw.Out.Length > 0) o.Line(nw.Out.ToString().TrimEnd());
            switch (value)
            {
                case null: break;
                case string s: o.Line(s); break;
                case ModelItemCollection items: o.Line(nw.Handle(items) + $" items {items.Count:#,0}"); break;
                case IEnumerable<ModelItem> items: var list = items.ToList(); o.Line(nw.Handle(list) + $" items {list.Count:#,0}"); break;
                case IDictionary dict: foreach (DictionaryEntry kv in dict) o.Line($"{kv.Key}: {kv.Value}"); break;
                case IEnumerable seq: foreach (var x in seq) o.Line(Out.Cell(x)); break;
                default: o.Line(Out.Cell(value)); break;
            }
            if (c.Doc.NextUndo != undoBefore && c.Doc.NextUndo == "MCP script") o.Line("undo: apply {ops:[{op:\"undo\"}]}");
        }
    }
}
