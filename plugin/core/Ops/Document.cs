using System;
using System.IO;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace NavisworksMcp
{
    /// Opening, combining and saving documents. Every file op runs alone and outside a transaction: Navisworks cannot undo it.
    static class DocumentOps
    {
        public static readonly Op[] All =
        {
            Anywhere(Catalog.Apply("open", "path:path", Open, "open a model (nwd, nwf, nwc, ifc, rvt...), replacing the current document; unsaved changes are lost", "{path:\"C:/Coord/Federated.nwf\"}").Alone()),
            Catalog.Apply("append", "paths:strs", (c, a) => Combine(c, a, false), "append models to the document", "{paths:[\"C:/Coord/MEP.nwc\", \"C:/Coord/STR.nwc\"]}").Alone(),
            Catalog.Apply("merge", "paths:strs", (c, a) => Combine(c, a, true), "merge models into the document, dropping duplicates (for NWFs that share models)", "{paths:\"C:/Coord/Level2.nwf\"}").Alone(),
            Catalog.Apply("refresh", "", Refresh, "reload models whose files changed on disk", "{}").Alone(),
            Catalog.Apply("model.remove", "model:int", Remove, "remove model # (from status) from the document", "{model:1}").Alone(),
            Catalog.Read("export", "nwd", "path:path", Nwd, "publish the whole document to a self-contained NWD; the open document keeps its name", "{path:\"C:/Coord/out/Federated.nwd\"}"),
            Catalog.Read("export", "nwf", "path:path", Nwf, "save the document as NWF (links to the model files); the open document becomes this file", "{path:\"C:/Coord/Federated.nwf\"}"),
            Catalog.Read("export", "csv", "items:target; cols:strs path:path", Csv,
                "one row per item: id, name, type, then cols (Tab.Prop, bbox, path, hidden, color, transparency)", "{items:\"r1\", cols:\"Pset_Fixture.FireRating,bbox\", path:\"C:/Coord/walls.csv\"}"),
        };

        static Op Anywhere(Op op) { op.NeedsDocument = false; return op; }

        static string Existing(string path)
        {
            if (!Path.IsPathRooted(path)) throw new NwError("bad_args", $"path \"{path}\" is not absolute", "pass a full path such as C:/Coord/model.nwd");
            if (!File.Exists(path)) throw new NwError("not_found", $"no file \"{path}\"", "check the path; it must exist on this machine");
            return Path.GetFullPath(path);
        }

        static string Summary(Document d) =>
            $"{d.Models.Count} model(s), {d.Models.RootItemDescendantsAndSelf.Count():#,0} items";

        static Step Open(Ctx c, Args a)
        {
            var path = Existing(a.Str("path"));
            return new Step($"open {path}" + (c.Doc.IsModified ? " (unsaved changes in the current document are lost)" : ""), () =>
            {
                c.Doc.OpenFile(path);
                return $"opened {Path.GetFileName(path)}: {Summary(c.Doc)}. Earlier handles point at the old document";
            });
        }

        static Step Combine(Ctx c, Args a, bool merge)
        {
            var paths = a.List("paths").Select(Existing).ToArray();
            if (paths.Length == 0) throw new NwError("bad_args", "paths is empty", "pass paths:[\"C:/Coord/MEP.nwc\"]");
            var verb = merge ? "merge" : "append";
            return new Step($"{verb} {string.Join(", ", paths.Select(Path.GetFileName))}", () =>
            {
                var before = c.Doc.Models.Count;
                if (merge) c.Doc.MergeFiles(paths); else c.Doc.AppendFiles(paths);
                return $"{(merge ? "merged" : "appended")} {paths.Length} file(s): {c.Doc.Models.Count - before} model(s) added, now {Summary(c.Doc)}";
            });
        }

        static Step Refresh(Ctx c, Args a) => new Step("reload changed model files", () =>
            (c.Doc.UpdateFiles() ? "reloaded changed files" : "no file changed") + $": {Summary(c.Doc)}");

        static Step Remove(Ctx c, Args a)
        {
            var i = a.Int("model");
            if (i < 0 || i >= c.Doc.Models.Count) throw new NwError("bad_args", $"no model {i}; the document has models 0-{c.Doc.Models.Count - 1}", "call status to list models");
            var name = c.Doc.Models[i].RootItem.DisplayName;
            return new Step($"remove model {i} {name}", () =>
            {
                c.Doc.RemoveFile(i);
                return $"removed model {i} {name}: now {Summary(c.Doc)}";
            });
        }

        static string Target(Args a, string ext)
        {
            var path = ExportOps.OutPath(a, ext, ext);
            if (!path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase)) throw new NwError("bad_args", $"{ext} path must end in .{ext}", $"pass path:\"C:/Coord/out.{ext}\"");
            return path;
        }

        static void Nwd(Ctx c, Args a, Out o)
        {
            var path = Target(a, "nwd");
            c.Doc.PublishFile(path, new PublishProperties { Title = c.Doc.Title, AllowResave = true });
            o.Line($"nwd {path} {Size(path)}");
        }

        static void Nwf(Ctx c, Args a, Out o)
        {
            var path = Target(a, "nwf");
            c.Doc.SaveFile(path);
            o.Line($"nwf {path} {Size(path)}; document is now {Path.GetFileName(path)}");
        }

        static string Size(string path) => $"{Math.Ceiling(new FileInfo(path).Length / 1024.0):#,0} KB";

        static void Csv(Ctx c, Args a, Out o)
        {
            var items = a.Items("items");
            var cols = a.List("cols");
            var path = ExportOps.OutPath(a, "items", "csv");
            using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true)))
            {
                w.WriteLine(string.Join(",", new[] { "id", "name", "type" }.Concat(cols).Select(Quote)));
                foreach (var item in items)
                    w.WriteLine(string.Join(",", new[] { ReadOps.Id(c.Doc, item), item.DisplayName, item.ClassDisplayName }.Concat(cols.Select(col => ReadOps.Column(c.Doc, item, col))).Select(Quote)));
            }
            o.Line($"csv {path} {items.Count:#,0} rows");
        }

        static string Quote(string s) => s == null ? "" : s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
