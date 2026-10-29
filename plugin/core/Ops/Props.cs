using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Newtonsoft.Json.Linq;
using Com = Autodesk.Navisworks.Api.Interop.ComApi;

namespace NavisworksMcp
{
    /// User-defined property tabs. Only the COM API can write them: a tab is rewritten whole through SetUserDefined.
    static class PropOps
    {
        public static readonly Op[] All =
        {
            Catalog.Apply("props.set", "items:target tab:str props:json", Set,
                "set user-defined properties {name: text|number|bool} in a tab, merged into an existing tab of that name",
                "{items:\"r1\", tab:\"QA\", props:{Status:\"Checked\", Round:2}}"),
            Catalog.Apply("props.delete", "items:target tab:str; names:strs", Delete,
                "delete user-defined properties from a tab, or the whole tab when names is omitted", "{items:\"r1\", tab:\"QA\", names:\"Round\"}"),
        };

        /// The 1-based index SetUserDefined and RemoveUserDefined take for an existing user tab (0 = none), with its properties.
        static (int index, List<KeyValuePair<string, object>> props) UserTab(Com.InwGUIPropertyNode2 node, string tab)
        {
            var i = 0;
            foreach (Com.InwGUIAttribute2 attr in node.GUIAttributes())
            {
                if (!attr.UserDefined) continue;
                i++;
                if (attr.ClassUserName != tab) continue;
                var props = new List<KeyValuePair<string, object>>();
                foreach (Com.InwOaProperty p in attr.Properties()) props.Add(new KeyValuePair<string, object>(p.UserName, p.value));
                return (i, props);
            }
            return (0, new List<KeyValuePair<string, object>>());
        }

        static Com.InwGUIPropertyNode2 Node(ModelItem item) =>
            (Com.InwGUIPropertyNode2)ComApiBridge.State.GetGUIPropertyNode(ComApiBridge.ToInwOaPath(item), true);

        static void Write(Com.InwGUIPropertyNode2 node, int index, string tab, IEnumerable<KeyValuePair<string, object>> props)
        {
            var state = ComApiBridge.State;
            var vec = (Com.InwOaPropertyVec)state.ObjectFactory(Com.nwEObjectType.eObjectType_nwOaPropertyVec, null, null);
            foreach (var kv in props)
            {
                var p = (Com.InwOaProperty)state.ObjectFactory(Com.nwEObjectType.eObjectType_nwOaProperty, null, null);
                p.name = kv.Key;
                p.UserName = kv.Key;
                p.value = kv.Value;
                vec.Properties().Add(p);
            }
            node.SetUserDefined(index, tab, tab, vec);
        }

        static object Value(string name, JToken v)
        {
            switch (v.Type)
            {
                case JTokenType.String: return (string)v;
                case JTokenType.Integer: return (long)v is var n && n >= int.MinValue && n <= int.MaxValue ? (object)(int)n : (double)n;
                case JTokenType.Float: return (double)v;
                case JTokenType.Boolean: return (bool)v;
                default: throw new NwError("bad_args", $"property {name} is {v.Type}; values are text, numbers or true/false", "pass props:{Status:\"Checked\", Round:2, Done:true}");
            }
        }

        static Step Set(Ctx c, Args a)
        {
            var items = a.Items("items");
            var tab = a.Str("tab");
            var given = a.Json("props") as JObject;
            if (given == null || !given.HasValues) throw new NwError("bad_args", "props must be an object of name: value", "pass props:{Status:\"Checked\", Round:2}");
            var props = given.Properties().Select(p => new KeyValuePair<string, object>(p.Name, Value(p.Name, p.Value))).ToList();
            var byName = props.ToDictionary(p => p.Key, p => p.Value);
            var names = string.Join(", ", props.Select(p => p.Key));
            return new Step($"set {names} in tab \"{tab}\" on {items.Count:#,0} items", () =>
            {
                foreach (var item in items)
                {
                    c.CheckCancelled();
                    var node = Node(item);
                    var (index, existing) = UserTab(node, tab);
                    var merged = existing.Select(e => byName.TryGetValue(e.Key, out var v) ? new KeyValuePair<string, object>(e.Key, v) : e)
                        .Concat(props.Where(p => !existing.Any(e => e.Key == p.Key)));
                    Write(node, index, tab, merged);
                }
                return $"set {names} in tab \"{tab}\" on {items.Count:#,0} items";
            });
        }

        static Step Delete(Ctx c, Args a)
        {
            var items = a.Items("items");
            var tab = a.Str("tab");
            var names = new HashSet<string>(a.List("names"));
            var what = names.Count == 0 ? $"tab \"{tab}\"" : $"{string.Join(", ", names)} from tab \"{tab}\"";
            return new Step($"delete {what} on {items.Count:#,0} items", () =>
            {
                var touched = 0;
                foreach (var item in items)
                {
                    c.CheckCancelled();
                    var node = Node(item);
                    var (index, existing) = UserTab(node, tab);
                    if (index == 0) continue;
                    var kept = existing.Where(e => !names.Contains(e.Key)).ToList();
                    if (kept.Count == existing.Count && names.Count > 0) continue;
                    if (names.Count == 0 || kept.Count == 0) node.RemoveUserDefined(index);
                    else Write(node, index, tab, kept);
                    touched++;
                }
                if (touched == 0) throw new NwError("not_found", $"none of the {items.Count:#,0} items has {what}", "check with read {what:\"props\", items:\"...\", tabs:\"" + tab + "\"}");
                return $"deleted {what} on {touched:#,0} items";
            });
        }
    }
}
