using System.Linq;
using Autodesk.Navisworks.Api;

namespace NavisworksMcp
{
    static class ModelOps
    {
        public static readonly Op[] All =
        {
            Catalog.Apply("select", "items:target; mode:enum(set|add|remove)=set", Select, "change the current selection", "{items:\"r1\"}"),
            Catalog.Apply("color", "items:target rgb:color; temp:bool=false", Color,
                "override colour; permanent (saved in NWF/NWD) unless temp", "{items:\"r1\", rgb:\"#ff0000\"}"),
            Catalog.Apply("transparency", "items:target pct:num; temp:bool=false", Transparency, "override transparency 0-100", "{items:\"r1\", pct:70}"),
            Catalog.Apply("hide", "items:target", (c, a) => Hidden(c, a, true), "hide items", "{items:\"r1\"}"),
            Catalog.Apply("show", "items:target", (c, a) => Hidden(c, a, false), "unhide items", "{items:\"r1\"}"),
            Catalog.Apply("isolate", "items:target", Isolate, "hide everything except items", "{items:\"r1\"}"),
            Catalog.Apply("reset", "; items:target what:enum(all|appearance|hidden)=all", Reset,
                "reset overrides (default: whole model)", "{what:\"appearance\"}"),
            Catalog.Apply("undo", "; steps:int=1", (c, a) => History(c, a, true), "undo the last steps (must be the only op)", "{}").Alone(),
            Catalog.Apply("redo", "; steps:int=1", (c, a) => History(c, a, false), "redo (must be the only op)", "{}").Alone(),
        };

        static string Count(ModelItemCollection items) => $"{items.Count:#,0} items";

        static Step Select(Ctx c, Args a)
        {
            var items = a.Items("items");
            var mode = a.Str("mode");
            return new Step($"{mode} selection to {Count(items)}", () =>
            {
                var sel = c.Doc.CurrentSelection;
                if (mode == "set") sel.CopyFrom(items);
                else if (mode == "add") sel.AddRange(items);
                else { var keep = new ModelItemCollection(sel.SelectedItems); foreach (var x in items) keep.Remove(x); sel.CopyFrom(keep); }
                return $"selection now {sel.SelectedItems.Count:#,0} items";
            });
        }

        static Step Color(Ctx c, Args a)
        {
            var items = a.Items("items");
            var color = a.Color("rgb");
            var temp = a.Bool("temp");
            var kind = temp ? "temporary" : "permanent";
            return new Step($"colour {Units.Hex(color)} on {Count(items)} ({kind})", () =>
            {
                if (temp) c.Doc.Models.OverrideTemporaryColor(items, color);
                else c.Doc.Models.OverridePermanentColor(items, color);
                return $"coloured {Count(items)} {Units.Hex(color)} ({kind})";
            });
        }

        static Step Transparency(Ctx c, Args a)
        {
            var items = a.Items("items");
            var pct = a.Num("pct");
            if (pct < 0 || pct > 100) throw new NwError("bad_args", "pct must be 0-100", "pass pct:70 for 70% transparent");
            var temp = a.Bool("temp");
            return new Step($"transparency {pct}% on {Count(items)}", () =>
            {
                if (temp) c.Doc.Models.OverrideTemporaryTransparency(items, pct / 100);
                else c.Doc.Models.OverridePermanentTransparency(items, pct / 100);
                return $"transparency {pct}% on {Count(items)}";
            });
        }

        static Step Hidden(Ctx c, Args a, bool hide)
        {
            var items = a.Items("items");
            var verb = hide ? "hide" : "show";
            return new Step($"{verb} {Count(items)}", () =>
            {
                c.Doc.Models.SetHidden(items, hide);
                return (hide ? "hid " : "showed ") + Count(items);
            });
        }

        static Step Isolate(Ctx c, Args a)
        {
            var items = a.Items("items");
            return new Step($"isolate {Count(items)}", () =>
            {
                var others = new ModelItemCollection(items);
                others.Invert(c.Doc);
                c.Doc.Models.ResetAllHidden();
                c.Doc.Models.SetHidden(others, true);
                return $"isolated {Count(items)}";
            });
        }

        static Step Reset(Ctx c, Args a)
        {
            var what = a.Str("what");
            var items = a.Has("items") ? a.Items("items") : null;
            var scope = items == null ? "whole model" : Count(items);
            return new Step($"reset {what} on {scope}", () =>
            {
                if (what != "hidden")
                {
                    if (items == null) { c.Doc.Models.ResetAllPermanentMaterials(); c.Doc.Models.ResetAllTemporaryMaterials(); }
                    else { c.Doc.Models.ResetPermanentMaterials(items); c.Doc.Models.ResetTemporaryMaterials(items); }
                }
                if (what != "appearance")
                {
                    if (items == null) c.Doc.Models.ResetAllHidden();
                    else c.Doc.Models.SetHidden(items, false);
                }
                return $"reset {what} on {scope}";
            });
        }

        static Step History(Ctx c, Args a, bool undo)
        {
            var steps = a.Int("steps");
            return new Step($"{(undo ? "undo" : "redo")} {steps} step(s), next is \"{(undo ? c.Doc.NextUndo : c.Doc.NextRedo)}\"", () =>
            {
                var names = Enumerable.Range(0, steps).Select(_ =>
                {
                    var name = undo ? c.Doc.NextUndo : c.Doc.NextRedo;
                    if (string.IsNullOrEmpty(name) || !(undo ? c.Doc.TryUndo() : c.Doc.TryRedo())) throw new NwError("not_found", "nothing left to " + (undo ? "undo" : "redo"), "call status");
                    return name;
                }).ToList();
                return (undo ? "undid " : "redid ") + string.Join(", ", names.Select(n => "\"" + n + "\""));
            });
        }
    }
}
