using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Navisworks.Api;

namespace NavisworksMcp
{
    /// Suppress native file-resolution/error dialogs only for the duration of an MCP file operation.
    sealed class NativeFiles : IDisposable
    {
        string error, missing;
        readonly Hook callback;
        readonly IntPtr hook;
        delegate IntPtr Hook(int code, IntPtr window, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int kind, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr window, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        public NativeFiles()
        {
            // Some readers show a second Win32 error/cancel dialog after ProgressErrorReporting.
            // Veto its creation on this UI thread, only during this file call. No polling, input,
            // process-global suppression or persistent changes to the user's UI options.
            callback = BeforeWindow;
            hook = SetWindowsHookEx(5 /* WH_CBT */, callback, IntPtr.Zero, GetCurrentThreadId());
            if (hook == IntPtr.Zero) throw new NwError("bad_args", "cannot establish noninteractive file handling", "restart Navisworks to restore native file handling");
            Application.FileInteractiveResolving += Resolving;
            Application.ProgressErrorReporting += Reporting;
        }
        IntPtr BeforeWindow(int code, IntPtr window, IntPtr data)
        {
            if (code == 3 /* HCBT_CREATEWND */ || code == 5 /* HCBT_ACTIVATE */)
            {
                var name = new StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (code == 3 && name.ToString() == "#32770")
                {
                    if (error == null) error = "the file operation requires an interactive dialog";
                    return new IntPtr(1);
                }
                // 2027's WPF reference resolver runs before the public resolving event.
                // Cancel only an owned dialog on this operation's thread, before it takes focus.
                if (code == 5 && name.ToString().StartsWith("HwndWrapper[") && GetWindow(window, 4 /* GW_OWNER */) != IntPtr.Zero)
                {
                    if (error == null) error = "the file operation requires an interactive dialog";
                    PostMessage(window, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                    return new IntPtr(1);
                }
                if (code == 5) return new IntPtr(1); // including reactivation of the owner after cancellation
            }
            return CallNextHookEx(hook, code, window, data);
        }
        void Resolving(object sender, FileInteractiveResolvingEventArgs e)
        {
            missing = e.DisplayString;
            e.Response = FileResolutionResponse.Cancel;
            e.Handled = true;
        }
        void Reporting(object sender, ProgressErrorReportingEventArgs e) { if (error == null) error = e.Message; e.Canceled = true; e.Handled = true; }
        public void Check(bool success, string path, bool writing = false)
        {
            if (missing != null) throw new NwError("not_found", "unresolved model reference: " + missing, "restore the missing referenced model at its original path, then open the federation again");
            if (!success || error != null) throw new NwError("bad_args", $"cannot {(writing ? "write" : "load")} \"{path}\"" + (error == null ? "" : ": " + error),
                writing ? "choose a new writable output path with the correct extension" : "use a supported, readable model with all referenced files present");
        }
        public void Dispose()
        {
            Application.FileInteractiveResolving -= Resolving;
            Application.ProgressErrorReporting -= Reporting;
            UnhookWindowsHookEx(hook);
        }
    }

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
                using (var files = new NativeFiles()) files.Check(c.Doc.TryOpenFile(path), path);
                return $"opened {Path.GetFileName(path)}: {Summary(c.Doc)}. Earlier handles point at the old document";
            });
        }

        static Step Combine(Ctx c, Args a, bool merge)
        {
            var paths = a.List("paths").Select(Existing).ToArray();
            if (paths.Length == 0) throw new NwError("bad_args", "paths is empty", "pass paths:[\"C:/Coord/MEP.nwc\"]");
            var verb = merge ? "merge" : "append";
            if (c.Doc.ActiveSheetType == SheetType.Sheet2D)
                throw new NwError("bad_args", $"cannot {verb} models while a 2D sheet is active", "open a 3D model with apply {ops:[{op:\"open\",path:\"" + paths[0].Replace("\\", "/") + "\"}]} before appending or merging");
            return new Step($"{verb} {string.Join(", ", paths.Select(Path.GetFileName))}", () =>
            {
                var before = c.Doc.Models.Count;
                using (var files = new NativeFiles()) files.Check(merge ? c.Doc.TryMergeFiles(paths) : c.Doc.TryAppendFiles(paths), string.Join(", ", paths));
                return $"{(merge ? "merged" : "appended")} {paths.Length} file(s): {c.Doc.Models.Count - before} model(s) added, now {Summary(c.Doc)}";
            });
        }

        static Step Refresh(Ctx c, Args a) => new Step("reload changed model files", () =>
        {
            using (var files = new NativeFiles())
            {
                var changed = c.Doc.UpdateFiles();
                files.Check(true, c.Doc.FileName);
                return (changed ? "reloaded changed files" : "no file changed") + $": {Summary(c.Doc)}";
            }
        });

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
            ExportOps.Write(path, temp =>
            {
                using (var files = new NativeFiles())
                {
                    try { Compat.PublishNwd(c.Doc, temp, new PublishProperties { Title = c.Doc.Title, AllowResave = true }); }
                    catch (DocumentFileException) { files.Check(false, path, true); }
                    files.Check(true, path, true);
                }
            });
            o.Line($"nwd {path} {Size(path)}");
        }

        static void Nwf(Ctx c, Args a, Out o)
        {
            var path = Target(a, "nwf");
            using (var files = new NativeFiles()) files.Check(c.Doc.TrySaveFile(path), path, true);
            o.Line($"nwf {path} {Size(path)}; document is now {Path.GetFileName(path)}");
        }

        static string Size(string path) => $"{Math.Ceiling(new FileInfo(path).Length / 1024.0):#,0} KB";

        static void Csv(Ctx c, Args a, Out o)
        {
            var items = a.Items("items");
            var cols = a.List("cols");
            var path = ExportOps.OutPath(a, "items", "csv");
            ExportOps.Write(path, temp =>
            {
                using (var w = new StreamWriter(temp, false, new System.Text.UTF8Encoding(true)))
                {
                    w.WriteLine(string.Join(",", new[] { "id", "name", "type" }.Concat(cols).Select(Quote)));
                    foreach (var item in items)
                        w.WriteLine(string.Join(",", new[] { ReadOps.Id(c.Doc, item), item.DisplayName, item.ClassDisplayName }.Concat(cols.Select(col => ReadOps.Column(c.Doc, item, col))).Select(Quote)));
                }
            });
            o.Line($"csv {path} {items.Count:#,0} rows");
        }

        static string Quote(string s) => s == null ? "" : s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
