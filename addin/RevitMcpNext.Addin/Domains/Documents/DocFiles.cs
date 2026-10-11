using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Shared helpers of the documents, worksharing, links and export lanes (P-DOCS-EXPORT): path normalization,
    /// open-document lookup, doc descriptors for re-pins, static plans for lifecycle previews, and native-call
    /// bookkeeping (DocumentRegistry.SetNative/ClearNative so status shows "opening", "sync", ...).
    /// </summary>
    internal static class DocFiles
    {
        /// <summary>Expands %VARS%, strips quotes and returns the absolute path; INVALID_ARGS when it is not a path.</summary>
        public static string FullPath(string raw, string param)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw OpException.InvalidArgs(param, "is required (a file path)");
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (!Path.IsPathRooted(expanded))
                {
                    throw OpException.InvalidArgs(param, "must be an absolute path (e.g. C:\\Projects\\Model.rvt), got " + raw);
                }
                return Path.GetFullPath(expanded);
            }
            catch (OpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw OpException.InvalidArgs(param, "is not a valid path (" + ex.Message + ")");
            }
        }

        /// <summary>INVALID_ARGS unless <paramref name="path"/> has one of the extensions (case-insensitive).</summary>
        public static void RequireExtension(string path, string param, IDictionary<string, object> example, params string[] extensions)
        {
            string extension = Path.GetExtension(path) ?? string.Empty;
            if (extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase))) return;
            throw OpException.InvalidArgs(param, "must end with " + string.Join(" or ", extensions) + ", got " + path, example);
        }

        /// <summary>A few files next to a missing path (NOT_FOUND candidates).</summary>
        public static IEnumerable<string> Siblings(string path, string pattern = "*.*")
        {
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new string[0];
                return Directory.GetFiles(folder, pattern).Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            }
            catch
            {
                return new string[0];
            }
        }

        public static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            return string.Equals(DocumentRegistry.NormalizePath(left), DocumentRegistry.NormalizePath(right), StringComparison.Ordinal);
        }

        /// <summary>The open (non-linked) document whose PathName is <paramref name="path"/>, or null.</summary>
        public static Document FindOpen(Autodesk.Revit.ApplicationServices.Application app, string path)
        {
            if (app == null || string.IsNullOrWhiteSpace(path)) return null;
            foreach (Document document in app.Documents)
            {
                if (document == null || document.IsLinked) continue;
                string candidate;
                try { candidate = document.PathName; } catch { continue; }
                if (PathsEqual(candidate, path)) return document;
            }
            return null;
        }

        /// <summary>An open (non-linked) document that is a local of <paramref name="centralPath"/>, or null.</summary>
        public static Document FindOpenLocalOf(Autodesk.Revit.ApplicationServices.Application app, string centralPath)
        {
            if (app == null || string.IsNullOrWhiteSpace(centralPath)) return null;
            foreach (Document document in app.Documents)
            {
                if (document == null || document.IsLinked) continue;
                bool workshared;
                try { workshared = document.IsWorkshared; } catch { continue; }
                if (!workshared) continue;
                if (PathsEqual(CentralPath(document), centralPath) && !PathsEqual(SafePath(document), centralPath)) return document;
            }
            return null;
        }

        /// <summary>Open (non-linked) documents other than <paramref name="except"/>.</summary>
        public static List<Document> OpenDocuments(Autodesk.Revit.ApplicationServices.Application app, Document except = null)
        {
            var list = new List<Document>();
            if (app == null) return list;
            foreach (Document document in app.Documents)
            {
                if (document == null || document.IsLinked) continue;
                if (except != null && Same(document, except)) continue;
                list.Add(document);
            }
            return list;
        }

        /// <summary>Identity of two Document wrappers (Equals, else the registry rid).</summary>
        public static bool Same(Document left, Document right)
        {
            if (left == null || right == null) return false;
            try
            {
                if (left.Equals(right)) return true;
            }
            catch
            {
                // Fall through to the rid compare.
            }
            DocumentRegistry registry = McpRuntime.Registry;
            return registry != null && registry.TryGetRid(left, out long a) && registry.TryGetRid(right, out long b) && a == b;
        }

        public static string SafePath(Document document)
        {
            try { return document?.PathName ?? string.Empty; } catch { return string.Empty; }
        }

        public static string SafeTitle(Document document)
        {
            try { return document?.Title ?? string.Empty; } catch { return string.Empty; }
        }

        public static string CentralPath(Document document)
        {
            try
            {
                if (document == null || !document.IsWorkshared) return string.Empty;
                ModelPath central = document.GetWorksharingCentralModelPath();
                return central == null ? string.Empty : ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>True when <paramref name="document"/> is the UI-active document.</summary>
        public static bool IsActive(UIApplication app, Document document)
        {
            Document active = null;
            try { active = app?.ActiveUIDocument?.Document; } catch { active = null; }
            return active != null && Same(active, document);
        }

        /// <summary>Rebuilds the registry (after opens/closes outside events) and describes <paramref name="document"/>.</summary>
        public static ResponseDoc Describe(RequestContext ctx, Document document, bool rebuild = true)
        {
            DocumentRegistry registry = ctx.Registry;
            if (registry == null || document == null) return null;
            try
            {
                if (rebuild) registry.Rebuild(ctx.RevitApp, ctx.App);
                return registry.Describe(document);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>{rid, key, title, path} of a document for result data.</summary>
        public static Dictionary<string, object> DocData(ResponseDoc doc, string path)
        {
            if (doc == null) return null;
            var data = new Dictionary<string, object> { ["rid"] = doc.Rid, ["key"] = doc.Key, ["title"] = doc.Title };
            if (!string.IsNullOrWhiteSpace(path)) data["path"] = path;
            return data;
        }

        /// <summary>The static plan of a lifecycle op in preview mode (SPEC §6.2): nothing runs.</summary>
        public static OpResult StaticPlan(string summary, Dictionary<string, object> plan)
        {
            var data = plan ?? new Dictionary<string, object>();
            data["static"] = true;
            return OpResult.Success(data, summary);
        }

        /// <summary>The Revit user name (Options > General), else the Windows user name.</summary>
        public static string RevitUser(Autodesk.Revit.ApplicationServices.Application app)
        {
            string user = null;
            try { user = app?.Username; } catch { user = null; }
            if (string.IsNullOrWhiteSpace(user)) user = Environment.UserName;
            return SafeFilePart(user);
        }

        /// <summary>A file name part without invalid characters (spaces kept).</summary>
        public static string SafeFilePart(string value)
        {
            string text = (value ?? string.Empty).Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) text = text.Replace(invalid, '_');
            return text.Length == 0 ? "unnamed" : text;
        }

        /// <summary>
        /// Runs a blocking native call (open, save, sync, reload) with the registry's native marker set, so status and
        /// busy classification report it; the marker is cleared afterwards even on failure.
        /// </summary>
        public static T Native<T>(RequestContext ctx, string kind, Document document, Func<T> action)
        {
            DocumentRegistry registry = ctx.Registry;
            try
            {
                registry?.SetNative(kind, document);
            }
            catch
            {
                // Status bookkeeping only.
            }
            try
            {
                return action();
            }
            finally
            {
                try { registry?.ClearNative(); } catch { }
            }
        }

        public static void Native(RequestContext ctx, string kind, Document document, Action action)
        {
            Native<object>(ctx, kind, document, () =>
            {
                action();
                return null;
            });
        }

        /// <summary>Bytes of a file, 0 when it is missing.</summary>
        public static long Bytes(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
        }

        public static string Mtime(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).ToString("o", CultureInfo.InvariantCulture) : null; } catch { return null; }
        }

        /// <summary>Maps Revit's file exceptions (missing, corrupt, newer version, locked central) to catalog errors.</summary>
        public static OpException MapFileException(Exception ex, string path, string what)
        {
            switch (ex)
            {
                case OpException op:
                    return op;
                case Autodesk.Revit.Exceptions.FileNotFoundException _:
                    return OpException.NotFound("path", "file", path, Siblings(path));
                case Autodesk.Revit.Exceptions.FileAccessException access:
                    return new OpException(ErrorCodes.RevitRefused, what + ": the file is locked or read-only (" + access.Message + ").",
                        new Dictionary<string, object> { ["apiMessage"] = access.Message, ["path"] = path, ["reason"] = "file_access" });
                case Autodesk.Revit.Exceptions.CentralModelException central:
                    return new OpException(ErrorCodes.RevitRefused, what + ": the central model refused the operation (" + central.Message + ").",
                        new Dictionary<string, object> { ["apiMessage"] = central.Message, ["path"] = path, ["reason"] = "central_model" });
                case Autodesk.Revit.Exceptions.OperationCanceledException _:
                    return new OpException(ErrorCodes.RequestCancelled, what + " was cancelled in Revit (a dialog was cancelled or the user pressed Esc).",
                        new Dictionary<string, object> { ["path"] = path });
                case Autodesk.Revit.Exceptions.ApplicationException revit:
                    return new OpException(ErrorCodes.RevitRefused, what + ": " + revit.Message,
                        new Dictionary<string, object> { ["apiMessage"] = revit.Message, ["exception"] = revit.GetType().Name, ["path"] = path });
                case IOException io:
                    return new OpException(ErrorCodes.RevitRefused, what + ": " + io.Message,
                        new Dictionary<string, object> { ["apiMessage"] = io.Message, ["path"] = path, ["reason"] = "io" });
                case UnauthorizedAccessException denied:
                    return new OpException(ErrorCodes.RevitRefused, what + ": access denied (" + denied.Message + ").",
                        new Dictionary<string, object> { ["apiMessage"] = denied.Message, ["path"] = path, ["reason"] = "access_denied" });
                default:
                    OpResult mapped = Dispatcher.MapException(RequestContext.Current, ex);
                    return new OpException(mapped.Code, mapped.Message, mapped.Details);
            }
        }
    }
}
