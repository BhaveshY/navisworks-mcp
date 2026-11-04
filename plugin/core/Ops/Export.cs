using System;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
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
                path = Path.Combine(dir, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}-{Pid}-{Interlocked.Increment(ref imageSeq)}.{ext}");
            }
            return ValidatePath(path, ext);
        }

        public static string ValidatePath(string path, string ext)
        {
            try
            {
                if (!Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3) throw new NwError("bad_args", $"path \"{path}\" is not absolute", "pass a full path such as C:/Coord/out." + ext);
                path = Path.GetFullPath(path);
                if (path.Length >= 260) throw new NwError("bad_args", "output path exceeds Navisworks' 259-character limit", "choose a shorter output folder and filename");
                if (!string.Equals(Path.GetExtension(path), "." + ext, StringComparison.OrdinalIgnoreCase))
                    throw new NwError("bad_args", $"output path must end in .{ext}", $"change the output filename extension to .{ext}");
                foreach (var part in path.Substring(Path.GetPathRoot(path).Length).Split(Path.DirectorySeparatorChar))
                {
                    var stem = part.Split('.')[0].ToUpperInvariant();
                    if (part.Length == 0 || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                        new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$"))
                        throw new NwError("bad_args", "output path contains an invalid Windows filename", "choose a filename without reserved names, invalid characters or trailing dots/spaces");
                }
                if (Directory.Exists(path)) throw new NwError("bad_args", "output path names a directory", "choose a file path in that directory");
                if (File.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("the destination file is read-only");
                    using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None)) { }
                }
                var dir = Path.GetDirectoryName(path);
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".nmcp-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return path;
            }
            catch (Exception e) when (FileError(e)) { throw OutputError(path, e); }
        }

        static bool FileError(Exception e) => e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException;

        public static NwError OutputError(string path, Exception e) => new NwError("bad_args", $"cannot write \"{path}\": {e.Message}",
            e is UnauthorizedAccessException ? "choose a writable output folder/file or remove its read-only restriction" :
            e is PathTooLongException ? "choose a shorter output folder and filename" :
            e is IOException ? "close any application locking the destination, or choose a new writable output path" : "choose a valid absolute Windows output path");

        /// Produce a sibling file before replacing an existing output, so a failed renderer leaves its bytes intact.
        public static void Write(string path, Action<string> write)
        {
            var temp = Path.Combine(Path.GetDirectoryName(path), ".nmcp-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            if (temp.Length >= 260) throw new NwError("bad_args", "output folder is too long for a temporary output file", "choose a shorter output folder");
            try
            {
                write(temp);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (Exception e) when (FileError(e)) { throw OutputError(path, e); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
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
                Write(path, temp => bmp.Save(temp, ImageFormat.Png));
            }
            o.Line($"image {path} {a.Int("w")}x{a.Int("h")}" + (view != null ? $" of \"{a.Str("view")}\"" : ""));
            if (a.Bool("inline")) o.Png = File.ReadAllBytes(path);
        }
    }
}
