using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using Autodesk.Navisworks.Api;

namespace NavisworksMcp
{
    static class ExportOps
    {
        static int imageSeq;
        static readonly int Pid = System.Diagnostics.Process.GetCurrentProcess().Id;

        public static readonly Op[] All =
        {
            Catalog.Read("export", "image", "; path:path view:str w:int=1280 h:int=720 style:enum(scene|overlay|raytrace)=scene inline:bool=false", Image,
                "render the current view, or saved viewpoint `view` (which stays shown), to PNG (default path in %TEMP%\\navisworks-mcp); inline:true also returns the picture",
                "{view:\"QA/Level 1\", w:1600, h:900, inline:true}"),
        };

        /// Absolute output path for a file the agent asked for, or a fresh temp path. The pid keeps the names of Navisworks instances that export in the same second apart.
        public static string OutPath(Args a, string stem, string ext)
        {
            var path = a.Str("path");
            if (string.IsNullOrEmpty(path))
            {
                var dir = Path.Combine(Path.GetTempPath(), "navisworks-mcp");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}-{Pid}-{Interlocked.Increment(ref imageSeq)}.{ext}");
            }
            if (!Path.IsPathRooted(path)) throw new NwError("bad_args", $"path \"{path}\" is not absolute", "pass a full path such as C:/Coord/out." + ext);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            return Path.GetFullPath(path);
        }

        static void Image(Ctx c, Args a, Out o)
        {
            var style = a.Str("style") == "raytrace" ? ImageGenerationStyle.SceneUsingRayTrace : a.Str("style") == "overlay" ? ImageGenerationStyle.ScenePlusOverlay : ImageGenerationStyle.Scene;
            var view = a.Has("view") ? Views.Find(c.Doc, a.Str("view")) : null;
            var path = OutPath(a, "image", "png");
            if (view != null) Views.Show(c.Doc, view);
            using (var bmp = c.Doc.ActiveView.GenerateImage(style, a.Int("w"), a.Int("h"), true))
            {
                if (bmp == null) throw new NwError("failed", "Navisworks returned no image (the view may be minimised)", "restore the Navisworks window, then repeat");
                bmp.Save(path, ImageFormat.Png);
            }
            o.Line($"image {path} {a.Int("w")}x{a.Int("h")}" + (view != null ? $" of \"{a.Str("view")}\"" : ""));
            if (a.Bool("inline")) o.Png = File.ReadAllBytes(path);
        }
    }
}
