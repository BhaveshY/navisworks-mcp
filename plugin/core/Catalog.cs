using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Autodesk.Navisworks.Api;
using Newtonsoft.Json.Linq;

namespace NavisworksMcp
{
    delegate void ReadHandler(Ctx c, Args a, Out o);
    delegate Step ApplyHandler(Ctx c, Args a);

    /// A prepared write. Plan describes it without side effects; Apply performs it and returns the result line.
    sealed class Step
    {
        public readonly string Plan;
        public readonly Func<string> Apply;
        public Step(string plan, Func<string> apply) { Plan = plan; Apply = apply; }
    }

    sealed class Param
    {
        public string Name, Type, Default;
        public bool Required;
        public string[] Choices;
    }

    sealed class Op
    {
        public string Tool, Name, Signature, Doc, Example;
        public Param[] Params;
        public ReadHandler Read;
        public ApplyHandler Apply;
        public bool NeedsDocument = true;
        public bool Standalone;
        /// Marks an op that must be the only op in its apply call and runs outside a transaction (undo, open, save).
        public Op Alone() { Standalone = true; return this; }
        public string Line => (Tool == Name ? "" : Name + " ") + Signature.Trim() + " : " + Doc + (Standalone ? "; must be the only op in its apply call" : "");
    }

    sealed class NwError : Exception
    {
        public readonly string Code, Next;
        public NwError(string code, string message, string next) : base(message) { Code = code; Next = next; }
    }

    static class Catalog
    {
        /// The one list of tools. A tool whose ops share its name takes the op's params directly;
        /// read selects an op with `what`, apply runs a list of `ops`, export runs a list of `out`.
        static readonly Dictionary<string, string> Tools = new Dictionary<string, string>
        {
            ["status"] = "Instance, document, models, units, selection and running jobs. Call first.",
            ["read"] = "Read anything; never changes the model. Lists return a handle r# with an exact total.",
            ["apply"] = "Change the model or view: compatible ops run in order as ONE undo step; preview:true only plans. Ops marked only op must run alone: open, append, merge, refresh, model.remove, undo, redo, tl.simulate. \"$0\" is the handle made by op 0.",
            ["export"] = "Write files: images, NWD/NWF, reports, CSV. Several outputs per call, run in order.",
            ["job"] = "Wait for or cancel a job j# that outlived its call.",
            ["script"] = "Run C# 5 against the Navisworks API for anything the other tools lack. The code is the body of `object Run(Nw nw)`; nw.Doc, nw.Items(target), nw.Handle(items) -> r#, nw.Mm(length), nw.Out.",
            ["help"] = "Signature and a working example for an op.",
        };

        public static readonly Op[] Ops = new[]
        {
            StatusOps.All, ReadOps.All, ModelOps.All, SetOps.All, ExportOps.All, ScriptOps.All,
            DocumentOps.All, ViewOps.All, PropOps.All, ClashOps.All, TimelinerOps.All, TakeoffOps.All,
        }.SelectMany(x => x).ToArray();

        static int writeSeq;
        static readonly Dictionary<string, Op> ByKey = Ops.ToDictionary(o => o.Tool + "/" + o.Name);

        public static readonly JObject Json = Build();

        public static bool RunsOnPipeThread(string tool) => tool == "job" || tool == "help" || tool == "status";

        static JObject Build()
        {
            var tools = new JArray();
            foreach (var t in Tools)
            {
                var ops = Ops.Where(o => o.Tool == t.Key).ToArray();
                if (ops.Length == 0) throw new InvalidOperationException("tool " + t.Key + " has no ops");
                var props = new JObject();
                var types = new Dictionary<string, string>();
                foreach (var p in ops.SelectMany(o => o.Params))
                {
                    var shape = p.Type + (p.Choices == null ? "" : "(" + string.Join("|", p.Choices) + ")");
                    if (types.TryGetValue(p.Name, out var seen))
                    {
                        if (seen != shape) throw new InvalidOperationException($"{t.Key}: param {p.Name} is {seen} in one op and {shape} in another");
                        continue;
                    }
                    types[p.Name] = shape;
                    props[p.Name] = Schema(p);
                }
                var desc = new StringBuilder(t.Value);
                JObject input;
                var selector = t.Key == "read" ? "what" : t.Key == "apply" ? "op" : t.Key == "export" ? "what" : null;
                if (selector == null)
                {
                    input = new JObject { ["type"] = "object", ["properties"] = props };
                    var required = ops[0].Params.Where(p => p.Required).Select(p => p.Name).ToArray();
                    if (required.Length > 0) input["required"] = new JArray(required);
                    desc.Append("\nargs: ").Append(ops[0].Signature.Trim());
                }
                else
                {
                    props[selector] = new JObject { ["type"] = "string", ["enum"] = new JArray(ops.Select(o => o.Name)) };
                    var entry = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = new JArray(selector) };
                    desc.Append("\n").Append(selector).Append(" (args before ; are required):");
                    foreach (var o in ops) desc.Append("\n  ").Append(o.Line);
                    if (t.Key == "read") input = entry;
                    else
                    {
                        var list = t.Key == "apply" ? "ops" : "out";
                        input = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject { [list] = new JObject { ["type"] = "array", ["items"] = entry } },
                            ["required"] = new JArray(list),
                        };
                        if (t.Key == "apply") input["properties"]["preview"] = new JObject { ["type"] = "boolean" };
                    }
                }
                tools.Add(new JObject { ["name"] = t.Key, ["description"] = desc.ToString(), ["inputSchema"] = input, ["readOnly"] = t.Key == "read" || t.Key == "status" || t.Key == "help" });
            }
            return new JObject { ["tools"] = tools, ["examples"] = new JObject(Ops.Select(o => new JProperty(o.Tool + "/" + o.Name, o.Example))) };
        }

        static JObject Schema(Param p)
        {
            switch (p.Type)
            {
                case "int": return new JObject { ["type"] = "integer" };
                case "num": case "len": return new JObject { ["type"] = "number" };
                case "bool": return new JObject { ["type"] = "boolean" };
                case "target": case "strs": return new JObject { ["type"] = new JArray("string", "array"), ["items"] = new JObject { ["type"] = "string" } };
                case "json": return new JObject();
                case "enum": return new JObject { ["type"] = "string", ["enum"] = new JArray(p.Choices) };
                default: return new JObject { ["type"] = "string" };
            }
        }

        /// Builds an op from a signature such as "items:target rgb:color; temp:bool=false".
        public static Op Read(string tool, string name, string signature, ReadHandler handler, string doc, string example) =>
            new Op { Tool = tool, Name = name, Signature = signature, Params = Parse(signature), Read = handler, Doc = doc, Example = example };

        public static Op Apply(string name, string signature, ApplyHandler handler, string doc, string example) =>
            new Op { Tool = "apply", Name = name, Signature = signature, Params = Parse(signature), Apply = handler, Doc = doc, Example = example };

        static Param[] Parse(string signature)
        {
            var parts = signature.Split(';');
            if (parts.Length > 2) throw new InvalidOperationException("two ';' in signature " + signature);
            return parts.SelectMany((part, i) => part.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(token =>
            {
                var colon = token.IndexOf(':');
                var p = new Param { Name = token.Substring(0, colon), Required = i == 0 };
                var type = token.Substring(colon + 1);
                var eq = type.IndexOf('=');
                if (eq >= 0) { p.Default = type.Substring(eq + 1); type = type.Substring(0, eq); }
                if (type.StartsWith("enum("))
                {
                    p.Choices = type.Substring(5, type.Length - 6).Split('|');
                    type = "enum";
                }
                if (!new[] { "str", "int", "num", "bool", "len", "color", "date", "path", "target", "where", "strs", "json", "enum" }.Contains(type))
                    throw new InvalidOperationException("unknown type " + type + " in " + signature);
                p.Type = type;
                return p;
            })).ToArray();
        }

        public static Reply Run(Ctx c, string tool, JObject args)
        {
            try
            {
                switch (tool)
                {
                    case "read": return RunRead(c, Find(tool, Selector(args, "what"), "what"), args);
                    case "apply": return RunApply(c, args);
                    case "export": return RunExport(c, args);
                    default:
                        if (!Tools.ContainsKey(tool)) throw new NwError("bad_args", "unknown tool " + tool, "use one of: " + string.Join(", ", Tools.Keys));
                        return RunRead(c, Find(tool, tool, "tool"), args);
                }
            }
            catch (NwError e) { return Reply.Error(e.Code, e.Message, e.Next); }
            catch (Exception e)
            {
                return Reply.Error("failed", e.GetType().Name + ": " + e.Message, "check the arguments with help {tool:\"" + tool + "\"}; if they are right, report this error");
            }
        }

        static string Selector(JObject args, string name)
        {
            if (args[name]?.Type != JTokenType.String)
                throw new NwError("bad_args", name + " must be a string", "help {tool:\"read\"} lists valid operations; supply the operation name as a string");
            return (string)args[name];
        }

        public static Op Find(string tool, string name, string selector)
        {
            if (name != null && ByKey.TryGetValue(tool + "/" + name, out var op)) return op;
            var names = Ops.Where(o => o.Tool == tool).Select(o => o.Name);
            throw new NwError("bad_args", $"{tool}: unknown {selector} \"{name}\"", "use one of: " + string.Join(", ", names));
        }

        static void RequireDocument(Ctx c, Op op)
        {
            if (op.NeedsDocument && c.Doc.IsClear)
                throw new NwError("no_document", "no model is open", "apply {ops:[{op:\"open\", path:\"C:/path/model.nwd\"}]}");
        }

        static Reply RunRead(Ctx c, Op op, JObject args)
        {
            var a = new Args(c, op, args);
            if (c.Doc != null) RequireDocument(c, op);
            var o = new Out(c) { Paging = op.Params.Any(p => p.Name == "page") };
            op.Read(c, a, o);
            return o.ToReply();
        }

        static Reply RunExport(Ctx c, JObject args)
        {
            var outs = Items(args, "out");
            var o = new Out(c);
            var bound = outs.Select(x => { var op = Find("export", Selector(x, "what"), "what"); return (op, args: new Args(c, op, x)); }).ToList();
            foreach (var (op, a) in bound)
            {
                RequireDocument(c, op);
                c.CheckCancelled();
                op.Read(c, a, o);
            }
            return o.ToReply();
        }

        static List<JObject> Items(JObject args, string list)
        {
            var items = args[list] as JArray;
            if (items == null || items.Count == 0) throw new NwError("bad_args", list + " must be a non-empty array", $"pass {list}:[{{...}}]");
            return items.Select(x => x as JObject ?? throw new NwError("bad_args", list + " entries must be objects", $"pass {list}:[{{...}}]")).ToList();
        }

        static Reply RunApply(Ctx c, JObject args)
        {
            var list = Items(args, "ops");
            if (args["preview"] != null && args["preview"].Type != JTokenType.Boolean)
                throw new NwError("bad_args", "preview must be boolean", "use preview:true to plan, or omit preview to apply");
            var preview = (bool?)args["preview"] ?? false;
            var bound = list.Select((x, i) =>
            {
                try { var op = Find("apply", Selector(x, "op"), "op"); return (op, args: new Args(c, op, x)); }
                catch (NwError e) { throw new NwError(e.Code, $"op {i}: {e.Message}", e.Next); }
            }).ToList();
            foreach (var (op, _) in bound) RequireDocument(c, op);
            var alone = bound.FirstOrDefault(b => b.op.Standalone).op;
            if (alone != null && bound.Count > 1)
                throw new NwError("bad_args", alone.Name + " must be the only op in its apply call", "send " + alone.Name + " in its own apply call");

            var o = new Out(c);
            if (preview)
            {
                c.Preview = true;
                for (var i = 0; i < bound.Count; i++)
                {
                    try { o.Line($"{i} would {bound[i].op.Apply(c, bound[i].args).Plan}"); }
                    catch (NwError e) when (i > 0) { o.Line($"{i} {bound[i].op.Name}: cannot plan before earlier ops apply ({e.Message})"); }
                    catch (NwError e) { throw new NwError(e.Code, $"op 0 ({bound[0].op.Name}): {e.Message}", e.Next); }
                }
                o.Line("nothing changed. Run the same ops without preview to apply.");
                return o.ToReply();
            }

            if (alone != null) return o.Line("0 " + bound[0].op.Apply(c, bound[0].args).Apply()).ToReply();

            // A unique label is how a failed batch recognises its own undo entry; op names alone repeat across calls.
            var label = $"MCP w{Interlocked.Increment(ref writeSeq)}: " + string.Join(", ", bound.Select(b => b.op.Name).Distinct());
            var done = 0;
            try
            {
                using (var tx = c.Doc.BeginTransaction(label))
                {
                    for (; done < bound.Count; done++)
                    {
                        c.CheckCancelled();
                        var step = bound[done].op.Apply(c, bound[done].args);
                        o.Line($"{done} {step.Apply()}");
                        c.BatchIndex++;
                    }
                    tx.Commit();
                }
            }
            catch (Exception e)
            {
                // The 2024 Transaction cannot roll back; the ended transaction is an undo entry if anything changed.
                var recorded = c.Doc.NextUndo == label;
                var undone = recorded && c.Doc.TryUndo();
                var err = e as NwError ?? new NwError("failed", e.GetType().Name + ": " + e.Message, "check op " + done + " with help {tool:\"apply\", op:\"" + bound[done].op.Name + "\"}");
                var state = !recorded ? "nothing changed" : undone ? "every change in this call was undone" : "changes in this call stayed applied; call apply {ops:[{op:\"undo\"}]}";
                return Reply.Error(err.Code, $"op {done} ({bound[done].op.Name}) failed: {err.Message}. {state}", err.Next);
            }
            if (c.Doc.NextUndo == label) o.Line("undo: apply {ops:[{op:\"undo\"}]}");
            return o.ToReply();
        }
    }

    /// Validated arguments for one op. Every getter reads a value the signature declared.
    sealed class Args
    {
        readonly Ctx c;
        readonly Op op;
        readonly JObject raw;

        public Args(Ctx c, Op op, JObject raw)
        {
            this.c = c; this.op = op; this.raw = raw;
            var known = new HashSet<string>(op.Params.Select(p => p.Name)) { "what", "op" };
            foreach (var prop in raw.Properties())
                if (!known.Contains(prop.Name))
                    throw new NwError("bad_args", $"{op.Name} has no argument \"{prop.Name}\"", Usage);
            foreach (var p in op.Params)
            {
                var v = raw[p.Name];
                if (v == null || v.Type == JTokenType.Null)
                {
                    if (p.Required) throw new NwError("bad_args", $"{op.Name} needs \"{p.Name}\"", Usage);
                    continue;
                }
                if (!Fits(p, v)) throw new NwError("bad_args", $"{op.Name}: \"{p.Name}\" must be {Describe(p)}, got {v.ToString(Newtonsoft.Json.Formatting.None)}", Usage);
                if (p.Type == "int")
                {
                    var min = new[] { "limit", "top", "wait", "index", "model", "offset" }.Contains(p.Name) ? 0 : 1;
                    var max = p.Name == "page" ? 1000000 : p.Name == "depth" ? 100 : p.Name == "wait" ? 120000 : p.Name == "w" || p.Name == "h" ? 16384 : p.Name == "limit" || p.Name == "top" ? 100000 : int.MaxValue;
                    if (!long.TryParse(v.ToString(), out var n) || n < min || n > max)
                        throw new NwError("bad_args", p.Name + " must be an integer in " + min + ".." + max, "set " + p.Name + " to " + (p.Name == "limit" || p.Name == "top" ? 50 : p.Name == "wait" ? 1000 : min));
                }
            }
            if (op.Params.Any(p => p.Name == "page") && ((long)Int("page") - 1) * Int("limit") > int.MaxValue)
                throw new NwError("bad_args", "page and limit exceed the supported row offset", "start with page:1 and follow the returned more: call");
            if (op.Params.Any(p => p.Name == "cols") && (List("cols").Length > 32 || List("cols").Any(x => x.Length > 128)))
                throw new NwError("bad_args", "cols supports at most 32 property names of at most 128 characters", "request fewer columns per read or export a CSV");
        }

        string Usage => "usage: " + op.Line;

        public string Next(int offset)
        {
            var next = (JObject)raw.DeepClone();
            next["offset"] = offset;
            return "more: read " + next.ToString(Newtonsoft.Json.Formatting.None);
        }

        static bool Fits(Param p, JToken v)
        {
            switch (p.Type)
            {
                case "int": return v.Type == JTokenType.Integer;
                case "num": case "len": return v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
                case "bool": return v.Type == JTokenType.Boolean;
                case "target": case "strs": return v.Type == JTokenType.String || (v.Type == JTokenType.Array && v.All(x => x.Type == JTokenType.String));
                case "json": return true;
                case "enum": return v.Type == JTokenType.String && p.Choices.Contains((string)v);
                default: return v.Type == JTokenType.String;
            }
        }

        static string Describe(Param p) => p.Type == "enum" ? "one of " + string.Join("|", p.Choices) : p.Type;

        Param P(string name) => op.Params.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException(op.Name + " reads undeclared argument " + name);

        JToken V(string name)
        {
            var p = P(name);
            var v = raw[name];
            if (v != null && v.Type != JTokenType.Null) return v;
            return p.Default == null ? null : JToken.FromObject(p.Type == "int" ? (object)int.Parse(p.Default) : p.Type == "num" || p.Type == "len" ? double.Parse(p.Default, System.Globalization.CultureInfo.InvariantCulture) : p.Type == "bool" ? (object)bool.Parse(p.Default) : p.Default);
        }

        public bool Has(string name) => V(name) != null;
        public string Str(string name) => (string)V(name);
        public int Int(string name) => (int?)V(name) ?? 0;
        public double Num(string name) => (double?)V(name) ?? 0;
        public bool Bool(string name) => (bool?)V(name) ?? false;
        public JToken Json(string name) => V(name);
        public string[] List(string name)
        {
            var v = V(name);
            if (v == null) return new string[0];
            return v.Type == JTokenType.Array ? v.Select(x => (string)x).ToArray() : ((string)v).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        /// A length given in mm, converted to document units.
        public double Len(string name) => Units.FromMm(c.Doc, Num(name));

        public Color Color(string name) => Units.ParseColor(Str(name));

        public DateTime Date(string name)
        {
            if (DateTime.TryParse(Str(name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var d)) return d;
            throw new NwError("bad_args", $"{name} \"{Str(name)}\" is not a date", "use ISO dates such as 2026-03-01 or 2026-03-01T08:00");
        }

        public Target Target(string name) => Has(name) ? NavisworksMcp.Target.Parse(c, V(name)) : null;

        public ModelItemCollection Items(string name) => Target(name)?.Items(c) ?? throw new NwError("bad_args", op.Name + " needs " + name, Usage);
    }
}
