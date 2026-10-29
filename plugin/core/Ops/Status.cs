using System.IO;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace NavisworksMcp
{
    static class StatusOps
    {
        public static readonly Op[] All =
        {
            new Op
            {
                Tool = "status", Name = "status", Signature = "", Params = new Param[0], NeedsDocument = false,
                Doc = "instance, document, models, units, selection, jobs", Example = "{}",
                Read = Status,
            },
            Catalog.Read("job", "job", "id:str; wait:int=60000 cancel:bool=false", (c, a, o) => o.Reply = c.Host.Await(a.Str("id"), a.Int("wait"), a.Bool("cancel")),
                "wait up to `wait` ms for a job; cancel:true asks it to stop", "{id:\"j3\", wait:120000}"),
            Catalog.Read("help", "help", "tool:str; op:str", Help, "signature and example", "{tool:\"apply\", op:\"color\"}"),
        };

        static void Status(Ctx c, Args a, Out o)
        {
            var d = c.Doc;
            o.Line($"navisworks pid {c.Host.Pid} · Manage 2024 · core {Path.GetFileName(c.Host.CorePath)}");
            if (d.IsClear) { o.Line("no document open. next: apply {ops:[{op:\"open\", path:\"C:/path/model.nwd\"}]}"); return; }
            var file = string.IsNullOrEmpty(d.FileName) ? d.Title : d.FileName;
            o.Line($"doc {file} · units mm (document {d.Units}) · selection {d.CurrentSelection.SelectedItems.Count} · viewpoints {d.SavedViewpoints.RootItem.Children.Count} · sets {d.SelectionSets.RootItem.Children.Count}");
            o.Table(new[] { "#", "model", "units", "source" }, d.Models.Select((m, i) => new object[] { i, m.RootItem.DisplayName, m.Units, m.SourceFileName ?? m.FileName }));
            var jobs = c.Host.RunningJobs().Where(j => j != c.Job).ToArray();
            if (jobs.Length > 0) o.Line("jobs: " + string.Join(", ", jobs.Select(j => $"{j.Id} {j.Label} {j.Clock.Elapsed.TotalSeconds:0}s")));
        }

        static void Help(Ctx c, Args a, Out o)
        {
            var tool = a.Str("tool");
            var ops = Catalog.Ops.Where(x => x.Tool == tool && (!a.Has("op") || x.Name == a.Str("op"))).ToArray();
            if (ops.Length == 0) throw new NwError("not_found", $"no op {a.Str("op")} in {tool}", "call help {tool:\"" + tool + "\"} to list its ops");
            var selector = tool == "read" || tool == "export" ? "what" : tool == "apply" ? "op" : null;
            foreach (var op in ops)
            {
                o.Line(op.Line);
                if (op.Example == null) continue;
                var ex = selector == null ? op.Example : op.Example.Insert(1, $"{selector}:\"{op.Name}\", ");
                o.Line("  e.g. " + tool + " " + (tool == "apply" ? "{ops:[" + ex + "]}" : tool == "export" ? "{out:[" + ex + "]}" : ex));
            }
        }
    }
}
