using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace NavisworksMcp
{
    // UI-thread cache, invalidated on model/sheet changes, transactions and unit changes.
    sealed class ReadCache
    {
        public readonly Dictionary<string, string> Text = new Dictionary<string, string>();
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        public readonly Dictionary<string, List<(string label, int count)>> Tallies = new Dictionary<string, List<(string label, int count)>>();
        public void Clear() { Text.Clear(); Counts.Clear(); Tallies.Clear(); }
    }

    static class ReadOps
    {
        public static readonly Op[] All =
        {
            Catalog.Read("read", "items", "; where:where in:target cols:strs group:str descendants:bool=false limit:int=50 page:int=1 offset:int=0", Items,
                "search topmost matching items (or page a handle) -> r#. descendants:true includes matching descendants. where: has Tab.Prop, !has Tab.Prop (missing, including globally absent properties), comparisons joined by and/or; JSON double quotes and backslash escapes protect literal values. cols adds properties; group shows the top 10 groups. limit:0 returns a summary without continuation",
                "{where:\"Item.Type = IfcWall and Pset_Fixture.FireRating = F90\", cols:\"Pset_Fixture.IsExternal\"}"),
            Catalog.Read("read", "props", "items:target; tabs:strs limit:int=3 offset:int=0", Props,
                "all properties of the first `limit` items, optionally only some tabs", "{items:\"r1#0\", tabs:\"Item,Element\"}"),
            Catalog.Read("read", "tree", "; under:target in:target depth:int=1 limit:int=100 page:int=1 offset:int=0", Tree,
                "children of a node (default: model roots) with descendant counts", "{under:\"r2#0\", depth:2}"),
            Catalog.Read("read", "values", "prop:str; in:target where:where top:int=30 offset:int=0", Values,
                "distinct values of one property with counts", "{prop:\"Item.Type\"}"),
            Catalog.Read("read", "stats", "", Stats, "model totals: items, geometry items and extents per model; type counts come from values Item.Type", "{}"),
            Catalog.Read("read", "page", "in:target; offset:int=0", TextPage, "continue a large text result using its more: call", "{in:\"last\", offset:1}"),
        };

        static void TextPage(Ctx c, Args a, Out o)
        {
            var h = a.Target("in").Whole;
            if (h == null || h.Kind != Kind.Text) throw new NwError("bad_args", "page needs an output-page handle", "copy the more: read call from the original response");
            var i = a.Int("offset");
            if (i >= h.Rows.Count) return;
            o.Line((string)h.Rows[i]);
            if (i + 1 < h.Rows.Count) o.Line($"more: read {{what:\"page\", in:\"{h.Name}\", offset:{i + 1}}}");
        }

        public static IEnumerable<object[]> Rows(Ctx c, IEnumerable<object> rows, int start, string[] cols) =>
            rows.Select((row, i) =>
            {
                var item = (ModelItem)row;
                return new object[] { start + i, Id(c.Doc, item), item.DisplayName, item.ClassDisplayName }.Concat(cols.Select(col => Column(c.Doc, item, col))).ToArray();
            });

        /// The model index and child-index path; unlike FindIdForModelItem, it exists for every file format.
        public static string Id(Document doc, ModelItem item)
        {
            var path = doc.Models.CreatePathId(item);
            return path.ModelIndex + ":" + path.PathId;
        }

        /// A column value as (exact key, display label), so grouping never merges values that only round alike.
        public static (string key, string label) Exact(Document doc, ModelItem item, string col)
        {
            var dot = col.IndexOf('.');
            if (dot < 0) { var s = Column(doc, item, col); return (s, s); }
            var v = item.PropertyCategories.FindPropertyByDisplayName(col.Substring(0, dot), col.Substring(dot + 1));
            return (Units.Key(v?.Value), Units.Value(doc, v, item, col.Substring(0, dot)));
        }

        /// Counts per exact key, most frequent first. Labels that two keys share get the exact value appended.
        public static List<(string label, int count)> Tally(IEnumerable<(string key, string label)> values)
        {
            var groups = values.GroupBy(v => v.key).Select(g => (g.Key, label: g.First().label, count: g.Count())).ToList();
            var shared = new HashSet<string>(groups.GroupBy(g => g.label).Where(g => g.Count() > 1).Select(g => g.Key));
            return groups.OrderByDescending(g => g.count).ThenBy(g => g.label)
                .Select(g => (shared.Contains(g.label) ? $"{g.label} [{g.Key}]" : g.label, g.count)).ToList();
        }

        public static string Column(Document doc, ModelItem item, string col)
        {
            switch (col)
            {
                case "bbox": return Units.Box(doc, item.BoundingBox());
                case "path": return string.Join("/", item.AncestorsAndSelf.Select(x => x.DisplayName));
                case "hidden": return item.AncestorsAndSelf.Any(x => x.IsHidden) ? "true" : "false";
                case "color": return item.FindFirstGeometry() is ModelGeometry g ? Units.Hex(g.ActiveColor) : "-";
                case "transparency": return item.FindFirstGeometry() is ModelGeometry t ? Out.Num(t.ActiveTransparency * 100) : "-";
            }
            var dot = col.IndexOf('.');
            if (dot < 0) throw new NwError("bad_args", $"column \"{col}\" is not Tab.Prop, bbox, path, hidden, color or transparency", "write columns as Tab.Prop, e.g. Element.Category");
            return Units.Value(doc, item.PropertyCategories.FindPropertyByDisplayName(col.Substring(0, dot), col.Substring(dot + 1)), item, col.Substring(0, dot));
        }

        static void Items(Ctx c, Args a, Out o)
        {
            var target = a.Target("in");
            Handle h;
            if (!a.Has("where") && target?.Whole?.Kind == Kind.Items)
                { h = target.Whole; h.Fill(); }
            else
            {
                var scope = target?.Items(c);
                if (!a.Has("where") && scope == null) throw new NwError("bad_args", "items needs where, in, or both", "e.g. read {what:\"items\", where:\"Item.Type = IfcWall\"}");
                var found = a.Has("where") ? Where.Parse(a.Str("where")).Find(c, scope, a.Bool("descendants")) : scope;
                h = c.Keep(new Handle { Kind = Kind.Items, Rows = found.Cast<object>().ToList(), Where = scope == null ? a.Str("where") : null, Searched = a.Has("where") || (target?.Text.StartsWith("?") ?? false), Descendants = a.Has("where") && a.Bool("descendants"), Label = a.Str("where") ?? target.Text });
            }
            Page(c, o, h, a.List("cols"), a.Int("limit"), a.Int("page"), a.Str("group"), a.Int("offset"));
        }

        public static void Page(Ctx c, Out o, Handle h, string[] cols, int limit, int page, string group = null, int offset = 0)
        {
            var head = $"{h.Name} items {h.Rows.Count:#,0} (exact)";
            if (h.Searched) head += h.Descendants ? " · all matching items" : " · topmost matching items";
            if (group != null)
            {
                var key = "group:" + h.Name + ":" + group;
                if (!c.Host.Reads.Tallies.TryGetValue(key, out var counts))
                {
                    counts = Tally(h.Rows.Cast<ModelItem>().Select(x => Exact(c.Doc, x, group)));
                    if (c.Host.Reads.Tallies.Count >= 128) c.Host.Reads.Tallies.Clear();
                    c.Host.Reads.Tallies[key] = counts;
                }
                head += $" · by {group}: " + string.Join(" · ", counts.Take(10).Select(g => $"{Out.Clip(g.label, 200)} {g.count:#,0}"));
                if (counts.Count > 10) head += $" · {counts.Count - 10} more groups; read values for {group} in {h.Name}";
            }
            o.Line(head);
            if (h.Rows.Count == 0) return;
            var start = Out.Start(page, limit, offset);
            o.Table(new[] { "#", "id", "name", "type" }.Concat(cols), Rows(c, h.Rows.Skip(start).Take(limit), start, cols));
            o.More("items", h, page, limit, cols.Length > 0 ? ", cols:" + Newtonsoft.Json.JsonConvert.SerializeObject(cols) : "", start);
        }

        static void Props(Ctx c, Args a, Out o)
        {
            var offset = a.Int("offset");
            var end = (long)offset + a.Int("limit");
            if (end >= int.MaxValue) throw new NwError("bad_args", "property page offset is too large", "start at offset:0 and follow more:");
            var items = a.Target("items").Items(c, (int)end + 1);
            var tabs = new HashSet<string>(a.List("tabs"), StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Skip(offset).Take(a.Int("limit")))
            {
                o.Line($"{Id(c.Doc, item)} {item.DisplayName} · {item.ClassDisplayName}");
                foreach (var cat in item.PropertyCategories)
                {
                    if (tabs.Count > 0 && !tabs.Contains(cat.DisplayName)) continue;
                    o.Line($"[{cat.DisplayName}] " + string.Join(" · ", cat.Properties.Select(p => p.DisplayName + "=" + Units.Value(c.Doc, p, item, cat.DisplayName))));
                }
            }
            if (a.Int("limit") > 0 && items.Count > end) o.Line(a.Next((int)end));
        }

        static void Tree(Ctx c, Args a, Out o)
        {
            var h = a.Target("in")?.Whole;
            if (h != null && h.Depths == null) throw new NwError("bad_args", "tree paging needs a tree handle", "read {what:\"tree\"} and follow its more: call");
            if (h == null)
            {
                var roots = a.Has("under") ? a.Items("under").SelectMany(x => x.Children).ToList() : c.Doc.Models.RootItems.ToList();
                h = c.Keep(new Handle { Kind = Kind.Items, Rows = new List<object>(), Depths = new List<int>(), Pending = Walk(roots, 0, a.Int("depth")).GetEnumerator(), Label = "tree" });
            }
            var limit = a.Int("limit");
            var page = a.Int("page");
            var start = Out.Start(page, limit, a.Int("offset"));
            // A deep walk over a large model takes seconds, so a page walks only one row past itself.
            h.Fill((int)Math.Min(int.MaxValue, (long)start + limit + 1));
            o.Line(h.Pending == null ? $"{h.Name} tree nodes {h.Rows.Count:#,0}" : $"{h.Name} tree nodes {h.Rows.Count:#,0} so far; paging on walks further");
            o.Table(new[] { "#", "id", "name", "type", "children", "descendants" }, h.Rows.Skip(start).Take(limit).Cast<ModelItem>().Select((x, i) => new object[]
            {
                start + i, Id(c.Doc, x), new string(' ', h.Depths[start + i] * 2) + x.DisplayName, x.ClassDisplayName, x.Children.Count(), Descendants(c, x),
            }));
            o.More("tree", h, page, limit, start: start);
        }

        static IEnumerable<(ModelItem item, int depth)> Walk(IEnumerable<ModelItem> level, int depth, int maxDepth)
        {
            foreach (var x in level)
            {
                yield return (x, depth);
                if (depth + 1 < maxDepth) foreach (var below in Walk(x.Children, depth + 1, maxDepth)) yield return below;
            }
        }

        static int Descendants(Ctx c, ModelItem item)
        {
            var id = Id(c.Doc, item);
            if (!c.Host.Reads.Counts.TryGetValue(id, out var count)) c.Host.Reads.Counts[id] = count = Count(c.Doc, item, "LcOaNode") - 1;
            return count;
        }

        static void Values(Ctx c, Args a, Out o)
        {
            var prop = a.Str("prop");
            var cacheKey = "values:" + prop;
            var cache = !a.Has("in") && !a.Has("where");
            var scope = a.Target("in")?.Items(c);
            var dot = prop.IndexOf('.');
            if (dot < 0) throw new NwError("bad_args", $"prop \"{prop}\" is not Tab.Prop", "e.g. Item.Type");
            var tab = prop.Substring(0, dot);
            var name = prop.Substring(dot + 1);
            List<(string label, int count)> top = null;
            if (!cache || !c.Host.Reads.Tallies.TryGetValue(cacheKey, out top))
            {
                IEnumerable<ModelItem> items;
                if (a.Has("where")) items = Where.Parse(a.Str("where")).Find(c, scope);
                else if (scope != null) items = scope;
                else
                {
                    var search = new Search { Locations = SearchLocations.DescendantsAndSelf, PruneBelowMatch = false };
                    search.Selection.SelectAll();
                    search.SearchConditions.Add(SearchCondition.HasPropertyByDisplayName(tab, name));
                    items = search.FindAll(c.Doc, false);
                }
                top = Tally(items.Select(item => (p: item.PropertyCategories.FindPropertyByDisplayName(tab, name), item)).Where(x => x.p != null).Select(x => (Units.Key(x.p.Value), Units.Value(c.Doc, x.p, x.item, tab))));
                if (cache) c.Host.Reads.Tallies[cacheKey] = top;
            }
            o.Line($"values {prop}: {top.Count:#,0} distinct in {top.Sum(g => g.count):#,0} items (exact)");
            var offset = a.Int("offset");
            o.Table(new[] { "value", "count" }, top.Skip(offset).Take(a.Int("top")).Select(g => new object[] { g.label, g.count }));
            if (a.Int("top") > 0 && (long)offset + a.Int("top") < top.Count) o.Line(a.Next(offset + a.Int("top")));
        }

        static void Stats(Ctx c, Args a, Out o)
        {
            if (c.Host.Reads.Text.TryGetValue("stats", out var cached)) { o.Line(cached); return; }
            var d = c.Doc;
            var output = new Out();
            // Native searches count in milliseconds; walking items through interop took seconds on large models.
            for (var i = 0; i < d.Models.Count; i++)
            {
                var root = d.Models[i].RootItem;
                var items = Count(d, root, "LcOaNode");
                var geometry = Count(d, root, "LcOpGeometryProperty");
                output.Line($"model {i} {root.DisplayName}: {items:#,0} items, {geometry:#,0} with geometry, extents {Units.Box(d, root.BoundingBox())} mm");
            }
            output.Line("types: read {what:\"values\", prop:\"Item.Type\"}");
            o.Line(c.Host.Reads.Text["stats"] = output.Text);
        }

        /// Items under root, inclusive, that carry a property category, by its internal name: every item has LcOaNode,
        /// every item with geometry has LcOpGeometryProperty.
        static int Count(Document d, ModelItem root, string category)
        {
            var s = new Search { Locations = SearchLocations.DescendantsAndSelf, PruneBelowMatch = false };
            s.Selection.CopyFrom(new ModelItemCollection { root });
            s.SearchConditions.Add(SearchCondition.HasCategoryByName(category));
            return s.FindAll(d, false).Count;
        }
    }
}
