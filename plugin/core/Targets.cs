using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Newtonsoft.Json.Linq;

namespace NavisworksMcp
{
    enum Kind { Items, Clashes, Tasks, Takeoff }

    /// A stored result list. Rows are ModelItems, IClashResults, TimeLiner tasks or takeoff rows, by Kind.
    sealed class Handle
    {
        public int N;
        public Kind Kind;
        public List<object> Rows;
        public string Where;
        public string Label;
        public int Generation;
        public string Name => "r" + N;
    }

    sealed class Handles
    {
        const int Capacity = 200;
        readonly Dictionary<int, Handle> table = new Dictionary<int, Handle>();
        int seq;
        public int Last { get; private set; }

        /// Bumped whenever the set of models changes (open, append, merge, close), because rows and takeoff ids
        /// belong to the models they were read from.
        public int Generation;

        public Handle Add(Handle h)
        {
            lock (table)
            {
                h.Generation = Generation;
                h.N = ++seq;
                table[h.N] = h;
                Last = h.N;
                table.Remove(h.N - Capacity);
                return h;
            }
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
                throw new NwError("stale_handle", $"r{n} {why}", "repeat the read that made it and use the new handle");
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

        static readonly Regex HandleRx = new Regex(@"^r(\d+)(?:#(\d+)(?:-(\d+))?)?$");
        static readonly Regex BatchRx = new Regex(@"^\$(\d+)$");
        static readonly Regex IdRx = new Regex(@"^id:(\d+):(\d+(?:/\d+)*)$");

        static Func<Ctx, IEnumerable<object>> Part(Ctx c, Target t, string s)
        {
            Match m;
            if ((m = HandleRx.Match(s)).Success)
            {
                var h = c.Host.Handles.Get(int.Parse(m.Groups[1].Value));
                t.HandleKind = h.Kind;
                if (!m.Groups[2].Success) { t.Whole = h; return _ => h.Rows; }
                var from = int.Parse(m.Groups[2].Value);
                var to = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : from;
                if (from > to || to >= h.Rows.Count) throw new NwError("bad_args", $"{s} is outside {h.Name}, which has rows 0-{h.Rows.Count - 1}", $"use rows inside 0-{h.Rows.Count - 1}");
                return _ => h.Rows.Skip(from).Take(to - from + 1);
            }
            if ((m = BatchRx.Match(s)).Success)
            {
                var i = int.Parse(m.Groups[1].Value);
                if (i >= c.BatchIndex && !c.Preview) throw new NwError("bad_args", $"{s} refers to op {i}, which has not run yet", "only refer to earlier ops in the same apply call");
                return ctx => ctx.Preview && !ctx.Made.ContainsKey(i) ? Enumerable.Empty<object>() : ctx.Made.TryGetValue(i, out var h) ? h.Rows : throw new NwError("bad_args", $"op {i} made no handle", "refer only to ops that return r#");
            }
            if (s == "selection") return ctx => ctx.Doc.CurrentSelection.SelectedItems.Cast<object>().ToList();
            if (s == "last") return Part(c, t, "r" + c.Host.Handles.Last);
            if (s == "all") return ctx => ctx.Doc.Models.RootItems.Cast<object>().ToList();
            if (s.StartsWith("set:")) { var set = Sets.Find(c.Doc, s.Substring(4)); return ctx => set.GetSelectedItems(ctx.Doc).Cast<object>().ToList(); }
            if ((m = IdRx.Match(s)).Success)
            {
                var path = new Autodesk.Navisworks.Api.DocumentParts.ModelItemPathId { ModelIndex = int.Parse(m.Groups[1].Value), PathId = m.Groups[2].Value };
                var text = s;
                return ctx => new List<object> { ctx.Doc.Models.ResolvePathId(path) ?? throw new NwError("not_found", $"no item at {text}", "ids change when models are added, removed or refreshed; search again with read items") };
            }
            if (s.StartsWith("?")) { var w = Where.Parse(s.Substring(1)); return ctx => w.Find(ctx, null).Cast<object>().ToList(); }
            throw new NwError("bad_args", $"\"{s}\" is not a target", "use r3, r3#5, r3#0-9, $0, selection, last, all, set:Folder/Name, id:0:0/2/1 or ?Item.Type = IfcWall");
        }

        public IEnumerable<object> Rows(Ctx c) => parts.SelectMany(p => p(c));

        /// Model items behind the target. A clash handle contributes both items of each result.
        public ModelItemCollection Items(Ctx c)
        {
            var items = new ModelItemCollection();
            foreach (var row in Rows(c))
            {
                if (row is ModelItem mi) items.Add(mi);
                else if (row is IClashResult r)
                {
                    foreach (var x in r.CompositeItemSelection1 ?? r.Selection1) items.Add(x);
                    foreach (var x in r.CompositeItemSelection2 ?? r.Selection2) items.Add(x);
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

        static readonly Regex Joiner = new Regex(@"\s+(and|or)\s+(?=(?:[^""]*""[^""]*"")*[^""]*$)", RegexOptions.IgnoreCase);
        static readonly Regex CondRx = new Regex(@"^(?<neg>!)?has\s+(?<name>.+)$|^(?<name>.+?)\s*(?<op>!=|>=|<=|~=|=|~|>|<)\s*(?<value>.*)$", RegexOptions.IgnoreCase);

        public static Where Parse(string text)
        {
            var w = new Where { Text = text.Trim() };
            var pieces = Joiner.Split(w.Text);
            var group = false;
            for (var i = 0; i < pieces.Length; i += 2)
            {
                if (i > 0) group = pieces[i - 1].Equals("or", StringComparison.OrdinalIgnoreCase);
                var m = CondRx.Match(pieces[i].Trim());
                if (!m.Success) throw Bad(pieces[i]);
                var name = Unquote(m.Groups["name"].Value.Trim());
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

        static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;

        static NwError Bad(string piece) => new NwError("bad_args", $"cannot read condition \"{piece.Trim()}\"",
            "write conditions as Tab.Prop op value, e.g. Item.Type = IfcWall and Pset_WallCommon.FireRating ~ 90 or has \"Element ID\".Value; ops = != ~ ~= > >= < <=");

        /// Runs the search natively. Values are parsed against the property's real type, found on the first item that has it.
        public ModelItemCollection Find(Ctx c, ModelItemCollection scope)
        {
            var missing = new List<string>();
            var found = ToSearch(c, scope, missing).FindAll(c.Doc, false);
            if (found.Count == 0 && missing.Count > 0)
                throw new NwError("not_found", "no item has property " + string.Join(", ", missing), "check names with read {what:\"props\", items:\"r1#0\"} or read {what:\"values\", prop:\"Tab.Prop\"}");
            return found;
        }

        public Search ToSearch(Ctx c, ModelItemCollection scope = null, List<string> missing = null)
        {
            var s = new Search { Locations = SearchLocations.DescendantsAndSelf };
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
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                if (v.IsDoubleLength) return VariantData.FromDoubleLength(Units.FromMm(doc, n));
                if (v.IsDoubleArea) return VariantData.FromDoubleArea(n / Math.Pow(Units.Meters(doc), 2));
                if (v.IsDoubleVolume) return VariantData.FromDoubleVolume(n / Math.Pow(Units.Meters(doc), 3));
                if (v.IsDoubleAngle) return VariantData.FromDoubleAngle(n * Math.PI / 180);
                if (v.IsInt32 && n == Math.Floor(n)) return VariantData.FromInt32((int)n);
                if (v.IsDouble) return VariantData.FromDouble(n / legacyScale);
            }
            if (v.IsBoolean && bool.TryParse(text, out var b)) return VariantData.FromBoolean(b);
            if (v.IsDisplayString) return VariantData.FromDisplayString(text);
            return sample.TryParseCompatibleValue(text) ?? VariantData.FromDisplayString(text);
        }
    }
}
