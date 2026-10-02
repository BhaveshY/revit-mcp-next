using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private static bool IsViewWorkflowOperation(string type) => new[] {
            "create_plan_view", "duplicate_view", "duplicate_sheet", "copy_view_annotations", "create_dimension", "update_dimension"
        }.Contains(type, StringComparer.Ordinal);

        private BridgeResponseEnvelope HandleViewWorkflow(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document doc = ResolveDocument(app, request);
            if (doc == null) return Failure(request, "NO_ACTIVE_DOCUMENT", "Open the targeted project document.", sw);
            var failure = ValidateExpectedGeneration(request, doc, sw, out long generation);
            if (failure != null) return failure;
            var payload = request.Payload;
            if (request.Operation == "get_dimensions")
            {
                var ids = GetStringList(payload, "elementIds");
                if (ids.Count < 1 || ids.Count > 100) throw new InvalidOperationException("Provide 1 to 100 explicit dimension elementIds.");
                var items = ids.Select(id => DimensionData(RequireElement<Dimension>(doc, id))).ToArray();
                return Success(request, new Dictionary<string, object> { ["document"] = BuildDocumentReference(doc, generation), ["items"] = items, ["units"] = "mm", ["source"] = "revit-api" }, sw, generation:generation);
            }
            View view = ResolveWorkflowView(doc, payload);
            if (request.Operation == "activate_view")
            {
                if (view.IsTemplate || view.ViewType == ViewType.Internal || !IsExactDocumentIdentity(doc, app.ActiveUIDocument?.Document))
                    throw new InvalidOperationException("Activation requires a non-template view in the exact active UI document.");
                if (doc.IsModifiable) throw new InvalidOperationException("Cannot activate a view during a transaction.");
                app.ActiveUIDocument.ActiveView = view;
                if (app.ActiveUIDocument.ActiveView.Id != view.Id) throw new InvalidOperationException("Revit did not activate the requested view.");
            }
            return Success(request, new Dictionary<string, object> { ["document"] = BuildDocumentReference(doc, generation), ["view"] = ViewDocumentationData(view), ["source"] = "revit-api" }, sw, generation:generation);
        }

        private static T RequireElement<T>(Document doc, string id) where T : Element
        {
            return ResolveElement(doc, id) as T ?? throw new InvalidOperationException("Expected " + typeof(T).Name + " at element ID " + id + ".");
        }

        private static View ResolveWorkflowView(Document doc, Dictionary<string, object> payload)
        {
            string id = GetString(payload, "viewId"), name = GetString(payload, "viewName");
            if (!string.IsNullOrWhiteSpace(id))
            {
                var view = RequireElement<View>(doc, id);
                if (!string.IsNullOrWhiteSpace(name) && view.Name != name) throw new InvalidOperationException("viewId and viewName identify different views.");
                return view;
            }
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Provide viewId or an exact viewName.");
            var matches = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.Name == name).Take(2).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("View name was missing or ambiguous. Use an exact viewId.");
            return matches[0];
        }

        private static Dictionary<string, object> ViewDocumentationData(View view)
        {
            var data = new Dictionary<string, object> {
                ["id"] = ToElementIdString(view.Id), ["uniqueId"] = view.UniqueId, ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(), ["templateId"] = ToElementIdString(view.ViewTemplateId),
                ["scale"] = view.Scale, ["isTemplate"] = view.IsTemplate,
                ["direction"] = VectorData(view.ViewDirection), ["rightDirection"] = VectorData(view.RightDirection), ["upDirection"] = VectorData(view.UpDirection),
                ["settings"] = ViewSettingParameters(view), ["units"] = "mm"
            };
            if (!view.IsTemplate && !(view is ViewSheet) && !(view is ViewSchedule))
            {
                data["cropActive"] = view.CropBoxActive; data["cropVisible"] = view.CropBoxVisible;
                var box = view.CropBox;
                if (box != null) data["cropBox"] = new Dictionary<string, object> { ["min"] = PointValue(box.Min), ["max"] = PointValue(box.Max), ["origin"] = PointValue(box.Transform.Origin), ["basisX"] = VectorData(box.Transform.BasisX), ["basisY"] = VectorData(box.Transform.BasisY), ["basisZ"] = VectorData(box.Transform.BasisZ) };
                using (var crop = view.GetCropRegionShapeManager())
                {
                    data["cropLoops"] = crop.GetCropShape().Select(loop => loop.Select(CurveData).ToArray()).ToArray();
                    if (crop.CanHaveAnnotationCrop) data["annotationCropOffsets"] = new[] { crop.LeftAnnotationCropOffset * 304.8, crop.RightAnnotationCropOffset * 304.8, crop.TopAnnotationCropOffset * 304.8, crop.BottomAnnotationCropOffset * 304.8 };
                }
            }
            if (view is ViewPlan plan)
            {
                data["levelId"] = ToElementIdString(plan.GenLevel.Id);
                using (var range = plan.GetViewRange()) data["viewRange"] = new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane }.Select(plane => new Dictionary<string, object> { ["plane"] = plane.ToString(), ["levelId"] = ToElementIdString(range.GetLevelId(plane)), ["offset"] = range.GetOffset(plane) * 304.8 }).ToArray();
            }
            if (view is ViewSheet sheet)
            {
                data["sheetNumber"] = sheet.SheetNumber;
                data["viewports"] = sheet.GetAllViewports().Select(id => ViewportData(RequireElement<Viewport>(view.Document, ToElementIdString(id)))).ToArray();
                data["schedules"] = SheetSchedules(sheet).Select(s => new Dictionary<string, object> { ["id"] = ToElementIdString(s.Id), ["scheduleId"] = ToElementIdString(s.ScheduleId), ["point"] = PointValue(s.Point), ["isTitleblockRevisionSchedule"] = s.IsTitleblockRevisionSchedule }).ToArray();
                var owned = new FilteredElementCollector(view.Document).OwnedByView(sheet.Id).WhereElementIsNotElementType();
                data["ownedElementCount"] = owned.GetElementCount();
                data["ownedElements"] = owned.Take(1000).Select(e => new Dictionary<string, object> { ["id"] = ToElementIdString(e.Id), ["uniqueId"] = e.UniqueId, ["class"] = e.GetType().Name, ["category"] = e.Category?.Name, ["typeId"] = ToElementIdString(e.GetTypeId()) }).ToArray();
                data["ownedElementsTruncated"] = (int)data["ownedElementCount"] > 1000;
                data["parameters"] = SheetParameterData(sheet);
            }
            return data;
        }

        private static object VectorData(XYZ point) => new Dictionary<string, object> { ["x"] = point.X, ["y"] = point.Y, ["z"] = point.Z };

        private static Dictionary<string, object> ViewSettingParameters(View view)
        {
            var data = new Dictionary<string, object>();
            foreach (string name in new[] { "VIEW_PHASE", "VIEW_PHASE_FILTER", "VIEW_DISCIPLINE", "VIEWER_VOLUME_OF_INTEREST_CROP", "VIEWER_ANNOTATION_CROP_ACTIVE", "VIEW_DETAIL_LEVEL" })
                if (Enum.TryParse(name, out BuiltInParameter bip))
                {
                    var p = view.get_Parameter(bip);
                    if (p != null) data[name] = ParameterSnapshot(p);
                }
            return data;
        }

        private static Dictionary<string, object> ViewportData(Viewport viewport)
        {
            var data = new Dictionary<string, object> { ["id"] = ToElementIdString(viewport.Id), ["viewId"] = ToElementIdString(viewport.ViewId), ["center"] = PointValue(viewport.GetBoxCenter()), ["typeId"] = ToElementIdString(viewport.GetTypeId()), ["rotation"] = viewport.Rotation.ToString() };
#if !REVIT2021
            data["labelOffset"] = PointValue(viewport.LabelOffset); data["labelLineLength"] = viewport.LabelLineLength * 304.8;
#endif
            return data;
        }

        private static List<ScheduleSheetInstance> SheetSchedules(ViewSheet sheet) => new FilteredElementCollector(sheet.Document).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Where(s => s.OwnerViewId == sheet.Id).ToList();

        private static object CurveData(Curve curve)
        {
            if (curve == null) return null;
            var data = new Dictionary<string, object> { ["kind"] = curve.GetType().Name, ["bounded"] = curve.IsBound };
            if (curve.IsBound) { data["start"] = PointValue(curve.GetEndPoint(0)); data["end"] = PointValue(curve.GetEndPoint(1)); }
            if (curve is Line line) { data["origin"] = PointValue(line.Origin); data["direction"] = VectorData(line.Direction); }
            if (curve is Arc arc) { data["center"] = PointValue(arc.Center); data["radius"] = arc.Radius * 304.8; data["normal"] = VectorData(arc.Normal); }
            return data;
        }

        private static Dictionary<string, object> DimensionData(Dimension dim)
        {
            var refs = new List<Dictionary<string, object>>();
            foreach (Reference reference in dim.References)
            {
                var item = new Dictionary<string, object> { ["stableRepresentation"] = reference.ConvertToStableRepresentation(dim.Document), ["elementId"] = ToElementIdString(reference.ElementId), ["linkedElementId"] = ToElementIdString(reference.LinkedElementId), ["referenceType"] = reference.ElementReferenceType.ToString() };
                refs.Add(item);
            }
            var data = new Dictionary<string, object> { ["id"] = ToElementIdString(dim.Id), ["uniqueId"] = dim.UniqueId, ["viewId"] = ToElementIdString(dim.OwnerViewId), ["dimensionTypeId"] = ToElementIdString(dim.GetTypeId()), ["style"] = dim.DimensionType.StyleType.ToString(), ["references"] = refs, ["curve"] = CurveData(dim.Curve), ["units"] = "mm", ["witnessLineControl"] = "Geometry references and dimension type parameters; Revit does not expose independent witness-line endpoints." };
            var text = new List<Dictionary<string, object>>();
            if (dim.NumberOfSegments > 0)
            {
                int index = 0;
                foreach (DimensionSegment segment in dim.Segments)
                {
                    var row = new Dictionary<string, object> { ["segmentIndex"] = index++, ["valueOverride"] = segment.ValueOverride, ["prefix"] = segment.Prefix, ["suffix"] = segment.Suffix, ["above"] = segment.Above, ["below"] = segment.Below, ["formattedValue"] = segment.ValueString, ["origin"] = PointValue(segment.Origin), ["isLocked"] = segment.IsLocked };
                    if (segment.IsTextPositionAdjustable()) ReadDimensionPoint(row, "textPosition", () => segment.TextPosition);
                    ReadDimensionPoint(row, "leaderEndPosition", () => segment.LeaderEndPosition);
                    text.Add(row);
                }
            }
            else
            {
                var row = new Dictionary<string, object> { ["valueOverride"] = dim.ValueOverride, ["prefix"] = dim.Prefix, ["suffix"] = dim.Suffix, ["above"] = dim.Above, ["below"] = dim.Below, ["formattedValue"] = dim.ValueString, ["isLocked"] = dim.IsLocked };
                if (dim.IsTextPositionAdjustable()) ReadDimensionPoint(row, "textPosition", () => dim.TextPosition);
                ReadDimensionPoint(row, "leaderEndPosition", () => dim.LeaderEndPosition);
                text.Add(row);
            }
            data["text"] = text;
            data["referencesAvailable"] = dim.AreReferencesAvailable;
            var witness = new Dictionary<string, object>();
            foreach (string name in Enum.GetNames(typeof(BuiltInParameter)).Where(n => n.StartsWith("DIM_WITNS", StringComparison.Ordinal) || n == "DIM_LINE_EXTENSION" || n == "EQUALITY_WITNESS_DISPLAY"))
            {
                var parameter = dim.DimensionType.get_Parameter((BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), name));
                if (parameter != null)
                {
                    var snapshot = ParameterSnapshot(parameter);
                    if (parameter.StorageType == StorageType.Double) snapshot["value"] = LengthValue(parameter.AsDouble());
                    witness[name] = snapshot;
                }
            }
            data["witnessLineTypeParameters"] = witness;
            return data;
        }

        private static void ReadDimensionPoint(Dictionary<string, object> row, string key, Func<XYZ> read)
        {
            try { var value = read(); row[key] = value == null ? null : PointValue(value); }
            catch (Exception ex) { row[key] = null; row[key + "UnavailableReason"] = ex.Message; }
        }

        private static Dictionary<string, object> PreviewViewOperation(Document doc, Dictionary<string, object> op, int index)
        {
            try
            {
                ValidateViewOperation(doc, op);
                if (GetString(op, "type") == "create_dimension" || (GetString(op, "type") == "update_dimension" && op.ContainsKey("references")))
                {
                    using (var probe = new Transaction(doc, "Revit MCP preview dimension references"))
                    {
                        if (probe.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot validate dimension references in the current document state.");
                        ConfigurePreviewProbeTransaction(probe);
                        try { ApplyViewOperation(doc, op, index); }
                        finally { RollBackPreviewProbeTransaction(probe); }
                    }
                }
                Element target = ResolveElement(doc, GetString(op, "viewId") ?? GetString(op, "sheetId") ?? GetString(op, "elementId") ?? GetString(op, "sourceViewId") ?? GetString(op, "levelId"));
                Dictionary<string, object> before = target is View v ? ViewDocumentationData(v) : target is Dimension d ? DimensionData(d) : null;
                return Change(op, index, "ready", target: target == null ? null : ElementTarget(target, null), before:before, after:new Dictionary<string, object>(op));
            }
            catch (Exception ex) { return BlockedChange(op, index, ex.Message); }
        }

        private static void EnsureViewName(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new InvalidOperationException("A unique non-empty view name (max 256 characters) is required.");
            if (new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("View name already exists: " + name);
        }

        private static void ValidateViewOperation(Document doc, Dictionary<string, object> op)
        {
            string type = GetString(op, "type");
            if (doc.IsReadOnly || doc.IsFamilyDocument) throw new InvalidOperationException("A writable project document is required.");
            if (type == "create_plan_view")
            {
                RequireElement<Level>(doc, GetString(op, "levelId"));
                if (RequireElement<ViewFamilyType>(doc, GetString(op, "viewFamilyTypeId")).ViewFamily != ViewFamily.FloorPlan) throw new InvalidOperationException("viewFamilyTypeId must be a FloorPlan type.");
                EnsureViewName(doc, GetString(op, "name"));
                string templateId = GetString(op, "templateId");
                if (templateId != null && !RequireElement<View>(doc, templateId).IsTemplate) throw new InvalidOperationException("templateId must identify a view template.");
            }
            else if (type == "duplicate_view" || type == "duplicate_sheet")
            {
                var source = RequireElement<View>(doc, GetString(op, type == "duplicate_sheet" ? "sheetId" : "viewId"));
                string error = ValidateExpectedUniqueId(op, source, ToElementIdString(source.Id));
                if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
                if (type == "duplicate_sheet")
                {
#if REVIT2021
                    throw new InvalidOperationException("Complete native sheet duplication requires Revit 2024 or 2027; Revit 2021 has no supported ViewSheet.Duplicate API.");
#else
                    var sheet = source as ViewSheet ?? throw new InvalidOperationException("sheetId must be a ViewSheet.");
                    string number = GetString(op, "sheetNumber"), prefix = GetString(op, "viewNamePrefix");
                    if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(prefix)) throw new InvalidOperationException("sheetNumber and viewNamePrefix are required.");
                    if (new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Any(s => s.SheetNumber == number)) throw new InvalidOperationException("Sheet number already exists.");
                    if (sheet.GetAllViewports().Count > 100) throw new InvalidOperationException("Sheet exceeds the 100-viewport transaction bound.");
                    foreach (var id in sheet.GetAllPlacedViews()) EnsureViewName(doc, prefix + doc.GetElement(id).Name);
#endif
                }
                else
                {
                    if (source is ViewSheet || source.IsTemplate || !source.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing)) throw new InvalidOperationException("This view cannot be duplicated with detailing.");
                    if (GetString(op, "name") != null) EnsureViewName(doc, GetString(op, "name"));
                    bool hasSheet = GetString(op, "sheetId") != null, hasCenter = GetDictionary(op, "center") != null;
                    if (hasSheet != hasCenter) throw new InvalidOperationException("sheetId and center must be supplied together.");
                    if (hasSheet) { RequireElement<ViewSheet>(doc, GetString(op, "sheetId")); ToInternalSheetPoint(GetDictionary(op, "center"), "center"); }
                }
            }
            else if (type == "copy_view_annotations")
            {
                var source = RequireElement<View>(doc, GetString(op, "sourceViewId")); var target = RequireElement<View>(doc, GetString(op, "targetViewId"));
                if (source.Id == target.Id || source.IsTemplate || target.IsTemplate) throw new InvalidOperationException("Select distinct non-template views.");
                ElementTransformUtils.GetTransformFromViewToView(source, target);
                var ids = GetStringList(op, "elementIds");
                if (ids.Count < 1 || ids.Count > 500 || ids.Distinct().Count() != ids.Count) throw new InvalidOperationException("Provide 1 to 500 distinct annotation IDs.");
                foreach (var id in ids)
                {
                    var element = RequireElement<Element>(doc, id);
                    if (!element.ViewSpecific || element.OwnerViewId != source.Id || element is Viewport || element is ScheduleSheetInstance || element is View)
                        throw new InvalidOperationException("Element " + id + " is not a copyable annotation owned by the source view.");
                }
            }
            else if (type == "create_dimension")
            {
                var view = RequireElement<View>(doc, GetString(op, "viewId"));
                if (view.IsTemplate || view is View3D || view is ViewSheet || view is ViewSchedule) throw new InvalidOperationException("Dimensions require a graphical 2D view.");
                if (RequireElement<DimensionType>(doc, GetString(op, "dimensionTypeId")).StyleType != DimensionStyleType.Linear) throw new InvalidOperationException("create_dimension requires a linear dimension type.");
                DimensionReferences(doc, op);
                Line.CreateBound(ToInternalPoint(GetDictionary(op, "start"), "start"), ToInternalPoint(GetDictionary(op, "end"), "end"));
            }
            else if (type == "update_dimension")
            {
                var dimension = RequireElement<Dimension>(doc, GetString(op, "elementId"));
                var error = ValidateExpectedUniqueId(op, dimension, ToElementIdString(dimension.Id));
                if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
                if (op.ContainsKey("references"))
                {
                    if (dimension.DimensionType.StyleType != DimensionStyleType.Linear || dimension.IsLocked || dimension.AreSegmentsEqual || dimension.Segments.Cast<DimensionSegment>().Any(s => s.IsLocked)) throw new InvalidOperationException("Reference replacement requires an unlocked linear dimension without equality constraints.");
                    DimensionReferences(doc, op);
                    Line.CreateBound(ToInternalPoint(GetDictionary(op, "start"), "start"), ToInternalPoint(GetDictionary(op, "end"), "end"));
                    if (GetDictionary(op, "translation") != null) throw new InvalidOperationException("Reference replacement uses start/end; omit translation.");
                }
                else if (op.ContainsKey("start") || op.ContainsKey("end")) throw new InvalidOperationException("start/end require references for atomic dimension recreation.");
                if (dimension.Pinned) throw new InvalidOperationException("Unpin the dimension explicitly before editing it.");
                if (GetString(op, "dimensionTypeId") != null && !dimension.IsValidType(RequireElement<DimensionType>(doc, GetString(op, "dimensionTypeId")).Id)) throw new InvalidOperationException("Incompatible dimension type.");
                if (GetDictionary(op, "translation") != null) ToInternalPoint(GetDictionary(op, "translation"), "translation");
                if (GetDictionary(op, "translation") == null && GetString(op, "dimensionTypeId") == null && !op.ContainsKey("text") && !op.ContainsKey("references")) throw new InvalidOperationException("Provide dimension type, translation or text edits.");
            }
        }

        private static ReferenceArray DimensionReferences(Document doc, Dictionary<string, object> op)
        {
            var values = GetStringList(op, "references");
            if (values.Count < 2 || values.Count > 256 || values.Distinct().Count() != values.Count) throw new InvalidOperationException("Provide 2 to 256 distinct stable geometry references.");
            var refs = new ReferenceArray();
            foreach (var value in values)
            {
                var reference = Reference.ParseFromStableRepresentation(doc, value);
                Element referenced = doc.GetElement(reference.ElementId);
                if (referenced == null) throw new InvalidOperationException("Dimension reference no longer resolves: " + value);
                // Dimension.References serializes grid datums as surfaces. NewDimension requires the datum element reference, not its curve/surface reference.
                if (referenced is Grid && reference.LinkedElementId == ElementId.InvalidElementId) reference = new Reference(referenced);
                refs.Append(reference);
            }
            return refs;
        }

        private static Dictionary<string, object> ApplyViewOperation(Document doc, Dictionary<string, object> op, int index)
        {
            ValidateViewOperation(doc, op);
            string type = GetString(op, "type"); Element result = null;
            if (type == "create_plan_view")
            {
                var view = ViewPlan.Create(doc, RequireElement<ViewFamilyType>(doc, GetString(op, "viewFamilyTypeId")).Id, RequireElement<Level>(doc, GetString(op, "levelId")).Id);
                view.Name = GetString(op, "name");
                if (GetString(op, "templateId") != null) view.ViewTemplateId = RequireElement<View>(doc, GetString(op, "templateId")).Id;
                result = view;
            }
            else if (type == "duplicate_view")
            {
                var source = RequireElement<View>(doc, GetString(op, "viewId"));
                var view = (View)doc.GetElement(source.Duplicate(ViewDuplicateOption.WithDetailing));
                if (GetString(op, "name") != null) view.Name = GetString(op, "name");
                result = view;
                doc.Regenerate(); VerifyViewSettings(source, view);
                if (GetString(op, "sheetId") != null)
                {
                    var sheet = RequireElement<ViewSheet>(doc, GetString(op, "sheetId"));
                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)) throw new InvalidOperationException("Duplicated view cannot be placed on the target sheet.");
                    var center = ToInternalSheetPoint(GetDictionary(op, "center"), "center");
                    Viewport.Create(doc, sheet.Id, view.Id, center);
                }
            }
            else if (type == "duplicate_sheet")
            {
#if !REVIT2021
                var source = RequireElement<ViewSheet>(doc, GetString(op, "sheetId"));
                var sheet = (ViewSheet)doc.GetElement(source.Duplicate(SheetDuplicateOption.DuplicateSheetWithViewsAndDetailing));
                CopySheetParameters(source, sheet);
                sheet.SheetNumber = GetString(op, "sheetNumber"); sheet.Name = GetString(op, "name") ?? source.Name;
                doc.Regenerate();
                var remaining = sheet.GetAllViewports().Select(id => (Viewport)doc.GetElement(id)).ToList();
                foreach (var sourceId in source.GetAllViewports())
                {
                    var original = (Viewport)doc.GetElement(sourceId);
                    var nearest = remaining.OrderBy(v => v.GetBoxCenter().DistanceTo(original.GetBoxCenter())).FirstOrDefault();
                    if (nearest == null || nearest.GetBoxCenter().DistanceTo(original.GetBoxCenter()) > 1e-5) throw new InvalidOperationException("Native sheet duplication did not preserve viewport placement; transaction rolled back.");
                    remaining.Remove(nearest);
                    var from = (View)doc.GetElement(original.ViewId); var to = (View)doc.GetElement(nearest.ViewId);
                    if (to.Id != from.Id) { to.Name = GetString(op, "viewNamePrefix") + from.Name; VerifyViewSettings(from, to); }
                    nearest.ChangeTypeId(original.GetTypeId()); nearest.Rotation = original.Rotation;
                    nearest.LabelOffset = original.LabelOffset; nearest.LabelLineLength = original.LabelLineLength;
                    nearest.SetBoxCenter(original.GetBoxCenter());
                }
                if (remaining.Count != 0) throw new InvalidOperationException("Native sheet duplication returned unexpected viewports.");
                var originals = SheetSchedules(source).Where(s => !s.IsTitleblockRevisionSchedule).ToList();
                var copies = SheetSchedules(sheet).Where(s => !s.IsTitleblockRevisionSchedule).ToList();
                foreach (var original in originals)
                {
                    var copy = copies.OrderBy(s => s.Point.DistanceTo(original.Point)).FirstOrDefault();
                    if (copy == null || copy.Point.DistanceTo(original.Point) > 1e-5) throw new InvalidOperationException("Native sheet duplication did not preserve schedule placement; transaction rolled back.");
                    copies.Remove(copy);
                }
                if (copies.Count != 0) throw new InvalidOperationException("Unexpected duplicated schedule instances.");
                result = sheet;
#endif
            }
            else if (type == "copy_view_annotations")
            {
                var source = RequireElement<View>(doc, GetString(op, "sourceViewId")); var target = RequireElement<View>(doc, GetString(op, "targetViewId"));
                var ids = GetStringList(op, "elementIds").Select(id => RequireElement<Element>(doc, id).Id).ToList();
                using (var options = new CopyPasteOptions())
                {
                    options.SetDuplicateTypeNamesHandler(new AbortDuplicateAnnotationTypes());
                    var copied = ElementTransformUtils.CopyElements(source, ids, target, Transform.Identity, options);
                    doc.Regenerate();
                    foreach (var group in ids.Select(id => doc.GetElement(id)).GroupBy(e => e.GetType()))
                        if (copied.Select(id => doc.GetElement(id)).Count(e => e != null && e.GetType() == group.Key && e.OwnerViewId == target.Id) < group.Count())
                            throw new InvalidOperationException("Revit omitted an annotation or its references. Copy rolled back.");
                    return Change(op, index, "applied", target:ElementTarget(target, null), before:null, after:new Dictionary<string, object> { ["createdElementIds"] = copied.Select(ToElementIdString).ToArray(), ["alignment"] = "native-view-to-view" });
                }
            }
            else if (type == "create_dimension")
            {
                var view = RequireElement<View>(doc, GetString(op, "viewId"));
                var line = Line.CreateBound(ToInternalPoint(GetDictionary(op, "start"), "start"), ToInternalPoint(GetDictionary(op, "end"), "end"));
                var dimension = doc.Create.NewDimension(view, line, DimensionReferences(doc, op), RequireElement<DimensionType>(doc, GetString(op, "dimensionTypeId")));
                if (dimension == null) throw new InvalidOperationException("Revit could not create a dimension with these references.");
                doc.Regenerate(); ApplyDimensionText(dimension, op); result = dimension;
            }
            else if (type == "update_dimension")
            {
                var dimension = RequireElement<Dimension>(doc, GetString(op, "elementId"));
                if (op.ContainsKey("references"))
                {
                    var original = dimension;
                    var view = (View)doc.GetElement(original.OwnerViewId);
                    var dimensionType = GetString(op, "dimensionTypeId") == null ? original.DimensionType : RequireElement<DimensionType>(doc, GetString(op, "dimensionTypeId"));
                    var line = Line.CreateBound(ToInternalPoint(GetDictionary(op, "start"), "start"), ToInternalPoint(GetDictionary(op, "end"), "end"));
                    dimension = doc.Create.NewDimension(view, line, DimensionReferences(doc, op), dimensionType);
                    if (dimension == null) throw new InvalidOperationException("Revit could not replace the dimension references.");
                    doc.Regenerate();
                    if (original.NumberOfSegments != dimension.NumberOfSegments && !op.ContainsKey("text")) throw new InvalidOperationException("Changed segment count requires explicit text edits to prevent misaligned overrides.");
                    if (original.NumberOfSegments == dimension.NumberOfSegments) CopyDimensionText(original, dimension);
                    ElementId originalId = original.Id;
                    var deleted = doc.Delete(originalId);
                    if (deleted.Count != 1 || !deleted.Contains(originalId)) throw new InvalidOperationException("Reference replacement would delete dependent elements; transaction rolled back.");
                }
                if (GetString(op, "dimensionTypeId") != null) dimension.ChangeTypeId(RequireElement<DimensionType>(doc, GetString(op, "dimensionTypeId")).Id);
                if (GetDictionary(op, "translation") != null) ElementTransformUtils.MoveElement(doc, dimension.Id, ToInternalPoint(GetDictionary(op, "translation"), "translation"));
                doc.Regenerate(); ApplyDimensionText(dimension, op); result = dimension;
            }
            if (result == null) throw new InvalidOperationException("No documentation element was created or updated.");
            doc.Regenerate();
            return Change(op, index, "applied", target:ElementTarget(result, null), before:null, after:result is View v ? ViewDocumentationData(v) : DimensionData((Dimension)result));
        }

        private static void VerifyViewSettings(View source, View target)
        {
            var left = ViewDocumentationData(source); var right = ViewDocumentationData(target);
            foreach (string field in new[] { "templateId", "scale", "settings", "cropActive", "cropVisible", "cropBox", "cropLoops", "annotationCropOffsets", "viewRange" })
            {
                left.TryGetValue(field, out object a); right.TryGetValue(field, out object b);
                // Both snapshots are generated by the same serializers and API, in deterministic order.
                if (CanonicalJson.Serialize(a) != CanonicalJson.Serialize(b)) throw new InvalidOperationException("Duplicated view did not preserve " + field + "; transaction rolled back.");
            }
        }

        private static void CopyDimensionText(Dimension source, Dimension target)
        {
            if (source.NumberOfSegments == 0)
            {
                target.ValueOverride = source.ValueOverride; target.Prefix = source.Prefix; target.Suffix = source.Suffix; target.Above = source.Above; target.Below = source.Below;
                if (source.IsTextPositionAdjustable() && target.IsTextPositionAdjustable() && TryReadDimensionPoint(() => source.TextPosition, out XYZ point)) target.TextPosition = point;
                return;
            }
            for (int i = 0; i < source.NumberOfSegments; i++)
            {
                var a = source.Segments.get_Item(i); var b = target.Segments.get_Item(i);
                b.ValueOverride = a.ValueOverride; b.Prefix = a.Prefix; b.Suffix = a.Suffix; b.Above = a.Above; b.Below = a.Below;
                if (a.IsTextPositionAdjustable() && b.IsTextPositionAdjustable() && TryReadDimensionPoint(() => a.TextPosition, out XYZ point)) b.TextPosition = point;
            }
        }

        private static bool TryReadDimensionPoint(Func<XYZ> read, out XYZ point)
        {
            try { point = read(); return point != null; }
            catch (Exception) { point = null; return false; }
        }

        private static Dictionary<string, object> SheetParameterData(ViewSheet sheet)
        {
            var result = new Dictionary<string, object>();
            foreach (Parameter p in sheet.GetOrderedParameters())
                if (!p.IsReadOnly && p.HasValue) result[ToElementIdString(p.Id)] = ParameterSnapshot(p);
            return result;
        }

        private static void CopySheetParameters(ViewSheet source, ViewSheet target)
        {
            foreach (Parameter from in source.GetOrderedParameters())
            {
                if (from.IsReadOnly || !from.HasValue || from.Id == new ElementId(BuiltInParameter.SHEET_NUMBER) || from.Id == new ElementId(BuiltInParameter.SHEET_NAME)) continue;
                Parameter to = target.get_Parameter(from.Definition);
                if (to == null || to.IsReadOnly || to.StorageType != from.StorageType) throw new InvalidOperationException("Cannot preserve sheet parameter: " + from.Definition.Name);
                bool success = true;
                switch (from.StorageType)
                {
                    case StorageType.String: if (to.AsString() != from.AsString()) success = to.Set(from.AsString()); break;
                    case StorageType.Double: if (to.AsDouble() != from.AsDouble()) success = to.Set(from.AsDouble()); break;
                    case StorageType.Integer: if (to.AsInteger() != from.AsInteger()) success = to.Set(from.AsInteger()); break;
                    case StorageType.ElementId: if (to.AsElementId() != from.AsElementId()) success = to.Set(from.AsElementId()); break;
                }
                if (!success) throw new InvalidOperationException("Revit refused sheet parameter: " + from.Definition.Name);
            }
        }

        private static void ApplyDimensionText(Dimension dim, Dictionary<string, object> op)
        {
            var edits = GetDictionaryList(op, "text");
            var seen = new HashSet<int>();
            foreach (var edit in edits)
            {
                int? segment = GetInt(edit, "segmentIndex");
                if (!seen.Add(segment ?? -1)) throw new InvalidOperationException("Duplicate text edit for the same dimension segment.");
                if (dim.NumberOfSegments > 0)
                {
                    if (!segment.HasValue || segment < 0 || segment >= dim.NumberOfSegments) throw new InvalidOperationException("Provide a valid segmentIndex for a multi-segment dimension.");
                    DimensionSegment row = dim.Segments.get_Item(segment.Value);
                    if (edit.ContainsKey("valueOverride")) row.ValueOverride = GetString(edit, "valueOverride");
                    if (edit.ContainsKey("prefix")) row.Prefix = GetString(edit, "prefix");
                    if (edit.ContainsKey("suffix")) row.Suffix = GetString(edit, "suffix");
                    if (edit.ContainsKey("above")) row.Above = GetString(edit, "above");
                    if (edit.ContainsKey("below")) row.Below = GetString(edit, "below");
                    if (GetDictionary(edit, "textPosition") != null) row.TextPosition = ToInternalPoint(GetDictionary(edit, "textPosition"), "textPosition");
                }
                else
                {
                    if (segment.HasValue) throw new InvalidOperationException("Single-segment dimensions do not use segmentIndex.");
                    if (edit.ContainsKey("valueOverride")) dim.ValueOverride = GetString(edit, "valueOverride");
                    if (edit.ContainsKey("prefix")) dim.Prefix = GetString(edit, "prefix");
                    if (edit.ContainsKey("suffix")) dim.Suffix = GetString(edit, "suffix");
                    if (edit.ContainsKey("above")) dim.Above = GetString(edit, "above");
                    if (edit.ContainsKey("below")) dim.Below = GetString(edit, "below");
                    if (GetDictionary(edit, "textPosition") != null) dim.TextPosition = ToInternalPoint(GetDictionary(edit, "textPosition"), "textPosition");
                }
            }
        }

        private sealed class AbortDuplicateAnnotationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.Abort;
        }
    }
}
