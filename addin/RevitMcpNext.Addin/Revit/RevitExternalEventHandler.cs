using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class RevitExternalEventHandler : IExternalEventHandler
    {
        private const string AddinVersion = "0.1.0";
        private const int MaxItemsPerExternalEvent = 16;
        private const int MaxQueryLimit = 500;
        private const int MaxChangeSetOperations = 50;
        private readonly RevitRequestQueue _queue;
        private readonly TransactionService _transactions;
        private readonly DocumentGenerationTracker _generations;
        private readonly PreviewTokenStore _previewTokens;

        public RevitExternalEventHandler(
            RevitRequestQueue queue,
            TransactionService transactions,
            DocumentGenerationTracker generations = null,
            PreviewTokenStore previewTokens = null)
        {
            _queue = queue;
            _transactions = transactions;
            _generations = generations ?? new DocumentGenerationTracker();
            _previewTokens = previewTokens ?? new PreviewTokenStore();
        }

        public void Execute(UIApplication app)
        {
            int processed = 0;
            while (processed < MaxItemsPerExternalEvent && _queue.TryDequeue(out RevitRequestQueue.WorkItem item))
            {
                processed++;
                item.TrySetResult(Handle(app, item.Envelope));
            }

            if (_queue.HasPending)
            {
                _queue.Raise();
            }
        }

        public string GetName()
        {
            return "Revit MCP Next External Event Handler";
        }

        private long GetActiveDocumentGeneration(UIApplication app)
        {
            Document activeDocument = app.ActiveUIDocument?.Document;
            return activeDocument == null ? 0 : _generations.GetGeneration(activeDocument);
        }

        private BridgeResponseEnvelope ValidateExpectedGeneration(
            BridgeRequestEnvelope request,
            Document document,
            Stopwatch sw,
            out long generation)
        {
            generation = _generations.GetGeneration(document);
            long? expectedGeneration =
                request.ExpectedGeneration ??
                GetLong(request.Payload, "expectedGeneration") ??
                GetLong(request.Payload, "baseGeneration");

            if (expectedGeneration.HasValue && expectedGeneration.Value != generation)
            {
                return Failure(
                    request,
                    "GENERATION_MISMATCH",
                    "The document generation is " + generation.ToString(CultureInfo.InvariantCulture) +
                    " but the request expected " + expectedGeneration.Value.ToString(CultureInfo.InvariantCulture) + ". Refresh the document state before retrying.",
                    sw);
            }

            return null;
        }

        private BridgeResponseEnvelope Handle(UIApplication app, BridgeRequestEnvelope request)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                return _transactions.Read(() =>
                {
                    switch (request.Operation)
                    {
                        case "status":
                            return Success(request, BuildStatus(app), sw, generation: GetActiveDocumentGeneration(app));
                        case "list_documents":
                            return Success(request, BuildDocumentList(app), sw, generation: GetActiveDocumentGeneration(app));
                        case "get_levels":
                            return HandleGetLevels(app, request, sw);
                        case "query":
                            return HandleQuery(app, request, sw);
                        case "preview_change_set":
                            return HandlePreviewChange(app, request, sw);
                        case "apply_change_set":
                            return HandleApplyChange(app, request, sw);
                        case "cancel_request":
                            return HandleCancel(request, sw);
                        default:
                            return Failure(request, "UNSUPPORTED_OPERATION", "Unsupported Revit MCP operation: " + request.Operation, sw);
                    }
                });
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("Revit command failed. requestId=" + request.RequestId + " operation=" + request.Operation, ex);
                return Failure(request, "REVIT_COMMAND_FAILED", ex.Message, sw);
            }
        }

        private BridgeResponseEnvelope HandleGetLevels(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_levels.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var collectorSw = Stopwatch.StartNew();
            var levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .Select(BuildLevelSummary)
                .ToArray();
            collectorSw.Stop();

            return Success(
                request,
                levels,
                sw,
                metrics: new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = levels.Length,
                    TotalCount = levels.Length
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleQuery(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.query.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            int limit = Math.Min(MaxQueryLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeFields(GetStringList(payload, "fields"), preset, warnings);

            string scope;
            IEnumerable<Element> elements = CreateFilteredElements(app, document, filter, warnings, out scope);
            List<Element> materialized = elements.ToList();
            int totalCount = materialized.Count;
            List<Element> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["items"] = page.Select(element => BuildQueryItem(element, fields)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["units"] = new Dictionary<string, object>
                {
                    ["elevation"] = "mm",
                    ["length"] = "mm"
                },
                ["scope"] = scope,
                ["source"] = "revit-addin"
            };

            if (includeTotalCount) data["totalCount"] = totalCount;
            if (offset + page.Count < totalCount) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

            return Success(
                request,
                data,
                sw,
                warnings,
                new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandlePreviewChange(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.preview_change_set.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> operations = GetOperations(payload);
            if (operations.Count == 0)
            {
                return Failure(request, "EMPTY_CHANGE_SET", "A change set must contain at least one operation.", sw);
            }
            if (operations.Count > MaxChangeSetOperations)
            {
                return Failure(request, "CHANGE_SET_TOO_LARGE", "A change set can contain at most 50 operations.", sw);
            }

            string transactionName = GetTransactionName(payload);
            string documentFingerprint = ComputeDocumentFingerprint(document);
            string previewId = ComputePreviewId(document, transactionName, operations);
            var changes = new List<Dictionary<string, object>>();
            var validationContext = new PreviewValidationContext();
            bool ready = true;
            string riskLevel = "low";

            for (int index = 0; index < operations.Count; index++)
            {
                Dictionary<string, object> change = PreviewOperation(document, operations[index], index, validationContext);
                changes.Add(change);
                string status = GetString(change, "status");
                if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase)) ready = false;
                string operationType = GetString(operations[index], "type");
                if (string.Equals(operationType, "create_level", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_wall", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "copy_element", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "change_element_type", StringComparison.OrdinalIgnoreCase))
                {
                    riskLevel = "medium";
                }
            }

            string operationsHash = ComputeChangeSetHash(operations);
            string changesHash = ComputeChangeSetHash(changes);
            string changeSetHash = ComputePreviewChangeSetHash(documentFingerprint, generation, transactionName, operationsHash, changesHash);
            PreviewToken token = _previewTokens.Issue(
                previewId,
                documentFingerprint,
                generation,
                transactionName,
                operationsHash,
                changesHash,
                changeSetHash,
                ready,
                operations.Count);

            var data = new Dictionary<string, object>
            {
                ["previewId"] = previewId,
                ["documentFingerprint"] = documentFingerprint,
                ["changeSetHash"] = changeSetHash,
                ["baseGeneration"] = generation,
                ["generation"] = generation,
                ["transactionName"] = transactionName,
                ["operationCount"] = operations.Count,
                ["ready"] = ready,
                ["requiresConfirmation"] = true,
                ["expiresAt"] = FormatUtc(token.ExpiresAtUtc),
                ["previewExpiresAtUtc"] = FormatUtc(token.ExpiresAtUtc),
                ["riskLevel"] = riskLevel,
                ["changes"] = changes.ToArray()
            };

            return Success(request, data, sw, warnings, new BridgeMetrics
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                ReturnedCount = changes.Count,
                TotalCount = changes.Count
            }, generation: generation);
        }

        private BridgeResponseEnvelope HandleApplyChange(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.apply_change_set.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            if (!GetBool(payload, "confirm", false))
            {
                return Failure(request, "CONFIRMATION_REQUIRED", "revit.apply_change_set requires confirm=true.", sw);
            }

            List<Dictionary<string, object>> operations = GetOperations(payload);
            if (operations.Count == 0)
            {
                return Failure(request, "EMPTY_CHANGE_SET", "A change set must contain at least one operation.", sw);
            }
            if (operations.Count > MaxChangeSetOperations)
            {
                return Failure(request, "CHANGE_SET_TOO_LARGE", "A change set can contain at most 50 operations.", sw);
            }

            string transactionName = GetTransactionName(payload);
            string documentFingerprint = ComputeDocumentFingerprint(document);
            string expectedPreviewId = ComputePreviewId(document, transactionName, operations);
            string providedPreviewId = GetString(payload, "previewId");
            if (!string.Equals(providedPreviewId, expectedPreviewId, StringComparison.Ordinal))
            {
                return Failure(request, "PREVIEW_ID_MISMATCH", "The supplied previewId does not match the current change set and document.", sw);
            }

            var previewChanges = new List<Dictionary<string, object>>();
            var validationContext = new PreviewValidationContext();
            for (int index = 0; index < operations.Count; index++)
            {
                Dictionary<string, object> change = PreviewOperation(document, operations[index], index, validationContext);
                previewChanges.Add(change);
            }

            PreviewTokenValidation tokenValidation = _previewTokens.Validate(
                providedPreviewId,
                documentFingerprint,
                generation,
                transactionName,
                ComputeChangeSetHash(operations),
                ComputeChangeSetHash(previewChanges),
                GetString(payload, "changeSetHash"));
            if (!tokenValidation.Ok)
            {
                return Failure(request, tokenValidation.Code, tokenValidation.Message, sw);
            }

            List<Dictionary<string, object>> appliedChanges = _transactions.Write(document, transactionName, () =>
            {
                var results = new List<Dictionary<string, object>>();
                for (int index = 0; index < operations.Count; index++)
                {
                    results.Add(ApplyOperation(document, operations[index], index));
                }
                return results;
            });
            _previewTokens.Consume(providedPreviewId);
            long appliedGeneration = _generations.GetGeneration(document);

            var data = new Dictionary<string, object>
            {
                ["previewId"] = expectedPreviewId,
                ["documentFingerprint"] = documentFingerprint,
                ["changeSetHash"] = tokenValidation.Token.ChangeSetHash,
                ["baseGeneration"] = tokenValidation.Token.Generation,
                ["generation"] = appliedGeneration,
                ["transactionName"] = transactionName,
                ["applied"] = true,
                ["changedCount"] = appliedChanges.Count,
                ["changes"] = appliedChanges.ToArray()
            };

            return Success(request, data, sw, metrics: new BridgeMetrics
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                ReturnedCount = appliedChanges.Count,
                TotalCount = appliedChanges.Count
            }, generation: appliedGeneration);
        }

        private static BridgeResponseEnvelope HandleCancel(BridgeRequestEnvelope request, Stopwatch sw)
        {
            var data = new Dictionary<string, object>
            {
                ["cancelled"] = false,
                ["message"] = "No queued cancellable request matched. In-flight Revit API work cannot be interrupted safely."
            };

            string requestId = GetString(request.Payload, "requestId");
            if (!string.IsNullOrWhiteSpace(requestId)) data["requestId"] = requestId;

            return Success(request, data, sw);
        }

        private static Dictionary<string, object> PreviewOperation(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string type = GetString(operation, "type");
            switch (type)
            {
                case "set_parameter":
                    return PreviewSetParameter(document, operation, index);
                case "create_level":
                    return PreviewCreateLevel(document, operation, index, validationContext);
                case "create_wall":
                    return PreviewCreateWall(document, operation, index);
                case "move_element":
                    return PreviewMoveElement(document, operation, index);
                case "rotate_element":
                    return PreviewRotateElement(document, operation, index);
                case "copy_element":
                    return PreviewCopyElement(document, operation, index);
                case "change_element_type":
                    return PreviewChangeElementType(document, operation, index);
                case "set_element_pinned":
                    return PreviewSetElementPinned(document, operation, index);
                default:
                    return BlockedChange(operation, index, "Unsupported change operation type: " + (type ?? "(missing)"));
            }
        }

        private static Dictionary<string, object> ApplyOperation(Document document, Dictionary<string, object> operation, int index)
        {
            string type = GetString(operation, "type");
            switch (type)
            {
                case "set_parameter":
                    return ApplySetParameter(document, operation, index);
                case "create_level":
                    return ApplyCreateLevel(document, operation, index);
                case "create_wall":
                    return ApplyCreateWall(document, operation, index);
                case "move_element":
                    return ApplyMoveElement(document, operation, index);
                case "rotate_element":
                    return ApplyRotateElement(document, operation, index);
                case "copy_element":
                    return ApplyCopyElement(document, operation, index);
                case "change_element_type":
                    return ApplyChangeElementType(document, operation, index);
                case "set_element_pinned":
                    return ApplySetElementPinned(document, operation, index);
                default:
                    throw new InvalidOperationException("Unsupported change operation type: " + (type ?? "(missing)"));
            }
        }

        private static Dictionary<string, object> PreviewSetParameter(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            string parameterName = GetString(operation, "parameterName");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "set_parameter requires elementId.");
            if (string.IsNullOrWhiteSpace(parameterName)) return BlockedChange(operation, index, "set_parameter requires parameterName.");
            if (!operation.TryGetValue("value", out object value)) return BlockedChange(operation, index, "set_parameter requires value.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");

            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter == null) return BlockedChange(operation, index, "Parameter '" + parameterName + "' was not found on element " + elementId + ".");
            if (parameter.IsReadOnly) return BlockedChange(operation, index, "Parameter '" + parameterName + "' is read-only.");

            return Change(operation, index, "ready", ElementTarget(element, parameterName),
                before: ParameterSnapshot(parameter),
                after: new Dictionary<string, object>
                {
                    ["value"] = value,
                    ["storageType"] = parameter.StorageType.ToString()
                });
        }

        private static Dictionary<string, object> ApplySetParameter(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewSetParameter(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "set_parameter preview failed.");
            }

            string elementId = GetString(operation, "elementId");
            string parameterName = GetString(operation, "parameterName");
            Element element = ResolveElement(document, elementId);
            Parameter parameter = element.LookupParameter(parameterName);
            object before = ParameterValue(parameter);
            SetParameterValue(parameter, operation["value"]);
            object after = ParameterValue(parameter);

            return Change(operation, index, "applied", ElementTarget(element, parameterName),
                before: new Dictionary<string, object>
                {
                    ["value"] = before,
                    ["storageType"] = parameter.StorageType.ToString()
                },
                after: new Dictionary<string, object>
                {
                    ["value"] = after,
                    ["storageType"] = parameter.StorageType.ToString()
                });
        }

        private static Dictionary<string, object> PreviewCreateLevel(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string name = GetString(operation, "name");
            Dictionary<string, object> elevation = GetDictionary(operation, "elevation");
            if (string.IsNullOrWhiteSpace(name)) return BlockedChange(operation, index, "create_level requires name.");
            if (elevation == null) return BlockedChange(operation, index, "create_level requires elevation.");
            if (LevelNameExists(document, name)) return BlockedChange(operation, index, "A level named '" + name + "' already exists.");
            if (validationContext != null && !validationContext.TryAddLevelName(name))
            {
                return BlockedChange(operation, index, "The change set creates duplicate level name '" + name + "'.");
            }

            double internalElevation;
            try
            {
                internalElevation = ToInternalElevation(elevation);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            return Change(operation, index, "ready",
                target: new Dictionary<string, object> { ["document"] = document.Title },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(internalElevation, UnitTypeId.Millimeters), "mm", "metric")
                });
        }

        private static Dictionary<string, object> ApplyCreateLevel(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateLevel(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_level preview failed.");
            }

            string name = GetString(operation, "name");
            double internalElevation = ToInternalElevation(GetDictionary(operation, "elevation"));
            Level level = Level.Create(document, internalElevation);
            level.Name = name;

            return Change(operation, index, "applied",
                target: ElementTarget(level, null),
                before: null,
                after: new Dictionary<string, object>
                {
                    ["id"] = ToElementIdString(level.Id),
                    ["uniqueId"] = level.UniqueId,
                    ["name"] = level.Name,
                    ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters), "mm", "metric")
                });
        }

        private static Dictionary<string, object> PreviewCreateWall(Document document, Dictionary<string, object> operation, int index)
        {
            string levelId = GetString(operation, "levelId");
            Dictionary<string, object> startValue = GetDictionary(operation, "start");
            Dictionary<string, object> endValue = GetDictionary(operation, "end");
            if (string.IsNullOrWhiteSpace(levelId)) return BlockedChange(operation, index, "create_wall requires levelId.");
            if (startValue == null) return BlockedChange(operation, index, "create_wall requires start.");
            if (endValue == null) return BlockedChange(operation, index, "create_wall requires end.");

            Level level = ResolveElement(document, levelId) as Level;
            if (level == null) return BlockedChange(operation, index, "Level " + levelId + " was not found.");

            string wallTypeId = GetString(operation, "wallTypeId");
            WallType wallType = null;
            if (!string.IsNullOrWhiteSpace(wallTypeId))
            {
                wallType = ResolveElement(document, wallTypeId) as WallType;
                if (wallType == null) return BlockedChange(operation, index, "Wall type " + wallTypeId + " was not found.");
            }

            XYZ start;
            XYZ end;
            double? height = null;
            try
            {
                start = ToInternalPoint(startValue, "start");
                end = ToInternalPoint(endValue, "end");
                Dictionary<string, object> heightValue = GetDictionary(operation, "height");
                if (heightValue != null)
                {
                    height = ToInternalLength(heightValue, "height");
                    if (height.Value <= 0) return BlockedChange(operation, index, "create_wall height must be greater than zero.");
                }
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string geometryError = ValidateWallBaseline(document, start, end);
            if (!string.IsNullOrWhiteSpace(geometryError)) return BlockedChange(operation, index, geometryError);

            bool structural = GetBool(operation, "structural", false);
            bool flip = GetBool(operation, "flip", false);
            double baseOffset = start.Z - level.Elevation;
            var target = new Dictionary<string, object>
            {
                ["document"] = document.Title,
                ["levelId"] = ToElementIdString(level.Id),
                ["levelName"] = level.Name
            };
            if (wallType != null)
            {
                target["wallTypeId"] = ToElementIdString(wallType.Id);
                target["wallTypeName"] = SafeElementName(wallType);
            }

            var after = new Dictionary<string, object>
            {
                ["levelId"] = ToElementIdString(level.Id),
                ["start"] = PointValue(start),
                ["end"] = PointValue(end),
                ["length"] = LengthValue(start.DistanceTo(end)),
                ["baseOffset"] = LengthValue(baseOffset),
                ["structural"] = structural,
                ["flip"] = flip
            };
            if (wallType != null) after["wallTypeId"] = ToElementIdString(wallType.Id);
            if (height.HasValue) after["height"] = LengthValue(height.Value);

            return Change(operation, index, "ready", target, before: null, after: after);
        }

        private static Dictionary<string, object> ApplyCreateWall(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateWall(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_wall preview failed.");
            }

            Level level = ResolveElement(document, GetString(operation, "levelId")) as Level;
            XYZ start = ToInternalPoint(GetDictionary(operation, "start"), "start");
            XYZ end = ToInternalPoint(GetDictionary(operation, "end"), "end");
            bool structural = GetBool(operation, "structural", false);
            bool flip = GetBool(operation, "flip", false);
            Wall wall = Wall.Create(document, Line.CreateBound(start, end), level.Id, structural);

            string wallTypeId = GetString(operation, "wallTypeId");
            if (!string.IsNullOrWhiteSpace(wallTypeId))
            {
                WallType wallType = ResolveElement(document, wallTypeId) as WallType;
                ElementId changedId = wall.ChangeTypeId(wallType.Id);
                if (IsValidElementId(changedId) &&
                    !string.Equals(ToElementIdString(changedId), ToElementIdString(wall.Id), StringComparison.Ordinal))
                {
                    Wall changedWall = document.GetElement(changedId) as Wall;
                    if (changedWall != null) wall = changedWall;
                }
            }

            SetWallDoubleParameter(wall, BuiltInParameter.WALL_BASE_OFFSET, start.Z - level.Elevation, "base offset");

            Dictionary<string, object> heightValue = GetDictionary(operation, "height");
            if (heightValue != null)
            {
                SetWallDoubleParameter(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM, ToInternalLength(heightValue, "height"), "height");
            }

            if (flip)
            {
                wall.Flip();
            }

            return Change(operation, index, "applied",
                target: ElementTarget(wall, null),
                before: null,
                after: WallSnapshot(wall));
        }

        private static Dictionary<string, object> PreviewMoveElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> translationValue = GetDictionary(operation, "translation");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "move_element requires elementId.");
            if (translationValue == null) return BlockedChange(operation, index, "move_element requires translation.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be moved.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot be moved.");

            XYZ translation;
            try
            {
                translation = ToInternalPoint(translationValue, "translation");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (VectorLength(translation) <= 0) return BlockedChange(operation, index, "move_element translation must be non-zero.");

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation)
                });
        }

        private static Dictionary<string, object> ApplyMoveElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewMoveElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "move_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = LocationSnapshot(element);
            XYZ translation = ToInternalPoint(GetDictionary(operation, "translation"), "translation");
            ElementTransformUtils.MoveElement(document, element.Id, translation);
            Element movedElement = document.GetElement(element.Id);

            return Change(operation, index, "applied", ElementTarget(movedElement, null),
                before: before,
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation),
                    ["location"] = LocationSnapshot(movedElement)
                });
        }

        private static Dictionary<string, object> PreviewRotateElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> axisStartValue = GetDictionary(operation, "axisStart");
            Dictionary<string, object> axisEndValue = GetDictionary(operation, "axisEnd");
            Dictionary<string, object> angleValue = GetDictionary(operation, "angle");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "rotate_element requires elementId.");
            if (axisStartValue == null) return BlockedChange(operation, index, "rotate_element requires axisStart.");
            if (axisEndValue == null) return BlockedChange(operation, index, "rotate_element requires axisEnd.");
            if (angleValue == null) return BlockedChange(operation, index, "rotate_element requires angle.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be rotated.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot be rotated.");

            XYZ axisStart;
            XYZ axisEnd;
            double angleRadians;
            try
            {
                axisStart = ToInternalPoint(axisStartValue, "axisStart");
                axisEnd = ToInternalPoint(axisEndValue, "axisEnd");
                angleRadians = ToInternalAngle(angleValue);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (axisStart.DistanceTo(axisEnd) <= Math.Max(document.Application.ShortCurveTolerance, 0.000001))
            {
                return BlockedChange(operation, index, "rotate_element axisStart and axisEnd must define a non-zero axis.");
            }
            if (Math.Abs(angleRadians) <= 0.000000001)
            {
                return BlockedChange(operation, index, "rotate_element angle must be non-zero.");
            }

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["axisStart"] = PointValue(axisStart),
                    ["axisEnd"] = PointValue(axisEnd),
                    ["angle"] = AngleValue(angleRadians)
                });
        }

        private static Dictionary<string, object> ApplyRotateElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewRotateElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "rotate_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = LocationSnapshot(element);
            XYZ axisStart = ToInternalPoint(GetDictionary(operation, "axisStart"), "axisStart");
            XYZ axisEnd = ToInternalPoint(GetDictionary(operation, "axisEnd"), "axisEnd");
            double angleRadians = ToInternalAngle(GetDictionary(operation, "angle"));
            ElementTransformUtils.RotateElement(document, element.Id, Line.CreateBound(axisStart, axisEnd), angleRadians);
            Element rotatedElement = document.GetElement(element.Id);

            return Change(operation, index, "applied", ElementTarget(rotatedElement, null),
                before: before,
                after: new Dictionary<string, object>
                {
                    ["axisStart"] = PointValue(axisStart),
                    ["axisEnd"] = PointValue(axisEnd),
                    ["angle"] = AngleValue(angleRadians),
                    ["location"] = LocationSnapshot(rotatedElement)
                });
        }

        private static Dictionary<string, object> PreviewCopyElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> translationValue = GetDictionary(operation, "translation");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "copy_element requires elementId.");
            if (translationValue == null) return BlockedChange(operation, index, "copy_element requires translation.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be copied.");
            if (element.ViewSpecific) return BlockedChange(operation, index, "Element " + elementId + " is view-specific and cannot be copied by copy_element.");

            XYZ translation;
            try
            {
                translation = ToInternalPoint(translationValue, "translation");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (VectorLength(translation) <= 0) return BlockedChange(operation, index, "copy_element translation must be non-zero.");

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["source"] = ElementSummary(document, element)
                },
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation)
                });
        }

        private static Dictionary<string, object> ApplyCopyElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCopyElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "copy_element preview failed.");
            }

            Element source = ResolveElement(document, GetString(operation, "elementId"));
            XYZ translation = ToInternalPoint(GetDictionary(operation, "translation"), "translation");
            ICollection<ElementId> copiedIds = ElementTransformUtils.CopyElement(document, source.Id, translation);
            if (copiedIds == null || copiedIds.Count == 0)
            {
                throw new InvalidOperationException("copy_element did not return any copied element ids.");
            }

            Element[] copiedElements = copiedIds
                .Select(id => document.GetElement(id))
                .Where(element => element != null)
                .ToArray();

            return Change(operation, index, "applied",
                target: new Dictionary<string, object>
                {
                    ["source"] = ElementSummary(document, source)
                },
                before: LocationSnapshot(source),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation),
                    ["copiedElementIds"] = copiedIds.Select(ToElementIdString).ToArray(),
                    ["copiedElements"] = copiedElements.Select(element => ElementSummary(document, element)).ToArray()
                });
        }

        private static Dictionary<string, object> PreviewChangeElementType(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            string typeId = GetString(operation, "typeId");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "change_element_type requires elementId.");
            if (string.IsNullOrWhiteSpace(typeId)) return BlockedChange(operation, index, "change_element_type requires typeId.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is already an element type and cannot change type.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot change type.");

            ElementType targetType = ResolveElement(document, typeId) as ElementType;
            if (targetType == null) return BlockedChange(operation, index, "Type " + typeId + " was not found.");

            ElementId currentTypeId = element.GetTypeId();
            if (!IsValidElementId(currentTypeId)) return BlockedChange(operation, index, "Element " + elementId + " does not expose a valid type id.");
            if (string.Equals(ToElementIdString(currentTypeId), ToElementIdString(targetType.Id), StringComparison.Ordinal))
            {
                return BlockedChange(operation, index, "Element " + elementId + " already has type " + typeId + ".");
            }
            if (!IsValidTypeForElement(element, targetType.Id))
            {
                return BlockedChange(operation, index, "Type " + typeId + " is not valid for element " + elementId + ".");
            }

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: TypeSnapshot(document, element),
                after: TypeSnapshot(document, targetType));
        }

        private static Dictionary<string, object> ApplyChangeElementType(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewChangeElementType(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "change_element_type preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = TypeSnapshot(document, element);
            ElementType targetType = ResolveElement(document, GetString(operation, "typeId")) as ElementType;
            ElementId changedElementId = element.ChangeTypeId(targetType.Id);
            Element changedElement = IsValidElementId(changedElementId) ? document.GetElement(changedElementId) : null;
            if (changedElement == null) changedElement = document.GetElement(element.Id) ?? element;

            return Change(operation, index, "applied", ElementTarget(changedElement, null),
                before: before,
                after: TypeSnapshot(document, changedElement));
        }

        private static Dictionary<string, object> PreviewSetElementPinned(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "set_element_pinned requires elementId.");
            if (!operation.TryGetValue("pinned", out object pinnedValue) || pinnedValue == null)
            {
                return BlockedChange(operation, index, "set_element_pinned requires pinned.");
            }

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be pinned or unpinned.");

            bool desiredPinned = Convert.ToBoolean(pinnedValue, CultureInfo.InvariantCulture);
            bool? expectedPinned = GetNullableBool(operation, "expectedPinned");
            if (expectedPinned.HasValue && expectedPinned.Value != element.Pinned)
            {
                return BlockedChange(
                    operation,
                    index,
                    "Element " + elementId + " pinned state is " + element.Pinned.ToString(CultureInfo.InvariantCulture) +
                    " but expectedPinned was " + expectedPinned.Value.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: new Dictionary<string, object>
                {
                    ["pinned"] = element.Pinned
                },
                after: new Dictionary<string, object>
                {
                    ["pinned"] = desiredPinned
                });
        }

        private static Dictionary<string, object> ApplySetElementPinned(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewSetElementPinned(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "set_element_pinned preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            bool before = element.Pinned;
            bool desiredPinned = GetBool(operation, "pinned", false);
            element.Pinned = desiredPinned;

            return Change(operation, index, "applied", ElementTarget(element, null),
                before: new Dictionary<string, object>
                {
                    ["pinned"] = before
                },
                after: new Dictionary<string, object>
                {
                    ["pinned"] = element.Pinned
                });
        }

        private static Dictionary<string, object> Change(
            Dictionary<string, object> operation,
            int index,
            string status,
            Dictionary<string, object> target,
            Dictionary<string, object> before,
            Dictionary<string, object> after,
            string message = null)
        {
            var change = new Dictionary<string, object>
            {
                ["operationIndex"] = index,
                ["type"] = GetString(operation, "type") ?? "unknown",
                ["status"] = status
            };

            string operationId = GetString(operation, "id");
            if (!string.IsNullOrWhiteSpace(operationId)) change["operationId"] = operationId;
            if (target != null) change["target"] = target;
            if (before != null) change["before"] = before;
            if (after != null) change["after"] = after;
            if (!string.IsNullOrWhiteSpace(message)) change["message"] = message;
            return change;
        }

        private static Dictionary<string, object> BlockedChange(Dictionary<string, object> operation, int index, string message)
        {
            return Change(operation, index, "blocked", null, null, null, message);
        }

        private sealed class PreviewValidationContext
        {
            private readonly HashSet<string> _levelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool TryAddLevelName(string name)
            {
                return _levelNames.Add((name ?? string.Empty).Trim());
            }
        }

        private static Dictionary<string, object> ElementTarget(Element element, string parameterName)
        {
            var target = new Dictionary<string, object>
            {
                ["elementId"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element)
            };
            if (element.Category != null) target["category"] = element.Category.Name;
            if (!string.IsNullOrWhiteSpace(parameterName)) target["parameterName"] = parameterName;
            return target;
        }

        private static Dictionary<string, object> ParameterSnapshot(Parameter parameter)
        {
            return new Dictionary<string, object>
            {
                ["value"] = ParameterValue(parameter),
                ["storageType"] = parameter.StorageType.ToString(),
                ["isReadOnly"] = parameter.IsReadOnly
            };
        }

        private static void SetParameterValue(Parameter parameter, object value)
        {
            if (parameter.IsReadOnly) throw new InvalidOperationException("Parameter is read-only.");

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    parameter.Set(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    break;
                case StorageType.Integer:
                    if (value is bool booleanValue)
                    {
                        parameter.Set(booleanValue ? 1 : 0);
                    }
                    else
                    {
                        parameter.Set(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    }
                    break;
                case StorageType.Double:
                    parameter.Set(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                case StorageType.ElementId:
                    parameter.Set(CreateElementId(Convert.ToString(value, CultureInfo.InvariantCulture)));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported parameter storage type: " + parameter.StorageType);
            }
        }

        private static Element ResolveElement(Document document, string elementId)
        {
            try
            {
                return document.GetElement(CreateElementId(elementId));
            }
            catch
            {
                return null;
            }
        }

        private static bool LevelNameExists(Document document, string name)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Any(level => string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static double ToInternalElevation(Dictionary<string, object> elevation)
        {
            return ToInternalLength(elevation, "Elevation");
        }

        private static double ToInternalLength(Dictionary<string, object> unitValue, string fieldName)
        {
            if (unitValue == null) throw new ArgumentException(fieldName + " is required.");
            double value = GetDouble(unitValue, "value") ?? throw new ArgumentException(fieldName + " value is required.");
            string unit = (GetString(unitValue, "unit") ?? "mm").Trim().ToLowerInvariant();

            switch (unit)
            {
                case "mm":
                case "millimeters":
                    return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
                case "m":
                case "meters":
                    return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters);
                case "ft":
                case "feet":
                case "revit-internal":
                    return value;
                default:
                    throw new ArgumentException("Unsupported " + fieldName.ToLowerInvariant() + " unit: " + unit);
            }
        }

        private static double ToInternalAngle(Dictionary<string, object> angleValue)
        {
            if (angleValue == null) throw new ArgumentException("Angle is required.");
            double value = GetDouble(angleValue, "value") ?? throw new ArgumentException("Angle value is required.");
            string unit = (GetString(angleValue, "unit") ?? "degrees").Trim().ToLowerInvariant();

            switch (unit)
            {
                case "degrees":
                    return value * Math.PI / 180.0;
                case "radians":
                    return value;
                default:
                    throw new ArgumentException("Unsupported angle unit: " + unit);
            }
        }

        private static XYZ ToInternalPoint(Dictionary<string, object> point, string fieldName)
        {
            if (point == null) throw new ArgumentException(fieldName + " is required.");
            return new XYZ(
                ToInternalLength(GetDictionary(point, "x"), fieldName + ".x"),
                ToInternalLength(GetDictionary(point, "y"), fieldName + ".y"),
                ToInternalLength(GetDictionary(point, "z"), fieldName + ".z"));
        }

        private static string ValidateWallBaseline(Document document, XYZ start, XYZ end)
        {
            if (Math.Abs(start.Z - end.Z) > 0.000001)
            {
                return "create_wall start and end must have the same z elevation.";
            }

            double length = start.DistanceTo(end);
            double minimumLength = Math.Max(document.Application.ShortCurveTolerance, 0.000001);
            if (length <= minimumLength)
            {
                return "create_wall baseline is shorter than Revit's minimum curve length.";
            }

            return null;
        }

        private static double VectorLength(XYZ vector)
        {
            return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        }

        private static Dictionary<string, object> LengthValue(double internalLength)
        {
            return UnitValue(UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Millimeters), "mm", "metric");
        }

        private static Dictionary<string, object> AngleValue(double radians)
        {
            return new Dictionary<string, object>
            {
                ["value"] = Math.Round(radians * 180.0 / Math.PI, 6),
                ["unit"] = "degrees",
                ["radians"] = Math.Round(radians, 9)
            };
        }

        private static Dictionary<string, object> PointValue(XYZ point)
        {
            return new Dictionary<string, object>
            {
                ["x"] = LengthValue(point.X),
                ["y"] = LengthValue(point.Y),
                ["z"] = LengthValue(point.Z)
            };
        }

        private static void SetWallDoubleParameter(Wall wall, BuiltInParameter builtInParameter, double value, string parameterName)
        {
            Parameter parameter = wall.get_Parameter(builtInParameter);
            if (parameter == null) throw new InvalidOperationException("Wall " + parameterName + " parameter was not found.");
            if (parameter.IsReadOnly) throw new InvalidOperationException("Wall " + parameterName + " parameter is read-only.");
            parameter.Set(value);
        }

        private static Dictionary<string, object> ElementSummary(Document document, Element element)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element)
            };

            if (element.Category != null) summary["category"] = element.Category.Name;

            ElementId typeId = element.GetTypeId();
            if (IsValidElementId(typeId)) summary["typeId"] = ToElementIdString(typeId);

            ElementId levelId = GetLevelId(element);
            if (IsValidElementId(levelId)) summary["levelId"] = ToElementIdString(levelId);

            ElementType elementType = element as ElementType;
            if (elementType != null && !string.IsNullOrWhiteSpace(elementType.FamilyName))
            {
                summary["familyName"] = elementType.FamilyName;
            }

            return summary;
        }

        private static Dictionary<string, object> TypeSnapshot(Document document, Element element)
        {
            ElementType elementType = element as ElementType;
            if (elementType != null) return ElementSummary(document, elementType);

            ElementId typeId = element?.GetTypeId();
            if (!IsValidElementId(typeId))
            {
                return new Dictionary<string, object>
                {
                    ["available"] = false
                };
            }

            ElementType resolvedType = document.GetElement(typeId) as ElementType;
            if (resolvedType == null)
            {
                return new Dictionary<string, object>
                {
                    ["typeId"] = ToElementIdString(typeId),
                    ["available"] = false
                };
            }

            return ElementSummary(document, resolvedType);
        }

        private static bool IsValidTypeForElement(Element element, ElementId typeId)
        {
            try
            {
                ICollection<ElementId> validTypeIds = element.GetValidTypes();
                if (validTypeIds == null || validTypeIds.Count == 0) return true;
                return validTypeIds.Any(candidate => string.Equals(ToElementIdString(candidate), ToElementIdString(typeId), StringComparison.Ordinal));
            }
            catch
            {
                return true;
            }
        }

        private static Dictionary<string, object> WallSnapshot(Wall wall)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(wall.Id),
                ["uniqueId"] = wall.UniqueId,
                ["name"] = SafeElementName(wall),
                ["typeId"] = ToElementIdString(wall.GetTypeId()),
                ["levelId"] = ToElementIdString(GetLevelId(wall)),
                ["flipped"] = wall.Flipped
            };

            Parameter height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (height != null && height.StorageType == StorageType.Double) snapshot["height"] = LengthValue(height.AsDouble());

            Parameter baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            if (baseOffset != null && baseOffset.StorageType == StorageType.Double) snapshot["baseOffset"] = LengthValue(baseOffset.AsDouble());

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve?.Curve != null && locationCurve.Curve.IsBound)
            {
                snapshot["start"] = PointValue(locationCurve.Curve.GetEndPoint(0));
                snapshot["end"] = PointValue(locationCurve.Curve.GetEndPoint(1));
                snapshot["length"] = LengthValue(locationCurve.Curve.Length);
            }

            return snapshot;
        }

        private static Dictionary<string, object> LocationSnapshot(Element element)
        {
            if (element == null) return null;

            LocationPoint point = element.Location as LocationPoint;
            if (point != null)
            {
                return new Dictionary<string, object>
                {
                    ["point"] = PointValue(point.Point),
                    ["rotation"] = Math.Round(point.Rotation, 6)
                };
            }

            LocationCurve curve = element.Location as LocationCurve;
            if (curve?.Curve != null && curve.Curve.IsBound)
            {
                return new Dictionary<string, object>
                {
                    ["start"] = PointValue(curve.Curve.GetEndPoint(0)),
                    ["end"] = PointValue(curve.Curve.GetEndPoint(1)),
                    ["length"] = LengthValue(curve.Curve.Length)
                };
            }

            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            if (boundingBox != null)
            {
                return new Dictionary<string, object>
                {
                    ["min"] = PointValue(boundingBox.Min),
                    ["max"] = PointValue(boundingBox.Max)
                };
            }

            return new Dictionary<string, object>
            {
                ["available"] = false
            };
        }

        private static List<Dictionary<string, object>> GetOperations(Dictionary<string, object> payload)
        {
            var operations = new List<Dictionary<string, object>>();
            if (payload == null || !payload.TryGetValue("operations", out object value) || value == null) return operations;

            if (value is object[] array)
            {
                foreach (object item in array)
                {
                    if (item is Dictionary<string, object> operation) operations.Add(operation);
                }
            }
            else if (value is ArrayList list)
            {
                foreach (object item in list)
                {
                    if (item is Dictionary<string, object> operation) operations.Add(operation);
                }
            }

            return operations;
        }

        private static string GetTransactionName(Dictionary<string, object> payload)
        {
            string name = GetString(payload, "transactionName");
            return string.IsNullOrWhiteSpace(name) ? "Revit MCP Next change" : name.Trim();
        }

        private static string ComputePreviewId(Document document, string transactionName, List<Dictionary<string, object>> operations)
        {
            string raw = ComputeDocumentFingerprint(document) + "|" + transactionName + "|" + Canonicalize(operations);
            return HashString(raw).Substring(0, 24);
        }

        private static string ComputeChangeSetHash(object value)
        {
            return "sha256:" + HashString(Canonicalize(value));
        }

        private static string ComputePreviewChangeSetHash(
            string documentFingerprint,
            long generation,
            string transactionName,
            string operationsHash,
            string changesHash)
        {
            return ComputeChangeSetHash(new Dictionary<string, object>
            {
                ["documentFingerprint"] = documentFingerprint,
                ["baseGeneration"] = generation,
                ["transactionName"] = transactionName,
                ["operationsHash"] = operationsHash,
                ["changesHash"] = changesHash
            });
        }

        private static string FormatUtc(DateTimeOffset value)
        {
            return value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        private static string Canonicalize(object value)
        {
            if (value == null) return "null";
            if (value is Dictionary<string, object> dictionary)
            {
                return "{" + string.Join(",", dictionary.Keys.OrderBy(key => key, StringComparer.Ordinal)
                    .Select(key => key + ":" + Canonicalize(dictionary[key]))) + "}";
            }
            if (value is IEnumerable<Dictionary<string, object>> dictionaryEnumerable)
            {
                return "[" + string.Join(",", dictionaryEnumerable.Select(Canonicalize)) + "]";
            }
            if (value is object[] array)
            {
                return "[" + string.Join(",", array.Select(Canonicalize)) + "]";
            }
            if (value is ArrayList list)
            {
                return "[" + string.Join(",", list.Cast<object>().Select(Canonicalize)) + "]";
            }
            if (value is bool boolean) return boolean ? "true" : "false";
            if (IsNumeric(value)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string HashString(string raw)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private Dictionary<string, object> BuildStatus(UIApplication app)
        {
            UIDocument activeUiDocument = app.ActiveUIDocument;
            Document activeDocument = activeUiDocument?.Document;
            var data = new Dictionary<string, object>
            {
                ["connected"] = true,
                ["brokerVersion"] = "unknown",
                ["addinVersion"] = AddinVersion,
                ["protocolVersion"] = BridgeProtocol.Version,
                ["revit"] = new Dictionary<string, object>
                {
                    ["version"] = app.Application.VersionNumber,
                    ["build"] = app.Application.VersionBuild,
                    ["processId"] = Process.GetCurrentProcess().Id
                },
                ["selection"] = new Dictionary<string, object>
                {
                    ["count"] = activeUiDocument?.Selection?.GetElementIds()?.Count ?? 0
                },
                ["capabilities"] = new[]
                {
                    "revit.status",
                    "revit.list_documents",
                    "revit.get_levels",
                    "revit.query",
                    "revit.preview_change_set",
                    "revit.apply_change_set",
                    "revit.cancel_request"
                },
                ["warnings"] = Array.Empty<object>()
            };

            if (activeDocument != null)
            {
                data["activeDocument"] = BuildDocumentSummary(activeDocument, activeDocument);
            }

            return data;
        }

        private object[] BuildDocumentList(UIApplication app)
        {
            Document activeDocument = app.ActiveUIDocument?.Document;
            return EnumerateDocuments(app)
                .Select(document => BuildDocumentSummary(document, activeDocument))
                .ToArray();
        }

        private static IEnumerable<Element> CreateFilteredElements(
            UIApplication app,
            Document document,
            Dictionary<string, object> filter,
            List<BridgeWarning> warnings,
            out string scope)
        {
            bool selectionOnly = GetBool(filter, "selectionOnly", false);
            string viewId = GetString(filter, "viewId");

            if (selectionOnly)
            {
                UIDocument uidocument = app.ActiveUIDocument;
                if (uidocument == null || !ReferenceEquals(uidocument.Document, document))
                {
                    scope = "selection";
                    warnings.Add(new BridgeWarning { Code = "SELECTION_UNAVAILABLE", Message = "Selection is only available on the active document." });
                    return Enumerable.Empty<Element>();
                }

                scope = "selection";
                return uidocument.Selection.GetElementIds()
                    .Select(id => document.GetElement(id))
                    .Where(element => element != null)
                    .Where(element => MatchesPostFilters(element, filter));
            }

            FilteredElementCollector collector;
            if (!string.IsNullOrWhiteSpace(viewId))
            {
                ElementId parsedViewId = CreateElementId(viewId);
                collector = new FilteredElementCollector(document, parsedViewId);
                scope = "view:" + viewId;
            }
            else
            {
                collector = new FilteredElementCollector(document);
                scope = "activeDocument";
            }

            collector.WhereElementIsNotElementType();
            bool nativeCategoryFilter = TryApplyCategoryFilter(collector, GetStringList(filter, "categories"), warnings);
            bool nativeClassFilter = TryApplyClassFilter(collector, GetStringList(filter, "classes"), warnings);

            IEnumerable<Element> elements = collector.ToElements();
            return (!nativeCategoryFilter || !nativeClassFilter)
                ? elements.Where(element => MatchesPostFilters(element, filter))
                : elements.Where(element => MatchesSecondaryPostFilters(element, filter));
        }

        private static bool TryApplyCategoryFilter(
            FilteredElementCollector collector,
            IReadOnlyList<string> categories,
            List<BridgeWarning> warnings)
        {
            if (categories.Count == 0) return true;

            var builtInCategories = new List<BuiltInCategory>();
            foreach (string category in categories)
            {
                if (!TryParseBuiltInCategory(category, out BuiltInCategory builtInCategory))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "CATEGORY_POST_FILTER",
                        Message = "Category '" + category + "' is not a BuiltInCategory name; applying a slower post-filter."
                    });
                    return false;
                }

                builtInCategories.Add(builtInCategory);
            }

            collector.WherePasses(new ElementMulticategoryFilter(builtInCategories));
            return true;
        }

        private static bool TryApplyClassFilter(
            FilteredElementCollector collector,
            IReadOnlyList<string> classes,
            List<BridgeWarning> warnings)
        {
            if (classes.Count == 0) return true;

            var elementTypes = new List<Type>();
            foreach (string className in classes)
            {
                Type type = ResolveElementType(className);
                if (type == null)
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "CLASS_POST_FILTER",
                        Message = "Class '" + className + "' is not a recognized Autodesk.Revit.DB element class; applying a slower post-filter."
                    });
                    return false;
                }

                elementTypes.Add(type);
            }

            collector.WherePasses(new ElementMulticlassFilter(elementTypes));
            return true;
        }

        private static bool MatchesPostFilters(Element element, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> categories = GetStringList(filter, "categories");
            if (categories.Count > 0 && !MatchesCategory(element, categories)) return false;

            IReadOnlyList<string> classes = GetStringList(filter, "classes");
            if (classes.Count > 0 && !MatchesClass(element, classes)) return false;

            return MatchesSecondaryPostFilters(element, filter);
        }

        private static bool MatchesSecondaryPostFilters(Element element, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> levelIds = GetStringList(filter, "levelIds");
            if (levelIds.Count > 0 && !levelIds.Contains(ToElementIdString(GetLevelId(element)), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> worksetIds = GetStringList(filter, "worksetIds");
            if (worksetIds.Count > 0 && !worksetIds.Contains(ToWorksetIdString(element.WorksetId), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> designOptionIds = GetStringList(filter, "designOptionIds");
            if (designOptionIds.Count > 0 && !designOptionIds.Contains(ToElementIdString(GetDesignOptionId(element)), StringComparer.OrdinalIgnoreCase)) return false;

            Dictionary<string, object> parameterEquals = GetDictionary(filter, "parameterEquals");
            if (parameterEquals != null)
            {
                foreach (KeyValuePair<string, object> expected in parameterEquals)
                {
                    if (!ParameterEquals(element, expected.Key, expected.Value)) return false;
                }
            }

            return true;
        }

        private static bool MatchesCategory(Element element, IReadOnlyList<string> categories)
        {
            string categoryName = element.Category?.Name ?? string.Empty;
            string builtInName = string.Empty;
            try
            {
                if (element.Category != null)
                {
                    builtInName = ((BuiltInCategory)GetElementIdValue(element.Category.Id)).ToString();
                }
            }
            catch
            {
                builtInName = string.Empty;
            }

            return categories.Any(category =>
                string.Equals(category, categoryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, builtInName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals("OST_" + category.Replace(" ", string.Empty), builtInName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesClass(Element element, IReadOnlyList<string> classes)
        {
            string className = element.GetType().Name;
            return classes.Any(value => string.Equals(value, className, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> BuildQueryItem(Element element, IReadOnlyList<string> fields)
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(element.Id)
            };

            foreach (string field in fields)
            {
                switch (field)
                {
                    case "id":
                        break;
                    case "uniqueId":
                        item["uniqueId"] = element.UniqueId;
                        break;
                    case "category":
                        item["category"] = element.Category?.Name;
                        break;
                    case "class":
                        item["class"] = element.GetType().Name;
                        break;
                    case "name":
                        item["name"] = SafeElementName(element);
                        break;
                    case "typeId":
                        item["typeId"] = ToElementIdString(element.GetTypeId());
                        break;
                    case "levelId":
                        ElementId levelId = GetLevelId(element);
                        if (IsValidElementId(levelId)) item["levelId"] = ToElementIdString(levelId);
                        break;
                    default:
                        if (field.StartsWith("param:", StringComparison.OrdinalIgnoreCase))
                        {
                            string parameterName = field.Substring("param:".Length);
                            Parameter parameter = element.LookupParameter(parameterName);
                            if (parameter != null)
                            {
                                Dictionary<string, object> extras = GetOrCreateFields(item);
                                extras[parameterName] = ParameterValue(parameter);
                            }
                        }
                        break;
                }
            }

            return item;
        }

        private static Dictionary<string, object> GetOrCreateFields(Dictionary<string, object> item)
        {
            if (!item.TryGetValue("fields", out object existing) || !(existing is Dictionary<string, object> fields))
            {
                fields = new Dictionary<string, object>();
                item["fields"] = fields;
            }

            return fields;
        }

        private static string[] NormalizeFields(IReadOnlyList<string> requested, string preset, List<BridgeWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "schedule":
                    defaults = new[] { "id", "category", "name", "typeId", "levelId" };
                    break;
                case "geometrySummary":
                    warnings.Add(new BridgeWarning
                    {
                        Code = "GEOMETRY_SUMMARY_NOT_READY",
                        Message = "geometrySummary currently returns summary fields; geometry extraction will be added behind explicit budgets."
                    });
                    defaults = SummaryFields();
                    break;
                default:
                    defaults = SummaryFields();
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedField(field))
                {
                    warnings.Add(new BridgeWarning
                    {
                        Code = "UNSUPPORTED_FIELD",
                        Message = "Field '" + field + "' is not supported by the current query projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static string[] SummaryFields()
        {
            return new[] { "id", "uniqueId", "category", "class", "name", "typeId", "levelId" };
        }

        private static bool IsSupportedField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "category":
                case "class":
                case "name":
                case "typeId":
                case "levelId":
                    return true;
                default:
                    return field.StartsWith("param:", StringComparison.OrdinalIgnoreCase) && field.Length > "param:".Length;
            }
        }

        private Dictionary<string, object> BuildDocumentSummary(Document document, Document activeDocument)
        {
            var summary = new Dictionary<string, object>
            {
                ["documentId"] = GetDocumentId(document),
                ["title"] = document.Title,
                ["fingerprint"] = ComputeDocumentFingerprint(document),
                ["isActive"] = ReferenceEquals(document, activeDocument),
                ["isWorkshared"] = document.IsWorkshared,
                ["isModified"] = document.IsModified,
                ["generation"] = _generations.GetGeneration(document)
            };

            if (!string.IsNullOrWhiteSpace(document.PathName)) summary["path"] = document.PathName;

            View activeView = SafeActiveView(document);
            if (activeView != null)
            {
                summary["activeView"] = BuildViewSummary(activeView);
            }

            return summary;
        }

        private static Dictionary<string, object> BuildViewSummary(View view)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(view.Id),
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(),
                ["isGraphical"] = !view.IsTemplate && view.CanBePrinted
            };

            if (view.Scale > 0) summary["scale"] = view.Scale;
            return summary;
        }

        private static Dictionary<string, object> BuildLevelSummary(Level level)
        {
            return new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(level.Id),
                ["uniqueId"] = level.UniqueId,
                ["name"] = level.Name,
                ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters), "mm", "metric"),
                ["isBuildingStory"] = IsBuildingStory(level)
            };
        }

        private static Dictionary<string, object> UnitValue(double value, string unit, string system)
        {
            return new Dictionary<string, object>
            {
                ["value"] = Math.Round(value, 6),
                ["unit"] = unit,
                ["system"] = system
            };
        }

        private static IEnumerable<Document> EnumerateDocuments(UIApplication app)
        {
            foreach (Document document in app.Application.Documents)
            {
                yield return document;
            }
        }

        private static Document ResolveDocument(UIApplication app, BridgeRequestEnvelope request)
        {
            string requestedFingerprint = request.DocumentFingerprint ?? GetString(request.Payload, "documentFingerprint");
            Document activeDocument = app.ActiveUIDocument?.Document;

            if (string.IsNullOrWhiteSpace(requestedFingerprint))
            {
                return activeDocument ?? EnumerateDocuments(app).FirstOrDefault();
            }

            return EnumerateDocuments(app)
                .FirstOrDefault(document => string.Equals(ComputeDocumentFingerprint(document), requestedFingerprint, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetDocumentId(Document document)
        {
            return string.IsNullOrWhiteSpace(document.PathName)
                ? document.Title + ":" + document.GetHashCode().ToString(CultureInfo.InvariantCulture)
                : document.PathName;
        }

        private static string ComputeDocumentFingerprint(Document document)
        {
            return DocumentGenerationTracker.ComputeDocumentFingerprint(document);
        }

        private static View SafeActiveView(Document document)
        {
            try
            {
                return document.ActiveView;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeElementName(Element element)
        {
            try
            {
                return element.Name;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static ElementId GetLevelId(Element element)
        {
            object value = element.GetType().GetProperty("LevelId")?.GetValue(element, null);
            if (value is ElementId reflectedLevelId && IsValidElementId(reflectedLevelId)) return reflectedLevelId;

            foreach (BuiltInParameter builtInParameter in new[]
            {
                BuiltInParameter.LEVEL_PARAM,
                BuiltInParameter.FAMILY_LEVEL_PARAM,
                BuiltInParameter.SCHEDULE_LEVEL_PARAM
            })
            {
                Parameter parameter = element.get_Parameter(builtInParameter);
                ElementId id = parameter?.AsElementId();
                if (IsValidElementId(id)) return id;
            }

            return ElementId.InvalidElementId;
        }

        private static ElementId GetDesignOptionId(Element element)
        {
            object value = element.GetType().GetProperty("DesignOption")?.GetValue(element, null);
            Element designOption = value as Element;
            return designOption?.Id ?? ElementId.InvalidElementId;
        }

        private static bool IsBuildingStory(Level level)
        {
            object reflected = typeof(Level).GetProperty("IsBuildingStory")?.GetValue(level, null);
            if (reflected is bool value) return value;

            if (Enum.IsDefined(typeof(BuiltInParameter), "LEVEL_IS_BUILDING_STORY"))
            {
                var builtInParameter = (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), "LEVEL_IS_BUILDING_STORY");
                Parameter parameter = level.get_Parameter(builtInParameter);
                return parameter != null && parameter.AsInteger() != 0;
            }

            return false;
        }

        private static bool ParameterEquals(Element element, string parameterName, object expected)
        {
            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter == null) return false;

            object actual = ParameterValue(parameter);
            if (actual == null) return expected == null;

            if (expected is bool expectedBool)
            {
                if (actual is int actualInt) return (actualInt != 0) == expectedBool;
                if (bool.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), out bool actualBool)) return actualBool == expectedBool;
            }

            if (IsNumeric(expected) && IsNumeric(actual))
            {
                return Math.Abs(Convert.ToDouble(actual, CultureInfo.InvariantCulture) - Convert.ToDouble(expected, CultureInfo.InvariantCulture)) < 0.000001;
            }

            return string.Equals(
                Convert.ToString(actual, CultureInfo.InvariantCulture),
                Convert.ToString(expected, CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
        }

        private static object ParameterValue(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return parameter.AsDouble();
                case StorageType.Integer:
                    return parameter.AsInteger();
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.ElementId:
                    return ToElementIdString(parameter.AsElementId());
                default:
                    return parameter.AsValueString();
            }
        }

        private static bool IsNumeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                   value is int || value is uint || value is long || value is ulong ||
                   value is float || value is double || value is decimal;
        }

        private static bool TryParseBuiltInCategory(string value, out BuiltInCategory category)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (Enum.TryParse(trimmed, true, out category)) return true;
            return Enum.TryParse("OST_" + trimmed.Replace(" ", string.Empty), true, out category);
        }

        private static Type ResolveElementType(string className)
        {
            string normalized = (className ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return null;
            Type type = typeof(Element).Assembly.GetType("Autodesk.Revit.DB." + normalized, false, true);
            return type != null && typeof(Element).IsAssignableFrom(type) ? type : null;
        }

        private static ElementId CreateElementId(string value)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long idValue))
            {
                throw new ArgumentException("Element id must be numeric: " + value);
            }

            try
            {
                return (ElementId)Activator.CreateInstance(typeof(ElementId), idValue);
            }
            catch
            {
                return (ElementId)Activator.CreateInstance(typeof(ElementId), Convert.ToInt32(idValue));
            }
        }

        private static bool IsValidElementId(ElementId id)
        {
            return id != null && GetElementIdValue(id) >= 0;
        }

        private static string ToElementIdString(ElementId id)
        {
            return id == null ? string.Empty : GetElementIdValue(id).ToString(CultureInfo.InvariantCulture);
        }

        private static string ToWorksetIdString(WorksetId id)
        {
            if (id == null) return string.Empty;

            object value = typeof(WorksetId).GetProperty("IntegerValue")?.GetValue(id, null);
            return value == null ? id.ToString() : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        private static long GetElementIdValue(ElementId id)
        {
            object value = typeof(ElementId).GetProperty("Value")?.GetValue(id, null);
            if (value != null) return Convert.ToInt64(value, CultureInfo.InvariantCulture);

#pragma warning disable CS0618
            return id.IntegerValue;
#pragma warning restore CS0618
        }

        private static int ParseCursor(string cursor, List<BridgeWarning> warnings)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            if (int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset) && offset >= 0) return offset;

            warnings.Add(new BridgeWarning
            {
                Code = "INVALID_CURSOR",
                Message = "Cursor '" + cursor + "' is invalid; returning the first page."
            });
            return 0;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? value as Dictionary<string, object> : null;
        }

        private static IReadOnlyList<string> GetStringList(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return Array.Empty<string>();
            if (value is string single) return new[] { single };
            if (value is object[] array) return array.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            if (value is ArrayList list) return list.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return Array.Empty<string>();
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static int? GetInt(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static long? GetLong(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static double? GetDouble(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(Dictionary<string, object> root, string key, bool defaultValue)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return defaultValue;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static bool? GetNullableBool(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static BridgeResponseEnvelope Success(
            BridgeRequestEnvelope request,
            object data,
            Stopwatch sw,
            List<BridgeWarning> warnings = null,
            BridgeMetrics metrics = null,
            long? generation = null)
        {
            sw.Stop();
            BridgeMetrics actualMetrics = metrics ?? new BridgeMetrics();
            actualMetrics.ElapsedMs = sw.ElapsedMilliseconds;
            return new BridgeResponseEnvelope
            {
                Ok = true,
                RequestId = request.RequestId,
                Data = data,
                Warnings = warnings ?? new List<BridgeWarning>(),
                Metrics = actualMetrics,
                Generation = generation ?? 0
            };
        }

        private static BridgeResponseEnvelope Failure(BridgeRequestEnvelope request, string code, string message, Stopwatch sw = null)
        {
            sw?.Stop();
            return new BridgeResponseEnvelope
            {
                Ok = false,
                RequestId = request.RequestId,
                Error = new BridgeError
                {
                    Code = code,
                    Message = message,
                    Recoverable = true
                },
                Metrics = new BridgeMetrics { ElapsedMs = sw?.ElapsedMilliseconds ?? 0 }
            };
        }
    }
}
