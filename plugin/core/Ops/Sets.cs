using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace NavisworksMcp
{
    /// Selection and search sets, addressed by a folder path such as "QA/Ducts without system".
    static class Sets
    {
        public static SelectionSet Find(Document doc, string path) =>
            Lookup(doc.SelectionSets.RootItem, path) as SelectionSet
            ?? throw new NwError("not_found", $"no set \"{path}\"", "list sets with read {what:\"sets\"} and use the exact path");

        public static SavedItem Lookup(GroupItem root, string path)
        {
            SavedItem node = root;
            foreach (var part in path.Split('/'))
            {
                node = (node as GroupItem)?.Children.FirstOrDefault(x => x.DisplayName == part);
                if (node == null) return null;
            }
            return node;
        }

        /// Returns the folder for a path's parent, creating missing folders through `add`.
        /// Navisworks 2027 leaves an item added to the selection sets out of the children of a group read before the add, so a new folder is looked up again from a fresh root.
        public static GroupItem Folder(System.Func<GroupItem> root, string path, System.Action<GroupItem, SavedItem> add)
        {
            var parts = path.Split('/');
            var folder = root();
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var next = folder.Children.FirstOrDefault(x => x.DisplayName == parts[i]) as GroupItem;
                if (next == null)
                {
                    add(folder, new FolderItem { DisplayName = parts[i] });
                    next = (GroupItem)Lookup(root(), string.Join("/", parts, 0, i + 1));
                }
                folder = next;
            }
            return folder;
        }

        public static string Leaf(string path) => path.Substring(path.LastIndexOf('/') + 1);

        public static IEnumerable<(string path, SavedItem item)> Walk(GroupItem root, string prefix = "")
        {
            foreach (var child in root.Children)
            {
                var path = prefix + child.DisplayName;
                yield return (path, child);
                if (child is GroupItem g && !(child is SelectionSet)) foreach (var x in Walk(g, path + "/")) yield return x;
            }
        }
    }

    static class SetOps
    {
        public static readonly Op[] All =
        {
            Catalog.Read("read", "sets", "; folder:str", List, "selection and search sets with kind and size", "{folder:\"QA\"}"),
            Catalog.Apply("set.save", "name:str; items:target where:where", Save,
                "save a set; where (or items from a where search) makes a live search set, items makes a fixed selection set. Replaces a set with the same path",
                "{name:\"QA/F90 walls\", where:\"Pset_Fixture.FireRating = F90\"}"),
            Catalog.Apply("set.delete", "name:str", Delete, "delete a set or folder", "{name:\"QA/F90 walls\"}"),
            Catalog.Apply("set.rename", "name:str to:str", Rename, "rename a set or folder (to is the new leaf name)", "{name:\"QA/F90 walls\", to:\"F90 walls (old)\"}"),
        };

        static void List(Ctx c, Args a, Out o)
        {
            GroupItem root = c.Doc.SelectionSets.RootItem;
            if (a.Has("folder")) root = Sets.Lookup(root, a.Str("folder")) as GroupItem ?? throw new NwError("not_found", $"no folder \"{a.Str("folder")}\"", "call read {what:\"sets\"}");
            var rows = Sets.Walk(root, a.Has("folder") ? a.Str("folder") + "/" : "").ToList();
            o.Line($"sets {rows.Count(r => r.item is SelectionSet):#,0} (exact)");
            o.Table(new[] { "path", "kind", "items" }, rows.Select(r =>
            {
                var set = r.item as SelectionSet;
                return new object[] { r.path, set == null ? "folder" : set.HasSearch ? "search" : "selection", set == null ? "-" : set.HasSearch ? "live" : set.ExplicitModelItems.Count.ToString("#,0") };
            }));
        }

        static Step Save(Ctx c, Args a)
        {
            var name = a.Str("name");
            var target = a.Target("items");
            string where = a.Str("where");
            if (where == null && target?.Whole?.Kind == Kind.Items)
                where = target.Whole.Where;
            if (where == null && target == null) throw new NwError("bad_args", "set.save needs items or where", "pass where:\"...\" for a live search set or items:\"r1\" for a fixed set");
            var items = where == null ? target.Items(c) : null;
            var search = where == null ? null : Where.Parse(where);
            var count = search != null ? search.Find(c, null).Count : items.Count;
            var kind = search != null ? "search set (live)" : $"selection set of {items.Count:#,0} items";
            return new Step($"save {kind} \"{name}\"", () =>
            {
                var sets = c.Doc.SelectionSets;
                var set = search != null ? new SelectionSet(search.ToSearch(c)) : new SelectionSet(items);
                set.DisplayName = Sets.Leaf(name);
                var folder = Sets.Folder(() => sets.RootItem, name, sets.AddCopy);
                var existing = folder.Children.FirstOrDefault(x => x.DisplayName == set.DisplayName);
                if (existing != null) sets.ReplaceWithCopy(folder, folder.Children.IndexOf(existing), set);
                else sets.AddCopy(folder, set);
                return $"{(existing != null ? "replaced" : "saved")} {(search != null ? "search" : "selection")} set \"{name}\" ({count:#,0} items now)";
            });
        }

        static Step Delete(Ctx c, Args a)
        {
            var name = a.Str("name");
            var item = Sets.Lookup(c.Doc.SelectionSets.RootItem, name) ?? throw new NwError("not_found", $"no set \"{name}\"", "call read {what:\"sets\"}");
            return new Step($"delete \"{name}\"", () =>
            {
                c.Doc.SelectionSets.Remove(item.Parent, item);
                return $"deleted \"{name}\"";
            });
        }

        static Step Rename(Ctx c, Args a)
        {
            var name = a.Str("name");
            var item = Sets.Lookup(c.Doc.SelectionSets.RootItem, name) ?? throw new NwError("not_found", $"no set \"{name}\"", "call read {what:\"sets\"}");
            var to = a.Str("to");
            return new Step($"rename \"{name}\" to \"{to}\"", () =>
            {
                c.Doc.SelectionSets.EditDisplayName(item, to);
                return $"renamed \"{name}\" to \"{to}\"";
            });
        }
    }
}
