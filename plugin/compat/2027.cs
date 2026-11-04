using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace NavisworksMcp
{
    /// The API calls with no form that compiles against every supported Navisworks; the build compiles plugin/compat/<year>.cs for its version.
    static class Compat
    {
        public static void PublishNwd(Document doc, string path, PublishProperties props) => doc.PublishFile(path, new NwdExportOptions(), props);
        public static void CopyClip(Viewpoint target, Viewpoint source) => target.ClipPlanes.CopyFrom(source.ClipPlanes);

        public static string Clip(Viewpoint vp)
        {
            var clip = vp.ClipPlanes;
            if (clip == null || !clip.Enabled) return "off";
            if (clip.Mode == ClipPlaneSetMode.Box) return "box";
            var on = Enumerable.Range(0, clip.Size()).Count(i => clip.Get(i).Enabled);
            return on == 0 ? "off" : on + " plane(s)";
        }

        public static string AssignedTo(IClashResult r) => string.IsNullOrEmpty(r.AssignedTo?.DisplayName) ? null : r.AssignedTo.DisplayName;

        public static void SetStatus(DocumentClashTests data, IClashResult r, ClashResultStatus status, string user) => data.TestsEditResultStatus(r, status, new Assignee(user));

        public static void SetApprovedBy(DocumentClashTests data, IClashResult r, string user) => data.TestsEditResultApprovedBy(r, new Assignee(user));

        public static void SetAssignedTo(DocumentClashTests data, IClashResult r, string name) => data.TestsEditResultAssignedTo(r, name.Length == 0 ? new Assignee() : new Assignee(name));

        public static Comment Comment(Document doc, string body, CommentStatus status, string author) => doc.CreateCommentWithUniqueId(body, status, new Assignee(author));
        public static string CommentAuthor(Comment comment) => comment.Author?.DisplayName;
    }
}
