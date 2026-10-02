using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private static Dictionary<string, object> PreviewCreateTextNote(Document document, Dictionary<string, object> operation, int index)
        {
            string viewId = GetString(operation, "viewId");
            string text = GetString(operation, "text");
            Dictionary<string, object> positionValue = GetDictionary(operation, "position");
            if (string.IsNullOrWhiteSpace(viewId)) return BlockedChange(operation, index, "create_text_note requires viewId.");
            if (string.IsNullOrWhiteSpace(text)) return BlockedChange(operation, index, "create_text_note requires text.");
            if (text.Length > 2048) return BlockedChange(operation, index, "create_text_note text can contain at most 2048 characters.");
            if (positionValue == null) return BlockedChange(operation, index, "create_text_note requires position.");

            View view = ResolveElement(document, viewId) as View;
            string viewError = ValidateTextNoteView(view);
            if (!string.IsNullOrWhiteSpace(viewError)) return BlockedChange(operation, index, viewError);

            TextNoteType textNoteType = ResolveTextNoteType(document, GetString(operation, "textNoteTypeId"));
            if (textNoteType == null) return BlockedChange(operation, index, "A usable TextNoteType was not found.");

            XYZ position;
            double? width = null;
            double rotationRadians = 0;
            bool hasRotation = false;
            try
            {
                position = ToInternalPoint(positionValue, "position");
                Dictionary<string, object> widthValue = GetDictionary(operation, "width");
                if (widthValue != null)
                {
                    width = ToInternalLength(widthValue, "width");
                    if (width.Value <= 0) return BlockedChange(operation, index, "create_text_note width must be greater than zero.");
                }

                Dictionary<string, object> rotationValue = GetDictionary(operation, "rotation");
                if (rotationValue != null)
                {
                    rotationRadians = ToInternalAngle(rotationValue);
                    if (!IsFinite(rotationRadians)) return BlockedChange(operation, index, "create_text_note rotation must be a finite angle.");
                    hasRotation = true;
                }
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            var after = new Dictionary<string, object>
            {
                ["viewId"] = ToElementIdString(view.Id),
                ["viewName"] = SafeElementName(view),
                ["textNoteTypeId"] = ToElementIdString(textNoteType.Id),
                ["textNoteTypeName"] = SafeElementName(textNoteType),
                ["textLength"] = text.Length,
                ["position"] = PointValue(position)
            };
            if (width.HasValue) after["width"] = LengthValue(width.Value);
            if (hasRotation) after["rotation"] = AngleValue(rotationRadians);

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["view"] = BuildViewSummary(view),
                    ["textNoteType"] = ElementSummary(document, textNoteType)
                },
                before: null,
                after: after);
        }

        private static Dictionary<string, object> ApplyCreateTextNote(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateTextNote(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_text_note preview failed.");
            }

            View view = ResolveElement(document, GetString(operation, "viewId")) as View;
            TextNoteType textNoteType = ResolveTextNoteType(document, GetString(operation, "textNoteTypeId"));
            XYZ position = ToInternalPoint(GetDictionary(operation, "position"), "position");
            TextNoteOptions options = new TextNoteOptions(textNoteType.Id);
            Dictionary<string, object> rotationValue = GetDictionary(operation, "rotation");
            if (rotationValue != null)
            {
                options.Rotation = ToInternalAngle(rotationValue);
            }

            TextNote textNote;
            Dictionary<string, object> widthValue = GetDictionary(operation, "width");
            if (widthValue != null)
            {
                textNote = TextNote.Create(document, view.Id, position, ToInternalLength(widthValue, "width"), GetString(operation, "text"), options);
            }
            else
            {
                textNote = TextNote.Create(document, view.Id, position, GetString(operation, "text"), options);
            }

            if (textNote == null)
            {
                throw new InvalidOperationException("Revit did not create a text note.");
            }

            return Change(operation, index, "applied",
                target: ElementTarget(textNote, null),
                before: null,
                after: TextNoteSnapshot(document, textNote));
        }

        private static Dictionary<string, object> PreviewTagRoom(Document document, Dictionary<string, object> operation, int index)
        {
            string roomId = GetString(operation, "roomId");
            string viewId = GetString(operation, "viewId");
            Dictionary<string, object> locationValue = GetDictionary(operation, "location");
            if (string.IsNullOrWhiteSpace(roomId)) return BlockedChange(operation, index, "tag_room requires roomId.");
            if (string.IsNullOrWhiteSpace(viewId)) return BlockedChange(operation, index, "tag_room requires viewId.");
            if (locationValue == null) return BlockedChange(operation, index, "tag_room requires location.");

            Room room = ResolveElement(document, roomId) as Room;
            if (room == null) return BlockedChange(operation, index, "Room " + roomId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, room, roomId, "expectedUniqueId", "Room");
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (!IsRoomPlaced(room)) return BlockedChange(operation, index, "Room " + roomId + " is unplaced and cannot be tagged.");

            View view = ResolveElement(document, viewId) as View;
            string viewError = ValidateRoomTagView(view);
            if (!string.IsNullOrWhiteSpace(viewError)) return BlockedChange(operation, index, viewError);

            Element tagType = ResolveRoomTagTypeElement(document, GetString(operation, "tagTypeId"));
            if (tagType == null) return BlockedChange(operation, index, "A usable room tag type was not found.");
            if (HasRoomTagInView(document, room, view))
            {
                return BlockedChange(operation, index, "Room " + roomId + " already has a room tag in view " + viewId + ".");
            }

            UV location;
            SpatialElementTagOrientation orientation;
            try
            {
                location = ToInternalUv(locationValue, "location");
                orientation = ParseRoomTagOrientation(GetString(operation, "orientation"));
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            bool hasLeader = GetBool(operation, "hasLeader", false);
            string probeError = ProbeRoomTagCreation(document, room, view, tagType, location, hasLeader, orientation);
            if (!string.IsNullOrWhiteSpace(probeError)) return BlockedChange(operation, index, probeError);

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["room"] = RoomSnapshot(room),
                    ["view"] = BuildViewSummary(view),
                    ["tagType"] = ElementSummary(document, tagType)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["roomId"] = ToElementIdString(room.Id),
                    ["viewId"] = ToElementIdString(view.Id),
                    ["tagTypeId"] = ToElementIdString(tagType.Id),
                    ["tagTypeName"] = SafeElementName(tagType),
                    ["location"] = Point2Value(location),
                    ["hasLeader"] = hasLeader,
                    ["orientation"] = orientation.ToString()
                });
        }

        private static Dictionary<string, object> ApplyTagRoom(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewTagRoom(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "tag_room preview failed.");
            }

            Room room = ResolveElement(document, GetString(operation, "roomId")) as Room;
            View view = ResolveElement(document, GetString(operation, "viewId")) as View;
            Element tagType = ResolveRoomTagTypeElement(document, GetString(operation, "tagTypeId"));
            UV location = ToInternalUv(GetDictionary(operation, "location"), "location");

            RoomTag tag = document.Create.NewRoomTag(new LinkElementId(room.Id), location, view.Id);
            if (tag == null)
            {
                throw new InvalidOperationException("Revit did not create a room tag.");
            }

            if (tagType != null)
            {
                tag.ChangeTypeId(tagType.Id);
            }
            tag.HasLeader = GetBool(operation, "hasLeader", false);
            tag.TagOrientation = ParseRoomTagOrientation(GetString(operation, "orientation"));
            document.Regenerate();

            return Change(operation, index, "applied",
                target: ElementTarget(tag, null),
                before: null,
                after: RoomTagSnapshot(document, tag));
        }

        private static Dictionary<string, object> PreviewTagElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            string viewId = GetString(operation, "viewId");
            string tagTypeId = GetString(operation, "tagTypeId");
            Dictionary<string, object> positionValue = GetDictionary(operation, "position");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "tag_element requires elementId.");
            if (string.IsNullOrWhiteSpace(viewId)) return BlockedChange(operation, index, "tag_element requires viewId.");
            if (string.IsNullOrWhiteSpace(tagTypeId)) return BlockedChange(operation, index, "tag_element requires tagTypeId.");
            if (positionValue == null) return BlockedChange(operation, index, "tag_element requires position.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            string targetError = ValidateIndependentTagTarget(element);
            if (!string.IsNullOrWhiteSpace(targetError)) return BlockedChange(operation, index, targetError);

            View view = ResolveElement(document, viewId) as View;
            string viewError = ValidateIndependentTagView(view);
            if (!string.IsNullOrWhiteSpace(viewError)) return BlockedChange(operation, index, viewError);
            if (!IsElementVisibleInView(document, element, view))
            {
                return BlockedChange(operation, index, "Element " + elementId + " is not visible in view " + viewId + ".");
            }

            FamilySymbol tagType = ResolveIndependentTagType(document, tagTypeId);
            if (tagType == null) return BlockedChange(operation, index, "tagTypeId must reference a non-material tag FamilySymbol.");
            if (HasIndependentTagForElementInView(document, element, view))
            {
                return BlockedChange(operation, index, "Element " + elementId + " already has an independent tag in view " + viewId + ".");
            }

            XYZ position;
            TagOrientation orientation;
            try
            {
                position = ToInternalPoint(positionValue, "position");
                orientation = ParseIndependentTagOrientation(GetString(operation, "orientation"));
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            bool hasLeader = GetBool(operation, "hasLeader", false);
            string probeError = ProbeIndependentTagCreation(document, element, view, tagType, position, hasLeader, orientation);
            if (!string.IsNullOrWhiteSpace(probeError)) return BlockedChange(operation, index, probeError);

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["element"] = ElementSummary(document, element),
                    ["view"] = BuildViewSummary(view),
                    ["tagType"] = ElementSummary(document, tagType)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["elementId"] = ToElementIdString(element.Id),
                    ["viewId"] = ToElementIdString(view.Id),
                    ["tagTypeId"] = ToElementIdString(tagType.Id),
                    ["tagTypeName"] = SafeElementName(tagType),
                    ["position"] = PointValue(position),
                    ["hasLeader"] = hasLeader,
                    ["orientation"] = orientation.ToString()
                });
        }

        private static Dictionary<string, object> ApplyTagElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewTagElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "tag_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            View view = ResolveElement(document, GetString(operation, "viewId")) as View;
            FamilySymbol tagType = ResolveIndependentTagType(document, GetString(operation, "tagTypeId"));
            XYZ position = ToInternalPoint(GetDictionary(operation, "position"), "position");
            TagOrientation orientation = ParseIndependentTagOrientation(GetString(operation, "orientation"));

            if (!tagType.IsActive)
            {
                tagType.Activate();
                document.Regenerate();
            }

            Reference reference = new Reference(element);
            IndependentTag tag = IndependentTag.Create(
                document,
                tagType.Id,
                view.Id,
                reference,
                GetBool(operation, "hasLeader", false),
                orientation,
                position);
            if (tag == null)
            {
                throw new InvalidOperationException("Revit did not create an independent tag.");
            }

            document.Regenerate();
            return Change(operation, index, "applied",
                target: ElementTarget(tag, null),
                before: null,
                after: IndependentTagSnapshot(document, tag));
        }

        private static TextNoteType ResolveTextNoteType(Document document, string textNoteTypeId)
        {
            if (!string.IsNullOrWhiteSpace(textNoteTypeId))
            {
                return ResolveElement(document, textNoteTypeId) as TextNoteType;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault();
        }

        private static Element ResolveRoomTagTypeElement(Document document, string tagTypeId)
        {
            if (!string.IsNullOrWhiteSpace(tagTypeId))
            {
                Element element = ResolveElement(document, tagTypeId);
                return IsRoomTagTypeElement(element) ? element : null;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .Cast<Element>()
                .FirstOrDefault();
        }

        private static FamilySymbol ResolveIndependentTagType(Document document, string tagTypeId)
        {
            if (string.IsNullOrWhiteSpace(tagTypeId)) return null;

            FamilySymbol symbol = ResolveElement(document, tagTypeId) as FamilySymbol;
            if (symbol == null) return null;
            if (!IsTagFamilySymbol(symbol)) return null;
            if (IsMaterialTagFamilySymbol(symbol)) return null;
            return symbol;
        }

        private static string ValidateTextNoteView(View view)
        {
            if (view == null) return "Target view was not found.";
            if (view.IsTemplate) return "Target view is a template and cannot host text notes.";
            if (view.ViewType == ViewType.ThreeD) return "3D views are not supported by create_text_note.";
            if (view is ViewSheet) return null;
            return IsGraphicalView(view) ? null : "Target view must be a graphical printable view or sheet.";
        }

        private static string ValidateRoomTagView(View view)
        {
            if (view == null) return "Target view was not found.";
            if (view.IsTemplate) return "Target view is a template and cannot host room tags.";
            if (view is ViewSheet) return "Sheets cannot host room tags.";
            if (!IsGraphicalView(view)) return "Target view must be a graphical printable view.";
            if (view.ViewType == ViewType.FloorPlan ||
                view.ViewType == ViewType.CeilingPlan ||
                view.ViewType == ViewType.EngineeringPlan ||
                view.ViewType == ViewType.Section)
            {
                return null;
            }

            return "Room tags require a plan or section view.";
        }

        private static string ValidateIndependentTagView(View view)
        {
            if (view == null) return "Target view was not found.";
            if (view.IsTemplate) return "Target view is a template and cannot host element tags.";
            if (view is ViewSheet) return "Sheets cannot host element tags.";
            if (view.ViewType == ViewType.ThreeD) return "3D views are not supported by tag_element.";
            return IsGraphicalView(view) ? null : "Target view must be a graphical printable view.";
        }

        private static string ValidateIndependentTagTarget(Element element)
        {
            if (element is ElementType) return "tag_element target must be a model instance, not an element type.";
            if (element is View) return "tag_element cannot target views or sheets.";
            if (element is Room) return "Use tag_room for rooms.";
            string className = element.GetType().Name;
            if (string.Equals(className, "Area", StringComparison.OrdinalIgnoreCase)) return "Area tags are not supported by tag_element yet.";
            if (string.Equals(className, "Space", StringComparison.OrdinalIgnoreCase)) return "Space tags are not supported by tag_element yet.";
            if (element.Category == null) return "tag_element target must expose a category.";
            return null;
        }

        private static SpatialElementTagOrientation ParseRoomTagOrientation(string orientation)
        {
            if (string.IsNullOrWhiteSpace(orientation)) return SpatialElementTagOrientation.Horizontal;
            if (string.Equals(orientation, "Horizontal", StringComparison.OrdinalIgnoreCase)) return SpatialElementTagOrientation.Horizontal;
            if (string.Equals(orientation, "Vertical", StringComparison.OrdinalIgnoreCase)) return SpatialElementTagOrientation.Vertical;
            if (string.Equals(orientation, "Model", StringComparison.OrdinalIgnoreCase)) return SpatialElementTagOrientation.Model;
            throw new ArgumentException("Unsupported room tag orientation: " + orientation + ".");
        }

        private static TagOrientation ParseIndependentTagOrientation(string orientation)
        {
            if (string.IsNullOrWhiteSpace(orientation)) return TagOrientation.Horizontal;
            if (string.Equals(orientation, "Horizontal", StringComparison.OrdinalIgnoreCase)) return TagOrientation.Horizontal;
            if (string.Equals(orientation, "Vertical", StringComparison.OrdinalIgnoreCase)) return TagOrientation.Vertical;
            if (string.Equals(orientation, "AnyModelDirection", StringComparison.OrdinalIgnoreCase))
            {
#if REVIT2021
                throw new ArgumentException("AnyModelDirection element tags require Revit 2022 or newer.");
#else
                return TagOrientation.AnyModelDirection;
#endif
            }
            throw new ArgumentException("Unsupported element tag orientation: " + orientation + ".");
        }

        private static bool HasRoomTagInView(Document document, Room room, View view)
        {
            try
            {
                return new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(RoomTag))
                    .Cast<RoomTag>()
                    .Any(tag =>
                    {
                        try
                        {
                            if (tag.TaggedLocalRoomId == room.Id) return true;
                            Room taggedRoom = tag.Room;
                            return taggedRoom != null && taggedRoom.Id == room.Id;
                        }
                        catch
                        {
                            return false;
                        }
                    });
            }
            catch
            {
                return false;
            }
        }

        private static string ProbeRoomTagCreation(
            Document document,
            Room room,
            View view,
            Element tagType,
            UV location,
            bool hasLeader,
            SpatialElementTagOrientation orientation)
        {
            if (document.IsModifiable) return null;

            using (var transaction = new Transaction(document, "Revit MCP preview tag_room"))
            {
                TransactionStatus startStatus = transaction.Start();
                if (startStatus != TransactionStatus.Started)
                {
                    return "Revit could not start a room-tag preview transaction. Status: " + startStatus + ".";
                }

                ConfigurePreviewProbeTransaction(transaction);
                try
                {
                    RoomTag tag = document.Create.NewRoomTag(new LinkElementId(room.Id), location, view.Id);
                    if (tag == null) return "Revit did not create a room tag during preview validation.";
                    if (tagType != null) tag.ChangeTypeId(tagType.Id);
                    tag.HasLeader = hasLeader;
                    tag.TagOrientation = orientation;
                    document.Regenerate();
                    return null;
                }
                catch (Exception ex)
                {
                    return FormatPreviewProbeError("tag_room", ex);
                }
                finally
                {
                    RollBackPreviewProbeTransaction(transaction);
                }
            }
        }

        private static string ProbeIndependentTagCreation(
            Document document,
            Element element,
            View view,
            FamilySymbol tagType,
            XYZ position,
            bool hasLeader,
            TagOrientation orientation)
        {
            if (document.IsModifiable) return null;

            using (var transaction = new Transaction(document, "Revit MCP preview tag_element"))
            {
                TransactionStatus startStatus = transaction.Start();
                if (startStatus != TransactionStatus.Started)
                {
                    return "Revit could not start an element-tag preview transaction. Status: " + startStatus + ".";
                }

                ConfigurePreviewProbeTransaction(transaction);
                try
                {
                    if (!tagType.IsActive)
                    {
                        tagType.Activate();
                        document.Regenerate();
                    }

                    IndependentTag tag = IndependentTag.Create(
                        document,
                        tagType.Id,
                        view.Id,
                        new Reference(element),
                        hasLeader,
                        orientation,
                        position);
                    if (tag == null) return "Revit did not create an element tag during preview validation.";
                    document.Regenerate();
                    return null;
                }
                catch (Exception ex)
                {
                    return FormatPreviewProbeError("tag_element", ex);
                }
                finally
                {
                    RollBackPreviewProbeTransaction(transaction);
                }
            }
        }

        private static bool HasIndependentTagForElementInView(Document document, Element element, View view)
        {
            try
            {
                return new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .Any(tag =>
                    {
                        try
                        {
                            return GetTaggedLocalElementIds(tag).Any(id => id == element.Id);
                        }
                        catch
                        {
                            return false;
                        }
                    });
            }
            catch
            {
                return false;
            }
        }

        private static bool IsElementVisibleInView(Document document, Element element, View view)
        {
            try
            {
                return new FilteredElementCollector(document, view.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Any(id => id == element.Id);
            }
            catch
            {
                return true;
            }
        }

        private static Dictionary<string, object> TextNoteSnapshot(Document document, TextNote textNote)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(textNote.Id),
                ["uniqueId"] = textNote.UniqueId,
                ["text"] = textNote.Text,
                ["textLength"] = (textNote.Text ?? string.Empty).Length,
                ["viewId"] = ToElementIdString(textNote.OwnerViewId)
            };

            ElementId typeId = textNote.GetTypeId();
            if (IsValidElementId(typeId))
            {
                snapshot["textNoteTypeId"] = ToElementIdString(typeId);
                Element type = document.GetElement(typeId);
                if (type != null) snapshot["textNoteTypeName"] = SafeElementName(type);
            }

            try
            {
                snapshot["position"] = PointValue(textNote.Coord);
            }
            catch
            {
                // Some text note states do not expose a stable coordinate.
            }

            try
            {
                if (textNote.Width > 0) snapshot["width"] = LengthValue(textNote.Width);
            }
            catch
            {
                // Unwrapped text notes may not expose a width.
            }

            return snapshot;
        }

        private static Dictionary<string, object> RoomTagSnapshot(Document document, RoomTag tag)
        {
            var snapshot = ElementSummary(document, tag);
            snapshot["viewId"] = ToElementIdString(tag.OwnerViewId);

            try
            {
                if (IsValidElementId(tag.TaggedLocalRoomId)) snapshot["roomId"] = ToElementIdString(tag.TaggedLocalRoomId);
            }
            catch
            {
                // Linked or orphaned room tag states may not expose a local room id.
            }

            try
            {
                Room room = tag.Room;
                if (room != null)
                {
                    snapshot["roomNumber"] = GetRoomNumber(room);
                    snapshot["roomName"] = GetRoomName(room);
                }
            }
            catch
            {
                // Orphaned or unusual tags may not expose the tagged room.
            }

            try
            {
                ElementId typeId = tag.GetTypeId();
                if (IsValidElementId(typeId))
                {
                    snapshot["tagTypeId"] = ToElementIdString(typeId);
                    Element tagType = document.GetElement(typeId);
                    if (tagType != null) snapshot["tagTypeName"] = SafeElementName(tagType);
                }
            }
            catch
            {
                // Some tag states may not expose their type id.
            }

            try
            {
                snapshot["position"] = PointValue(tag.TagHeadPosition);
            }
            catch
            {
                // Tag head position can be unavailable for some orphaned states.
            }

            try
            {
                snapshot["hasLeader"] = tag.HasLeader;
            }
            catch
            {
                // Ignore leader state when unavailable.
            }

            try
            {
                snapshot["orientation"] = tag.TagOrientation.ToString();
            }
            catch
            {
                // Ignore orientation when unavailable.
            }

            try
            {
                string tagText = tag.TagText;
                if (!string.IsNullOrWhiteSpace(tagText)) snapshot["tagText"] = tagText;
            }
            catch
            {
                // Ignore tag text when unavailable.
            }

            return snapshot;
        }

        private static Dictionary<string, object> IndependentTagSnapshot(Document document, IndependentTag tag)
        {
            var snapshot = ElementSummary(document, tag);
            snapshot["viewId"] = ToElementIdString(tag.OwnerViewId);

            ElementId typeId = tag.GetTypeId();
            if (IsValidElementId(typeId))
            {
                snapshot["tagTypeId"] = ToElementIdString(typeId);
                Element type = document.GetElement(typeId);
                if (type != null) snapshot["tagTypeName"] = SafeElementName(type);
            }

            try
            {
                string[] localIds = GetTaggedLocalElementIds(tag)
                    .Where(IsValidElementId)
                    .Select(ToElementIdString)
                    .ToArray();
                if (localIds.Length > 0) snapshot["taggedElementIds"] = localIds;
            }
            catch
            {
                // Some independent tag variants may not expose local tagged ids.
            }

            try
            {
                snapshot["position"] = PointValue(tag.TagHeadPosition);
            }
            catch
            {
                // Tag head position can be unavailable for some orphaned states.
            }

            try
            {
                snapshot["hasLeader"] = tag.HasLeader;
            }
            catch
            {
                // Some tag behavior variants do not expose leader state.
            }

            try
            {
                snapshot["orientation"] = tag.TagOrientation.ToString();
            }
            catch
            {
                // Some tag behavior variants do not expose orientation.
            }

            try
            {
                snapshot["isMulticategoryTag"] = tag.IsMulticategoryTag;
                snapshot["isMaterialTag"] = tag.IsMaterialTag;
            }
            catch
            {
                // Ignore variant flags when unavailable.
            }

            try
            {
                string tagText = tag.TagText;
                if (!string.IsNullOrWhiteSpace(tagText)) snapshot["tagText"] = tagText;
            }
            catch
            {
                // Ignore tag text when unavailable.
            }

            return snapshot;
        }

        private static IEnumerable<ElementId> GetTaggedLocalElementIds(IndependentTag tag)
        {
#if REVIT2021
#pragma warning disable CS0618
            Element taggedElement = tag?.GetTaggedLocalElement();
#pragma warning restore CS0618
            return taggedElement == null ? Enumerable.Empty<ElementId>() : new[] { taggedElement.Id };
#else
            return tag?.GetTaggedLocalElementIds() ?? Enumerable.Empty<ElementId>();
#endif
        }
    }
}
