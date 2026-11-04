using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace NavisworksMcp
{
    /// The API calls with no form that compiles against every supported Navisworks; the build compiles plugin/compat/<year>.cs for its version.
    static class Compat
    {
        public static void PublishNwd(Document doc, string path, PublishProperties props) => doc.PublishFile(path, props);
        public static void CopyClip(Viewpoint target, Viewpoint source) => target.InternalClipPlanes.CopyFrom(source.InternalClipPlanes);

        public static string Clip(Viewpoint vp)
        {
            var clip = vp.InternalClipPlanes;
            if (clip == null || !clip.IsEnabled()) return "off";
            if (clip.GetMode() == Autodesk.Navisworks.Api.Interop.LcOaClipPlaneSetMode.eMODE_BOX) return "box";
            var on = Enumerable.Range(0, clip.Size()).Count(i => clip.Get(i).IsEnabled());
            return on == 0 ? "off" : on + " plane(s)";
        }

        public static string AssignedTo(IClashResult r) => string.IsNullOrEmpty(r.AssignedTo) ? null : r.AssignedTo;

        public static void SetStatus(DocumentClashTests data, IClashResult r, ClashResultStatus status, string user) => data.TestsEditResultStatus(r, status);

        public static void SetApprovedBy(DocumentClashTests data, IClashResult r, string user) => data.TestsEditResultApprovedBy(r, user);

        public static void SetAssignedTo(DocumentClashTests data, IClashResult r, string name) => data.TestsEditResultAssignedTo(r, name);

        public static Comment Comment(Document doc, string body, CommentStatus status, string author) => doc.CreateCommentWithUniqueId(body, status, author);
    }
}
