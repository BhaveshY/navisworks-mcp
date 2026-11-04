using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Data;
using Autodesk.Navisworks.Api.Interop;
using Autodesk.Navisworks.Api.Takeoff;

namespace NavisworksMcp
{
    /// One row of a Kind.Takeoff handle. Level is catalog (Kind group or item), takeoffs (a TK_Object row) or totals (grouped By).
    sealed class QtoRow
    {
        public string Level, By, Kind, Path, Model, Wbs;
        public long Id;
        public int Takeoffs;
        public double? Count, Length, Area, Volume;
    }

    /// Quantification. The takeoff database is SQLite behind doc.GetTakeoff(); catalog groups and items are
    /// rows of TK_ItemGroup and TK_Item, takeoffs are rows of TK_Object, addressed by paths such as "Structure/Slabs".
    static class TakeoffOps
    {
        public static readonly Op[] All =
        {
            Catalog.Read("read", "qto", "; level:enum(catalog|takeoffs|totals)=totals by:str item:str in:target limit:int=50 page:int=1 offset:int=0", List,
                "Quantification -> r#. catalog: groups and items; takeoffs: one row per taken-off model item; totals: sums by item (default), by group, or by a model property Tab.Prop. item limits to a group or item path. Lengths mm, areas m2, volumes m3",
                "{level:\"totals\", by:\"Pset_Fixture.FireRating\"}"),
            Catalog.Apply("qto.group.add", "name:str; wbs:str", AddGroup,
                "add a catalog group by path, creating missing parent groups (sets up Quantification, metric, on first use)", "{name:\"Structure/Concrete\"}"),
            Catalog.Apply("qto.item.add", "name:str; wbs:str rgb:color", AddItem,
                "add a catalog item by path; missing groups on the path are created", "{name:\"Structure/Concrete/Slabs\", rgb:\"#808080\"}"),
            Catalog.Apply("qto.takeoff", "items:target item:str; from:enum(model|bbox)=model", Takeoff,
                "take off model items into a catalog item. model: quantities from Navisworks' property mapping; bbox: fill quantities the mapping left empty from the bounding box (length and width = plan sides, height = z, thickness = smallest side, area = largest face, volume = box). Items already in the catalog item are skipped",
                "{items:\"?Item.Type = IfcWall\", item:\"Structure/Walls\", from:\"bbox\"}"),
            Catalog.Apply("qto.delete", "; name:str takeoffs:target", Delete,
                "delete a catalog group or item (with everything taken off into it), or takeoff rows from a takeoffs handle", "{takeoffs:\"r5#0-3\"}"),
            Catalog.Read("export", "qto_csv", "; path:path level:enum(catalog|takeoffs|totals)=takeoffs by:str", ExportCsv,
                "write the quantities to CSV (same columns as read qto, all rows)", "{path:\"C:/Coord/qto.csv\", level:\"totals\"}"),
        };

        static DocumentTakeoff Tk(Ctx c) => c.Doc.GetTakeoff();

        static bool IsSetUp(DocumentTakeoff tk) => DocumentTakeoffExtensions.GetHasSetUp(tk);

        static readonly (string name, TakeoffUnitGroup group, TakeoffUnits unit)[] Quantities =
        {
            ("Length", TakeoffUnitGroup.Length, TakeoffUnits.Meter), ("Width", TakeoffUnitGroup.Length, TakeoffUnits.Meter),
            ("Thickness", TakeoffUnitGroup.Length, TakeoffUnits.Meter), ("Height", TakeoffUnitGroup.Length, TakeoffUnits.Meter),
            ("Perimeter", TakeoffUnitGroup.Length, TakeoffUnits.Meter), ("Area", TakeoffUnitGroup.Area, TakeoffUnits.SquareMeter),
            ("Volume", TakeoffUnitGroup.Volume, TakeoffUnits.CubicMeter), ("Weight", TakeoffUnitGroup.Weight, TakeoffUnits.Kilogram),
        };

        /// Does what the Quantification window's Project Setup does with the default template and metric units.
        /// Returns true when it had to set the database up.
        static bool EnsureSetUp(DocumentTakeoff tk)
        {
            if (IsSetUp(tk))
            {
                if (!tk.HasDatabaseInitialized) DocumentTakeoffExtensions.InitializeDatabase(tk);
                return false;
            }
            var cm = LcTkConfigManager.Instance;
            cm.UnitsSystem = TakeoffUnitSystem.Metric;
            if (!cm.LoadTemplateConfigFile(out var err))
                throw new NwError("failed", "the Quantification template did not load: " + err, "run Project Setup once in the Quantification window, then repeat");
            cm.UnitsSystem = TakeoffUnitSystem.Metric;
            for (var i = 0; i < Quantities.Length; i++) cm.AddTakeoffProperty("Model" + Quantities[i].name, true, Quantities[i].group, Quantities[i].unit);
            for (var i = 0; i < Quantities.Length; i++) cm.AddTakeoffPropertyRelatedCalculationColumn("Model" + Quantities[i].name, true, Quantities[i].group, Quantities[i].unit, i);
            if (!cm.CreateSchema(out err))
                throw new NwError("failed", "the Quantification database could not be created: " + err, "run Project Setup once in the Quantification window, then repeat");
            DocumentTakeoffExtensions.SetHasSetUp(tk, true);
            DocumentTakeoffExtensions.InitializeDatabase(tk);
            return true;
        }

        static NavisworksCommand Command(NavisworksConnection conn, string sql, object[] args)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("@p" + i, args[i] ?? DBNull.Value);
            return cmd;
        }

        static List<object[]> Query(DocumentTakeoff tk, string sql, params object[] args)
        {
            var rows = new List<object[]>();
            using (var r = Command(tk.Database.Value, sql, args).ExecuteReader())
                while (r.Read())
                {
                    var row = new object[r.FieldCount];
                    r.GetValues(row);
                    rows.Add(row);
                }
            return rows;
        }

        static long Insert(NavisworksTransaction tr, string sql, params object[] args)
        {
            Command(tr.Connection, sql, args).ExecuteNonQuery();
            return (long)Command(tr.Connection, "SELECT last_insert_rowid()", new object[0]).ExecuteScalar();
        }

        static void Exec(NavisworksTransaction tr, string sql, params object[] args) => Command(tr.Connection, sql, args).ExecuteNonQuery();

        sealed class Node
        {
            public long Id;
            public long? Parent;
            public string Name, Wbs, Path;
            public bool IsItem;
        }

        /// Groups and items with their paths, in tree order.
        static List<Node> CatalogNodes(DocumentTakeoff tk)
        {
            if (!IsSetUp(tk)) return new List<Node>();
            EnsureSetUp(tk);
            Node Read(object[] r, bool item) => new Node { Id = (long)r[0], Parent = r[1] is DBNull ? (long?)null : Convert.ToInt64(r[1]), Name = r[2] as string, Wbs = r[3] as string, IsItem = item };
            var groups = Query(tk, "SELECT ID, Parent, Name, WBS FROM TK_ItemGroup").Select(r => Read(r, false)).ToList();
            var items = Query(tk, "SELECT ID, Parent, Name, WBS FROM TK_Item").Select(r => Read(r, true)).ToList();
            var byId = groups.ToDictionary(g => g.Id);
            var ordered = new List<Node>();
            void Visit(long? parent, string prefix)
            {
                foreach (var g in groups.Where(g => g.Parent == parent).OrderBy(g => g.Wbs, WbsOrder.Instance))
                {
                    g.Path = prefix + g.Name;
                    ordered.Add(g);
                    Visit(g.Id, g.Path + "/");
                }
                foreach (var i in items.Where(i => i.Parent == parent).OrderBy(i => i.Wbs, WbsOrder.Instance))
                {
                    i.Path = prefix + i.Name;
                    ordered.Add(i);
                }
            }
            Visit(null, "");
            return ordered;
        }

        sealed class WbsOrder : IComparer<string>
        {
            public static readonly WbsOrder Instance = new WbsOrder();
            public int Compare(string a, string b) =>
                int.TryParse(a, out var x) && int.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);
        }

        static double Si(TakeoffUnits u)
        {
            switch (u)
            {
                case TakeoffUnits.Kilometer: return 1000;
                case TakeoffUnits.Centimeter: return 0.01;
                case TakeoffUnits.Millimeter: return 0.001;
                case TakeoffUnits.Micrometer: return 1e-6;
                case TakeoffUnits.Inch: return 0.0254;
                case TakeoffUnits.Feet: return 0.3048;
                case TakeoffUnits.Yard: return 0.9144;
                case TakeoffUnits.Mile: return 1609.344;
                case TakeoffUnits.SquareKilometer: return 1e6;
                case TakeoffUnits.SquareCentimeter: return 1e-4;
                case TakeoffUnits.SquareMillimeter: return 1e-6;
                case TakeoffUnits.SquareInch: return 0.0254 * 0.0254;
                case TakeoffUnits.SquareFeet: return 0.3048 * 0.3048;
                case TakeoffUnits.SquareYard: return 0.9144 * 0.9144;
                case TakeoffUnits.SquareMile: return 1609.344 * 1609.344;
                case TakeoffUnits.CubicCentimeter: return 1e-6;
                case TakeoffUnits.CubicMillimeter: return 1e-9;
                case TakeoffUnits.CubicKilometer: return 1e9;
                case TakeoffUnits.CubicInch: return Math.Pow(0.0254, 3);
                case TakeoffUnits.CubicFeet: return Math.Pow(0.3048, 3);
                case TakeoffUnits.CubicYard: return Math.Pow(0.9144, 3);
                case TakeoffUnits.Gallon: return 0.003785411784;
                default: return 1;
            }
        }

        /// Count, length (m), area (m2), volume (m3) of one row of a takeoff table, through the API so units convert.
        static void Fill(TakeoffTable table, long id, QtoRow row)
        {
            foreach (var v in table.SelectVariables(id))
            {
                var value = v.ActiveValue;
                if (value == null || value.IsNone) continue;
                var x = value.IsDouble ? value.ToDouble() : value.IsInt32 ? value.ToInt32() : double.NaN;
                if (double.IsNaN(x)) continue;
                switch (v.Definition.Name)
                {
                    case "Count": row.Count = x; break;
                    case "Length": row.Length = x * Si(v.ActiveUnits); break;
                    case "Area": row.Area = x * Si(v.ActiveUnits); break;
                    case "Volume": row.Volume = x * Si(v.ActiveUnits); break;
                }
            }
        }

        static double? Sum(IEnumerable<double?> xs) { double? s = null; foreach (var x in xs) if (x != null) s = (s ?? 0) + x; return s; }

        static Dictionary<Guid, ModelItem> ByGuid(Document doc, ICollection<Guid> want)
        {
            var found = new Dictionary<Guid, ModelItem>();
            // NULL ModelItemID denotes a virtual/unlinked takeoff, not the first GUID-less model node.
            want = new HashSet<Guid>(want.Where(g => g != Guid.Empty));
            if (want.Count == 0) return found;
            foreach (var x in doc.Models.RootItemDescendantsAndSelf)
            {
                if (want.Contains(x.InstanceGuid) && !found.ContainsKey(x.InstanceGuid)) found[x.InstanceGuid] = x;
                if (found.Count == want.Count) break;
            }
            return found;
        }

        static Guid AsGuid(object v) => v is Guid g ? g : v is byte[] b && b.Length == 16 ? new Guid(b) : v is string s && Guid.TryParse(s, out var p) ? p : Guid.Empty;

        sealed class Taken
        {
            public long Id, Item;
            public Guid Guid;
        }

        static List<Taken> Takeoffs(DocumentTakeoff tk) =>
            Query(tk, "SELECT ID, Parent, ModelItemID FROM TK_Object").Select(r => new Taken { Id = (long)r[0], Item = Convert.ToInt64(r[1]), Guid = AsGuid(r[2]) }).ToList();

        static List<Node> Scope(List<Node> nodes, string path)
        {
            if (path == null) return nodes;
            var root = nodes.FirstOrDefault(n => n.Path == path) ?? throw new NwError("not_found", $"no catalog group or item \"{path}\"", "list the catalog with read {what:\"qto\", level:\"catalog\"}");
            return nodes.Where(n => n.Path == path || n.Path.StartsWith(root.Path + "/")).ToList();
        }

        static List<QtoRow> Rows(Ctx c, string level, string by, string item)
        {
            var tk = Tk(c);
            var nodes = Scope(CatalogNodes(tk), item);
            var items = nodes.Where(n => n.IsItem).ToDictionary(n => n.Id);
            var objects = IsSetUp(tk) ? Takeoffs(tk).Where(t => items.ContainsKey(t.Item)).ToList() : new List<Taken>();
            if (level == "catalog")
            {
                var counts = objects.GroupBy(t => t.Item).ToDictionary(g => g.Key, g => g.Count());
                return nodes.Select(n => new QtoRow
                {
                    Level = level, Kind = n.IsItem ? "item" : "group", Id = n.Id, Path = n.Path, Wbs = n.Wbs,
                    Takeoffs = n.IsItem ? (counts.TryGetValue(n.Id, out var k) ? k : 0) : nodes.Where(x => x.IsItem && x.Path.StartsWith(n.Path + "/")).Sum(x => counts.TryGetValue(x.Id, out var m) ? m : 0),
                }).ToList();
            }
            if (level == "totals" && (by == null || by == "item"))
                return nodes.Where(n => n.IsItem).Select(n =>
                {
                    var row = new QtoRow { Level = level, By = "item", Id = n.Id, Path = n.Path, Takeoffs = objects.Count(t => t.Item == n.Id) };
                    Fill(tk.Items, n.Id, row);
                    return row;
                }).ToList();
            var needModel = level == "takeoffs" || by != "group";
            var models = needModel ? ByGuid(c.Doc, new HashSet<Guid>(objects.Select(t => t.Guid))) : new Dictionary<Guid, ModelItem>();
            var rows = objects.Select(t =>
            {
                var row = new QtoRow { Level = "takeoffs", Id = t.Id, Path = items[t.Item].Path, Model = models.TryGetValue(t.Guid, out var mi) ? mi.DisplayName : "-", Takeoffs = 1 };
                Fill(tk.Objects, t.Id, row);
                return (t, row, mi);
            }).ToList();
            if (level == "takeoffs") return rows.Select(x => x.row).ToList();

            Func<(Taken t, QtoRow row, ModelItem mi), (string key, string label)> key;
            if (by == "group") key = x => { var p = x.row.Path; var slash = p.LastIndexOf('/'); var g = slash < 0 ? "-" : p.Substring(0, slash); return (g, g); };
            else
            {
                if (by.IndexOf('.') < 0) throw new NwError("bad_args", $"by \"{by}\" is not item, group or Tab.Prop", "e.g. by:\"group\" or by:\"Item.Type\"");
                key = x => x.mi == null ? ("-", "-") : ReadOps.Exact(c.Doc, x.mi, by);
            }
            return rows.GroupBy(x => key(x)).OrderBy(g => g.Key.label, StringComparer.Ordinal).Select(g => new QtoRow
            {
                Level = level, By = by, Path = g.Key.label, Takeoffs = g.Count(),
                Count = Sum(g.Select(x => x.row.Count)), Length = Sum(g.Select(x => x.row.Length)), Area = Sum(g.Select(x => x.row.Area)), Volume = Sum(g.Select(x => x.row.Volume)),
            }).ToList();
        }

        static string[] Header(string level, string by) =>
            level == "catalog" ? new[] { "#", "kind", "path", "wbs", "takeoffs" }
            : level == "takeoffs" ? new[] { "#", "id", "item", "model item", "count", "length_mm", "area_m2", "volume_m3" }
            : new[] { "#", by ?? "item", "takeoffs", "count", "length_mm", "area_m2", "volume_m3" };

        static object[] Cells(QtoRow r, int i, string level, Func<double, string> num)
        {
            object Q(double? v, double scale = 1) => v == null ? "-" : num(v.Value * scale);
            return level == "catalog" ? new object[] { i, r.Kind, r.Path, r.Wbs, r.Takeoffs }
                : level == "takeoffs" ? new object[] { i, r.Id, r.Path, r.Model, Q(r.Count), Q(r.Length, 1000), Q(r.Area), Q(r.Volume) }
                : new object[] { i, r.Path, r.Takeoffs, Q(r.Count), Q(r.Length, 1000), Q(r.Area), Q(r.Volume) };
        }

        static void List(Ctx c, Args a, Out o)
        {
            var level = a.Str("level");
            var by = a.Str("by");
            var target = a.Target("in");
            Handle h;
            if (target != null)
            {
                if (target.Whole?.Kind != Kind.Takeoff)
                    throw new NwError("bad_args", $"in:\"{target.Text}\" is not a whole qto handle", "pass in:\"r5\" from an earlier read {what:\"qto\"}");
                h = target.Whole;
                var first = h.Rows.Cast<QtoRow>().FirstOrDefault();
                if (first != null) { level = first.Level; by = first.By; }
            }
            else
            {
                if (!IsSetUp(Tk(c)))
                {
                    o.Line("Quantification is not set up in this document; nothing is taken off. next: apply {ops:[{op:\"qto.item.add\", name:\"Group/Item\"}]}");
                    return;
                }
                if (level != "totals" && by != null) throw new NwError("bad_args", "by only applies to level totals", "drop by, or pass level:\"totals\"");
                h = c.Keep(new Handle { Kind = Kind.Takeoff, Rows = Rows(c, level, by, a.Str("item")).Cast<object>().ToList(), Label = "qto " + level });
            }
            var rows = h.Rows.Cast<QtoRow>().ToList();
            var head = $"{h.Name} qto {level}" + (level == "totals" ? " by " + (by ?? "item") : "") + $" {rows.Count:#,0} (exact)";
            if (level != "catalog")
                head += $" · all: count {Out.Num(Sum(rows.Select(r => r.Count)) ?? 0)}, length {Out.Num((Sum(rows.Select(r => r.Length)) ?? 0) * 1000)} mm, area {Out.Num(Sum(rows.Select(r => r.Area)) ?? 0)} m2, volume {Out.Num(Sum(rows.Select(r => r.Volume)) ?? 0)} m3";
            o.Line(head);
            if (rows.Count == 0) return;
            var limit = a.Int("limit");
            var page = a.Int("page");
            var start = Out.Start(page, limit, a.Int("offset"));
            o.Table(Header(level, by), rows.Skip(start).Take(limit).Select((r, i) => Cells(r, start + i, level, Out.Num)));
            o.More("qto", h, page, limit, start: start, total: rows.Count);
        }

        static string Leaf(string path) => path.Substring(path.LastIndexOf('/') + 1);
        static string ParentPath(string path) => path.Contains("/") ? path.Substring(0, path.LastIndexOf('/')) : null;

        static void CheckPath(string path)
        {
            if (path.Split('/').Any(p => p.Trim().Length == 0)) throw new NwError("bad_args", $"\"{path}\" has an empty part", "write paths as Group/Sub group/Item");
        }

        /// Creates the groups on a path that do not exist yet; returns the id of the last one (null for the root).
        static long? Groups(NavisworksTransaction tr, List<Node> nodes, string path, string wbs, List<string> made)
        {
            if (path == null) return null;
            var existing = nodes.FirstOrDefault(n => n.Path == path);
            if (existing != null)
            {
                if (existing.IsItem) throw new NwError("bad_args", $"\"{path}\" is a catalog item, not a group", "items cannot contain groups or items; choose another path");
                return existing.Id;
            }
            var parent = Groups(tr, nodes, ParentPath(path), null, made);
            var id = Insert(tr, "INSERT INTO TK_ItemGroup (WBS, Name, Parent) VALUES (@p0, @p1, @p2)", wbs ?? NextWbs(nodes, parent), Leaf(path), parent);
            nodes.Add(new Node { Id = id, Parent = parent, Name = Leaf(path), Path = path, Wbs = wbs ?? NextWbs(nodes, parent) });
            made.Add(path);
            return id;
        }

        static string NextWbs(List<Node> nodes, long? parent) =>
            (nodes.Where(n => n.Parent == parent).Select(n => int.TryParse(n.Wbs, out var w) ? w : 0).DefaultIfEmpty(0).Max() + 1).ToString(CultureInfo.InvariantCulture);

        static string SetupNote(DocumentTakeoff tk) => IsSetUp(tk) ? "" : " (first sets up Quantification with metric units)";

        static Step AddGroup(Ctx c, Args a)
        {
            var tk = Tk(c);
            var name = a.Str("name");
            CheckPath(name);
            var wbs = a.Str("wbs");
            return new Step($"add catalog group \"{name}\"{SetupNote(tk)}", () =>
            {
                var setup = EnsureSetUp(tk) ? "set up Quantification (metric); " : "";
                var nodes = CatalogNodes(tk);
                if (nodes.Any(n => n.Path == name && !n.IsItem)) return setup + $"catalog group \"{name}\" exists";
                var made = new List<string>();
                using (var tr = tk.Database.BeginTransaction(DatabaseChangedAction.Edited))
                {
                    Groups(tr, nodes, name, wbs, made);
                    tr.Commit();
                }
                return setup + "added catalog group " + string.Join(", ", made.Select(m => "\"" + m + "\""));
            });
        }

        static Step AddItem(Ctx c, Args a)
        {
            var tk = Tk(c);
            var name = a.Str("name");
            CheckPath(name);
            var wbs = a.Str("wbs");
            var rgb = a.Has("rgb") ? a.Color("rgb") : Color.FromByteRGB(128, 128, 128);
            return new Step($"add catalog item \"{name}\"{SetupNote(tk)}", () =>
            {
                var setup = EnsureSetUp(tk) ? "set up Quantification (metric); " : "";
                var nodes = CatalogNodes(tk);
                var existing = nodes.FirstOrDefault(n => n.Path == name);
                if (existing != null && existing.IsItem) return setup + $"catalog item \"{name}\" exists";
                if (existing != null) throw new NwError("bad_args", $"\"{name}\" is a catalog group", "pick another path for the item");
                var made = new List<string>();
                using (var tr = tk.Database.BeginTransaction(DatabaseChangedAction.Edited))
                {
                    var parent = Groups(tr, nodes, ParentPath(name), null, made);
                    var argb = unchecked((int)0xFF000000) | rgb.GetClampedByteValue(0) << 16 | rgb.GetClampedByteValue(1) << 8 | rgb.GetClampedByteValue(2);
                    Insert(tr, "INSERT INTO TK_Item (WBS, Name, Parent, Transparency, Color, LineThickness, CountSymbol, CountSize) VALUES (@p0, @p1, @p2, 0, @p3, 0.1, 1, 1)",
                        wbs ?? NextWbs(nodes, parent), Leaf(name), parent, argb);
                    tr.Commit();
                }
                return setup + $"added catalog item \"{name}\"" + (made.Count > 0 ? " and group " + string.Join(", ", made.Select(m => "\"" + m + "\"")) : "");
            });
        }

        static void FromBox(Document doc, ModelItem item, TakeoffVariableCollection vars)
        {
            var b = item.BoundingBox();
            if (b == null || b.IsEmpty) return;
            var m = Units.Meters(doc);
            double dx = b.Size.X * m, dy = b.Size.Y * m, dz = b.Size.Z * m;
            var sides = new[] { dx, dy, dz }.OrderByDescending(x => x).ToArray();
            var values = new Dictionary<string, double>
            {
                ["ModelLength"] = Math.Max(dx, dy), ["ModelWidth"] = Math.Min(dx, dy), ["ModelHeight"] = dz, ["ModelThickness"] = sides[2],
                ["ModelArea"] = sides[0] * sides[1], ["ModelPerimeter"] = 2 * (sides[0] + sides[1]), ["ModelVolume"] = dx * dy * dz,
            };
            foreach (var v in vars)
            {
                if (!values.TryGetValue(v.Definition.Name, out var x) || (v.Value != null && !v.Value.IsNone)) continue;
                v.Value = VariantData.FromDouble(x);
                v.Units = v.UnitsGroup == TakeoffUnitGroup.Area ? TakeoffUnits.SquareMeter : v.UnitsGroup == TakeoffUnitGroup.Volume ? TakeoffUnits.CubicMeter : TakeoffUnits.Meter;
            }
        }

        static readonly string[] Measured = { "ModelLength", "ModelWidth", "ModelThickness", "ModelHeight", "ModelPerimeter", "ModelArea", "ModelVolume" };

        static Step Takeoff(Ctx c, Args a)
        {
            var tk = Tk(c);
            var items = a.Items("items");
            var path = a.Str("item");
            var bbox = a.Str("from") == "bbox";
            if (!IsSetUp(tk) || !CatalogNodes(tk).Any(n => n.IsItem && n.Path == path))
                throw new NwError("not_found", $"no catalog item \"{path}\"", $"add it first: apply {{ops:[{{op:\"qto.item.add\", name:\"{path}\"}}, {{op:\"qto.takeoff\", items:..., item:\"{path}\"}}]}}");
            var withGuid = items.Where(x => x.InstanceGuid != Guid.Empty).ToList();
            var noGuid = items.Count - withGuid.Count;
            return new Step($"take off {withGuid.Count:#,0} items into \"{path}\" ({(bbox ? "model properties, then bounding boxes" : "model properties")})" + (noGuid > 0 ? $"; {noGuid:#,0} items without a GUID cannot be taken off" : ""), () =>
            {
                var item = CatalogNodes(tk).First(n => n.IsItem && n.Path == path);
                var already = new HashSet<Guid>(Takeoffs(tk).Where(t => t.Item == item.Id).Select(t => t.Guid));
                var made = new List<long>();
                var skipped = 0;
                var modelValues = 0;
                using (var tr = tk.Database.BeginTransaction(DatabaseChangedAction.Edited))
                {
                    foreach (var mi in withGuid)
                    {
                        if (!already.Add(mi.InstanceGuid)) { skipped++; continue; }
                        var vars = tk.Objects.CreateDefaultInputVariables();
                        tk.Objects.MapModelItemPropertiesToInputVariables(item.Id, mi, vars);
                        if (vars.Any(v => Measured.Contains(v.Definition.Name) && v.Value != null && !v.Value.IsNone)) modelValues++;
                        if (bbox) FromBox(c.Doc, mi, vars);
                        made.Add(tk.Objects.InsertModelItemTakeoff(item.Id, mi.InstanceGuid, vars, false));
                    }
                    tr.Commit();
                }
                var rows = made.Select(id => { var r = new QtoRow { Level = "takeoffs", Id = id, Path = path }; Fill(tk.Objects, id, r); return (object)r; }).ToList();
                var h = c.Keep(new Handle { Kind = Kind.Takeoff, Rows = rows, Label = "qto.takeoff" });
                var text = $"took off {made.Count:#,0} items into \"{path}\" -> {h.Name}; {modelValues:#,0} had quantities from model properties";
                if (skipped > 0) text += $"; {skipped:#,0} were already in it";
                if (noGuid > 0) text += $"; {noGuid:#,0} without a GUID were left out";
                if (!bbox && modelValues < made.Count) text += ". next: for quantities from geometry, delete these takeoffs and take off again with from:\"bbox\"";
                else if (noGuid > 0) text += ". next: Quantification links items by GUID; for an IFC model, set Options > File Readers > IFC > Conversion Method to v3 (v4, the 2027 default, assigns none), reopen the model, then take off again";
                return text;
            });
        }

        static Step Delete(Ctx c, Args a)
        {
            var tk = Tk(c);
            if (a.Has("name") == a.Has("takeoffs")) throw new NwError("bad_args", "qto.delete needs name or takeoffs, not both", "pass name:\"Group/Item\" or takeoffs:\"r5#0-3\"");
            if (!IsSetUp(tk)) throw new NwError("not_found", "Quantification is not set up in this document", "nothing to delete");
            List<long> objects, items, groups;
            string what;
            if (a.Has("takeoffs"))
            {
                var t = a.Target("takeoffs");
                var rows = t.Rows(c).Select(r => r as QtoRow).ToList();
                if (rows.Any(r => r == null || r.Level != "takeoffs")) throw new NwError("bad_args", $"{t.Text} does not hold takeoff rows", "pass a handle from read {what:\"qto\", level:\"takeoffs\"} or from qto.takeoff");
                objects = rows.Select(r => r.Id).Distinct().ToList();
                items = new List<long>();
                groups = new List<long>();
                what = $"{objects.Count:#,0} takeoffs";
            }
            else
            {
                var name = a.Str("name");
                var scope = Scope(CatalogNodes(tk), name);
                items = scope.Where(n => n.IsItem).Select(n => n.Id).ToList();
                groups = scope.Where(n => !n.IsItem).Select(n => n.Id).ToList();
                var inItems = new HashSet<long>(items);
                objects = Takeoffs(tk).Where(o => inItems.Contains(o.Item)).Select(o => o.Id).ToList();
                what = $"\"{name}\" ({groups.Count:#,0} groups, {items.Count:#,0} items, {objects.Count:#,0} takeoffs)";
            }
            return new Step("delete " + what, () =>
            {
                string In(List<long> ids) => "(" + string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ")";
                using (var tr = tk.Database.BeginTransaction(DatabaseChangedAction.Edited))
                {
                    if (objects.Count > 0)
                    {
                        Exec(tr, $"DELETE FROM TK_ObjectResource WHERE ObjectStepID IN (SELECT ID FROM TK_ObjectStep WHERE ObjectID IN {In(objects)})");
                        Exec(tr, $"DELETE FROM TK_ObjectStep WHERE ObjectID IN {In(objects)}");
                        Exec(tr, $"DELETE FROM TK_Object WHERE ID IN {In(objects)}");
                    }
                    if (items.Count > 0)
                    {
                        Exec(tr, $"DELETE FROM TK_StepResource WHERE StepID IN (SELECT ID FROM TK_Step WHERE ItemID IN {In(items)})");
                        Exec(tr, $"DELETE FROM TK_Step WHERE ItemID IN {In(items)}");
                        Exec(tr, $"DELETE FROM TK_Item WHERE ID IN {In(items)}");
                    }
                    if (groups.Count > 0) Exec(tr, $"DELETE FROM TK_ItemGroup WHERE ID IN {In(groups)}");
                    tr.Commit();
                }
                return "deleted " + what;
            });
        }

        /// Six significant digits: the database stores single-precision floats, so more digits are noise.
        static string Sig(double x) => x == 0 ? "0" : Math.Round(x, Math.Max(0, Math.Min(15, 5 - (int)Math.Floor(Math.Log10(Math.Abs(x)))))).ToString(CultureInfo.InvariantCulture);

        static string Csv(object v)
        {
            var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            return s == "-" ? "" : s.IndexOfAny(new[] { ',', '"', ';', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static void ExportCsv(Ctx c, Args a, Out o)
        {
            var level = a.Str("level");
            var by = a.Str("by");
            if (!IsSetUp(Tk(c))) throw new NwError("not_found", "Quantification is not set up in this document", "take off items first with qto.item.add and qto.takeoff");
            var rows = Rows(c, level, by, null);
            var path = ExportOps.OutPath(a, "qto", "csv");
            var sb = new StringBuilder();
            sb.Append(string.Join(",", Header(level, by).Skip(1))).Append("\r\n");
            foreach (var r in rows.Select((r, i) => Cells(r, i, level, Sig).Skip(1)))
                sb.Append(string.Join(",", r.Select(Csv))).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            o.Line($"qto_csv {path} {rows.Count:#,0} rows ({level})");
        }
    }
}
