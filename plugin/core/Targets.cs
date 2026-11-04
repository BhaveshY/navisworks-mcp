using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Newtonsoft.Json.Linq;

namespace NavisworksMcp
{
    enum Kind { Items, Clashes, Tasks, Takeoff, Text }

    /// A stored result list. Rows are ModelItems, IClashResults, TimeLiner tasks or takeoff rows, by Kind.
    sealed class Handle
    {
        public int N;
        public Kind Kind;
        public List<object> Rows;
        public string Where;
        public string Label;
        public int Generation;
        public string Session;
        public bool Descendants;
        public bool Searched;
        public List<int> Depths;
        public string Name => "r" + Session + "-" + N;
    }

    sealed class Handles
    {
        const int Capacity = 200;
        readonly Dictionary<int, Handle> table = new Dictionary<int, Handle>();
        int seq;
        // Five random [a-z0-9] characters: 60 million stamps keep a restarted process from reusing a handle name, in few tokens.
        readonly string session = new string(Guid.NewGuid().ToByteArray().Take(5).Select(b => "abcdefghijklmnopqrstuvwxyz0123456789"[b % 36]).ToArray());
        public int Last { get; private set; }

        /// Bumped whenever the set of models changes (open, append, merge, close), because rows and takeoff ids
        /// belong to the models they were read from.
        public int Generation;

        public Handle Add(Handle h)
        {
            lock (table)
            {
                h.Generation = Generation;
                h.Session = session;
                h.N = ++seq;
                table[h.N] = h;
                Last = h.N;
                table.Remove(h.N - Capacity);
                var retained = table.Values.Sum(x => (long)x.Rows.Count);
                foreach (var old in table.Values.Where(x => x != h).OrderBy(x => x.N).ToArray())
                {
                    if (retained <= 250000) break;
                    retained -= old.Rows.Count;
                    table.Remove(old.N);
                }
                return h;
            }
        }

        public string LastName => "r" + session + "-" + Last;
        public void Invalidate()
        {
            lock (table)
            {
                Generation++;
                foreach (var h in table.Values) { h.Rows.Clear(); h.Depths?.Clear(); }
            }
        }

        public Handle Get(string sessionId, string number)
        {
            if (sessionId != session || !int.TryParse(number, out var n))
                throw new NwError("stale_handle", "the handle belongs to another Navisworks session or is invalid", "read {what:\"items\", in:\"all\"} and use its new handle");
            return Get(n);
        }

        public Handle Get(int n)
        {
            lock (table)
            {
                if (table.TryGetValue(n, out var h))
                {
                    if (h.Generation == Generation) return h;
                    throw new NwError("stale_handle", $"r{n} was read before the models changed (a file was opened, appended, merged or removed)", "repeat the read that made it and use the new handle");
                }
                var why = n > seq ? "was never made in this Navisworks session (it restarted, or the number is wrong)" : $"was dropped; only the last {Capacity} handles are kept";
                throw new NwError("stale_handle", $"r{n} {why}", "read {what:\"items\", in:\"all\"} and use its new handle");
            }
        }
    }

    /// One parsed target expression: r3, r3#5, r3#0-49, $0, selection, last, set:Folder/Name, id:0:0/2/1, ?where, or a list of these.
    sealed class Target
    {
        readonly List<Func<Ctx, IEnumerable<object>>> parts = new List<Func<Ctx, IEnumerable<object>>>();
        public Kind? HandleKind;
        /// The handle itself when the target is exactly one whole handle (r3 or last), for ops that page or reuse it.
        public Handle Whole;
        public string Text;

        public static Target Parse(Ctx c, JToken token)
        {
            var t = new Target();
            var texts = token.Type == JTokenType.Array ? token.Select(x => (string)x).ToArray() : new[] { (string)token };
            t.Text = string.Join(", ", texts);
            foreach (var s in texts) t.parts.Add(Part(c, t, s.Trim()));
            if (texts.Length != 1) t.Whole = null;
            return t;
        }

        static readonly Regex HandleRx = new Regex(@"^r(?:(?<session>[A-Za-z0-9_-]+)-)?(?<number>\d+)(?:#(?<from>\d+)(?:-(?<to>\d+))?)?$");
        static readonly Regex BatchRx = new Regex(@"^\$(\d+)$");
        static readonly Regex IdRx = new Regex(@"^id:(\d+):(a|\d+(?:/\d+)*)$");
        public static bool IsReference(string text) => text == "last" || HandleRx.IsMatch(text) || BatchRx.IsMatch(text);

        static int Index(string text)
        {
            if (int.TryParse(text, out var value)) return value;
            throw new NwError("bad_args", "target index is outside 0..2147483647", "use an id or slice copied from a fresh read");
        }

        static Func<Ctx, IEnumerable<object>> Part(Ctx c, Target t, string s)
        {
            Match m;
            if ((m = HandleRx.Match(s)).Success)
            {
                var h = c.Host.Handles.Get(m.Groups["session"].Value, m.Groups["number"].Value);
                t.HandleKind = h.Kind;
                if (!m.Groups["from"].Success) { t.Whole = h; return _ => h.Rows; }
                var from = Index(m.Groups["from"].Value);
                var to = m.Groups["to"].Success ? Index(m.Groups["to"].Value) : from;
                if (from > to || to >= h.Rows.Count) throw new NwError("bad_args", $"{s} is outside {h.Name}, which has rows 0-{h.Rows.Count - 1}", $"use rows inside 0-{h.Rows.Count - 1}");
                return _ => h.Rows.Skip(from).Take(to - from + 1);
            }
            if ((m = BatchRx.Match(s)).Success)
            {
                var i = Index(m.Groups[1].Value);
                if (i >= c.BatchIndex && !c.Preview) throw new NwError("bad_args", $"{s} refers to op {i}, which has not run yet", "only refer to earlier ops in the same apply call");
                return ctx => ctx.Preview && !ctx.Made.ContainsKey(i) ? Enumerable.Empty<object>() : ctx.Made.TryGetValue(i, out var h) ? h.Rows : throw new NwError("bad_args", $"op {i} made no handle", "refer only to ops that return r#");
            }
            if (s == "selection") return ctx => ctx.Doc.CurrentSelection.SelectedItems.Cast<object>().ToList();
            if (s == "last") return Part(c, t, c.Host.Handles.LastName);
            if (s == "all") return ctx => ctx.Doc.Models.RootItems.Cast<object>().ToList();
            if (s.StartsWith("set:")) { var set = Sets.Find(c.Doc, s.Substring(4)); return ctx => set.GetSelectedItems(ctx.Doc).Cast<object>().ToList(); }
            if ((m = IdRx.Match(s)).Success)
            {
                var path = new Autodesk.Navisworks.Api.DocumentParts.ModelItemPathId { ModelIndex = Index(m.Groups[1].Value), PathId = m.Groups[2].Value };
                if (path.PathId != "a") foreach (var part in path.PathId.Split('/')) Index(part);
                var text = s;
                return ctx => new List<object> { (path.ModelIndex < ctx.Doc.Models.Count ? ctx.Doc.Models.ResolvePathId(path) : null) ?? throw new NwError("not_found", $"no item at {text}", "read {what:\"tree\"} to obtain current item ids") };
            }
            if (s.StartsWith("?")) { var w = Where.Parse(s.Substring(1)); return ctx => w.Find(ctx, null).Cast<object>().ToList(); }
            throw new NwError("bad_args", $"\"{s}\" is not a target", "read {what:\"items\", in:\"all\"} and use its handle, optionally followed by #0 or #0-9; selection and inline ?where searches are also targets");
        }

        public IEnumerable<object> Rows(Ctx c) => parts.SelectMany(p => p(c));

        /// Model items behind the target. A clash handle contributes both items of each result.
        public ModelItemCollection Items(Ctx c, int limit = int.MaxValue)
        {
            var items = new ModelItemCollection();
            foreach (var row in Rows(c))
            {
                if (items.Count >= limit) break;
                if (row is ModelItem mi) items.Add(mi);
                else if (row is IClashResult r)
                {
                    var live = c.Doc.GetClash().TestsData.ResolveGuid(((SavedItem)r).Guid) as ClashResult;
                    if (live == null) throw new NwError("stale_handle", "the clash result no longer exists", "read {what:\"tests\"} and read its clashes again");
                    // Selection collections are detached snapshots, even on a live result. The item
                    // properties are the document-bound pair used by the clash table's own ids.
                    var first = live.CompositeItem1 ?? live.Item1;
                    var second = live.CompositeItem2 ?? live.Item2;
                    if (first != null) items.Add(first);
                    if (second != null && items.Count < limit) items.Add(second);
                }
                else throw new NwError("bad_args", $"{Text} holds {row.GetType().Name} rows, not model items", "pass items, a set, or a clash handle");
            }
            return items;
        }
    }

    /// The where language: `Tab.Prop op value` joined by `and` / `or`, with `has Tab.Prop` and `!has Tab.Prop`.
    /// Ops: = != ~ (contains) ~= (wildcard * ?) > >= < <=. `or` starts a new group, so a where is an or of ands,
    /// which is exactly what Navisworks Search evaluates.
    sealed class Where
    {
        sealed class Cond { public string Tab, Prop, Op, Value; public bool Group; }
        readonly List<Cond> conds = new List<Cond>();
        public string Text;

        // Tokenize joins only outside JSON quoted strings, including escaped quotes/backslashes.
        public static string[] Split(string text)
        {
            var parts = new List<string>();
            var quoted = false;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (quoted && text[i] == '\\') { if (++i >= text.Length) throw Bad(text); continue; }
                if (text[i] == '"') { quoted = !quoted; continue; }
                if (quoted || !char.IsWhiteSpace(text[i])) continue;
                var m = Regex.Match(text.Substring(i), @"^\s+(and|or)\s+", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                parts.Add(text.Substring(start, i - start).Trim());
                parts.Add(m.Groups[1].Value.ToLowerInvariant());
                i += m.Length - 1;
                start = i + 1;
            }
            if (quoted) throw Bad(text);
            parts.Add(text.Substring(start).Trim());
            return parts.ToArray();
        }

        public static string[] AndParts(string text)
        {
            var parts = Split(text);
            if (parts.Where((_, i) => i % 2 == 1).Any(x => x != "and"))
                throw new NwError("bad_args", "this filter supports and only", "combine task conditions with and; quote literal values containing or");
            return parts.Where((_, i) => i % 2 == 0).ToArray();
        }
        static readonly Regex CondRx = new Regex(@"^(?<neg>!)?has\s+(?<name>.+)$|^(?<name>.+?)\s*(?<op>!=|>=|<=|~=|=|~|>|<)\s*(?<value>.*)$", RegexOptions.IgnoreCase);

        public static Where Parse(string text)
        {
            var w = new Where { Text = text.Trim() };
            var pieces = Split(w.Text);
            var group = false;
            for (var i = 0; i < pieces.Length; i += 2)
            {
                if (i > 0) group = pieces[i - 1].Equals("or", StringComparison.OrdinalIgnoreCase);
                var m = CondRx.Match(pieces[i].Trim());
                if (!m.Success) throw Bad(pieces[i]);
                var name = m.Groups["name"].Value.Trim();
                var dot = SplitName(name);
                if (dot < 0) throw Bad(pieces[i]);
                w.conds.Add(new Cond
                {
                    Tab = Unquote(name.Substring(0, dot)),
                    Prop = Unquote(name.Substring(dot + 1)),
                    Op = m.Groups["op"].Success ? m.Groups["op"].Value : m.Groups["neg"].Success ? "!has" : "has",
                    Value = m.Groups["value"].Success ? Unquote(m.Groups["value"].Value.Trim()) : null,
                    Group = group,
                });
            }
            return w;
        }

        static int SplitName(string name)
        {
            if (name.StartsWith("\"")) { var close = name.IndexOf('"', 1); return close > 0 && close + 1 < name.Length && name[close + 1] == '.' ? close + 1 : -1; }
            return name.IndexOf('.');
        }

        public static string Unquote(string s)
        {
            if (!s.StartsWith("\"")) return s;
            try { return Newtonsoft.Json.JsonConvert.DeserializeObject<string>(s); }
            catch (Newtonsoft.Json.JsonException) { throw Bad(s); }
        }

        static NwError Bad(string piece) => new NwError("bad_args", $"cannot read condition \"{piece.Trim()}\"",
            "write conditions as Tab.Prop op value, e.g. Item.Type = IfcWall and Pset_WallCommon.FireRating ~ 90 or has \"Element ID\".Value; ops = != ~ ~= > >= < <=");

        /// Runs the search natively. Values are parsed against the property's real type, found on the first item that has it.
        public ModelItemCollection Find(Ctx c, ModelItemCollection scope, bool descendants = false)
        {
            var missing = new List<string>();
            var found = ToSearch(c, scope, missing, descendants).FindAll(c.Doc, false);
            if (found.Count == 0 && missing.Count > 0)
                throw new NwError("not_found", "no item has property " + string.Join(", ", missing), "inspect current property names with read {what:\"props\", items:\"?has Item.Name\", limit:3}");
            return found;
        }

        public Search ToSearch(Ctx c, ModelItemCollection scope = null, List<string> missing = null, bool descendants = false)
        {
            var s = new Search { Locations = SearchLocations.DescendantsAndSelf, PruneBelowMatch = !descendants };
            if (scope == null) s.Selection.SelectAll(); else s.Selection.CopyFrom(scope);
            foreach (var cond in conds)
            {
                var sc = SearchCondition.HasPropertyByDisplayName(cond.Tab, cond.Prop);
                switch (cond.Op)
                {
                    case "has": break;
                    case "!has": sc = SearchCondition.HasPropertyByDisplayName(cond.Tab, cond.Prop).Negate(); break;
                    case "~": sc = sc.DisplayStringContains(cond.Value).IgnoreStringValueCase(); break;
                    case "~=": sc = sc.DisplayStringWildcard(cond.Value).IgnoreStringValueCase(); break;
                    default:
                        var sample = Sample(c.Doc, cond.Tab, cond.Prop, out var legacyScale);
                        if (sample == null) { missing?.Add(cond.Tab + "." + cond.Prop); sc = sc.EqualValue(VariantData.FromDisplayString(cond.Value)); break; }
                        var value = Typed(c.Doc, sample, cond.Value, legacyScale);
                        sc = cond.Op == "=" ? sc.EqualValue(value)
                            : cond.Op == "!=" ? sc.CompareWith(SearchConditionComparison.NotEqual, value)
                            : sc.CompareWith(cond.Op == ">" ? SearchConditionComparison.NumericGreaterThan : cond.Op == ">=" ? SearchConditionComparison.NumericGreaterThanOrEqual : cond.Op == "<" ? SearchConditionComparison.NumericLessThan : SearchConditionComparison.NumericLessThanOrEqual, value);
                        if (value.IsDisplayString) sc = sc.IgnoreStringValueCase();
                        break;
                }
                if (cond.Group) sc = sc.StartGroup();
                s.SearchConditions.Add(sc);
            }
            return s;
        }

        static DataProperty Sample(Document doc, string tab, string prop, out double legacyScale)
        {
            var s = new Search { Locations = SearchLocations.DescendantsAndSelf };
            s.Selection.SelectAll();
            s.SearchConditions.Add(SearchCondition.HasPropertyByDisplayName(tab, prop));
            var item = s.FindFirst(doc, false);
            var property = item?.PropertyCategories.FindPropertyByDisplayName(tab, prop);
            legacyScale = Units.LegacyScale(property, item, tab);
            return property;
        }

        static VariantData Typed(Document doc, DataProperty sample, string text, double legacyScale)
        {
            var v = sample.Value;
            if (v.IsAnyDouble || v.IsInt32)
            {
                var suffix = Regex.Match(text, @"^\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*([a-zA-Z]+[23]?)\s*$");
                if (suffix.Success)
                    throw new NwError("bad_args", "numeric property filters do not accept unit suffixes", "remove the suffix and convert the value to mm for length, m2 for area, m3 for volume or degrees for angles (e.g. 3 m = 3000, 10 ft = 3048 mm)");
            }
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                if (v.IsDoubleLength) return VariantData.FromDoubleLength(Units.FromMm(doc, n));
                if (v.IsDoubleArea) return VariantData.FromDoubleArea(n / Math.Pow(Units.Meters(doc), 2));
                if (v.IsDoubleVolume) return VariantData.FromDoubleVolume(n / Math.Pow(Units.Meters(doc), 3));
                if (v.IsDoubleAngle) return VariantData.FromDoubleAngle(n * Math.PI / 180);
                if (v.IsInt32 && n == Math.Floor(n) && n >= int.MinValue && n <= int.MaxValue) return VariantData.FromInt32((int)n);
                if (v.IsDouble) return VariantData.FromDouble(n / legacyScale);
            }
            if (v.IsAnyDouble || v.IsInt32)
                throw new NwError("bad_args", "numeric property value is invalid or outside its supported range", "use a finite number without a unit suffix; lengths are mm, areas m2, volumes m3 and angles degrees");
            if (v.IsBoolean && bool.TryParse(text, out var b)) return VariantData.FromBoolean(b);
            if (v.IsDisplayString) return VariantData.FromDisplayString(text);
            return sample.TryParseCompatibleValue(text) ?? VariantData.FromDisplayString(text);
        }
    }
}
