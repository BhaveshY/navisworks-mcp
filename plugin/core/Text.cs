using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Newtonsoft.Json.Linq;

namespace NavisworksMcp
{
    /// What one request may touch. Doc is the active document, null only for pipe-thread tools.
    sealed class Ctx
    {
        public readonly Host Host;
        public readonly Job Job;
        public readonly Document Doc;
        public bool Preview;
        public int BatchIndex;
        public readonly Dictionary<int, Handle> Made = new Dictionary<int, Handle>();

        public Ctx(Host host, Job job)
        {
            Host = host;
            Job = job;
            if (job != null) Doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
        }

        public void CheckCancelled()
        {
            if (Job != null && Job.Cancelled) throw new NwError("cancelled", "cancelled", "repeat the call if you still need it");
        }

        /// Stores a result as a handle and remembers it as the output of the current batch op ($n).
        public Handle Keep(Handle h)
        {
            Host.Handles.Add(h);
            Made[BatchIndex] = h;
            return h;
        }
    }

    sealed class Reply
    {
        public string Text, Code, Message, Next, JobId;
        public byte[] Png { get; set; }

        public static Reply Error(string code, string message, string next) => new Reply { Code = code, Message = message, Next = next };

        public static Reply Running(Job job) => new Reply
        {
            JobId = job.Id,
            Text = $"{job.Id} running {job.Label} for {job.Clock.Elapsed.TotalSeconds:0}s\nnext: job {{id:\"{job.Id}\", wait:60000}}. Do not repeat the call.",
        };

        public JObject ToWire(long id)
        {
            var o = new JObject { ["id"] = id, ["ok"] = Code == null };
            if (Code != null) { o["code"] = Out.Clip(Code, 100); o["message"] = Out.Clip(Message, 24000); o["next"] = Out.Clip(Next, 5000); }
            else o["text"] = Out.Clip(Text, Out.Budget);
            if (JobId != null) o["job"] = JobId;
            if (Png != null) o["png"] = Convert.ToBase64String(Png);
            return o;
        }
    }

    /// Builds the compact text the agent reads: lines, pipe tables, and a `more:` line when a list is cut.
    sealed class Out
    {
        public const int Budget = 30000;
        readonly StringBuilder sb = new StringBuilder();
        readonly Ctx context;
        public bool Paging;
        public int TableCount;
        public byte[] Png { get; set; }
        public Out(Ctx context = null) { this.context = context; }
        public static string Clip(string text, int length) => text == null || text.Length <= length ? text : text.Substring(0, length - 15) + " [text clipped]";

        public static int Start(int page, int limit, int offset = 0)
        {
            var n = (long)(page - 1) * limit + offset;
            if (n < 0 || n > int.MaxValue) throw new NwError("bad_args", "page offset is out of range", "start at page:1 and follow more:");
            return (int)n;
        }

        public Out Line(string text)
        {
            sb.Append(text).Append('\n');
            return this;
        }

        public Out Table(IEnumerable<string> header, IEnumerable<IEnumerable<object>> rows)
        {
            var headings = header.ToArray();
            Line(string.Join("|", headings));
            TableCount = 0;
            foreach (var r in rows)
            {
                var cells = r.Select(Cell).ToArray();
                // A single wide cell must not prevent paging from advancing by at least one row.
                var line = string.Join("|", cells.Select((x, i) => i < headings.Length && (headings[i] == "#" || headings[i].StartsWith("id", StringComparison.OrdinalIgnoreCase)) ? x : Clip(x, Math.Max(32, 12000 / Math.Max(1, cells.Length)))));
                if (Paging && sb.Length + line.Length > Budget - 6000) break;
                Line(line);
                TableCount++;
            }
            return this;
        }

        /// The exact call that returns the next page of a handle.
        public Out More(string what, Handle h, int page, int limit, string extra = "", int start = 0, int total = -1)
        {
            if (limit <= 0 || TableCount <= 0 || (long)start + TableCount >= (total < 0 ? h.Rows.Count : total)) return this;
            if (TableCount == limit && start % limit == 0 && start / limit + 2 <= 1000000)
                return Line($"more: read {{what:\"{what}\", in:\"{h.Name}\", page:{start / limit + 2}, limit:{limit}{extra}}}");
            return Line($"more: read {{what:\"{what}\", in:\"{h.Name}\", page:1, offset:{start + TableCount}, limit:{limit}{extra}}}");
        }

        public static string Cell(object v)
        {
            var s = v as string ?? (v is double d ? Num(d) : v is int i ? Num(i) : v?.ToString());
            return string.IsNullOrEmpty(s) ? "-" : s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
        }

        public static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "-";
            // Six significant digits, at most four decimals: 460.05 m3 stays 460.05, 12,345.678 mm becomes 12,345.7.
            var digits = v == 0 ? 0 : 5 - (int)Math.Floor(Math.Log10(Math.Abs(v)));
            var r = Math.Round(v, Math.Max(0, Math.Min(4, digits)));
            return r.ToString(r == Math.Floor(r) ? "#,0" : "#,0.####", CultureInfo.InvariantCulture);
        }

        /// Set by ops that hand back a finished reply as is, such as `job` returning another call's result.
        public Reply Reply;
        public string Text => sb.ToString().TrimEnd('\n');

        public Reply ToReply()
        {
            if (Reply != null) return Reply;
            var text = Text;
            if (text.Length > Budget && context?.Doc != null)
            {
                var chunks = new List<object>();
                while (text.Length > Budget - 1000)
                {
                    var end = text.LastIndexOf('\n', Budget - 1000);
                    if (end < 1) end = Budget - 1000;
                    chunks.Add(text.Substring(0, end));
                    text = text.Substring(end).TrimStart('\n');
                }
                chunks.Add(text);
                var h = context.Keep(new Handle { Kind = Kind.Text, Rows = chunks, Label = "output pages" });
                text = (string)chunks[0] + $"\nmore: read {{what:\"page\", in:\"{h.Name}\", offset:1}}";
            }
            return new Reply { Text = Clip(text, Budget), Png = Png };
        }
    }

    static class Units
    {
        static double ToMeters(Autodesk.Navisworks.Api.Units u)
        {
            switch (u)
            {
                case Autodesk.Navisworks.Api.Units.Meters: return 1;
                case Autodesk.Navisworks.Api.Units.Centimeters: return 0.01;
                case Autodesk.Navisworks.Api.Units.Millimeters: return 0.001;
                case Autodesk.Navisworks.Api.Units.Feet: return 0.3048;
                case Autodesk.Navisworks.Api.Units.Inches: return 0.0254;
                case Autodesk.Navisworks.Api.Units.Yards: return 0.9144;
                case Autodesk.Navisworks.Api.Units.Kilometers: return 1000;
                case Autodesk.Navisworks.Api.Units.Miles: return 1609.344;
                case Autodesk.Navisworks.Api.Units.Micrometers: return 1e-6;
                case Autodesk.Navisworks.Api.Units.Mils: return 0.0000254;
                case Autodesk.Navisworks.Api.Units.Microinches: return 0.0000000254;
                default: throw new ArgumentOutOfRangeException(nameof(u));
            }
        }

        public static double Meters(Document doc) => ToMeters(doc.Units);
        public static double ToMm(Document doc, double v) => v * Meters(doc) * 1000;
        public static double FromMm(Document doc, double mm) => mm / 1000 / Meters(doc);
        public static string Mm(Document doc, double v) => Out.Num(ToMm(doc, v));
        /// Coordinates carry no digit grouping, because the comma separates them.
        public static string Point(Document doc, Point3D p) => string.Join(",", new[] { p.X, p.Y, p.Z }.Select(v => Math.Round(ToMm(doc, v)).ToString(CultureInfo.InvariantCulture)));
        public static Point3D ParsePoint(Document doc, JToken t)
        {
            var a = t as JArray;
            if (a == null || a.Count != 3) throw new NwError("bad_args", "a point is [x,y,z] in mm", "pass e.g. [1000,2000,0]");
            return new Point3D(FromMm(doc, (double)a[0]), FromMm(doc, (double)a[1]), FromMm(doc, (double)a[2]));
        }

        public static string Box(Document doc, BoundingBox3D b) => b == null || b.IsEmpty ? "-" : Point(doc, b.Min) + " .. " + Point(doc, b.Max);

        /// Property values keep Navisworks' own display text except lengths, areas and volumes, which are normalised to mm, m2 and m3.
        public static string Value(Document doc, VariantData v)
        {
            if (v == null) return "-";
            switch (v.DataType)
            {
                case VariantDataType.None: return "-";
                case VariantDataType.DoubleLength: return Out.Num(ToMm(doc, v.ToDoubleLength())) + " mm";
                case VariantDataType.DoubleArea: return Out.Num(v.ToDoubleArea() * Math.Pow(Meters(doc), 2)) + " m2";
                case VariantDataType.DoubleVolume: return Out.Num(v.ToDoubleVolume() * Math.Pow(Meters(doc), 3)) + " m3";
                case VariantDataType.DoubleAngle: return Out.Num(v.ToDoubleAngle() * 180 / Math.PI) + " deg";
                case VariantDataType.Double: return Out.Num(v.ToDouble());
                case VariantDataType.Int32: return v.ToInt32().ToString(CultureInfo.InvariantCulture);
                case VariantDataType.Boolean: return v.ToBoolean() ? "true" : "false";
                case VariantDataType.DateTime: return v.ToDateTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                case VariantDataType.DisplayString: return v.ToDisplayString();
                case VariantDataType.IdentifierString: return v.ToIdentifierString();
                case VariantDataType.NamedConstant: return v.ToNamedConstant().DisplayName;
                case VariantDataType.Point3D: return Point(doc, v.ToPoint3D());
                default: return v.ToString();
            }
        }

        /// The exact identity of a value, for grouping. Doubles keep full precision in document units.
        public static string Key(VariantData v)
        {
            if (v == null || v.IsNone) return "-";
            if (v.IsAnyDouble) return v.DataType + ":" + v.ToAnyDouble().ToString("R", CultureInfo.InvariantCulture);
            if (v.IsDisplayString) return v.ToDisplayString();
            return v.DataType + ":" + v;
        }

        static readonly Dictionary<string, string> Named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = "#ff0000", ["green"] = "#00c000", ["blue"] = "#0000ff", ["yellow"] = "#ffff00", ["orange"] = "#ff8000",
            ["magenta"] = "#ff00ff", ["cyan"] = "#00ffff", ["white"] = "#ffffff", ["black"] = "#000000", ["grey"] = "#808080", ["gray"] = "#808080",
        };

        public static Color ParseColor(string s)
        {
            if (Named.TryGetValue(s, out var hex)) s = hex;
            if (s.Length == 7 && s[0] == '#' && int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return Color.FromByteRGB((byte)(rgb >> 16), (byte)(rgb >> 8 & 255), (byte)(rgb & 255));
            throw new NwError("bad_args", $"\"{s}\" is not a colour", "use #rrggbb or one of " + string.Join(", ", Named.Keys));
        }

        public static string Hex(Color c) => "#" + string.Concat(Enumerable.Range(0, 3).Select(i => c.GetClampedByteValue(i).ToString("x2")));
    }
}
