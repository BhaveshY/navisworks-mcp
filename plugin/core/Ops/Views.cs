using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Navisworks.Api;
using Newtonsoft.Json.Linq;

namespace NavisworksMcp
{
    /// Saved viewpoints, addressed by a folder path such as "QA/Level 1", plus camera and sectioning of the current view.
    static class Views
    {
        public static SavedViewpoint Find(Document doc, string path) =>
            Sets.Lookup(doc.SavedViewpoints.RootItem, path) as SavedViewpoint
            ?? throw new NwError("not_found", $"no viewpoint \"{path}\"", "list viewpoints with read {what:\"viewpoints\"} and use the exact path");

        /// Applies camera, hide/colour overrides and section. Setting CurrentSavedViewpoint alone leaves the section of the previous view.
        public static void Show(Document doc, SavedViewpoint vp)
        {
            doc.SavedViewpoints.CurrentSavedViewpoint = vp;
            doc.ActiveView.CopyViewpointFrom(vp.Viewpoint, ViewChange.JumpCut);
        }

        public static void Zoom(Document doc, BoundingBox3D box)
        {
            var vp = doc.CurrentViewpoint.CreateCopy();
            vp.ZoomBox(box);
            doc.CurrentViewpoint.CopyFrom(vp);
        }

        public static BoundingBox3D Box(ModelItemCollection items)
        {
            var box = items.BoundingBox();
            if (box == null || box.IsEmpty) throw new NwError("bad_args", $"the {items.Count:#,0} items have no geometry", "pass items that have geometry");
            return box;
        }
    }

    /// The ClipPlaneSet JSON that View.Get/SetClippingPlanes speak, in document units:
    /// planes mode {"Planes":[{"Normal":[x,y,z],"Distance":d,"Enabled":true}]} keeps points with normal·p >= d;
    /// box mode {"OrientedBox":{"Box":[[min],[max]],"Rotation":[0,0,0]}} keeps what is inside the box.
    static class Section
    {
        static readonly Regex AxisRx = new Regex(@"^\s*([xyz])\s*([<>])\s*(-?[\d.]+)\s*$", RegexOptions.IgnoreCase);

        public static JObject Off() => Set(new JObject { ["Planes"] = new JArray(), ["Linked"] = false }, false);

        static JObject Set(JObject body, bool enabled)
        {
            var o = new JObject { ["Type"] = "ClipPlaneSet", ["Version"] = 1 };
            foreach (var p in body.Properties()) o.Add(p.Name, p.Value);
            o["Enabled"] = enabled;
            return o;
        }

        public static JObject Box(BoundingBox3D box, double pad) => Set(new JObject
        {
            ["OrientedBox"] = new JObject
            {
                ["Type"] = "OrientedBox3D", ["Version"] = 1,
                ["Box"] = new JArray(new JArray(box.Min.X - pad, box.Min.Y - pad, box.Min.Z - pad), new JArray(box.Max.X + pad, box.Max.Y + pad, box.Max.Z + pad)),
                ["Rotation"] = new JArray(0, 0, 0),
            },
        }, true);

        /// Each plane is "z<1500" (keep below 1500 mm) or {normal:[x,y,z], point:[x,y,z]} (keep the side the normal points to), mm.
        public static JObject Planes(Document doc, JToken planes)
        {
            var list = planes as JArray ?? new JArray(planes);
            if (list.Count == 0 || list.Count > 6) throw Bad("planes needs 1 to 6 planes");
            var json = new JArray(list.Select(t => Plane(doc, t)));
            return Set(new JObject { ["Planes"] = json, ["Linked"] = false }, true);
        }

        static JObject Plane(Document doc, JToken t)
        {
            double[] n;
            double d;
            Match m;
            if (t.Type == JTokenType.String && (m = AxisRx.Match((string)t)).Success)
            {
                var axis = "xyz".IndexOf(char.ToLowerInvariant(m.Groups[1].Value[0]));
                var sign = m.Groups[2].Value == ">" ? 1 : -1;
                n = new double[3];
                n[axis] = sign;
                d = sign * Units.FromMm(doc, double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (t is JObject o && o["normal"] is JArray na && na.Count == 3 && o["point"] != null)
            {
                n = na.Select(x => (double)x).ToArray();
                var len = Math.Sqrt(n.Sum(x => x * x));
                if (len == 0) throw Bad("normal must not be [0,0,0]");
                n = n.Select(x => x / len).ToArray();
                var p = Units.ParsePoint(doc, o["point"]);
                d = n[0] * p.X + n[1] * p.Y + n[2] * p.Z;
            }
            else throw Bad($"cannot read plane {t.ToString(Newtonsoft.Json.Formatting.None)}");
            return new JObject { ["Type"] = "ClipPlane", ["Version"] = 1, ["Normal"] = new JArray(n), ["Distance"] = d, ["Enabled"] = true };
        }

        /// mm without digit grouping, so coordinates can be joined with commas.
        static string Mm(Document doc, double v) => Units.Mm(doc, v).Replace(",", "");

        static NwError Bad(string message) => new NwError("bad_args", message,
            "pass planes:[\"z<1500\"] (keep below 1500 mm), \"x>2000\", or {normal:[1,1,0], point:[5000,0,0]} (keep the side the normal points to); up to 6");

        /// One line for the agent: "off", "planes z<1500 · x>2000" or "box 0,0,0 .. 5000,3000,1500 mm".
        public static string Describe(Document doc, string json)
        {
            var o = JObject.Parse(json);
            if (!(bool?)o["Enabled"] ?? true) return "off";
            if (o["OrientedBox"] is JObject box)
            {
                var b = (JArray)box["Box"];
                Func<JToken, string> pt = p => string.Join(",", p.Select(v => Mm(doc, (double)v)));
                var rot = box["Rotation"]?.Any(v => (double)v != 0) == true ? " rotated " + string.Join(",", box["Rotation"].Select(v => Out.Num((double)v * 180 / Math.PI))) + " deg" : "";
                return $"box {pt(b[0])} .. {pt(b[1])} mm{rot}";
            }
            var planes = ((JArray)o["Planes"] ?? new JArray()).Where(p => (bool?)p["Enabled"] ?? true).ToList();
            if (planes.Count == 0) return "off";
            return "planes " + string.Join(" · ", planes.Select(p =>
            {
                var n = p["Normal"].Select(v => (double)v).ToArray();
                var d = (double)p["Distance"];
                var axis = Array.FindIndex(n, v => Math.Abs(Math.Abs(v) - 1) < 1e-9);
                if (axis >= 0) return $"{"xyz"[axis]}{(n[axis] > 0 ? ">" : "<")}{Mm(doc, d * n[axis])}";
                return $"normal {string.Join(",", n.Select(v => Out.Num(v)))} d={Mm(doc, d)}mm";
            }));
        }
    }

    static class ViewOps
    {
        public static readonly Op[] All =
        {
            Catalog.Read("read", "viewpoints", "; folder:str", List, "saved viewpoints: path, kind, saved overrides, section", "{folder:\"QA\"}"),
            Catalog.Read("read", "section", "", (c, a, o) => o.Line("section " + Section.Describe(c.Doc, c.Doc.ActiveView.GetClippingPlanes())),
                "sectioning of the current view", "{}"),
            Catalog.Apply("viewpoint.save", "name:str; items:target", Save,
                "save the current view (camera, section, hide and colour overrides) as a viewpoint; items zooms to them first. Replaces a viewpoint with the same path",
                "{name:\"QA/Level 1\", items:\"r1\"}"),
            Catalog.Apply("viewpoint.show", "name:str", ShowOp, "go to a saved viewpoint, with its overrides and section", "{name:\"QA/Level 1\"}"),
            Catalog.Apply("viewpoint.rename", "name:str to:str", Rename, "rename a viewpoint or folder (to is the new leaf name)", "{name:\"QA/Level 1\", to:\"Level 1 (old)\"}"),
            Catalog.Apply("viewpoint.delete", "name:str", Delete, "delete a viewpoint or folder", "{name:\"QA/Level 1\"}"),
            Catalog.Apply("zoom", "items:target", Zoom, "fit the view to items", "{items:\"r1\"}"),
            Catalog.Apply("section", "; planes:json box:target pad:len=0 off:bool=false", SectionOp,
                "section the view: planes (\"z<1500\" keeps below 1500 mm, {normal, point} for oblique), a box around items grown by pad mm, or off",
                "{planes:[\"z<1500\", \"x>2000\"]}"),
        };

        static void List(Ctx c, Args a, Out o)
        {
            GroupItem root = c.Doc.SavedViewpoints.RootItem;
            if (a.Has("folder")) root = Sets.Lookup(root, a.Str("folder")) as GroupItem ?? throw new NwError("not_found", $"no folder \"{a.Str("folder")}\"", "call read {what:\"viewpoints\"}");
            var rows = Sets.Walk(root, a.Has("folder") ? a.Str("folder") + "/" : "").ToList();
            o.Line($"viewpoints {rows.Count(r => r.item is SavedViewpoint):#,0} (exact)");
            o.Table(new[] { "path", "kind", "overrides", "section" }, rows.Select(r =>
            {
                if (!(r.item is SavedViewpoint vp)) return new object[] { r.path, r.item is SavedViewpointAnimation ? "animation" : "folder", "-", "-" };
                var overrides = new[] { vp.ContainsVisibilityOverrides ? "hide" : null, vp.ContainsAppearanceOverrides ? "colour" : null }.Where(x => x != null);
                return new object[] { r.path, "view", string.Join("+", overrides), Compat.Clip(vp.Viewpoint) };
            }));
        }

        static Step Save(Ctx c, Args a)
        {
            var name = a.Str("name");
            var items = a.Has("items") ? a.Items("items") : null;
            var box = items == null ? null : Views.Box(items);
            return new Step($"save viewpoint \"{name}\"" + (items == null ? "" : $" zoomed to {items.Count:#,0} items"), () =>
            {
                if (box != null) Views.Zoom(c.Doc, box);
                var vps = c.Doc.SavedViewpoints;
                var folder = Sets.Folder(() => vps.RootItem, name, vps.AddCopy);
                var leaf = Sets.Leaf(name);
                // CaptureRuntimeOverrides holds the hide and colour overrides but no camera; ReplaceFromCurrentView adds camera and section.
                var vp = vps.CaptureRuntimeOverrides();
                vp.DisplayName = leaf;
                var existing = folder.Children.FirstOrDefault(x => x.DisplayName == leaf);
                if (existing != null) vps.ReplaceWithCopy(folder, folder.Children.IndexOf(existing), vp);
                else vps.AddCopy(folder, vp);
                var saved = (SavedViewpoint)folder.Children.First(x => x.DisplayName == leaf);
                vps.ReplaceFromCurrentView(saved);
                saved = Views.Find(c.Doc, name);
                return $"{(existing != null ? "replaced" : "saved")} viewpoint \"{name}\" (section {Compat.Clip(saved.Viewpoint)}, overrides {(saved.ContainsVisibilityOverrides || saved.ContainsAppearanceOverrides ? "kept" : "none")})";
            });
        }

        static Step ShowOp(Ctx c, Args a)
        {
            var name = a.Str("name");
            var vp = Views.Find(c.Doc, name);
            return new Step($"show viewpoint \"{name}\"", () =>
            {
                Views.Show(c.Doc, vp);
                return $"showing \"{name}\", section {Section.Describe(c.Doc, c.Doc.ActiveView.GetClippingPlanes())}";
            });
        }

        static SavedItem Lookup(Ctx c, string name) =>
            Sets.Lookup(c.Doc.SavedViewpoints.RootItem, name) ?? throw new NwError("not_found", $"no viewpoint \"{name}\"", "call read {what:\"viewpoints\"}");

        static Step Rename(Ctx c, Args a)
        {
            var name = a.Str("name");
            var item = Lookup(c, name);
            var to = a.Str("to");
            if (to.Contains("/")) throw new NwError("bad_args", "to is the new leaf name and cannot contain /", $"pass to:\"{Sets.Leaf(to)}\"");
            return new Step($"rename viewpoint \"{name}\" to \"{to}\"", () =>
            {
                c.Doc.SavedViewpoints.EditDisplayName(item, to);
                return $"renamed viewpoint \"{name}\" to \"{to}\"";
            });
        }

        static Step Delete(Ctx c, Args a)
        {
            var name = a.Str("name");
            var item = Lookup(c, name);
            return new Step($"delete viewpoint \"{name}\"", () =>
            {
                c.Doc.SavedViewpoints.Remove(item.Parent, item);
                return $"deleted viewpoint \"{name}\"";
            });
        }

        static Step Zoom(Ctx c, Args a)
        {
            var items = a.Items("items");
            var box = Views.Box(items);
            return new Step($"zoom to {items.Count:#,0} items", () =>
            {
                Views.Zoom(c.Doc, box);
                return $"zoomed to {items.Count:#,0} items ({Units.Box(c.Doc, box)} mm)";
            });
        }

        static Step SectionOp(Ctx c, Args a)
        {
            var given = new[] { "planes", "box" }.Where(a.Has).ToList();
            if (a.Bool("off")) given.Add("off");
            if (given.Count != 1) throw new NwError("bad_args", "section needs exactly one of planes, box or off:true", "e.g. {op:\"section\", planes:[\"z<1500\"]} or {op:\"section\", box:\"r1\", pad:500} or {op:\"section\", off:true}");
            JObject json;
            if (given[0] == "off") json = Section.Off();
            else if (given[0] == "planes") json = Section.Planes(c.Doc, a.Json("planes"));
            else json = Section.Box(Views.Box(a.Items("box")), a.Len("pad"));
            var text = json.ToString(Newtonsoft.Json.Formatting.None);
            var plan = Section.Describe(c.Doc, text);
            return new Step("section " + plan, () =>
            {
                c.Doc.ActiveView.SetClippingPlanes(text);
                return "section " + Section.Describe(c.Doc, c.Doc.ActiveView.GetClippingPlanes());
            });
        }
    }
}
