using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// links.* (SPEC Appendix A/B, D3 §3.10): link RVT/IFC/CAD and import CAD (ChangeEngine writes, one undo step),
    /// reload / reload_from / unload (lifecycle: outside transactions, as Revit requires), remove and
    /// acquire_coordinates (writes; always confirmed by the broker first). The link param takes an RVT link type or
    /// instance (name, file name or id) or a CAD link/import (name or id).
    /// </summary>
    internal static class LinkOps
    {
        private static readonly string[] CadExtensions = { ".dwg", ".dxf", ".dgn", ".sat", ".skp", ".3dm", ".obj", ".stl" };

        // ---------------------------------------------------------------------------------------------- link / import

        [Op("links.link_rvt")]
        public static void LinkRvt(ChangeContext c)
        {
            Document doc = c.Doc;
            string path = DocFiles.FullPath(c.Args.ReqStr("path"), "path");
            DocFiles.RequireExtension(path, "path", new Dictionary<string, object> { ["op"] = "link_rvt", ["path"] = Path.ChangeExtension(path, ".rvt") }, ".rvt");
            if (!File.Exists(path)) throw OpException.NotFound("path", "file", path, DocFiles.Siblings(path, "*.rvt"));
            if (DocFiles.PathsEqual(DocFiles.SafePath(doc), path)) throw OpException.InvalidArgs("path", "is the host model itself; a model cannot link itself");
            string position = c.Args.Enum("position", "origin", "origin", "center", "shared", "base_point");

            LinkLoadResult load = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new RevitLinkOptions(true));
            EnsureLoaded(load, path);
            RevitLinkInstance instance = PlaceRvt(doc, load.ElementId, position);
            c.Created(load.ElementId);
            c.Created(instance);
            c.Output("link", instance.Id);
            c.Data(LinkData(doc, instance, position));
            c.Summary("linked " + Path.GetFileName(path) + " (" + PositionText(position) + ")");
        }

        /// <summary>
        /// IFC links need the Revit cache file (&lt;file&gt;.ifc.RVT) built by Revit's IFC importer, which opens a document
        /// and cannot run inside the host transaction; so this handler builds the cache first and then runs the link
        /// itself through the ChangeEngine (single calls only; inside change_set use a prior single call).
        /// </summary>
        [Op("links.link_ifc")]
        public static OpResult LinkIfc(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            string path = DocFiles.FullPath(ctx.Args.ReqStr("path"), "path");
            DocFiles.RequireExtension(path, "path", new Dictionary<string, object> { ["op"] = "link_ifc", ["path"] = Path.ChangeExtension(path, ".ifc") }, ".ifc", ".ifczip", ".ifcxml");
            if (!File.Exists(path)) throw OpException.NotFound("path", "file", path, DocFiles.Siblings(path, "*.ifc*"));
            string position = ctx.Args.Enum("position", "origin", "origin", "center", "shared", "base_point");
            string cache = path + ".RVT";
            bool cacheFresh = File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(path);
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("link " + Path.GetFileName(path) + " (" + PositionText(position) + ")" + (cacheFresh ? string.Empty : "; Revit first converts it to " + Path.GetFileName(cache)),
                    new Dictionary<string, object> { ["path"] = path, ["cache"] = cache, ["cache_exists"] = cacheFresh, ["position"] = position });
            }
            if (!cacheFresh) BuildIfcCache(ctx, path, cache);

            var binding = new OpBinding
            {
                Key = ctx.Key,
                UsesChangeContext = true,
                InvokeChange = c =>
                {
                    LinkLoadResult load = RevitLinkType.CreateFromIFC(c.Doc, path, cache, false, new RevitLinkOptions(false));
                    EnsureLoaded(load, path);
                    RevitLinkInstance instance = PlaceRvt(c.Doc, load.ElementId, position);
                    c.Created(load.ElementId);
                    c.Created(instance);
                    c.Output("link", instance.Id);
                    c.Data(LinkData(c.Doc, instance, position));
                    c.Summary("linked " + Path.GetFileName(path) + " (" + PositionText(position) + ")");
                }
            };
            return ChangeEngine.RunSingle(ctx, binding);
        }

        private static void BuildIfcCache(RequestContext ctx, string path, string cache)
        {
            Document ifcDoc = null;
            try
            {
                var options = new IFCImportOptions { Action = IFCImportAction.Link, Intent = IFCImportIntent.Reference, RevitLinkFileName = cache };
                ctx.Progress("converting " + Path.GetFileName(path));
                ifcDoc = DocFiles.Native(ctx, "opening", null, () => ctx.RevitApp.OpenIFCDocument(path, options));
                if (ifcDoc != null && ifcDoc.IsValidObject && !File.Exists(cache))
                {
                    ifcDoc.SaveAs(cache, new SaveAsOptions { OverwriteExistingFile = true });
                }
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, path, "Converting " + Path.GetFileName(path) + " for linking");
            }
            finally
            {
                try { if (ifcDoc != null && ifcDoc.IsValidObject) ifcDoc.Close(false); } catch { }
            }
            if (!File.Exists(cache))
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit's IFC importer did not produce " + Path.GetFileName(cache) + ".",
                    new Dictionary<string, object> { ["path"] = path, ["cache"] = cache, ["apiMessage"] = "no cache file after OpenIFCDocument" });
            }
        }

        [Op("links.link_cad")]
        public static void LinkCad(ChangeContext c)
        {
            PlaceCad(c, link: true);
        }

        [Op("links.import_cad")]
        public static void ImportCad(ChangeContext c)
        {
            PlaceCad(c, link: false);
        }

        private static void PlaceCad(ChangeContext c, bool link)
        {
            Document doc = c.Doc;
            string op = link ? "link_cad" : "import_cad";
            string path = DocFiles.FullPath(c.Args.ReqStr("path"), "path");
            DocFiles.RequireExtension(path, "path", new Dictionary<string, object> { ["op"] = op, ["path"] = Path.ChangeExtension(path, ".dwg") }, CadExtensions);
            if (!File.Exists(path)) throw OpException.NotFound("path", "file", path, DocFiles.Siblings(path));
            string position = c.Args.Enum("position", "origin", "origin", "center", "shared", "base_point");
            string units = c.Args.Enum("cad_units", "auto", "auto", "mm", "cm", "m", "in", "ft");
            bool thisViewOnly = c.Args.Bool("this_view_only", false);
            View view = c.View("view", required: false) ?? c.Ctx.UiDoc?.ActiveView ?? doc.ActiveView;
            if (view == null || view.IsTemplate || view is ViewSchedule || view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser)
            {
                throw OpException.InvalidArgs("view", "needs a graphical view to place the CAD file in (a plan, section, 3D or drafting view)",
                    new Dictionary<string, object> { ["op"] = op, ["path"] = path, ["view"] = "Level 1" });
            }
            if (view is View3D && thisViewOnly)
            {
                c.Warn(WarningCodes.ParamIgnored, "this_view_only ignored: CAD in a 3D view is always placed in all views");
                thisViewOnly = false;
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            ElementId placed = ElementId.InvalidElementId;
            switch (extension)
            {
                case ".dwg":
                case ".dxf":
                {
                    var options = new DWGImportOptions();
                    Configure(options, position, units, thisViewOnly);
                    bool ok = link ? doc.Link(path, options, view, out placed) : doc.Import(path, options, view, out placed);
                    if (!ok) placed = ElementId.InvalidElementId;
                    break;
                }
                case ".dgn":
                {
                    var options = new DGNImportOptions();
                    Configure(options, position, units, thisViewOnly);
                    bool ok = link ? doc.Link(path, options, view, out placed) : doc.Import(path, options, view, out placed);
                    if (!ok) placed = ElementId.InvalidElementId;
                    break;
                }
                case ".sat":
                {
                    var options = new SATImportOptions();
                    Configure(options, position, units, thisViewOnly);
                    placed = link ? doc.Link(path, options, view) : doc.Import(path, options, view);
                    break;
                }
                case ".skp":
                {
                    var options = new SKPImportOptions();
                    Configure(options, position, units, thisViewOnly);
                    placed = link ? doc.Link(path, options, view) : doc.Import(path, options, view);
                    break;
                }
                case ".3dm":
                {
                    var options = new ImportOptions3DM();
                    Configure(options, position, units, thisViewOnly);
                    placed = link ? doc.Link(path, options, view) : doc.Import(path, options, view);
                    break;
                }
                case ".obj":
                {
                    var options = new OBJImportOptions();
                    Configure(options, position, units, thisViewOnly);
                    placed = link ? doc.Link(path, options, view) : doc.Import(path, options, view);
                    break;
                }
                case ".stl":
                {
                    var options = new STLImportOptions();
                    Configure(options, position, units, thisViewOnly);
                    placed = link ? doc.Link(path, options, view) : doc.Import(path, options, view);
                    break;
                }
            }
            if (placed == null || placed == ElementId.InvalidElementId)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not " + (link ? "link " : "import ") + Path.GetFileName(path) + ".",
                    new Dictionary<string, object> { ["path"] = path, ["apiMessage"] = (link ? "Link" : "Import") + " returned no element" });
            }
            c.Created(placed);
            Element instance = doc.GetElement(placed);
            if (instance != null && instance.GetTypeId() != ElementId.InvalidElementId) c.Created(instance.GetTypeId());
            c.Output("link", placed);
            c.Data(new Dictionary<string, object>
            {
                ["id"] = placed.Value,
                ["name"] = instance?.Name ?? Path.GetFileName(path),
                ["linked"] = link,
                ["view"] = view.Name,
                ["this_view_only"] = thisViewOnly,
                ["position"] = position,
                ["cad_units"] = units
            });
            c.Summary((link ? "linked " : "imported ") + Path.GetFileName(path) + " into '" + view.Name + "' (" + PositionText(position) + (thisViewOnly ? ", this view only" : string.Empty) + ")");
        }

        private static void Configure(BaseImportOptions options, string position, string units, bool thisViewOnly)
        {
            options.ThisViewOnly = thisViewOnly;
            switch (position)
            {
                case "center": options.Placement = ImportPlacement.Centered; break;
                case "shared": options.Placement = ImportPlacement.Shared; break;
                case "base_point": options.Placement = ImportPlacement.Site; break;
                default: options.Placement = ImportPlacement.Origin; break;
            }
            switch (units)
            {
                case "mm": options.Unit = ImportUnit.Millimeter; break;
                case "cm": options.Unit = ImportUnit.Centimeter; break;
                case "m": options.Unit = ImportUnit.Meter; break;
                case "in": options.Unit = ImportUnit.Inch; break;
                case "ft": options.Unit = ImportUnit.Foot; break;
                default: options.Unit = ImportUnit.Default; break;
            }
        }

        // ---------------------------------------------------------------------------------------------- lifecycle

        [Op("links.reload")]
        public static OpResult Reload(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            LinkTarget target = LinkTarget.Resolve(doc, ctx.Args.ReqStr("link"));
            if (ctx.IsPreview) return DocFiles.StaticPlan("reload the link " + target.Name + " from " + (target.Path ?? "its saved path"), target.ToData());
            LinkLoadResult load;
            try
            {
                ctx.Progress("reloading " + target.Name);
                load = DocFiles.Native(ctx, "reloading", doc, () => target.Rvt != null ? target.Rvt.Reload() : target.Cad.Reload());
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, target.Path, "Reloading the link " + target.Name);
            }
            EnsureLoaded(load, target.Path ?? target.Name);
            return OpResult.Success(LinkTarget.Resolve(doc, target.TypeId.Value.ToString(CultureInfo.InvariantCulture)).ToData(), "reloaded the link " + target.Name)
                .WithChanges(null, new[] { target.TypeId }, null);
        }

        [Op("links.reload_from")]
        public static OpResult ReloadFrom(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            LinkTarget target = LinkTarget.Resolve(doc, ctx.Args.ReqStr("link"));
            string path = DocFiles.FullPath(ctx.Args.ReqStr("path"), "path");
            if (target.Rvt != null)
            {
                DocFiles.RequireExtension(path, "path", new Dictionary<string, object> { ["op"] = "reload_from", ["link"] = target.Name, ["path"] = Path.ChangeExtension(path, ".rvt") }, ".rvt");
            }
            else
            {
                DocFiles.RequireExtension(path, "path", new Dictionary<string, object> { ["op"] = "reload_from", ["link"] = target.Name, ["path"] = Path.ChangeExtension(path, ".dwg") }, CadExtensions);
            }
            if (!File.Exists(path)) throw OpException.NotFound("path", "file", path, DocFiles.Siblings(path));
            if (ctx.IsPreview)
            {
                var plan = target.ToData();
                plan["new_path"] = path;
                return DocFiles.StaticPlan("reload the link " + target.Name + " from " + path, plan);
            }
            LinkLoadResult load;
            try
            {
                ctx.Progress("reloading " + target.Name + " from " + Path.GetFileName(path));
                load = DocFiles.Native(ctx, "reloading", doc, () => target.Rvt != null
                    ? target.Rvt.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new WorksetConfiguration())
                    : target.Cad.LoadFrom(path));
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, path, "Reloading the link " + target.Name + " from " + Path.GetFileName(path));
            }
            EnsureLoaded(load, path);
            return OpResult.Success(LinkTarget.Resolve(doc, target.TypeId.Value.ToString(CultureInfo.InvariantCulture)).ToData(), "reloaded the link " + target.Name + " from " + path)
                .WithChanges(null, new[] { target.TypeId }, null);
        }

        [Op("links.unload")]
        public static OpResult Unload(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            LinkTarget target = LinkTarget.Resolve(doc, ctx.Args.ReqStr("link"));
            if (target.Rvt == null)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "The Revit API cannot unload CAD links; remove it (links {\"op\":\"remove\"}) or unload it in Revit (Manage Links).",
                    new Dictionary<string, object> { ["link"] = target.Name, ["alternative"] = "links {\"op\":\"remove\",\"link\":\"" + target.Name + "\"}" });
            }
            if (ctx.IsPreview) return DocFiles.StaticPlan("unload the link " + target.Name + " (it stays in the model and can be reloaded)", target.ToData());
            if (!target.Loaded)
            {
                return OpResult.Success(target.ToData(), "the link " + target.Name + " is already unloaded");
            }
            try
            {
                DocFiles.Native(ctx, "reloading", doc, () => target.Rvt.Unload(new KeepLinkPositions()));
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, target.Path, "Unloading the link " + target.Name);
            }
            return OpResult.Success(LinkTarget.Resolve(doc, target.TypeId.Value.ToString(CultureInfo.InvariantCulture)).ToData(), "unloaded the link " + target.Name + " (reload it with links {\"op\":\"reload\"})")
                .WithChanges(null, new[] { target.TypeId }, null);
        }

        // ---------------------------------------------------------------------------------------------- writes

        [Op("links.remove")]
        public static void Remove(ChangeContext c)
        {
            Document doc = c.Doc;
            LinkTarget target = LinkTarget.Resolve(doc, c.Args.ReqStr("link"));
            var ids = new List<ElementId>();
            if (target.TypeId != null && target.TypeId != ElementId.InvalidElementId) ids.Add(target.TypeId);
            else ids.AddRange(target.Instances);
            WorksharingGuard.EnsureEditable(doc, ids.Concat(target.Instances));
            ICollection<ElementId> deleted = c.Delete(ids);
            c.Data(new Dictionary<string, object> { ["removed"] = target.Name, ["instances"] = target.Instances.Count, ["deleted"] = deleted.Count });
            c.Summary("removed the link " + target.Name + " (" + target.Instances.Count.ToString(CultureInfo.InvariantCulture) + " instance(s))");
        }

        [Op("links.acquire_coordinates")]
        public static void AcquireCoordinates(ChangeContext c)
        {
            Document doc = c.Doc;
            LinkTarget target = LinkTarget.Resolve(doc, c.Args.ReqStr("link"));
            if (target.Rvt == null)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Coordinates can be acquired from Revit links only; '" + target.Name + "' is a CAD file.",
                    new Dictionary<string, object> { ["link"] = target.Name });
            }
            if (target.Instances.Count == 0) throw new OpException(ErrorCodes.NotFound, "The link " + target.Name + " has no placed instance.", new Dictionary<string, object> { ["param"] = "link", ["kind"] = "link", ["value"] = target.Name, ["candidates"] = new List<object>() });
            if (target.Instances.Count > 1 && target.InstanceId == null)
            {
                throw OpException.Ambiguous("link", "link instance", target.Name, target.Instances.Select(id => doc.GetElement(id)?.Name + " (id " + id.Value.ToString(CultureInfo.InvariantCulture) + ")"));
            }
            if (!target.Loaded) throw new OpException(ErrorCodes.UnsupportedOp, "The link " + target.Name + " is unloaded; reload it first.", new Dictionary<string, object> { ["link"] = target.Name, ["alternative"] = "links {\"op\":\"reload\"}" });
            ElementId instanceId = target.InstanceId ?? target.Instances[0];
            BasePoint survey = BasePoint.GetSurveyPoint(doc);
            BasePoint project = BasePoint.GetProjectBasePoint(doc);
            WorksharingGuard.EnsureEditable(doc, new[] { survey.Id, project.Id, ProjectLocationId(doc) }.Where(id => id != null && id != ElementId.InvalidElementId));
            doc.AcquireCoordinates(instanceId);
            c.Modified(survey);
            c.Modified(project);
            ElementId location = ProjectLocationId(doc);
            if (location != null && location != ElementId.InvalidElementId) c.Modified(location);
            c.MarkDirty();
            c.Data(new Dictionary<string, object> { ["link"] = target.Name, ["instance"] = instanceId.Value, ["site"] = SafeLocationName(doc) });
            c.Summary("acquired the shared coordinates of " + target.Name);
        }

        // ---------------------------------------------------------------------------------------------- helpers

        private static RevitLinkInstance PlaceRvt(Document doc, ElementId typeId, string position)
        {
            RevitLinkInstance instance;
            switch (position)
            {
                case "center":
                    instance = RevitLinkInstance.Create(doc, typeId, ImportPlacement.Centered);
                    break;
                case "shared":
                    instance = RevitLinkInstance.Create(doc, typeId, ImportPlacement.Shared);
                    break;
                case "base_point":
                    instance = RevitLinkInstance.Create(doc, typeId, ImportPlacement.Origin);
                    instance.MoveBasePointToHostBasePoint(false);
                    break;
                default:
                    instance = RevitLinkInstance.Create(doc, typeId, ImportPlacement.Origin);
                    break;
            }
            if (instance == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not place the link instance.", new Dictionary<string, object> { ["apiMessage"] = "RevitLinkInstance.Create returned null" });
            return instance;
        }

        private static void EnsureLoaded(LinkLoadResult load, string path)
        {
            if (load == null) throw new OpException(ErrorCodes.RevitRefused, "Revit returned no load result for " + path + ".", new Dictionary<string, object> { ["path"] = path });
            if (LinkLoadResult.IsCodeSuccess(load.LoadResult)) return;
            throw new OpException(ErrorCodes.RevitRefused, "Revit could not load " + Path.GetFileName(path) + ": " + load.LoadResult + ".",
                new Dictionary<string, object> { ["path"] = path, ["apiMessage"] = load.LoadResult.ToString(), ["reason"] = "link_load_" + load.LoadResult.ToString().ToLowerInvariant() });
        }

        private static Dictionary<string, object> LinkData(Document doc, RevitLinkInstance instance, string position)
        {
            Document linked = null;
            try { linked = instance.GetLinkDocument(); } catch { linked = null; }
            return new Dictionary<string, object>
            {
                ["id"] = instance.Id.Value,
                ["type"] = instance.GetTypeId().Value,
                ["name"] = instance.Name,
                ["position"] = position,
                ["loaded"] = linked != null,
                ["origin"] = OutputJson.Point(instance.GetTotalTransform().Origin)
            };
        }

        private static string PositionText(string position)
        {
            switch (position)
            {
                case "center": return "center to center";
                case "shared": return "by shared coordinates";
                case "base_point": return "project base point to project base point";
                default: return "origin to origin";
            }
        }

        private static ElementId ProjectLocationId(Document doc)
        {
            try { return doc.ActiveProjectLocation?.Id; } catch { return null; }
        }

        private static string SafeLocationName(Document doc)
        {
            try { return doc.ActiveProjectLocation?.Name; } catch { return null; }
        }

        /// <summary>Unload never writes positions back to the linked file (the user decides that in Revit).</summary>
        private sealed class KeepLinkPositions : ISaveSharedCoordinatesCallback
        {
            public SaveModifiedLinksOptions GetSaveModifiedLinksOption(RevitLinkType link)
            {
                return SaveModifiedLinksOptions.DoNotSaveLinks;
            }
        }
    }

    /// <summary>A resolved link: an RVT link type (with instances) or a CAD link/import (type + instances).</summary>
    internal sealed class LinkTarget
    {
        public string Name { get; private set; }
        public string Path { get; private set; }
        public RevitLinkType Rvt { get; private set; }
        public CADLinkType Cad { get; private set; }
        public ElementId TypeId { get; private set; }
        /// <summary>Set when the caller named one instance.</summary>
        public ElementId InstanceId { get; private set; }
        public List<ElementId> Instances { get; private set; } = new List<ElementId>();
        public bool Loaded { get; private set; }

        public Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                ["link"] = Name,
                ["kind"] = Rvt != null ? "rvt" : "cad",
                ["type"] = TypeId?.Value,
                ["instances"] = Instances.Select(id => (object)id.Value).ToList(),
                ["path"] = Path,
                ["loaded"] = Loaded
            };
        }

        /// <summary>id (type or instance) | type name | file name (with or without extension) | instance name.</summary>
        public static LinkTarget Resolve(Document doc, string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) throw OpException.InvalidArgs("link", "is required (a link name or id; list {\"kind\":\"links\"} shows them)");

            Element byId = null;
            long? id = Wire.ToLong(text);
            if (id.HasValue) byId = global::RevitMcpNext.Addin.Resolve.SafeGet(doc, new ElementId(id.Value));
            if (byId != null)
            {
                LinkTarget fromId = FromElement(doc, byId);
                if (fromId != null) return fromId;
            }

            var rvtTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            var cadTypes = new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>().ToList();
            var candidates = new List<Element>();
            foreach (RevitLinkType type in rvtTypes) if (NameMatches(type.Name, text)) candidates.Add(type);
            foreach (CADLinkType type in cadTypes) if (NameMatches(type.Name, text)) candidates.Add(type);
            if (candidates.Count == 0)
            {
                foreach (RevitLinkInstance instance in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    if (string.Equals(instance.Name, text, StringComparison.OrdinalIgnoreCase)) candidates.Add(instance);
                }
            }
            if (candidates.Count == 1) return FromElement(doc, candidates[0]);
            IEnumerable<string> names = rvtTypes.Select(t => t.Name).Concat(cadTypes.Select(t => t.Name));
            if (candidates.Count > 1) throw OpException.Ambiguous("link", "link", value, candidates.Select(e => e.Name + " (id " + e.Id.Value.ToString(CultureInfo.InvariantCulture) + ")"));
            throw OpException.NotFound("link", "link", value, global::RevitMcpNext.Addin.Resolve.Closest(text, names));
        }

        private static bool NameMatches(string name, string text)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) return true;
            string bare = System.IO.Path.GetFileNameWithoutExtension(name);
            return string.Equals(bare, text, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(bare, System.IO.Path.GetFileNameWithoutExtension(text), StringComparison.OrdinalIgnoreCase) && System.IO.Path.HasExtension(text);
        }

        private static LinkTarget FromElement(Document doc, Element element)
        {
            switch (element)
            {
                case RevitLinkType rvt:
                    return ForRvt(doc, rvt, null);
                case RevitLinkInstance instance:
                    return ForRvt(doc, doc.GetElement(instance.GetTypeId()) as RevitLinkType, instance.Id);
                case CADLinkType cad:
                    return ForCad(doc, cad, null);
                case ImportInstance import:
                {
                    CADLinkType type = doc.GetElement(import.GetTypeId()) as CADLinkType;
                    if (type != null) return ForCad(doc, type, import.Id);
                    return new LinkTarget { Name = import.Name, TypeId = ElementId.InvalidElementId, Instances = new List<ElementId> { import.Id }, InstanceId = import.Id, Loaded = true };
                }
                default:
                    return null;
            }
        }

        private static LinkTarget ForRvt(Document doc, RevitLinkType type, ElementId instanceId)
        {
            if (type == null) return null;
            if (type.IsNestedLink)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + type.Name + "' is a nested link; change it through its parent link.",
                    new Dictionary<string, object> { ["link"] = type.Name });
            }
            List<ElementId> instances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Where(i => i.GetTypeId() == type.Id).Select(i => i.Id).ToList();
            string path = null;
            try
            {
                ExternalFileReference reference = type.GetExternalFileReference();
                if (reference != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            }
            catch
            {
                path = null;
            }
            bool loaded;
            try { loaded = RevitLinkType.IsLoaded(doc, type.Id); } catch { loaded = false; }
            return new LinkTarget { Name = type.Name, Path = path, Rvt = type, TypeId = type.Id, InstanceId = instanceId, Instances = instances, Loaded = loaded };
        }

        private static LinkTarget ForCad(Document doc, CADLinkType type, ElementId instanceId)
        {
            List<ElementId> instances = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => i.GetTypeId() == type.Id).Select(i => i.Id).ToList();
            string path = null;
            try
            {
                ExternalFileReference reference = type.GetExternalFileReference();
                if (reference != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            }
            catch
            {
                path = null;
            }
            return new LinkTarget { Name = type.Name, Path = path, Cad = type, TypeId = type.Id, InstanceId = instanceId, Instances = instances, Loaded = true };
        }
    }
}
