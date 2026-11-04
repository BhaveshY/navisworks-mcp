using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace NavisworksMcp
{
    static class ReadOps
    {
        public static readonly Op[] All =
        {
            Catalog.Read("read", "items", "; where:where in:target cols:strs group:str limit:int=50 page:int=1", Items,
                "search model items (or page a handle) -> r#. cols adds properties (Tab.Prop, or bbox, path); group counts by a property",
                "{where:\"Item.Type = IfcWall and Pset_Fixture.FireRating = F90\", cols:\"Pset_Fixture.IsExternal\"}"),
            Catalog.Read("read", "props", "items:target; tabs:strs limit:int=3", Props,
                "all properties of the first `limit` items, optionally only some tabs", "{items:\"r1#0\", tabs:\"Item,Element\"}"),
            Catalog.Read("read", "tree", "; under:target depth:int=1 limit:int=100", Tree,
                "children of a node (default: model roots) with descendant counts", "{under:\"r2#0\", depth:2}"),
            Catalog.Read("read", "values", "prop:str; in:target where:where top:int=30", Values,
                "distinct values of one property with counts", "{prop:\"Item.Type\"}"),
            Catalog.Read("read", "stats", "", Stats, "model totals: items, geometry items, extents, most common types", "{}"),
        };

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
                h = target.Whole;
            else
            {
                var scope = target?.Items(c);
                if (!a.Has("where") && scope == null) throw new NwError("bad_args", "items needs where, in, or both", "e.g. read {what:\"items\", where:\"Item.Type = IfcWall\"}");
                var found = a.Has("where") ? Where.Parse(a.Str("where")).Find(c, scope) : scope;
                h = c.Keep(new Handle { Kind = Kind.Items, Rows = found.Cast<object>().ToList(), Where = scope == null ? a.Str("where") : null, Label = a.Str("where") ?? target.Text });
            }
            Page(c, o, h, a.List("cols"), a.Int("limit"), a.Int("page"), a.Str("group"));
        }

        public static void Page(Ctx c, Out o, Handle h, string[] cols, int limit, int page, string group = null)
        {
            var head = $"{h.Name} items {h.Rows.Count:#,0} (exact)";
            if (group != null)
            {
                var counts = Tally(h.Rows.Cast<ModelItem>().Select(x => Exact(c.Doc, x, group))).Select(g => $"{g.label} {g.count:#,0}");
                head += $" · by {group}: " + string.Join(" · ", counts);
            }
            o.Line(head);
            if (h.Rows.Count == 0) return;
            var start = Math.Max(0, (page - 1) * limit);
            o.Table(new[] { "#", "id", "name", "type" }.Concat(cols), Rows(c, h.Rows.Skip(start).Take(limit), start, cols));
            if (start + limit < h.Rows.Count)
                o.More("items", h, page, limit, cols.Length > 0 ? ", cols:\"" + string.Join(",", cols) + "\"" : "");
        }

        static void Props(Ctx c, Args a, Out o)
        {
            var items = a.Items("items");
            var tabs = new HashSet<string>(a.List("tabs"), StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Take(a.Int("limit")))
            {
                o.Line($"{Id(c.Doc, item)} {item.DisplayName} · {item.ClassDisplayName}");
                foreach (var cat in item.PropertyCategories)
                {
                    if (tabs.Count > 0 && !tabs.Contains(cat.DisplayName)) continue;
                    o.Line($"[{cat.DisplayName}] " + string.Join(" · ", cat.Properties.Select(p => p.DisplayName + "=" + Units.Value(c.Doc, p, item, cat.DisplayName))));
                }
            }
            if (items.Count > a.Int("limit")) o.Line($"{items.Count - a.Int("limit"):#,0} more items not shown; raise limit or narrow items");
        }

        static void Tree(Ctx c, Args a, Out o)
        {
            var roots = a.Has("under") ? a.Items("under").SelectMany(x => x.Children).ToList() : c.Doc.Models.RootItems.ToList();
            var rows = new List<ModelItem>();
            var depths = new List<int>();
            void Walk(IEnumerable<ModelItem> level, int depth)
            {
                foreach (var x in level)
                {
                    if (rows.Count >= a.Int("limit")) return;
                    rows.Add(x);
                    depths.Add(depth);
                    if (depth + 1 < a.Int("depth")) Walk(x.Children, depth + 1);
                }
            }
            Walk(roots, 0);
            var h = c.Keep(new Handle { Kind = Kind.Items, Rows = rows.Cast<object>().ToList(), Label = "tree" });
            o.Line($"{h.Name} tree nodes {rows.Count:#,0}" + (rows.Count >= a.Int("limit") ? $" (cut at limit {a.Int("limit")})" : ""));
            o.Table(new[] { "#", "id", "name", "type", "children", "descendants" }, rows.Select((x, i) => new object[]
            {
                i, Id(c.Doc, x), new string(' ', depths[i] * 2) + x.DisplayName, x.ClassDisplayName, x.Children.Count(), x.Descendants.Count(),
            }));
        }

        static void Values(Ctx c, Args a, Out o)
        {
            var prop = a.Str("prop");
            var scope = a.Target("in")?.Items(c);
            IEnumerable<ModelItem> items = a.Has("where") ? Where.Parse(a.Str("where")).Find(c, scope) : scope ?? (IEnumerable<ModelItem>)c.Doc.Models.RootItemDescendantsAndSelf;
            var dot = prop.IndexOf('.');
            if (dot < 0) throw new NwError("bad_args", $"prop \"{prop}\" is not Tab.Prop", "e.g. Item.Type");
            var tab = prop.Substring(0, dot);
            var name = prop.Substring(dot + 1);
            var found = items.Select(item => { var p = item.PropertyCategories.FindPropertyByDisplayName(tab, name); return (p, item); }).Where(x => x.p != null).Select(x => (Units.Key(x.p.Value), Units.Value(c.Doc, x.p, x.item, tab))).ToList();
            var top = Tally(found);
            o.Line($"values {prop}: {top.Count:#,0} distinct in {found.Count:#,0} items (exact)");
            o.Table(new[] { "value", "count" }, top.Take(a.Int("top")).Select(g => new object[] { g.label, g.count }));
            if (top.Count > a.Int("top")) o.Line($"{top.Count - a.Int("top"):#,0} more values; raise top");
        }

        static void Stats(Ctx c, Args a, Out o)
        {
            var d = c.Doc;
            for (var i = 0; i < d.Models.Count; i++)
            {
                var root = d.Models[i].RootItem;
                var all = root.DescendantsAndSelf.ToList();
                var geometry = all.Count(x => x.HasGeometry);
                o.Line($"model {i} {root.DisplayName}: {all.Count:#,0} items, {geometry:#,0} with geometry, extents {Units.Box(d, root.BoundingBox())} mm");
                var types = all.Where(x => x.HasGeometry).GroupBy(x => x.ClassDisplayName).OrderByDescending(g => g.Count()).Take(15);
                o.Line("  geometry by type: " + string.Join(" · ", types.Select(g => $"{g.Key} {g.Count():#,0}")));
            }
        }
    }
}
