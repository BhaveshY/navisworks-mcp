using System;
using System.IO;
using System.Reflection;
using Autodesk.Navisworks.Api.Plugins;

[Plugin("NavisworksMcp", "NMCP", DisplayName = "Navisworks MCP")]
public sealed class Shim : EventWatcherPlugin
{
    Type entry;

    public override void OnLoaded()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("NAVISWORKS_MCP_CORE");
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Path.GetDirectoryName(typeof(Shim).Assembly.Location), "NavisworksMcp.Core.dll");
            // Loading from bytes keeps the file unlocked, so a rebuild never fights a running Navisworks.
            var core = Assembly.Load(File.ReadAllBytes(path));
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => new AssemblyName(e.Name).Name == core.GetName().Name ? core : null;
            entry = core.GetType("NavisworksMcp.Entry", true);
            entry.GetMethod("Start").Invoke(null, new object[] { path });
        }
        catch (Exception e)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "navisworks-mcp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "plugin.log"), $"{DateTime.Now:o} pid {System.Diagnostics.Process.GetCurrentProcess().Id} failed to start: {e}\n");
        }
    }

    public override void OnUnloading() => entry?.GetMethod("Stop").Invoke(null, null);
}
