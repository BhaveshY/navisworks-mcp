using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Newtonsoft.Json.Linq;

namespace NavisworksMcp
{
    static class ClashOps
    {
        const string Types = "type:enum(hard|hard_conservative|clearance|duplicate)";
        const string Keys = "status, level, grid, assigned, group (native group), item1.Tab.Prop or item2.Tab.Prop";

        public static readonly Op[] All =
        {
            Catalog.Read("read", "tests", "", ListTests, "clash tests: type, tolerance, status, last run, result counts by status, native groups, selections", "{}"),
            Catalog.Read("read", "clashes", "; test:str in:target where:where status:strs group:str limit:int=50 page:int=1", Clashes,
                "results of a clash test -> r# (as an items target it means both items of each result). where keeps results where either item matches; group counts by " + Keys,
                "{test:\"Ducts vs Walls\", status:\"new,active\", group:\"level\"}"),
            Catalog.Apply("clash.add", "name:str a:target b:target; " + Types + "=hard tol:len=0 self:bool=false rules:strs", Add,
                "add a clash test. A set stays live as a set reference; a where (?.. or a where-handle) is saved as search set \"Clash/<name> A|B\" and referenced; other targets are fixed items. rules: built-in display names, e.g. Items in same file or Items in same composite object; omitted means none",
                "{name:\"Ducts vs Walls\", a:\"set:MEP/Ducts\", b:\"?Item.Type = IfcWall\", type:\"clearance\", tol:25}"),
            Catalog.Apply("clash.update", "test:str; a:target b:target " + Types + " tol:len self:bool to:str rules:strs", Update,
                "change a test's selections, type, tolerance, self-intersect, built-in rules or name (to); rules replaces the rule list, [] clears it, omission preserves it. Results stay until the next run",
                "{test:\"Ducts vs Walls\", tol:50, to:\"Ducts vs Walls 50\"}"),
            Catalog.Apply("clash.delete", "test:str", Delete, "delete a clash test and its results", "{test:\"Ducts vs Walls\"}"),
            Catalog.Apply("clash.run", "test:str", Run, "run one test, or every test with test:\"all\"; a long run returns a job j#", "{test:\"Ducts vs Walls\"}"),
            Catalog.Apply("clash.edit", "items:target; status:enum(new|active|reviewed|approved|resolved) assign:str comment:str approve:bool", Edit,
                "edit results from a clash handle (r3, r3#0-9): status, assigned to, add a comment, approve (status approved, approved by the Windows user)",
                "{items:\"r3\", status:\"active\", assign:\"MEP Lead\", comment:\"reroute below the beam\"}"),
            Catalog.Apply("clash.group", "test:str by:str", Group,
                "group a test's results as native Clash Detective groups (saved with the NWF/NWD) by " + Keys + "; replaces existing groups",
                "{test:\"Ducts vs Walls\", by:\"level\"}"),
            Catalog.Apply("clash.ungroup", "test:str", Ungroup, "move every result out of its group and delete the groups", "{test:\"Ducts vs Walls\"}"),
            Catalog.Read("export", "clash_report", "test:str; format:enum(html|csv)=html images:bool=true path:path", Report,
                "clash report written by the plugin: html with one PNG per result, or csv with one row per result (images adds PNGs and an image column)",
                "{test:\"Ducts vs Walls\", format:\"html\", path:\"C:/Coord/ducts-walls.html\"}"),
        };

        static readonly Dictionary<string, ClashTestType> TestTypes = new Dictionary<string, ClashTestType>
        {
            ["hard"] = ClashTestType.Hard, ["hard_conservative"] = ClashTestType.HardConservative,
            ["clearance"] = ClashTestType.Clearance, ["duplicate"] = ClashTestType.Duplicate,
        };

        static readonly ClashResultStatus[] Statuses = { ClashResultStatus.New, ClashResultStatus.Active, ClashResultStatus.Reviewed, ClashResultStatus.Approved, ClashResultStatus.Resolved };

        static string Name(ClashResultStatus s) => s.ToString().ToLowerInvariant();
        static string Name(ClashTestType t) => TestTypes.FirstOrDefault(kv => kv.Value == t).Key ?? t.ToString().ToLowerInvariant();

        static int dataGeneration = -1;
        static DocumentClashTests data;

        /// Saved items read from TestsData are disposed when the TestsData wrapper is collected, so one wrapper per document generation is kept alive.
        static DocumentClashTests Data(Ctx c)
        {
            var generation = c.Host.Handles.Generation;
            if (generation != dataGeneration || data == null) { dataGeneration = generation; data = c.Doc.GetClash().TestsData; }
            return data;
        }

        static GroupItem Root(Ctx c) => Data(c).Value.TestsRoot;

        static IEnumerable<(string path, ClashTest test)> Tests(IEnumerable<SavedItem> items, string prefix = "")
        {
            foreach (var item in items)
            {
                if (item is ClashTest t) yield return (prefix + t.DisplayName, t);
                else if (item is GroupItem g) foreach (var x in Tests(g.Children, prefix + g.DisplayName + "/")) yield return x;
            }
        }

        static ClashTest FindTest(Ctx c, string name) =>
            Tests(Root(c).Children).Where(t => t.path == name || t.test.DisplayName == name).Select(t => t.test).FirstOrDefault()
            ?? throw new NwError("not_found", $"no clash test \"{name}\"", "list tests with read {what:\"tests\"} and use the exact name");

        /// A clash edit or run disposes the wrappers read before it, so ops keep guids and handles keep owned copies,
        /// and every use goes through the live item with that guid.
        static T Live<T>(Ctx c, Guid guid) where T : SavedItem => Data(c).ResolveGuid(guid) as T;
        static ClashResult Live(Ctx c, ClashResult copy) => Live<ClashResult>(c, copy.Guid) ?? copy;

        static IEnumerable<ClashResult> Results(GroupItem g) =>
            g.Children.SelectMany(x => x is ClashResult r ? new[] { r } : x is GroupItem sub ? Results(sub) : Enumerable.Empty<ClashResult>());

        static ModelItem Item(ClashResult r, int side) => side == 1 ? r.CompositeItem1 ?? r.Item1 : r.CompositeItem2 ?? r.Item2;

        static IEnumerable<ModelItem> AllItems(ClashResult r) =>
            new[] { r.Selection1, r.Selection2, r.CompositeItemSelection1, r.CompositeItemSelection2 }.Where(x => x != null).SelectMany(x => x);

        /// Grid intersection and level of a result centre from the active grid system. Without grid lines the level is the highest one at or below the centre.
        static (string grid, string level) Location(Ctx c, ClashResult r)
        {
            var sys = c.Doc.Grids.ActiveSystem;
            if (sys == null || r.Center == null) return (null, null);
            var at = sys.ClosestIntersection(r.Center);
            if (at != null) return (at.DisplayName, at.Level?.DisplayName);
            var level = sys.Levels.Where(l => l.Elevation <= r.Center.Z + Units.FromMm(c.Doc, 1)).OrderByDescending(l => l.Elevation).FirstOrDefault();
            return (null, level?.DisplayName);
        }

        static readonly Regex ItemKey = new Regex(@"^item([12])\.(.+\..+)$");

        static void CheckKey(string by)
        {
            if (new[] { "status", "level", "grid", "assigned", "group" }.Contains(by) || ItemKey.IsMatch(by)) return;
            throw new NwError("bad_args", $"cannot group by \"{by}\"", "use " + Keys + ", e.g. item1.Element.Category");
        }

        static string Key(Ctx c, ClashResult r, string by)
        {
            switch (by)
            {
                case "status": return Name(r.Status);
                case "level": return Location(c, r).level;
                case "grid": return Location(c, r).grid;
                case "assigned": return Compat.AssignedTo(r);
                case "group": return (r.Parent as ClashResultGroup)?.DisplayName;
            }
            var m = ItemKey.Match(by);
            var item = Item(r, m.Groups[1].Value == "1" ? 1 : 2);
            var value = item == null ? null : ReadOps.Column(c.Doc, item, m.Groups[2].Value);
            return value == "-" ? null : value;
        }

        static readonly string[] Header = { "clash", "status", "dist", "item1", "id1", "item2", "id2", "level", "grid", "assigned", "centre", "group" };

        static object[] Cells(Ctx c, ClashResult r)
        {
            var (grid, level) = Location(c, r);
            var a = Item(r, 1);
            var b = Item(r, 2);
            return new object[]
            {
                r.DisplayName, Name(r.Status), Units.ToMm(c.Doc, r.Distance), a?.DisplayName, a == null ? null : ReadOps.Id(c.Doc, a),
                b?.DisplayName, b == null ? null : ReadOps.Id(c.Doc, b), level, grid, Compat.AssignedTo(r), r.Center == null ? null : Units.Point(c.Doc, r.Center),
                (r.Parent as ClashResultGroup)?.DisplayName,
            };
        }

        static string Counts(IEnumerable<ClashResult> results)
        {
            var list = results.ToList();
            return $"{list.Count:#,0} results" + string.Concat(Statuses.Select(s => (s, n: list.Count(r => r.Status == s))).Where(x => x.n > 0).Select(x => $" · {Name(x.s)} {x.n:#,0}"));
        }

        static void ListTests(Ctx c, Args a, Out o)
        {
            var tests = Tests(Root(c).Children).ToList();
            o.Line($"tests {tests.Count:#,0} (exact)");
            if (tests.Count == 0) return;
            o.Table(new[] { "test", "type", "tol", "status", "last run", "results" }.Concat(Statuses.Select(Name)).Concat(new[] { "groups", "a", "b", "rules" }), tests.Select(x =>
            {
                var t = x.test;
                var results = Results(t).ToList();
                return new object[]
                {
                    x.path, Name(t.TestType), Units.ToMm(c.Doc, t.Tolerance), t.Status.ToString().ToLowerInvariant(),
                    t.LastRun?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), results.Count,
                }.Concat(Statuses.Select(s => (object)results.Count(r => r.Status == s)))
                 .Concat(new object[] { t.Children.OfType<ClashResultGroup>().Count(), Describe(c, t.SelectionA), Describe(c, t.SelectionB), string.Join(", ", t.IgnoreRules.Select(r => r.DisplayName)) });
            }));
        }

        static string Describe(Ctx c, ClashSelection s)
        {
            var sel = s.Selection;
            var text = sel.HasSelectionSources
                ? string.Join(" + ", sel.SelectionSources.Select(src => c.Doc.SelectionSets.ResolveSelectionSource(src) is SavedItem set ? "set:" + SetPath(set) : src.ToString()))
                : $"{sel.GetSelectedItems(c.Doc).Count:#,0} items";
            return s.SelfIntersect ? text + " (self)" : text;
        }

        static string SetPath(SavedItem item)
        {
            var names = new List<string>();
            for (var x = item; x != null && x.Parent != null; x = x.Parent) names.Insert(0, x.DisplayName);
            return string.Join("/", names);
        }

        static void Clashes(Ctx c, Args a, Out o)
        {
            var target = a.Target("in");
            if (target == null && !a.Has("test")) throw new NwError("bad_args", "clashes needs test or in", "e.g. read {what:\"clashes\", test:\"Ducts vs Walls\"}");
            if (target != null && target.HandleKind != Kind.Clashes) throw new NwError("bad_args", $"{target.Text} is not a clash handle", "pass in:\"r#\" from read {what:\"clashes\"}, or test:\"name\"");
            var statuses = a.List("status").Select(ParseStatus).ToList();
            Handle h;
            var whole = target?.Whole;
            if (whole != null && statuses.Count == 0 && !a.Has("where"))
                h = whole;
            else
            {
                var test = target == null ? FindTest(c, a.Str("test")) : null;
                var rows = (test != null ? Results(test) : target.Rows(c).Cast<ClashResult>().Select(r => Live(c, r))).ToList();
                if (statuses.Count > 0) rows = rows.Where(r => statuses.Contains(r.Status)).ToList();
                if (a.Has("where"))
                {
                    var scope = new ModelItemCollection();
                    scope.AddRange(rows.SelectMany(AllItems));
                    var found = new HashSet<ModelItem>(scope.Count == 0 ? Enumerable.Empty<ModelItem>() : Where.Parse(a.Str("where")).Find(c, scope));
                    rows = rows.Where(r => AllItems(r).Any(found.Contains)).ToList();
                }
                h = c.Keep(new Handle { Kind = Kind.Clashes, Rows = rows.Select(r => (object)r.CreateCopy()).ToList(), Label = test?.DisplayName ?? target.Text });
            }
            Page(c, o, h, a.Str("group"), a.Int("limit"), a.Int("page"));
        }

        static ClashResultStatus ParseStatus(string s)
        {
            foreach (var x in Statuses) if (Name(x) == s.Trim().ToLowerInvariant()) return x;
            throw new NwError("bad_args", $"\"{s}\" is not a clash status", "use " + string.Join(", ", Statuses.Select(Name)));
        }

        static void Page(Ctx c, Out o, Handle h, string group, int limit, int page)
        {
            var rows = h.Rows.Cast<ClashResult>().Select(r => Live(c, r)).ToList();
            var head = $"{h.Name} clashes {rows.Count:#,0} (exact) · {h.Label}";
            if (group != null)
            {
                CheckKey(group);
                head += $" · by {group}: " + string.Join(" · ", rows.GroupBy(r => Key(c, r, group) ?? "-").OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => $"{g.Key} {g.Count():#,0}"));
            }
            o.Line(head);
            if (rows.Count == 0 || limit <= 0) return;
            var start = Math.Max(0, (page - 1) * limit);
            o.Table(new[] { "#" }.Concat(Header), rows.Skip(start).Take(limit).Select((r, i) => new object[] { start + i }.Concat(Cells(c, r))));
            if (start + limit < rows.Count) o.More("clashes", h, page, limit);
        }

        /// One side of a test: an existing set, a where to save as a search set, or fixed items.
        sealed class Side
        {
            public SelectionSet Set;
            public Where Search;
            public string SetPath;
            public ModelItemCollection Items;
            public string Plan => Set != null ? "set:" + SetPath : Search != null ? $"search set \"{SetPath}\" ({Search.Text})" : $"{Items.Count:#,0} fixed items";
        }

        static Side ReadSide(Ctx c, Args a, string name, string test)
        {
            var target = a.Target(name);
            if (target == null) return null;
            var single = a.Json(name).Type == JTokenType.String;
            var text = target.Text.Trim();
            if (single && text.StartsWith("set:")) return new Side { Set = Sets.Find(c.Doc, text.Substring(4)), SetPath = text.Substring(4) };
            string where = null;
            if (single && text.StartsWith("?")) where = text.Substring(1);
            var whole = target.Whole;
            if (whole?.Kind == Kind.Items) where = whole.Where;
            if (where != null) return new Side { Search = Where.Parse(where), SetPath = $"Clash/{test.Replace('/', '-')} {name.ToUpperInvariant()}" };
            var items = target.Items(c);
            if (items.Count == 0) throw new NwError("bad_args", $"{name} ({target.Text}) has no items", "pass a set, a where or a non-empty handle");
            return new Side { Items = items };
        }

        static void Fill(Ctx c, ClashSelection sel, Side side)
        {
            if (side.Items != null) { sel.Selection.CopyFrom(side.Items); return; }
            var set = side.Set ?? SaveSearchSet(c, side.SetPath, side.Search);
            sel.Selection.CopyFrom(new SelectionSourceCollection { c.Doc.SelectionSets.CreateSelectionSource(set) });
        }

        static SavedItem SaveSearchSet(Ctx c, string path, Where search)
        {
            var sets = c.Doc.SelectionSets;
            var set = new SelectionSet(search.ToSearch(c)) { DisplayName = Sets.Leaf(path) };
            var folder = Sets.Folder(() => sets.RootItem, path, sets.AddCopy);
            var at = folder.Children.IndexOfDisplayName(set.DisplayName);
            if (at >= 0) sets.ReplaceWithCopy(folder, at, set);
            else sets.AddCopy(folder, set);
            return Sets.Lookup(sets.RootItem, path);
        }

        static string Settings(Ctx c, ClashTest t) => $"{Name(t.TestType)}, tol {Units.Mm(c.Doc, t.Tolerance)} mm";

        static void SetRules(Ctx c, ClashTest test, Args a)
        {
            if (!a.Has("rules")) return;
            using (var builtins = new RuleCollection())
            {
                Autodesk.Navisworks.Api.Interop.LcClClashRules.LoadBaseRuleSet(builtins, c.Doc.State);
                test.IgnoreRules.Clear();
                foreach (var name in a.List("rules").Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var rule = builtins.FirstOrDefault(r => string.Equals(r.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                    if (rule == null || !rule.IsImplemented)
                        throw new NwError("bad_args", $"unknown or unavailable clash rule \"{name}\"",
                            "use built-in rule names: " + string.Join(", ", builtins.Where(r => r.IsImplemented).Select(r => r.DisplayName)) + "; use [] for none");
                    // The native runner applies every member, even IsEnabled=false. The UI also copies only selected rules.
                    var copy = rule.CreateCopy();
                    copy.IsEnabled = true;
                    test.IgnoreRules.Add(copy); // The collection takes ownership.
                }
            }
        }

        static void CheckTolerance(ClashTest t)
        {
            if (t.TestType == ClashTestType.Clearance && t.Tolerance <= 0)
                throw new NwError("bad_args", "a clearance test needs tol > 0", "pass tol in mm, e.g. tol:25");
        }

        static Step Add(Ctx c, Args a)
        {
            var name = a.Str("name");
            if (Tests(Root(c).Children).Any(t => t.path == name || t.test.DisplayName == name))
                throw new NwError("bad_args", $"clash test \"{name}\" already exists", $"change it with clash.update {{test:\"{name}\"}} or delete it with clash.delete first");
            var sideA = ReadSide(c, a, "a", name);
            var sideB = ReadSide(c, a, "b", name);
            var test = new ClashTest { DisplayName = name, TestType = TestTypes[a.Str("type")], Tolerance = a.Len("tol") };
            CheckTolerance(test);
            SetRules(c, test, a);
            test.SelectionA.SelfIntersect = test.SelectionB.SelfIntersect = a.Bool("self");
            return new Step($"add clash test \"{name}\" ({Settings(c, test)}): a {sideA.Plan} · b {sideB.Plan}", () =>
            {
                Fill(c, test.SelectionA, sideA);
                Fill(c, test.SelectionB, sideB);
                Data(c).TestsAddCopy(Root(c), test);
                return $"added clash test \"{name}\" ({Settings(c, test)}): a {sideA.Plan} · b {sideB.Plan}. next: clash.run {{test:\"{name}\"}}";
            });
        }

        static Step Update(Ctx c, Args a)
        {
            var test = FindTest(c, a.Str("test"));
            var name = a.Str("to") ?? test.DisplayName;
            var sideA = ReadSide(c, a, "a", name);
            var sideB = ReadSide(c, a, "b", name);
            var copy = (ClashTest)test.CreateCopyWithoutChildren();
            if (a.Has("type")) copy.TestType = TestTypes[a.Str("type")];
            if (a.Has("tol")) copy.Tolerance = a.Len("tol");
            CheckTolerance(copy);
            SetRules(c, copy, a);
            if (a.Has("rules")) copy.Status = ClashTestStatus.Old;
            if (a.Has("self")) copy.SelectionA.SelfIntersect = copy.SelectionB.SelfIntersect = a.Bool("self");
            var changes = new List<string>();
            if (a.Has("type") || a.Has("tol")) changes.Add(Settings(c, copy));
            if (sideA != null) changes.Add("a " + sideA.Plan);
            if (sideB != null) changes.Add("b " + sideB.Plan);
            if (a.Has("self")) changes.Add("self-intersect " + (a.Bool("self") ? "on" : "off"));
            if (a.Has("rules")) changes.Add("rules " + (copy.IgnoreRules.Count == 0 ? "none" : string.Join(", ", copy.IgnoreRules.Select(r => r.DisplayName))));
            if (name != test.DisplayName) changes.Add($"rename to \"{name}\"");
            if (changes.Count == 0) throw new NwError("bad_args", "clash.update changes nothing", "pass at least one of a, b, type, tol, self, to, rules");
            var summary = string.Join(" · ", changes);
            var guid = test.Guid;
            var old = test.DisplayName;
            return new Step($"update clash test \"{old}\": {summary}", () =>
            {
                if (sideA != null) Fill(c, copy.SelectionA, sideA);
                if (sideB != null) Fill(c, copy.SelectionB, sideB);
                var data = Data(c);
                data.TestsEditTestFromCopy(Live<ClashTest>(c, guid), copy);
                if (name != old) data.TestsEditDisplayName(Live<ClashTest>(c, guid), name);
                return $"updated clash test \"{name}\": {summary}";
            });
        }

        static Step Delete(Ctx c, Args a)
        {
            var test = FindTest(c, a.Str("test"));
            var name = test.DisplayName;
            var guid = test.Guid;
            return new Step($"delete clash test \"{name}\" ({Counts(Results(test))})", () =>
            {
                var data = Data(c);
                var live = Live<ClashTest>(c, guid);
                var root = Root(c);
                data.TestsRemove(root.Children.IndexOfGuid(guid) >= 0 ? root : live.Parent, live);
                return $"deleted clash test \"{name}\"";
            });
        }

        static Step Run(Ctx c, Args a)
        {
            var all = a.Str("test") == "all";
            var tests = (all ? Tests(Root(c).Children).Select(t => t.test) : new[] { FindTest(c, a.Str("test")) }).Select(t => (guid: t.Guid, name: t.DisplayName)).ToList();
            if (tests.Count == 0) throw new NwError("not_found", "the document has no clash tests", "add one with clash.add");
            return new Step("run " + string.Join(", ", tests.Select(t => $"\"{t.name}\"")), () =>
            {
                var lines = new List<string>();
                foreach (var t in tests)
                {
                    c.CheckCancelled();
                    Data(c).TestsRunTest(Live<ClashTest>(c, t.guid));
                    lines.Add($"ran \"{t.name}\": {Counts(Results(Live<ClashTest>(c, t.guid)))}");
                }
                return string.Join("\n", lines);
            });
        }

        static Step Edit(Ctx c, Args a)
        {
            var target = a.Target("items");
            if (target.HandleKind != Kind.Clashes) throw new NwError("bad_args", $"{target.Text} is not a clash handle", "pass items:\"r#\" from read {what:\"clashes\"}");
            var rows = target.Rows(c).Cast<ClashResult>().Select(r => r.Guid).ToList();
            if (rows.Count == 0) throw new NwError("bad_args", $"{target.Text} has no results", "read the clashes again; the filter matched nothing");
            var approve = a.Bool("approve");
            if (approve && a.Has("status")) throw new NwError("bad_args", "approve sets the status to approved; pass status or approve, not both", "drop status, or drop approve");
            var status = a.Has("status") ? ParseStatus(a.Str("status")) : (ClashResultStatus?)null;
            var assign = a.Str("assign");
            var comment = a.Str("comment");
            var user = Environment.UserName;
            var changes = new List<string>();
            if (status != null) changes.Add("status " + Name(status.Value));
            if (approve) changes.Add("approved by " + user);
            if (assign != null) changes.Add("assigned " + (assign.Length == 0 ? "to nobody" : assign));
            if (comment != null) changes.Add("comment added");
            if (changes.Count == 0) throw new NwError("bad_args", "clash.edit changes nothing", "pass status, assign, comment or approve:true");
            var summary = string.Join(" · ", changes);
            return new Step($"edit {rows.Count:#,0} results: {summary}", () =>
            {
                var data = Data(c);
                foreach (var guid in rows)
                {
                    c.CheckCancelled();
                    ClashResult R() => Live<ClashResult>(c, guid) ?? throw new NwError("stale_handle", "a result in " + target.Text + " no longer exists (the test was rerun or deleted)", "read the clashes again and edit the new handle");
                    if (status != null) Compat.SetStatus(data, R(), status.Value, user);
                    if (approve)
                    {
                        Compat.SetStatus(data, R(), ClashResultStatus.Approved, user);
                        Compat.SetApprovedBy(data, R(), user);
                        data.TestsEditResultApprovedTime(R(), DateTime.UtcNow);
                    }
                    if (assign != null) Compat.SetAssignedTo(data, R(), assign);
                    if (comment != null)
                    {
                        var comments = new CommentCollection(R().Comments);
                        comments.Add(Compat.Comment(c.Doc, comment, CommentStatus.New, user));
                        data.TestsEditResultComments(R(), comments);
                    }
                }
                return $"edited {rows.Count:#,0} results: {summary}";
            });
        }

        static Step Group(Ctx c, Args a)
        {
            var test = FindTest(c, a.Str("test"));
            var by = a.Str("by");
            CheckKey(by);
            if (by == "group") throw new NwError("bad_args", "cannot group by the existing groups", "group by " + Keys.Replace(", group (native group)", ""));
            var keys = Results(test).Select(r => (guid: r.Guid, key: Key(c, r, by) ?? "(none)")).ToList();
            if (keys.Count == 0) throw new NwError("not_found", $"\"{test.DisplayName}\" has no results", $"run it first with clash.run {{test:\"{test.DisplayName}\"}}");
            var groups = keys.GroupBy(k => k.key).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var summary = $"{keys.Count:#,0} results of \"{test.DisplayName}\" into {groups.Count} groups by {by}: " + string.Join(" · ", groups.Select(g => $"{g.Key} {g.Count():#,0}"));
            var guid = test.Guid;
            return new Step("group " + summary, () =>
            {
                var data = Data(c);
                Flatten(c, guid);
                foreach (var g in groups)
                {
                    c.CheckCancelled();
                    var group = new ClashResultGroup { DisplayName = g.Key, Guid = Guid.NewGuid() };
                    data.TestsAddCopy(Live<ClashTest>(c, guid), group);
                    var n = 0;
                    foreach (var (result, _) in g)
                    {
                        var r = data.ResolveGuid(result);
                        var from = r.Parent;
                        data.TestsMove(from, from.Children.IndexOfGuid(result), (GroupItem)data.ResolveGuid(group.Guid), n++);
                    }
                }
                return "grouped " + summary;
            });
        }

        static Step Ungroup(Ctx c, Args a)
        {
            var test = FindTest(c, a.Str("test"));
            var groups = test.Children.OfType<ClashResultGroup>().Count();
            var guid = test.Guid;
            var name = test.DisplayName;
            return new Step($"ungroup {groups} groups of \"{name}\"", () => $"ungrouped {Flatten(c, guid)} results from {groups} groups of \"{name}\"");
        }

        /// Moves every grouped result to the end of the test and deletes the emptied groups. Returns the number of results moved.
        static int Flatten(Ctx c, Guid test)
        {
            var data = Data(c);
            var moved = 0;
            foreach (var guid in Live<ClashTest>(c, test).Children.OfType<ClashResultGroup>().Select(g => g.Guid).ToList())
            {
                foreach (var r in Results((GroupItem)data.ResolveGuid(guid)).Select(r => r.Guid).ToList())
                {
                    var item = data.ResolveGuid(r);
                    var t = Live<ClashTest>(c, test);
                    data.TestsMove(item.Parent, item.Parent.Children.IndexOfGuid(r), t, t.Children.Count);
                    moved++;
                }
                var parent = Live<ClashTest>(c, test);
                data.TestsRemove(parent, parent.Children[parent.Children.IndexOfGuid(guid)]);
            }
            return moved;
        }

        static void Report(Ctx c, Args a, Out o)
        {
            var test = FindTest(c, a.Str("test"));
            var html = a.Str("format") == "html";
            var path = ExportOps.OutPath(a, "clash-report", html ? "html" : "csv");
            var results = Results(test).ToList();
            var images = new string[results.Count];
            if (a.Bool("images") && results.Count > 0)
            {
                var dir = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_images");
                Directory.CreateDirectory(dir);
                for (var i = 0; i < results.Count; i++)
                {
                    c.CheckCancelled();
                    using (var bmp = Data(c).TestsImageForResult(results[i], ImageGenerationStyle.Scene, 480, 360))
                    {
                        if (bmp == null) continue;
                        images[i] = Path.Combine(dir, $"{i + 1:000}-{Safe(results[i].DisplayName)}.png");
                        bmp.Save(images[i], System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }
            var rows = results.Select(r => Cells(c, r).Select(Out.Cell).ToArray()).ToList();
            var text = html ? Html(c, test, results, rows, images, path) : Csv(rows, images, a.Bool("images"));
            File.WriteAllText(path, text, new UTF8Encoding(true));
            o.Line($"clash_report {path} · {Counts(results)} · {images.Count(x => x != null)} images");
        }

        static string Safe(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));

        static string Csv(List<string[]> rows, string[] images, bool withImages)
        {
            string Field(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            var sb = new StringBuilder();
            sb.Append(string.Join(",", Header.Concat(withImages ? new[] { "image" } : new string[0]))).Append("\r\n");
            for (var i = 0; i < rows.Count; i++)
                sb.Append(string.Join(",", rows[i].Concat(withImages ? new[] { images[i] ?? "-" } : new string[0]).Select(Field))).Append("\r\n");
            return sb.ToString();
        }

        static string Html(Ctx c, ClashTest test, List<ClashResult> results, List<string[]> rows, string[] images, string path)
        {
            string E(string s) => WebUtility.HtmlEncode(s);
            var dir = Path.GetDirectoryName(path);
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(E(test.DisplayName)).Append("</title><style>")
              .Append("body{font:13px Segoe UI,Arial,sans-serif;margin:24px}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:4px 6px;text-align:left;vertical-align:top}th{background:#eee}img{width:240px}")
              .Append("</style></head><body>");
            sb.Append("<h1>").Append(E(test.DisplayName)).Append("</h1><p>")
              .Append(E($"{Name(test.TestType)} · tolerance {Out.Num(Units.ToMm(c.Doc, test.Tolerance))} mm · last run {test.LastRun?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never"} · {Counts(results)}"))
              .Append("</p><table><tr><th>image</th>");
            foreach (var h in Header) sb.Append("<th>").Append(E(h)).Append("</th>");
            sb.Append("</tr>");
            for (var i = 0; i < rows.Count; i++)
            {
                sb.Append("<tr><td>");
                if (images[i] != null) sb.Append("<img src=\"").Append(E(Uri.EscapeUriString(MakeRelative(dir, images[i])))).Append("\">");
                sb.Append("</td>");
                foreach (var cell in rows[i]) sb.Append("<td>").Append(E(cell)).Append("</td>");
                sb.Append("</tr>");
            }
            return sb.Append("</table></body></html>").ToString();
        }

        static string MakeRelative(string dir, string file) => file.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
    }
}
