using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Interop;
using Autodesk.Navisworks.Api.Timeliner;
using Newtonsoft.Json.Linq;

namespace NavisworksMcp
{
    /// The native helper exposes GetViewType but no setter. Drive its own settings dialog
    /// on this thread before it can activate; do not synthesize actual task dates to simulate a plan.
    sealed class PlannedSimulation : IDisposable
    {
        readonly Hook callback;
        readonly IntPtr hook;
        bool configured;
        delegate IntPtr Hook(int code, IntPtr window, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int kind, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr window, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr window, int id);
        [DllImport("user32.dll")] static extern bool CheckRadioButton(IntPtr window, int first, int last, int selected);
        [DllImport("user32.dll")] static extern bool CheckDlgButton(IntPtr window, int id, uint check);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        PlannedSimulation()
        {
            callback = BeforeActivation;
            hook = SetWindowsHookEx(5 /* WH_CBT */, callback, IntPtr.Zero, GetCurrentThreadId());
            if (hook == IntPtr.Zero) throw Unavailable();
        }
        static NwError Unavailable() => new NwError("unsupported_state", "cannot select Planned simulation settings", "in TimeLiner > Simulate > Settings select Planned and disable Override Start / End Dates");
        IntPtr BeforeActivation(int code, IntPtr window, IntPtr data)
        {
            if (code == 5 /* HCBT_ACTIVATE */)
            {
                var name = new StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() == "#32770")
                {
                    // Resource ids in the 2024/2027 TimeLiner dialog; independent of UI language.
                    bool expected = GetDlgItem(window, 23063) != IntPtr.Zero && GetDlgItem(window, 23061) != IntPtr.Zero && GetDlgItem(window, 23047) != IntPtr.Zero;
                    configured = expected && CheckRadioButton(window, 23061, 23065, 23063) && CheckDlgButton(window, 23047, 0);
                    ShowWindow(window, 0 /* SW_HIDE */);
                    PostMessage(window, 0x111 /* WM_COMMAND */, new IntPtr(configured ? 1 : 2), IntPtr.Zero);
                    return new IntPtr(1); // never take keyboard focus, even for an unexpected dialog
                }
                return new IntPtr(1); // DialogBox must not activate its owner when it closes, either
            }
            return CallNextHookEx(hook, code, window, data);
        }
        public static void Configure(LcTlSimulationHelper sim)
        {
            using (var dialog = new PlannedSimulation())
            {
                sim.DisplaySettingsDialog();
                if (!dialog.configured) throw Unavailable();
            }
        }
        public void Dispose() { UnhookWindowsHookEx(hook); }
    }

    /// TimeLiner tasks, addressed by a handle row (r3#0) or a name path such as "Level 1/Walls".
    static class TimelinerOps
    {
        public static readonly Op[] All =
        {
            Catalog.Read("read", "tasks", "; where:where in:target limit:int=50 page:int=1 offset:int=0", List,
                "TimeLiner tasks -> r#, with the document's task types and the simulation date. where filters fields name, path, type, start, end, actual_start, actual_end, items with = != ~ > >= < <= joined by and",
                "{where:\"type = Construct and start >= 2026-03-01\"}"),
            Catalog.Apply("tl.add", "name:str start:date end:date; task_type:str=Construct parent:str items:target actual_start:date actual_end:date", Add,
                "add a task (parent is a task path). items attaches sets live when every target is set:..., else the items themselves",
                "{name:\"Walls L1\", start:\"2026-03-01\", end:\"2026-03-10\", parent:\"Level 1\", items:\"set:QA/Walls\"}"),
            Catalog.Apply("tl.update", "tasks:strs; name:str start:date end:date actual_start:date actual_end:date task_type:str", Update,
                "change name, dates or type of tasks", "{tasks:\"Level 1/Walls L1\", end:\"2026-03-12\"}"),
            Catalog.Apply("tl.delete", "tasks:strs", Delete, "delete tasks with their subtasks", "{tasks:\"r4#0-2\"}"),
            Catalog.Apply("tl.attach", "; tasks:strs items:target match:str", Attach,
                "attach items to tasks: set:... targets attach live sets, other targets attach the items. match:\"name=set\" attaches to each task (default all) the set named like it",
                "{match:\"name=set\"}"),
            Catalog.Apply("tl.import", "csv:path; map:json replace:bool=false", Import,
                "create tasks from a CSV with a header row. map names the columns {name, start, end, type, actual_start, actual_end, parent, set, format}; by default headers Name, Start, End, Type, ActualStart, ActualEnd, Parent, Set. replace clears existing tasks",
                "{csv:\"C:/Coord/schedule.csv\", map:{name:\"Task\", start:\"Begin\", end:\"Finish\", format:\"dd.MM.yyyy\"}}"),
            Catalog.Apply("tl.simulate", "; at:date stop:bool=false", Simulate,
                "drive the TimeLiner simulation (planned) to a date, clamped to the schedule; the view then shows the 4D state for export image. stop:true leaves the simulation",
                "{at:\"2026-03-05\"}").Alone(),
            Catalog.Read("export", "tasks_csv", "; path:path", ExportCsv,
                "write all tasks to CSV; tl.import reads it back with the default map", "{path:\"C:/Coord/tasks.csv\"}"),
        };

        static DocumentTimeliner Tl(Ctx c) => c.Doc.GetTimeliner();

        static IEnumerable<TimelinerTask> Walk(GroupItem parent)
        {
            foreach (var t in parent.Children.OfType<TimelinerTask>())
            {
                yield return t;
                foreach (var x in Walk(t)) yield return x;
            }
        }

        static string PathOf(TimelinerTask t)
        {
            var names = new List<string>();
            for (GroupItem x = t; x is TimelinerTask; x = x.Parent) names.Insert(0, x.DisplayName);
            return string.Join("/", names);
        }

        static int Depth(TimelinerTask t)
        {
            var d = 0;
            for (var x = t.Parent; x is TimelinerTask; x = x.Parent) d++;
            return d;
        }

        static TimelinerTask Find(DocumentTimeliner tl, string path)
        {
            GroupItem node = tl.TasksRoot;
            foreach (var part in path.Split('/'))
            {
                node = node.Children.OfType<TimelinerTask>().FirstOrDefault(x => x.DisplayName == part);
                if (node == null) return null;
            }
            return (TimelinerTask)node;
        }

        /// Throws when the task no longer belongs to the document, so a stale handle never edits a detached copy.
        static void Live(DocumentTimeliner tl, TimelinerTask t)
        {
            try { tl.TaskCreateIndexPath(t); }
            catch (ArgumentException) { throw new NwError("stale_handle", $"task \"{t.DisplayName}\" was deleted", "read {what:\"tasks\"} again and use the new handle"); }
        }

        static string Id(DocumentTimeliner tl, TimelinerTask t) => string.Join(".", tl.TaskCreateIndexPath(t).Select(i => i + 1));

        static List<TimelinerTask> Resolve(Ctx c, Args a, string name)
        {
            var tl = Tl(c);
            var list = new List<TimelinerTask>();
            foreach (var s in a.List(name))
            {
                if (Target.IsReference(s))
                    foreach (var row in Target.Parse(c, new JValue(s)).Rows(c))
                        list.Add(row as TimelinerTask ?? throw new NwError("bad_args", $"{s} holds {row.GetType().Name} rows, not tasks", "pass a handle from read {what:\"tasks\"} or a task path"));
                else list.Add(Find(tl, s) ?? throw new NwError("not_found", $"no task \"{s}\"", "list tasks with read {what:\"tasks\"} and use the path or the handle row"));
            }
            foreach (var t in list) Live(tl, t);
            return list.Distinct().ToList();
        }

        static string Date(DateTime? d) => d == null ? "-" : d.Value.TimeOfDay == TimeSpan.Zero ? d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.Value.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

        static string Count(int n) => n == 1 ? "1 task" : $"{n:#,0} tasks";

        static string SetPath(SavedItem s)
        {
            var names = new List<string>();
            for (var x = s; x != null && x.Parent != null; x = x.Parent) names.Insert(0, x.DisplayName);
            return string.Join("/", names);
        }

        static IEnumerable<string> SetsOf(Ctx c, TimelinerTask t) =>
            t.Selection.HasSelectionSources ? t.Selection.SelectionSources.Select(s => c.Doc.SelectionSets.ResolveSelectionSource(s)).Where(s => s != null).Select(SetPath) : Enumerable.Empty<string>();

        static string Source(Ctx c, TimelinerTask t)
        {
            var sel = t.Selection;
            if (sel.IsClear) return "-";
            if (sel.HasSelectionSources) return string.Join("; ", SetsOf(c, t).Select(p => "set:" + p));
            return sel.HasSearch ? "search" : "explicit";
        }

        static int ItemCount(Ctx c, TimelinerTask t) => t.Selection.IsClear ? 0 : t.Selection.GetSelectedItems(c.Doc).Count;

        static string Types(DocumentTimeliner tl) => string.Join(", ", tl.SimulationTaskTypes.Select(x => x.DisplayName));

        // Task filter: `field op value` joined by and.
        static readonly Regex CondRx = new Regex(@"^(?<f>\w+)\s*(?<op>!=|>=|<=|=|~|>|<)\s*(?<v>.*)$");
        static readonly string[] Fields = { "name", "path", "type", "start", "end", "actual_start", "actual_end", "items" };

        static Func<TimelinerTask, bool> Filter(Ctx c, string where)
        {
            var conds = Where.AndParts(where.Trim()).Select(piece =>
            {
                var m = CondRx.Match(piece.Trim());
                if (!m.Success || !Fields.Contains(m.Groups["f"].Value))
                    throw new NwError("bad_args", $"cannot read task condition \"{piece.Trim()}\"", "write field op value with fields " + string.Join(", ", Fields) + ", e.g. type = Construct and start >= 2026-03-01");
                var field = m.Groups["f"].Value;
                var op = m.Groups["op"].Value;
                var text = Where.Unquote(m.Groups["v"].Value.Trim());
                var isDate = field == "start" || field == "end" || field.StartsWith("actual_");
                DateTime date = default;
                if (isDate && !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    throw new NwError("bad_args", $"\"{text}\" is not a date", "use ISO dates such as 2026-03-01");
                Func<TimelinerTask, bool> test = t =>
                {
                    if (isDate)
                    {
                        var d = field == "start" ? t.PlannedStartDate : field == "end" ? t.PlannedEndDate : field == "actual_start" ? t.ActualStartDate : t.ActualEndDate;
                        if (d == null) return op == "!=";
                        var cmp = d.Value.CompareTo(date);
                        return op == "=" ? cmp == 0 : op == "!=" ? cmp != 0 : op == ">" ? cmp > 0 : op == ">=" ? cmp >= 0 : op == "<" ? cmp < 0 : op == "<=" ? cmp <= 0 : d.Value.ToString("yyyy-MM-dd").Contains(text);
                    }
                    if (field == "items")
                    {
                        var n = ItemCount(c, t);
                        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) throw new NwError("bad_args", $"items \"{text}\" is not a whole number", "e.g. items = 0");
                        return op == "=" ? n == v : op == "!=" ? n != v : op == ">" ? n > v : op == ">=" ? n >= v : op == "<" ? n < v : op == "<=" ? n <= v : false;
                    }
                    var s = field == "name" ? t.DisplayName : field == "path" ? PathOf(t) : t.SimulationTaskTypeName ?? "";
                    var eq = string.Equals(s, text, StringComparison.OrdinalIgnoreCase);
                    return op == "=" ? eq : op == "!=" ? !eq : op == "~" ? s.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 : throw new NwError("bad_args", $"{field} takes = != ~", "compare text fields with = != ~");
                };
                return test;
            }).ToList();
            return t => conds.All(f => f(t));
        }

        static void List(Ctx c, Args a, Out o)
        {
            var tl = Tl(c);
            var target = a.Target("in");
            Handle h;
            if (!a.Has("where") && target?.Whole?.Kind == Kind.Tasks)
                h = target.Whole;
            else
            {
                var rows = target != null ? target.Rows(c).Select(r => r as TimelinerTask ?? throw new NwError("bad_args", $"{target.Text} holds {r.GetType().Name} rows, not tasks", "pass a handle from read {what:\"tasks\"}")).ToList() : Walk(tl.TasksRoot).ToList();
                if (a.Has("where")) rows = rows.Where(Filter(c, a.Str("where"))).ToList();
                h = c.Keep(new Handle { Kind = Kind.Tasks, Rows = rows.Cast<object>().ToList(), Label = a.Str("where") ?? "tasks" });
            }
            var sim = LcTlSimulationHelper.Instance;
            var simulating = simulatedAt != null && simulatedGeneration == c.Host.Handles.Generation && sim.IsGUIActive();
            o.Line($"{h.Name} tasks {h.Rows.Count:#,0} (exact) · types: {Types(tl)}" + (simulating ? " · simulation at " + Date(simulatedAt) : ""));
            if (h.Rows.Count == 0)
            {
                if (tl.Tasks.Count == 0) o.Line("no tasks yet. next: apply {ops:[{op:\"tl.add\", ...}]} or tl.import");
                return;
            }
            var limit = a.Int("limit");
            var page = a.Int("page");
            var start = Out.Start(page, limit, a.Int("offset"));
            var header = new List<string> { "#", "id", "task", "type", "start", "end", "actual_start", "actual_end", "items", "attached" };
            if (simulating) header.Add("active");
            o.Table(header, h.Rows.Skip(start).Take(limit).Select((r, i) =>
            {
                var t = (TimelinerTask)r;
                Live(tl, t);
                var row = new List<object>
                {
                    start + i, Id(tl, t), new string(' ', Depth(t) * 2) + t.DisplayName, t.SimulationTaskTypeName, Date(t.PlannedStartDate), Date(t.PlannedEndDate),
                    Date(t.ActualStartDate), Date(t.ActualEndDate), ItemCount(c, t), Source(c, t),
                };
                if (simulating) row.Add(sim.IsActiveTask(t) ? "yes" : "no");
                return row;
            }));
            o.More("tasks", h, page, limit, start: start);
        }

        static string TaskType(DocumentTimeliner tl, string name)
        {
            var found = tl.SimulationTaskTypes.FirstOrDefault(x => string.Equals(x.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            if (found == null) throw new NwError("bad_args", $"no task type \"{name}\"", "use one of: " + Types(tl));
            return found.DisplayName;
        }

        static void CheckDates(DateTime? start, DateTime? end, string what)
        {
            if (start != null && end != null && end < start) throw new NwError("bad_args", $"{what} end {Date(end)} is before start {Date(start)}", "pass an end on or after the start");
        }

        /// What a target attaches: live set references when every part is set:..., else a fixed list of items.
        sealed class Attachment
        {
            public List<SelectionSet> Sets;
            public ModelItemCollection Items;
            public string Text => Sets != null ? string.Join(", ", Sets.Select(s => "set:" + SetPath(s))) : $"{Items.Count:#,0} items";

            public void Into(Ctx c, TimelinerSelection sel)
            {
                if (Sets == null) { sel.CopyFrom(Items); return; }
                var sources = new SelectionSourceCollection();
                foreach (var s in Sets) sources.Add(c.Doc.SelectionSets.CreateSelectionSource(s));
                sel.CopyFrom(sources);
            }
        }

        static Attachment Attachable(Ctx c, Args a, string name)
        {
            if (!a.Has(name)) return null;
            var parts = a.List(name);
            if (parts.All(p => p.StartsWith("set:"))) return new Attachment { Sets = parts.Select(p => Sets.Find(c.Doc, p.Substring(4))).ToList() };
            return new Attachment { Items = a.Items(name) };
        }

        static Step Add(Ctx c, Args a)
        {
            var tl = Tl(c);
            var name = a.Str("name");
            var start = Day(a, "start");
            var end = Day(a, "end");
            CheckDates(start, end, "planned");
            DateTime? actualStart = a.Has("actual_start") ? Day(a, "actual_start") : (DateTime?)null;
            DateTime? actualEnd = a.Has("actual_end") ? Day(a, "actual_end") : (DateTime?)null;
            CheckDates(actualStart, actualEnd, "actual");
            var type = TaskType(tl, a.Str("task_type"));
            GroupItem parent = tl.TasksRoot;
            if (a.Has("parent")) parent = Find(tl, a.Str("parent")) ?? throw new NwError("not_found", $"no parent task \"{a.Str("parent")}\"", "add the parent first (an earlier op in the same call works) or list tasks with read {what:\"tasks\"}");
            var attach = Attachable(c, a, "items");
            var where = a.Has("parent") ? $" under \"{a.Str("parent")}\"" : "";
            var desc = $"task \"{name}\"{where} {Date(start)}..{Date(end)} {type}" + (attach != null ? ", " + attach.Text : "");
            return new Step("add " + desc, () =>
            {
                var t = new TimelinerTask { DisplayName = name, PlannedStartDate = start, PlannedEndDate = end, ActualStartDate = actualStart, ActualEndDate = actualEnd, SimulationTaskTypeName = type };
                attach?.Into(c, t.Selection);
                var added = AddCopy(tl, parent, t);
                Summarise(tl, added);
                var h = c.Keep(new Handle { Kind = Kind.Tasks, Rows = new List<object> { added }, Label = "tl.add" });
                return $"added {desc} -> {h.Name}";
            });
        }

        /// TasksRoot is a snapshot, so a task added at the top is looked up on a fresh root; task parents update in place.
        static TimelinerTask AddCopy(DocumentTimeliner tl, GroupItem parent, TimelinerTask task)
        {
            tl.TaskAddCopy(parent, task);
            return (parent is TimelinerTask ? parent : tl.TasksRoot).Children.OfType<TimelinerTask>().Last();
        }

        /// Dates the agent passes are wall-clock schedule dates; a Local kind would be shifted to UTC when stored.
        static DateTime Day(Args a, string name) => DateTime.SpecifyKind(a.Date(name), DateTimeKind.Unspecified);

        /// A parent task spans its subtasks, as in the TimeLiner window. The API recalculates only a detached copy.
        static void Summarise(DocumentTimeliner tl, GroupItem from)
        {
            if (!(from is TimelinerTask top)) return;
            while (top.Parent is TimelinerTask up) top = up;
            if (top.Children.Count == 0) return;
            var copy = top.CreateCopy();
            DocumentTimeliner.TaskSummaryHierarchyRecalculateDates(copy);
            tl.TaskEdit(top.Parent, top.Parent.Children.IndexOf(top), copy);
        }

        static void Edit(DocumentTimeliner tl, TimelinerTask t, Action<TimelinerTask> change)
        {
            var copy = t.CreateCopy();
            change(copy);
            tl.TaskEdit(t.Parent, t.Parent.Children.IndexOf(t), copy);
        }

        static Step Update(Ctx c, Args a)
        {
            var tl = Tl(c);
            var tasks = Resolve(c, a, "tasks");
            var changes = new List<(string text, Action<TimelinerTask> apply)>();
            if (a.Has("name")) { var v = a.Str("name"); changes.Add(($"name \"{v}\"", t => t.DisplayName = v)); }
            if (a.Has("start")) { var v = Day(a, "start"); changes.Add(("start " + Date(v), t => t.PlannedStartDate = v)); }
            if (a.Has("end")) { var v = Day(a, "end"); changes.Add(("end " + Date(v), t => t.PlannedEndDate = v)); }
            if (a.Has("actual_start")) { var v = Day(a, "actual_start"); changes.Add(("actual_start " + Date(v), t => t.ActualStartDate = v)); }
            if (a.Has("actual_end")) { var v = Day(a, "actual_end"); changes.Add(("actual_end " + Date(v), t => t.ActualEndDate = v)); }
            if (a.Has("task_type")) { var v = TaskType(tl, a.Str("task_type")); changes.Add(("type " + v, t => t.SimulationTaskTypeName = v)); }
            if (changes.Count == 0) throw new NwError("bad_args", "tl.update needs something to change", "pass name, start, end, actual_start, actual_end or task_type");
            foreach (var t in tasks)
            {
                var probe = t.CreateCopyWithoutChildren();
                foreach (var ch in changes) ch.apply(probe);
                CheckDates(probe.PlannedStartDate, probe.PlannedEndDate, $"\"{t.DisplayName}\" planned");
                CheckDates(probe.ActualStartDate, probe.ActualEndDate, $"\"{t.DisplayName}\" actual");
            }
            var text = string.Join(", ", changes.Select(x => x.text));
            return new Step($"set {text} on {Count(tasks.Count)}", () =>
            {
                foreach (var t in tasks)
                {
                    Edit(tl, t, copy => { foreach (var ch in changes) ch.apply(copy); });
                    Summarise(tl, t);
                }
                return $"set {text} on {Count(tasks.Count)}";
            });
        }

        static Step Delete(Ctx c, Args a)
        {
            var tl = Tl(c);
            var tasks = Resolve(c, a, "tasks");
            var gone = new HashSet<TimelinerTask>(tasks.SelectMany(t => Walk(t)));
            var roots = tasks.Where(t => !gone.Contains(t)).ToList();
            var names = string.Join(", ", roots.Take(5).Select(t => "\"" + PathOf(t) + "\"")) + (roots.Count > 5 ? $" and {roots.Count - 5} more" : "");
            return new Step($"delete {names} ({Count(roots.Count + gone.Count)} with subtasks)", () =>
            {
                foreach (var t in roots)
                {
                    var parent = t.Parent;
                    tl.TaskRemoveAt(parent, parent.Children.IndexOf(t));
                    Summarise(tl, parent);
                }
                return $"deleted {names}";
            });
        }

        static Step Attach(Ctx c, Args a)
        {
            var tl = Tl(c);
            if (a.Has("items") == a.Has("match")) throw new NwError("bad_args", "tl.attach needs items or match, not both", "pass tasks and items:\"set:QA/Walls\", or match:\"name=set\"");
            if (a.Has("items"))
            {
                if (!a.Has("tasks")) throw new NwError("bad_args", "tl.attach with items needs tasks", "pass tasks:\"r3#0\" or a task path");
                var tasks = Resolve(c, a, "tasks");
                var attach = Attachable(c, a, "items");
                return new Step($"attach {attach.Text} to {Count(tasks.Count)}", () =>
                {
                    foreach (var t in tasks) Edit(tl, t, copy => attach.Into(c, copy.Selection));
                    return $"attached {attach.Text} to {Count(tasks.Count)}";
                });
            }
            if (!string.Equals(a.Str("match").Replace(" ", ""), "name=set", StringComparison.OrdinalIgnoreCase))
                throw new NwError("bad_args", $"match \"{a.Str("match")}\" is not supported", "use match:\"name=set\" (task name equals set name)");
            var scope = a.Has("tasks") ? Resolve(c, a, "tasks") : Walk(tl.TasksRoot).ToList();
            var sets = Sets.Walk(c.Doc.SelectionSets.RootItem).Where(x => x.item is SelectionSet).ToList();
            var pairs = new List<(TimelinerTask task, SelectionSet set)>();
            var unmatched = new List<string>();
            foreach (var t in scope)
            {
                var hits = sets.Where(s => s.path == t.DisplayName || s.item.DisplayName == t.DisplayName).Select(s => (SelectionSet)s.item).Distinct().ToList();
                if (hits.Count == 1) pairs.Add((t, hits[0]));
                else unmatched.Add(t.DisplayName + (hits.Count > 1 ? $" ({hits.Count} sets match)" : ""));
            }
            var tail = unmatched.Count == 0 ? "" : $"; no single set for {unmatched.Count}: " + string.Join(", ", unmatched.Take(5)) + (unmatched.Count > 5 ? ", ..." : "");
            return new Step($"attach sets by name to {Count(pairs.Count)}{tail}", () =>
            {
                foreach (var (t, s) in pairs) Edit(tl, t, copy => new Attachment { Sets = new List<SelectionSet> { s } }.Into(c, copy.Selection));
                return $"attached sets by name to {Count(pairs.Count)}{tail}";
            });
        }

        static readonly Dictionary<string, string[]> DefaultColumns = new Dictionary<string, string[]>
        {
            ["name"] = new[] { "name", "task", "task name" },
            ["start"] = new[] { "start", "planned start" },
            ["end"] = new[] { "end", "finish", "planned end" },
            ["type"] = new[] { "type", "task type" },
            ["actual_start"] = new[] { "actualstart", "actual start", "actual_start" },
            ["actual_end"] = new[] { "actualend", "actual end", "actual_end" },
            ["parent"] = new[] { "parent" },
            ["set"] = new[] { "set", "sets" },
        };

        public static List<string[]> ReadCsv(string path)
        {
            var text = File.ReadAllText(path);
            var delim = text.Split('\n')[0].Count(ch => ch == ';') > text.Split('\n')[0].Count(ch => ch == ',') ? ';' : ',';
            var rows = new List<string[]>();
            var row = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else cell.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == delim) { row.Add(cell.ToString()); cell.Clear(); }
                else if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Any(x => x.Length > 0)) rows.Add(row.ToArray());
                    row = new List<string>();
                }
                else cell.Append(ch);
            }
            row.Add(cell.ToString());
            if (row.Any(x => x.Length > 0)) rows.Add(row.ToArray());
            return rows;
        }

        sealed class CsvTask
        {
            public int Line;
            public string Name, Type, Parent;
            public DateTime Start, End;
            public DateTime? ActualStart, ActualEnd;
            public List<SelectionSet> Sets = new List<SelectionSet>();
        }

        static SelectionSet SetByName(Ctx c, string name) =>
            Sets.Lookup(c.Doc.SelectionSets.RootItem, name) as SelectionSet
            ?? Single(Sets.Walk(c.Doc.SelectionSets.RootItem).Select(x => x.item).OfType<SelectionSet>().Where(s => s.DisplayName == name));

        /// The one element, or null when there are none or several, so ambiguous names report instead of throwing.
        static T Single<T>(IEnumerable<T> xs) where T : class { var two = xs.Take(2).ToList(); return two.Count == 1 ? two[0] : null; }

        static Step Import(Ctx c, Args a)
        {
            var tl = Tl(c);
            var path = a.Str("csv");
            if (!File.Exists(path)) throw new NwError("not_found", $"no file {path}", "pass the absolute path of an existing CSV");
            var rows = ReadCsv(path);
            if (rows.Count < 2) throw new NwError("bad_args", $"{path} has no data rows", "the first row is the header, one task per following row");
            var header = rows[0].Select(h => h.Trim()).ToArray();
            var map = a.Json("map") as JObject ?? new JObject();
            foreach (var key in map.Properties().Select(p => p.Name))
                if (key != "format" && !DefaultColumns.ContainsKey(key)) throw new NwError("bad_args", $"map key \"{key}\" is unknown", "map keys: " + string.Join(", ", DefaultColumns.Keys) + ", format");
            var format = (string)map["format"];
            int Col(string key)
            {
                var names = map[key] != null ? new[] { (string)map[key] } : DefaultColumns[key];
                var i = Array.FindIndex(header, h => names.Any(n => string.Equals(h, n, StringComparison.OrdinalIgnoreCase)));
                if (i < 0 && map[key] != null) throw new NwError("bad_args", $"column \"{map[key]}\" for {key} is not in the header", "header columns: " + string.Join(", ", header));
                return i;
            }
            var cols = DefaultColumns.Keys.ToDictionary(k => k, Col);
            foreach (var need in new[] { "name", "start", "end" })
                if (cols[need] < 0) throw new NwError("bad_args", $"no {need} column", $"pass map:{{{need}:\"<column>\"}}; header columns: " + string.Join(", ", header));

            var errors = new List<string>();
            var tasks = new List<CsvTask>();
            string Cell(string[] row, string key) => cols[key] >= 0 && cols[key] < row.Length ? row[cols[key]].Trim() : "";
            DateTime? When(string text, int line, string key)
            {
                if (text.Length == 0) return null;
                var ok = format != null
                    ? DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    : DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
                if (!ok) errors.Add($"line {line}: {key} \"{text}\" is not a date");
                return ok ? d : (DateTime?)null;
            }
            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                var line = i + 1;
                var t = new CsvTask { Line = line, Name = Cell(row, "name"), Parent = Cell(row, "parent") };
                if (t.Name.Length == 0) { errors.Add($"line {line}: empty name"); continue; }
                var start = When(Cell(row, "start"), line, "start");
                var end = When(Cell(row, "end"), line, "end");
                if (start == null || end == null) { if (Cell(row, "start").Length == 0 || Cell(row, "end").Length == 0) errors.Add($"line {line}: start and end are required"); continue; }
                t.Start = start.Value;
                t.End = end.Value;
                if (t.End < t.Start) errors.Add($"line {line}: end before start");
                t.ActualStart = When(Cell(row, "actual_start"), line, "actual_start");
                t.ActualEnd = When(Cell(row, "actual_end"), line, "actual_end");
                var type = Cell(row, "type");
                t.Type = type.Length == 0 ? "Construct" : tl.SimulationTaskTypes.FirstOrDefault(x => string.Equals(x.DisplayName, type, StringComparison.OrdinalIgnoreCase))?.DisplayName;
                if (t.Type == null) errors.Add($"line {line}: no task type \"{type}\" (types: {Types(tl)})");
                foreach (var s in Cell(row, "set").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    var set = SetByName(c, s);
                    if (set == null) errors.Add($"line {line}: no single set \"{s}\""); else t.Sets.Add(set);
                }
                tasks.Add(t);
            }
            var paths = new HashSet<string>(a.Bool("replace") ? Enumerable.Empty<string>() : Walk(tl.TasksRoot).Select(PathOf));
            foreach (var t in tasks)
            {
                if (t.Parent.Length > 0 && !paths.Contains(t.Parent)) errors.Add($"line {t.Line}: parent \"{t.Parent}\" is neither an existing task nor an earlier row");
                paths.Add(t.Parent.Length > 0 ? t.Parent + "/" + t.Name : t.Name);
            }
            if (errors.Count > 0)
                throw new NwError("bad_args", $"{errors.Count} problem(s) in {Path.GetFileName(path)}: " + string.Join("; ", errors.Take(8)) + (errors.Count > 8 ? "; ..." : ""), "fix the CSV or the map and import again; nothing was imported");

            var attached = tasks.Count(t => t.Sets.Count > 0);
            var desc = $"{Count(tasks.Count)} from {Path.GetFileName(path)}" + (attached > 0 ? $", {attached} attached to sets" : "") + (a.Bool("replace") ? $", replacing {Count(Walk(tl.TasksRoot).Count())}" : "");
            return new Step("import " + desc, () =>
            {
                if (a.Bool("replace")) tl.TasksClear();
                var made = new List<object>();
                foreach (var t in tasks)
                {
                    GroupItem parent = t.Parent.Length > 0 ? Find(tl, t.Parent) : tl.TasksRoot;
                    var task = new TimelinerTask { DisplayName = t.Name, PlannedStartDate = t.Start, PlannedEndDate = t.End, ActualStartDate = t.ActualStart, ActualEndDate = t.ActualEnd, SimulationTaskTypeName = t.Type };
                    if (t.Sets.Count > 0) new Attachment { Sets = t.Sets }.Into(c, task.Selection);
                    var added = AddCopy(tl, parent, task);
                    Summarise(tl, added);
                    made.Add(added);
                }
                var h = c.Keep(new Handle { Kind = Kind.Tasks, Rows = made, Label = "tl.import" });
                return $"imported {desc} -> {h.Name}";
            });
        }

        /// GetSimulationTime lags behind SetSimulationTime until the pane repaints, so the date set last is kept here.
        static DateTime? simulatedAt;
        static int simulatedGeneration;

        static Step Simulate(Ctx c, Args a)
        {
            var tl = Tl(c);
            var stop = a.Bool("stop");
            if (stop == a.Has("at")) throw new NwError("bad_args", "tl.simulate needs at or stop:true", "pass at:\"2026-03-05\" to show the schedule at that date, or stop:true");
            if (stop) return new Step("leave the TimeLiner simulation", () =>
            {
                LcTlSimulationHelper.Instance.SetGUIActive(false);
                simulatedAt = null;
                return "left the TimeLiner simulation; the model shows its normal appearance";
            });
            var at = Day(a, "at");
            var dated = Walk(tl.TasksRoot).Where(t => t.PlannedStartDate != null && t.PlannedEndDate != null && !t.Selection.IsClear).ToList();
            if (dated.Count == 0) throw new NwError("not_found", "no task has planned dates and attached items, so there is nothing to simulate", "add tasks with tl.add or tl.import, and attach items with tl.attach");
            return new Step("simulate the schedule at " + Date(at), () =>
            {
                var sim = LcTlSimulationHelper.Instance;
                // The pane initializes the helper; legacy NWDs can retain Actual mode with no actual dates.
                var pane = Application.Plugins.FindPlugin("TimelinerDockPane.Navisworks");
                if (pane != null && !pane.IsLoaded) pane.LoadPlugin();
                sim.SetGUIActive(false);
                PlannedSimulation.Configure(sim);
                sim.SetGUIActive(true);
                sim.SetSimulationTime(at);
                if (!sim.GetTimeControlsValid() || sim.GetViewType() != LcTlSimulationHelperViewType.ePLANNED)
                {
                    sim.SetGUIActive(false);
                    simulatedAt = null;
                    throw new NwError("unsupported_state", "the model did not initialize a planned TimeLiner simulation", "open TimeLiner > Simulate > Settings and check the planned date range and task attachments");
                }
                var first = sim.GetStartTime();
                var last = sim.GetEndTime();
                var now = at < first ? first : at > last ? last : at;
                simulatedAt = now;
                simulatedGeneration = c.Host.Handles.Generation;
                var clamp = now == at ? "" : $" (clamped to the schedule {Date(first)}..{Date(last)})";
                var active = Walk(tl.TasksRoot).Where(sim.IsActiveTask).Select(t => t.DisplayName).ToList();
                return $"simulation at {Date(now)}{clamp}; active: " + (active.Count == 0 ? "none" : string.Join(", ", active.Take(10)) + (active.Count > 10 ? $" and {active.Count - 10} more" : "")) + ". next: export {out:[{what:\"image\"}]}";
            });
        }

        static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r', ';' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        static void ExportCsv(Ctx c, Args a, Out o)
        {
            var tl = Tl(c);
            var path = ExportOps.OutPath(a, "tasks", "csv");
            var tasks = Walk(tl.TasksRoot).ToList();
            var sb = new StringBuilder("Id,Name,Parent,Type,Start,End,ActualStart,ActualEnd,Items,Set\r\n");
            foreach (var t in tasks)
            {
                var parent = t.Parent is TimelinerTask p ? PathOf(p) : "";
                var cells = new[]
                {
                    Id(tl, t), t.DisplayName, parent, t.SimulationTaskTypeName ?? "", Date(t.PlannedStartDate), Date(t.PlannedEndDate),
                    Date(t.ActualStartDate), Date(t.ActualEndDate), ItemCount(c, t).ToString(CultureInfo.InvariantCulture), string.Join(";", SetsOf(c, t)),
                };
                sb.Append(string.Join(",", cells.Select(x => Csv(x == "-" ? "" : x)))).Append("\r\n");
            }
            ExportOps.Write(path, temp => File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(true)));
            o.Line($"tasks_csv {path} {Count(tasks.Count)}");
        }
    }
}
