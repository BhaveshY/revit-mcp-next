using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMcpNext.Addin.Ipc;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class ModelDeliveryWorkflow
    {
        private static readonly TimeSpan PreviewTimeToLive = TimeSpan.FromMinutes(15);
        private const string StagingDirectoryName = ".revit-mcp-staging";
        private const string OwnershipFileName = ".revit-mcp-delivery.json";
        private readonly object _gate = new object();
        private readonly Dictionary<string, DeliveryPreviewToken> _previews =
            new Dictionary<string, DeliveryPreviewToken>(StringComparer.Ordinal);
        private readonly Dictionary<string, DeliveryJob> _jobs =
            new Dictionary<string, DeliveryJob>(StringComparer.Ordinal);
        private readonly string _runtimeInstanceId;
        private readonly Func<Document, long> _generationProvider;

        public ModelDeliveryWorkflow(string runtimeInstanceId, Func<Document, long> generationProvider)
        {
            _runtimeInstanceId = string.IsNullOrWhiteSpace(runtimeInstanceId) ? "in-process" : runtimeInstanceId;
            _generationProvider = generationProvider ?? throw new ArgumentNullException(nameof(generationProvider));
        }

        public bool HasPendingWork
        {
            get
            {
                lock (_gate)
                {
                    return _jobs.Values.Any(job => !job.IsTerminal);
                }
            }
        }

        public Dictionary<string, object> Preview(
            UIApplication uiApplication,
            string sessionId,
            DeliveryTargetBinding targetBinding,
            Dictionary<string, object> payload)
        {
            if (uiApplication == null) throw new ModelDeliveryException("REVIT_APPLICATION_REQUIRED", "Revit is not available.");
            VerifyTargetAvailable(uiApplication.Application, targetBinding);
            DeliveryRecipe recipe = ParseRecipe(payload);
            var blockers = new List<DeliveryIssue>();
            var warnings = new List<DeliveryIssue>();
            ValidateRecipePaths(recipe, blockers, warnings);

            var modelPlans = new List<DeliveryModelPlan>();
            var sourceFingerprints = new Dictionary<string, SourceFingerprint>(StringComparer.OrdinalIgnoreCase);
            var openPaths = GetOpenDocumentPaths(uiApplication.Application);
            foreach (DeliverySource source in recipe.Sources)
            {
                if (!File.Exists(source.SourcePath))
                {
                    blockers.Add(new DeliveryIssue("SOURCE_NOT_FOUND", "Source RVT was not found.", source.Id, source.SourcePath));
                    continue;
                }

                SourceFingerprint fingerprint = SourceFingerprint.Read(source.SourcePath);
                sourceFingerprints[source.Id] = fingerprint;
                if (openPaths.Contains(source.SourcePath))
                {
                    blockers.Add(new DeliveryIssue(
                        "SOURCE_DOCUMENT_OPEN",
                        "Close the production source document in Revit before previewing or executing delivery.",
                        source.Id,
                        source.SourcePath));
                    continue;
                }

                try
                {
                    DeliveryModelPlan plan = InspectSource(uiApplication.Application, recipe, source, blockers, warnings);
                    modelPlans.Add(plan);
                }
                catch (Exception ex)
                {
                    blockers.Add(new DeliveryIssue("SOURCE_INSPECTION_FAILED", ex.Message, source.Id, source.SourcePath));
                }
            }

            foreach (DeliveryLinkRule rule in recipe.LinkRules.Where(rule => rule.Required))
            {
                if (!modelPlans.Any(plan => plan.MatchedRuleIds.Contains(rule.Id)))
                {
                    blockers.Add(new DeliveryIssue(
                        "REQUIRED_LINK_RULE_UNMATCHED",
                        "Required link rule did not match any link: " + rule.Id + ".",
                        rule.SourceModelId));
                }
            }

            string canonicalRecipe = CanonicalJson.Serialize(GetDictionary(payload, "recipe"));
            string planHash = Hash(targetBinding.CanonicalIdentity + "|" + canonicalRecipe + "|" + string.Join("|", sourceFingerprints
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + ":" + pair.Value.Length.ToString(CultureInfo.InvariantCulture) + ":" + pair.Value.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture) + ":" + pair.Value.Sha256)));
            string previewId = "delivery-" + Guid.NewGuid().ToString("N");
            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(PreviewTimeToLive);
            bool ready = blockers.Count == 0 && modelPlans.Count == recipe.Sources.Count;
            var token = new DeliveryPreviewToken(
                previewId,
                sessionId,
                planHash,
                canonicalRecipe,
                recipe,
                targetBinding,
                sourceFingerprints,
                modelPlans,
                ready,
                expiresAt);
            lock (_gate)
            {
                RemoveExpiredUnsafe(DateTimeOffset.UtcNow);
                _previews[PreviewKey(sessionId, previewId)] = token;
            }

            return new Dictionary<string, object>
            {
                ["previewId"] = previewId,
                ["planHash"] = planHash,
                ["expiresAt"] = expiresAt.ToUniversalTime().ToString("o"),
                ["ready"] = ready,
                ["requiresConfirmation"] = true,
                ["deliveryId"] = recipe.DeliveryId,
                ["packagePath"] = recipe.PackagePath,
                ["stagingPath"] = recipe.StagingPath,
                ["targetBinding"] = targetBinding.ToDictionary(),
                ["models"] = modelPlans.Select(plan => plan.ToDictionary()).ToArray(),
                ["blockers"] = blockers.Select(issue => issue.ToDictionary()).ToArray(),
                ["warnings"] = warnings.Select(issue => issue.ToDictionary()).ToArray()
            };
        }

        public Dictionary<string, object> Inspect(
            UIApplication uiApplication,
            DeliveryTargetBinding targetBinding,
            Dictionary<string, object> payload)
        {
            if (uiApplication == null) throw new ModelDeliveryException("REVIT_APPLICATION_REQUIRED", "Revit is not available.");
            VerifyTargetAvailable(uiApplication.Application, targetBinding);
            IReadOnlyList<string> requestedPaths = GetStringList(payload, "sourcePaths");
            if (requestedPaths.Count == 0) throw new ModelDeliveryException("DELIVERY_SOURCES_REQUIRED", "At least one local, mapped-drive, or UNC RVT source path is required.");
            if (requestedPaths.Count > 64) throw new ModelDeliveryException("DELIVERY_SOURCE_LIMIT", "A maximum of 64 source RVTs can be inspected in one request.");

            List<string> sourcePaths = requestedPaths.Select(NormalizeAbsolutePath).ToList();
            EnsureUnique(sourcePaths, "DUPLICATE_SOURCE_PATH", "Source model paths must be unique.");
            Dictionary<string, object> previousRaw = GetDictionary(payload, "previousRecipe");
            DeliveryRecipe previousRecipe = previousRaw == null
                ? null
                : ParseRecipe(new Dictionary<string, object> { ["recipe"] = previousRaw });
            var inspections = new List<Dictionary<string, object>>();
            var detectedChanges = new List<Dictionary<string, object>>();
            bool hasUnmappedLinks = false;
            bool needsOpeningViewDecision = false;

            foreach (string sourcePath in sourcePaths)
            {
                if (!File.Exists(sourcePath)) throw new ModelDeliveryException("SOURCE_NOT_FOUND", "Source RVT was not found: " + sourcePath);
                BasicFileInfo basic = BasicFileInfo.Extract(sourcePath);
                Document document = FindOpenDocument(uiApplication.Application, sourcePath);
                bool closeDocument = document == null;
                string isolatedInspectionPath = null;
                if (closeDocument)
                {
                    isolatedInspectionPath = CreateIsolatedSourceCopy(sourcePath, "inspect");
                    document = OpenSource(uiApplication.Application, isolatedInspectionPath, basic.IsWorkshared);
                }
                try
                {
                    DeliverySource previousSource = previousRecipe?.Sources.FirstOrDefault(source => PathsEqual(source.SourcePath, sourcePath));
                    string suggestedId = previousSource?.Id ?? Slug(Path.GetFileNameWithoutExtension(sourcePath));
                    string suggestedTarget = previousSource?.TargetFileName ?? Path.GetFileName(sourcePath);
                    List<View> userViews = new FilteredElementCollector(document)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(view => !view.IsTemplate && !IsSystemView(view) && !(view is ViewSheet) && !(view is ViewSchedule))
                        .OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    string[] openingCandidates = userViews
                        .Where(view => view.ViewType != ViewType.ThreeD)
                        .Select(view => view.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(50)
                        .ToArray();
                    string[] threeDCandidates = userViews
                        .Where(view => view.ViewType == ViewType.ThreeD)
                        .Select(view => view.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(25)
                        .ToArray();
                    string inferredStart = previousSource?.StartViewName ?? openingCandidates.FirstOrDefault(name => name.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0);
                    string inferred3d = previousSource?.Start3dViewName ?? threeDCandidates.FirstOrDefault(name => name.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (string.IsNullOrWhiteSpace(inferredStart)) needsOpeningViewDecision = true;

                    var links = new List<Dictionary<string, object>>();
                    var instancesByType = new FilteredElementCollector(document)
                        .OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>()
                        .GroupBy(instance => IdValue(instance.GetTypeId()))
                        .ToDictionary(group => group.Key, group => group.ToList());
                    foreach (RevitLinkType linkType in new FilteredElementCollector(document).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
                    {
                        string linkPath = GetLinkPath(document, linkType);
                        string linkName = Path.GetFileName(linkPath ?? string.Empty);
                        List<RevitLinkInstance> instances = instancesByType.TryGetValue(IdValue(linkType.Id), out List<RevitLinkInstance> found)
                            ? found
                            : new List<RevitLinkInstance>();
                        bool mapsToSource = sourcePaths.Any(candidate => PathsEqual(candidate, linkPath)) ||
                                            sourcePaths.Count(candidate => string.Equals(Path.GetFileName(candidate), linkName, StringComparison.OrdinalIgnoreCase)) == 1;
                        bool matchedPreviousRule = previousRecipe != null && previousRecipe.LinkRules.Any(rule =>
                            string.Equals(rule.SourceModelId, suggestedId, StringComparison.Ordinal) && rule.Matches(linkPath, linkName));
                        if (!mapsToSource && !matchedPreviousRule) hasUnmappedLinks = true;
                        links.Add(new Dictionary<string, object>
                        {
                            ["path"] = linkPath,
                            ["fileName"] = linkName,
                            ["instanceCount"] = instances.Count,
                            ["transforms"] = instances.Select(TransformSignature).ToArray(),
                            ["automaticallyMapsToSource"] = mapsToSource,
                            ["coveredByPreviousRecipe"] = matchedPreviousRule
                        });
                    }

                    if (previousRecipe != null && previousSource == null)
                    {
                        detectedChanges.Add(new Dictionary<string, object> { ["code"] = "SOURCE_ADDED", ["path"] = sourcePath, ["message"] = "A source model is new and needs a role/target-name decision." });
                    }
                    if (previousSource != null)
                    {
                        foreach (string protectedName in ProtectedViewNames(previousRecipe.Cleanup, previousSource))
                        {
                            if (!userViews.Any(view => string.Equals(view.Name, protectedName, StringComparison.OrdinalIgnoreCase)))
                            {
                                detectedChanges.Add(new Dictionary<string, object> { ["code"] = "PROTECTED_VIEW_CHANGED", ["modelId"] = previousSource.Id, ["message"] = "A protected view from the previous recipe is missing: " + protectedName + "." });
                            }
                        }
                    }

                    FileInfo file = new FileInfo(sourcePath);
                    inspections.Add(new Dictionary<string, object>
                    {
                        ["sourcePath"] = sourcePath,
                        ["suggestedId"] = suggestedId,
                        ["suggestedTargetFileName"] = suggestedTarget,
                        ["isWorkshared"] = basic.IsWorkshared,
                        ["bytes"] = file.Length,
                        ["lastWriteUtc"] = file.LastWriteTimeUtc.ToString("o"),
                        ["suggestedStartViewName"] = inferredStart,
                        ["suggestedStart3dViewName"] = inferred3d,
                        ["openingViewCandidates"] = openingCandidates,
                        ["threeDViewCandidates"] = threeDCandidates,
                        ["sheetCount"] = new FilteredElementCollector(document).OfClass(typeof(ViewSheet)).GetElementCount(),
                        ["scheduleCount"] = new FilteredElementCollector(document).OfClass(typeof(ViewSchedule)).GetElementCount(),
                        ["viewTemplateCount"] = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Count(view => view.IsTemplate),
                        ["filterCount"] = new FilteredElementCollector(document).WherePasses(new LogicalOrFilter(new ElementClassFilter(typeof(ParameterFilterElement)), new ElementClassFilter(typeof(SelectionFilterElement)))).GetElementCount(),
                        ["warningCount"] = document.GetWarnings().Count,
                        ["links"] = links.ToArray()
                    });
                }
                finally
                {
                    if (closeDocument) document.Close(false);
                    DeleteTemporarySourceCopy(isolatedInspectionPath);
                }
            }

            if (previousRecipe != null)
            {
                foreach (DeliverySource removed in previousRecipe.Sources.Where(source => !sourcePaths.Any(path => PathsEqual(path, source.SourcePath))))
                {
                    detectedChanges.Add(new Dictionary<string, object> { ["code"] = "SOURCE_REMOVED", ["modelId"] = removed.Id, ["path"] = removed.SourcePath, ["message"] = "A source from the previous recipe is not in the current delivery set." });
                }
            }

            var missingDecisions = new List<Dictionary<string, object>>();
            if (previousRecipe == null)
            {
                missingDecisions.Add(Decision("package", "Where should the package be written, and what should this delivery folder be called?"));
                missingDecisions.Add(Decision("modelRolesAndNames", "Confirm the suggested model roles and output RVT names."));
                missingDecisions.Add(Decision("cleanupAndExports", "Choose one cleanup profile and the required IFC/DWG/NWC exports."));
            }
            if (needsOpeningViewDecision) missingDecisions.Add(Decision("openingViews", "Choose an opening view only for models where no clear start view was detected."));
            if (hasUnmappedLinks) missingDecisions.Add(Decision("unmappedLinks", "Choose retain, unload, or remove only for links that do not map to another packaged source."));
            if (previousRecipe != null && detectedChanges.Count > 0) missingDecisions.Add(Decision("detectedChanges", "Review only the detected differences from the saved recipe."));

            return new Dictionary<string, object>
            {
                ["mode"] = previousRecipe == null ? "firstTime" : "repeat",
                ["targetBinding"] = targetBinding.ToDictionary(),
                ["recipeReusable"] = previousRecipe != null && detectedChanges.Count == 0 && missingDecisions.Count == 0,
                ["models"] = inspections.ToArray(),
                ["detectedChanges"] = detectedChanges.ToArray(),
                ["missingDecisions"] = missingDecisions.ToArray()
            };
        }

        public Dictionary<string, object> Execute(
            UIApplication uiApplication,
            string sessionId,
            DeliveryTargetBinding targetBinding,
            Dictionary<string, object> payload)
        {
            if (uiApplication == null) throw new ModelDeliveryException("REVIT_APPLICATION_REQUIRED", "Revit is not available.");
            if (!GetBool(payload, "confirm", false))
            {
                throw new ModelDeliveryException("CONFIRMATION_REQUIRED", "revit.execute_model_delivery requires confirm=true.");
            }

            string previewId = RequireString(payload, "previewId");
            string planHash = RequireString(payload, "planHash");
            DeliveryPreviewToken token;
            lock (_gate)
            {
                RemoveExpiredUnsafe(DateTimeOffset.UtcNow);
                if (!_previews.TryGetValue(PreviewKey(sessionId, previewId), out token))
                {
                    throw new ModelDeliveryException("DELIVERY_PREVIEW_NOT_FOUND", "The delivery preview was not issued to this session, expired, or was already consumed.");
                }

                if (!token.Ready)
                {
                    throw new ModelDeliveryException("DELIVERY_PREVIEW_NOT_READY", "The approved delivery preview contains blockers and cannot execute.");
                }

                if (!string.Equals(token.PlanHash, planHash, StringComparison.Ordinal))
                {
                    throw new ModelDeliveryException("DELIVERY_PLAN_HASH_MISMATCH", "The planHash does not match the approved delivery preview.");
                }

                if (!token.TargetBinding.Equals(targetBinding))
                {
                    throw new ModelDeliveryException(
                        "DELIVERY_TARGET_CHANGED",
                        "The approved delivery preview is bound to " + token.TargetBinding.Describe() +
                        ", but this request targets " + targetBinding.Describe() +
                        ". Call revit.list_documents, revit.set_target with the intended instanceId and documentFingerprint, then preview again.");
                }

                VerifyTargetAvailable(uiApplication.Application, targetBinding);

                string canonicalRecipe = CanonicalJson.Serialize(GetDictionary(payload, "recipe"));
                if (!string.Equals(token.CanonicalRecipe, canonicalRecipe, StringComparison.Ordinal))
                {
                    throw new ModelDeliveryException("DELIVERY_PREVIEW_MISMATCH", "The delivery recipe changed after preview. Run preview again.");
                }

                DeliveryJob active = _jobs.Values.FirstOrDefault(candidate => !candidate.IsTerminal);
                if (active != null)
                {
                    throw new ModelDeliveryException(
                        "DELIVERY_JOB_ACTIVE",
                        "Another model delivery is already running: " + active.JobId + ". Wait for it, cancel it, or inspect its status before starting another delivery.");
                }

                foreach (DeliverySource source in token.Recipe.Sources)
                {
                    SourceFingerprint current = SourceFingerprint.Read(source.SourcePath);
                    if (!token.SourceFingerprints.TryGetValue(source.Id, out SourceFingerprint expected) || !expected.Equals(current))
                    {
                        throw new ModelDeliveryException("DELIVERY_SOURCE_CHANGED", "Source model changed after preview: " + source.SourcePath + ". Run preview again.");
                    }
                }

                _previews.Remove(PreviewKey(sessionId, previewId));
            }

            var job = new DeliveryJob(sessionId, token);
            lock (_gate)
            {
                _jobs[job.JobId] = job;
            }

            return job.ToDictionary();
        }

        public Dictionary<string, object> GetStatus(string sessionId, Dictionary<string, object> payload)
        {
            string jobId = RequireString(payload, "jobId");
            lock (_gate)
            {
                DeliveryJob job = GetOwnedJobUnsafe(sessionId, jobId);
                return job.ToDictionary();
            }
        }

        public Dictionary<string, object> Cancel(string sessionId, Dictionary<string, object> payload)
        {
            string jobId = RequireString(payload, "jobId");
            string reason = GetString(payload, "reason") ?? "Cancelled by the user through Codex.";
            lock (_gate)
            {
                DeliveryJob job = GetOwnedJobUnsafe(sessionId, jobId);
                if (!job.IsTerminal)
                {
                    job.CancellationRequested = true;
                    job.CancellationReason = reason;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                }
                return job.ToDictionary();
            }
        }

        public void ProcessNext(UIApplication uiApplication)
        {
            DeliveryJob job;
            lock (_gate)
            {
                job = _jobs.Values
                    .Where(candidate => !candidate.IsTerminal)
                    .OrderBy(candidate => candidate.CreatedAt)
                    .FirstOrDefault();
            }
            if (job == null) return;

            try
            {
                if (job.CancellationRequested)
                {
                    FinishCancelled(job);
                    return;
                }

                EventHandler<DialogBoxShowingEventArgs> dialogGuard = (sender, eventArgs) =>
                {
                    if (string.Equals(eventArgs.DialogId, "TaskDialog_Location_Position_Changed", StringComparison.Ordinal))
                    {
                        eventArgs.OverrideResult(1002); // Do not save linked-model positioning back to any source.
                    }
                };
                uiApplication.DialogBoxShowing += dialogGuard;
                try
                {
                    ProcessJobStep(uiApplication.Application, job);
                }
                finally
                {
                    uiApplication.DialogBoxShowing -= dialogGuard;
                }
            }
            catch (Exception ex)
            {
                string code = ex is ModelDeliveryException deliveryException ? deliveryException.Code : "DELIVERY_EXECUTION_FAILED";
                job.Errors.Add(new DeliveryIssue(code, ex.Message));
                FinishFailed(job);
            }
        }

        public Dictionary<string, object> GetDiagnosticsSnapshot()
        {
            lock (_gate)
            {
                RemoveExpiredUnsafe(DateTimeOffset.UtcNow);
                return new Dictionary<string, object>
                {
                    ["activePreviewCount"] = _previews.Count,
                    ["previewTtlSeconds"] = (int)PreviewTimeToLive.TotalSeconds,
                    ["deliveryJobCount"] = _jobs.Count,
                    ["activeDeliveryJobCount"] = _jobs.Values.Count(job => !job.IsTerminal)
                };
            }
        }

        private DeliveryJob GetOwnedJobUnsafe(string sessionId, string jobId)
        {
            if (!_jobs.TryGetValue(jobId, out DeliveryJob job) ||
                !string.Equals(job.SessionId, sessionId ?? string.Empty, StringComparison.Ordinal))
            {
                throw new ModelDeliveryException("DELIVERY_JOB_NOT_FOUND", "No model-delivery job with that ID belongs to this Codex session.");
            }
            return job;
        }

        private void ProcessJobStep(Autodesk.Revit.ApplicationServices.Application application, DeliveryJob job)
        {
            VerifyTargetAvailable(application, job.Token.TargetBinding);
            DeliveryRecipe recipe = job.Token.Recipe;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            switch (job.Phase)
            {
                case DeliveryJobPhase.Queued:
                    if (GetOpenDocumentPaths(application).Overlaps(recipe.Sources.Select(source => source.SourcePath)))
                    {
                        throw new ModelDeliveryException("SOURCE_DOCUMENT_OPEN", "All production source documents must be closed before delivery execution.");
                    }
                    VerifySourceFingerprints(job.Token, "before staging");
                    PrepareStaging(recipe);
                    job.StagingPrepared = true;
                    WriteOwnershipMarker(recipe);
                    job.Phase = DeliveryJobPhase.Copying;
                    job.Message = "Delivery staging prepared; creating standalone RVT copies.";
                    break;

                case DeliveryJobPhase.Copying:
                    if (job.SourceIndex < recipe.Sources.Count)
                    {
                        DeliverySource source = recipe.Sources[job.SourceIndex++];
                        CreateStandaloneCopy(application, source, Path.Combine(recipe.StagingPath, source.TargetFileName));
                        job.CompletedUnits++;
                        job.Message = "Created standalone copy " + job.SourceIndex.ToString(CultureInfo.InvariantCulture) + " of " + recipe.Sources.Count.ToString(CultureInfo.InvariantCulture) + ".";
                    }
                    if (job.SourceIndex >= recipe.Sources.Count)
                    {
                        job.SourceIndex = 0;
                        job.Phase = DeliveryJobPhase.Processing;
                    }
                    break;

                case DeliveryJobPhase.Processing:
                    if (job.SourceIndex < recipe.Sources.Count)
                    {
                        DeliverySource source = recipe.Sources[job.SourceIndex++];
                        DeliveryModelResult result = job.Results[source.Id];
                        ProcessStagedModel(application, recipe, source, Path.Combine(recipe.StagingPath, source.TargetFileName), result);
                        job.CompletedUnits++;
                        job.Message = "Applied links, cleanup, and exports to model " + job.SourceIndex.ToString(CultureInfo.InvariantCulture) + " of " + recipe.Sources.Count.ToString(CultureInfo.InvariantCulture) + ".";
                    }
                    if (job.SourceIndex >= recipe.Sources.Count)
                    {
                        job.SourceIndex = 0;
                        job.Phase = DeliveryJobPhase.Validating;
                    }
                    break;

                case DeliveryJobPhase.Validating:
                    if (job.SourceIndex < recipe.Sources.Count)
                    {
                        DeliverySource source = recipe.Sources[job.SourceIndex++];
                        DeliveryModelResult result = job.Results[source.Id];
                        string stagedPath = Path.Combine(recipe.StagingPath, source.TargetFileName);
                        ValidateStagedModel(application, recipe, source, stagedPath, result, job.Token.ModelPlans.First(plan => string.Equals(plan.ModelId, source.Id, StringComparison.Ordinal)), recipe.StagingPath);
                        if (!result.Success)
                        {
                            foreach (string message in result.Errors)
                            {
                                job.Errors.Add(new DeliveryIssue("MODEL_QA_FAILED", message, source.Id, stagedPath));
                            }
                        }
                        job.CompletedUnits++;
                        job.Message = "Reopened and validated model " + job.SourceIndex.ToString(CultureInfo.InvariantCulture) + " of " + recipe.Sources.Count.ToString(CultureInfo.InvariantCulture) + ".";
                    }
                    if (job.SourceIndex >= recipe.Sources.Count)
                    {
                        job.SourceIndex = 0;
                        job.Phase = DeliveryJobPhase.VerifyingSources;
                    }
                    break;

                case DeliveryJobPhase.VerifyingSources:
                    VerifySourceFingerprints(job.Token, "after output validation");
                    job.CompletedUnits++;
                    if (job.Errors.Count > 0)
                    {
                        FinishFailed(job);
                    }
                    else
                    {
                        job.Phase = DeliveryJobPhase.Publishing;
                        job.Message = "All RVTs passed QA; publishing the package atomically.";
                    }
                    break;

                case DeliveryJobPhase.Publishing:
                    if (Directory.Exists(recipe.PackagePath))
                    {
                        throw new ModelDeliveryException("FINAL_PACKAGE_EXISTS", "Final package already exists and will not be overwritten: " + recipe.PackagePath);
                    }
                    WriteReports(recipe, job.Token, job.Results.Values, job.Errors, recipe.StagingPath, published: true);
                    Directory.Move(recipe.StagingPath, recipe.PackagePath);
                    job.MovedToFinal = true;
                    foreach (DeliveryModelResult result in job.Results.Values)
                    {
                        RebaseResultPaths(result, recipe.StagingPath, recipe.PackagePath);
                    }
                    WriteReports(recipe, job.Token, job.Results.Values, job.Errors, recipe.PackagePath, published: true);
                    job.CompletedUnits++;
                    job.SourceIndex = 0;
                    job.Phase = DeliveryJobPhase.FinalValidating;
                    job.Message = "Package moved atomically; reopening outputs from their final location.";
                    break;

                case DeliveryJobPhase.FinalValidating:
                    if (job.SourceIndex < recipe.Sources.Count)
                    {
                        DeliverySource source = recipe.Sources[job.SourceIndex++];
                        DeliveryModelResult result = job.Results[source.Id];
                        string finalPath = Path.Combine(recipe.PackagePath, source.TargetFileName);
                        string expectedHash = result.OutputSha256;
                        ValidateStagedModel(application, recipe, source, finalPath, result, job.Token.ModelPlans.First(plan => string.Equals(plan.ModelId, source.Id, StringComparison.Ordinal)), recipe.PackagePath);
                        if (!string.Equals(expectedHash, result.OutputSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Errors.Add("Output RVT hash changed during atomic publication.");
                            result.Success = false;
                        }
                        if (!result.Success)
                        {
                            foreach (string message in result.Errors)
                            {
                                job.Errors.Add(new DeliveryIssue("FINAL_PACKAGE_QA_FAILED", message, source.Id, finalPath));
                            }
                        }
                        job.CompletedUnits++;
                        job.Message = "Reopened and validated final model " + job.SourceIndex.ToString(CultureInfo.InvariantCulture) + " of " + recipe.Sources.Count.ToString(CultureInfo.InvariantCulture) + ".";
                    }
                    if (job.SourceIndex >= recipe.Sources.Count)
                    {
                        VerifySourceFingerprints(job.Token, "after final package validation");
                        if (job.Errors.Count > 0)
                        {
                            FinishFailed(job);
                        }
                        else
                        {
                            WriteReports(recipe, job.Token, job.Results.Values, job.Errors, recipe.PackagePath, published: true);
                            job.CompletedUnits = job.TotalUnits;
                            job.Phase = DeliveryJobPhase.Succeeded;
                            job.Published = true;
                            job.Message = "Delivery package published after final-location validation passed.";
                            job.CompletedAt = DateTimeOffset.UtcNow;
                        }
                    }
                    break;
            }
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        private void VerifyTargetAvailable(
            Autodesk.Revit.ApplicationServices.Application application,
            DeliveryTargetBinding targetBinding)
        {
            if (targetBinding == null)
            {
                throw new ModelDeliveryException(
                    "DELIVERY_TARGET_REQUIRED",
                    "Model Delivery requires an exact session target. Call revit.list_documents, then revit.set_target with instanceId and documentFingerprint.");
            }
            if (!string.Equals(targetBinding.InstanceId, _runtimeInstanceId, StringComparison.Ordinal))
            {
                throw new ModelDeliveryException(
                    "DELIVERY_TARGET_INSTANCE_CHANGED",
                    "The delivery target belongs to instance " + targetBinding.InstanceId +
                    " but this is instance " + _runtimeInstanceId +
                    ". Call revit.list_instances and select the intended instance again.");
            }

            Document document = application.Documents
                .Cast<Document>()
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    candidate.IsValidObject &&
                    !candidate.IsLinked &&
                    string.Equals(
                        DocumentGenerationTracker.ComputeDocumentFingerprint(candidate),
                        targetBinding.DocumentFingerprint,
                        StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                throw new ModelDeliveryException(
                    "DELIVERY_TARGET_UNAVAILABLE",
                    "The delivery target " + targetBinding.Describe() +
                    " is closed, changed, or unavailable. Call revit.list_documents and revit.set_target before previewing again.");
            }

            var current = new DeliveryTargetBinding(
                _runtimeInstanceId,
                DocumentGenerationTracker.ComputeDocumentFingerprint(document),
                _generationProvider(document),
                document.PathName,
                GetCentralModelPath(document));
            if (!targetBinding.Equals(current))
            {
                throw new ModelDeliveryException(
                    "DELIVERY_TARGET_CHANGED",
                    "The delivery target changed from " + targetBinding.Describe() +
                    " to " + current.Describe() +
                    ". Call revit.list_documents, select the exact target again, and create a new preview.");
            }
        }

        private static void VerifySourceFingerprints(DeliveryPreviewToken token, string checkpoint)
        {
            foreach (DeliverySource source in token.Recipe.Sources)
            {
                SourceFingerprint current = SourceFingerprint.Read(source.SourcePath);
                if (!token.SourceFingerprints.TryGetValue(source.Id, out SourceFingerprint expected) || !expected.Equals(current))
                {
                    throw new ModelDeliveryException("DELIVERY_SOURCE_CHANGED", "Source model changed " + checkpoint + ": " + source.SourcePath + ". Nothing was published.");
                }
            }
        }

        private static void FinishCancelled(DeliveryJob job)
        {
            RollBackFinalPackageBestEffort(job);
            job.Errors.Add(new DeliveryIssue("DELIVERY_CANCELLED", job.CancellationReason ?? "Delivery was cancelled."));
            job.Phase = DeliveryJobPhase.Cancelled;
            job.Published = false;
            job.Message = "Delivery cancelled at a safe checkpoint; no final package was published.";
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = job.CompletedAt.Value;
            WriteFailureReportsBestEffort(job);
        }

        private static void FinishFailed(DeliveryJob job)
        {
            RollBackFinalPackageBestEffort(job);
            job.Phase = DeliveryJobPhase.Failed;
            job.Published = false;
            job.Message = "Delivery failed validation or execution; no final package was published.";
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = job.CompletedAt.Value;
            WriteFailureReportsBestEffort(job);
        }

        private static void RollBackFinalPackageBestEffort(DeliveryJob job)
        {
            if (!job.MovedToFinal || !Directory.Exists(job.Token.Recipe.PackagePath)) return;
            try
            {
                if (Directory.Exists(job.Token.Recipe.StagingPath))
                {
                    throw new ModelDeliveryException("FINAL_PACKAGE_ROLLBACK_BLOCKED", "Final package validation failed, but the owned staging path already exists: " + job.Token.Recipe.StagingPath);
                }
                Directory.Move(job.Token.Recipe.PackagePath, job.Token.Recipe.StagingPath);
                job.MovedToFinal = false;
                foreach (DeliveryModelResult result in job.Results.Values)
                {
                    RebaseResultPaths(result, job.Token.Recipe.PackagePath, job.Token.Recipe.StagingPath);
                }
            }
            catch (Exception ex)
            {
                job.Errors.Add(new DeliveryIssue("FINAL_PACKAGE_ROLLBACK_FAILED", "The unpublished package could not be returned to staging: " + ex.Message, path: job.Token.Recipe.PackagePath));
            }
        }

        private static void WriteFailureReportsBestEffort(DeliveryJob job)
        {
            if (!job.StagingPrepared || !Directory.Exists(job.Token.Recipe.StagingPath)) return;
            try
            {
                WriteReports(job.Token.Recipe, job.Token, job.Results.Values, job.Errors, job.Token.Recipe.StagingPath, published: false);
            }
            catch
            {
                // Preserve the primary delivery failure and owned staging for safe retry.
            }
        }

        private Dictionary<string, object> ExecuteApproved(Autodesk.Revit.ApplicationServices.Application application, DeliveryPreviewToken token)
        {
            DeliveryRecipe recipe = token.Recipe;
            var results = recipe.Sources.ToDictionary(
                source => source.Id,
                source => new DeliveryModelResult(source.Id, source.SourcePath, Path.Combine(recipe.PackagePath, source.TargetFileName)),
                StringComparer.Ordinal);
            var errors = new List<DeliveryIssue>();
            bool stagingPrepared = false;

            try
            {
                if (GetOpenDocumentPaths(application).Overlaps(recipe.Sources.Select(source => source.SourcePath)))
                {
                    throw new ModelDeliveryException("SOURCE_DOCUMENT_OPEN", "All production source documents must be closed before delivery execution.");
                }

                PrepareStaging(recipe);
                stagingPrepared = true;
                WriteOwnershipMarker(recipe);

                foreach (DeliverySource source in recipe.Sources)
                {
                    string stagedPath = Path.Combine(recipe.StagingPath, source.TargetFileName);
                    CreateStandaloneCopy(application, source, stagedPath);
                }

                foreach (DeliverySource source in recipe.Sources)
                {
                    DeliveryModelResult result = results[source.Id];
                    string stagedPath = Path.Combine(recipe.StagingPath, source.TargetFileName);
                    ProcessStagedModel(application, recipe, source, stagedPath, result);
                }

                foreach (DeliverySource source in recipe.Sources)
                {
                    DeliveryModelResult result = results[source.Id];
                    string stagedPath = Path.Combine(recipe.StagingPath, source.TargetFileName);
                    ValidateStagedModel(application, recipe, source, stagedPath, result, token.ModelPlans.First(plan => string.Equals(plan.ModelId, source.Id, StringComparison.Ordinal)), recipe.StagingPath);
                    if (!result.Success)
                    {
                        foreach (string message in result.Errors)
                        {
                            errors.Add(new DeliveryIssue("MODEL_QA_FAILED", message, source.Id, stagedPath));
                        }
                    }
                }

                if (errors.Count > 0)
                {
                    WriteReports(recipe, token, results.Values, errors, recipe.StagingPath, published: false);
                    return BuildExecutionResult(token, results.Values, errors, published: false, manifestPath: Path.Combine(recipe.StagingPath, "manifest.json"), auditPath: Path.Combine(recipe.StagingPath, "audit.json"));
                }

                WriteReports(recipe, token, results.Values, errors, recipe.StagingPath, published: true);
                if (Directory.Exists(recipe.PackagePath))
                {
                    throw new ModelDeliveryException("FINAL_PACKAGE_EXISTS", "Final package already exists and will not be overwritten: " + recipe.PackagePath);
                }

                Directory.Move(recipe.StagingPath, recipe.PackagePath);
                foreach (DeliveryModelResult result in results.Values)
                {
                    RebaseResultPaths(result, recipe.StagingPath, recipe.PackagePath);
                }
                WriteReports(recipe, token, results.Values, errors, recipe.PackagePath, published: true);

                return BuildExecutionResult(
                    token,
                    results.Values,
                    errors,
                    published: true,
                    manifestPath: Path.Combine(recipe.PackagePath, "manifest.json"),
                    auditPath: Path.Combine(recipe.PackagePath, "audit.json"));
            }
            catch (Exception ex)
            {
                string code = ex is ModelDeliveryException deliveryException ? deliveryException.Code : "DELIVERY_EXECUTION_FAILED";
                errors.Add(new DeliveryIssue(code, ex.Message));
                if (stagingPrepared)
                {
                    try
                    {
                        WriteReports(recipe, token, results.Values, errors, recipe.StagingPath, published: false);
                    }
                    catch
                    {
                        // The primary failure remains authoritative.
                    }
                }

                return BuildExecutionResult(
                    token,
                    results.Values,
                    errors,
                    published: false,
                    manifestPath: stagingPrepared ? Path.Combine(recipe.StagingPath, "manifest.json") : null,
                    auditPath: stagingPrepared ? Path.Combine(recipe.StagingPath, "audit.json") : null);
            }
        }

        private static void CreateStandaloneCopy(Autodesk.Revit.ApplicationServices.Application application, DeliverySource source, string stagedPath)
        {
            if (File.Exists(stagedPath)) throw new ModelDeliveryException("STAGING_OUTPUT_EXISTS", "Staging output already exists: " + stagedPath);
            string isolatedSourcePath = Path.Combine(Path.GetDirectoryName(stagedPath), ".source-" + Guid.NewGuid().ToString("N") + ".rvt");
            File.Copy(source.SourcePath, isolatedSourcePath, overwrite: false);
            try
            {
                UnloadLinksInIsolatedSource(isolatedSourcePath);
                BasicFileInfo basic = BasicFileInfo.Extract(isolatedSourcePath);
                Document document = OpenSource(application, isolatedSourcePath, basic.IsWorkshared);
                try
                {
                    if (document.IsWorkshared)
                    {
                        throw new ModelDeliveryException("DETACH_FAILED", "Source remained workshared after DetachAndDiscardWorksets: " + source.SourcePath);
                    }

                    var options = new SaveAsOptions
                    {
                        Compact = true,
                        OverwriteExistingFile = false,
                        MaximumBackups = 1
                    };
                    document.SaveAs(stagedPath, options);
                }
                finally
                {
                    document.Close(false);
                }
            }
            finally
            {
                if (File.Exists(isolatedSourcePath)) File.Delete(isolatedSourcePath);
            }
        }

        private static void UnloadLinksInIsolatedSource(string isolatedSourcePath)
        {
            ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(isolatedSourcePath);
            TransmissionData transmission = TransmissionData.ReadTransmissionData(modelPath);
            if (transmission == null) return;
            bool changed = false;
            foreach (ElementId referenceId in transmission.GetAllExternalFileReferenceIds())
            {
                ExternalFileReference reference = transmission.GetLastSavedReferenceData(referenceId);
                if (reference == null || reference.ExternalFileReferenceType != ExternalFileReferenceType.RevitLink) continue;
                transmission.SetDesiredReferenceData(referenceId, reference.GetPath(), reference.PathType, false);
                changed = true;
            }
            if (!changed) return;
            transmission.IsTransmitted = true;
            TransmissionData.WriteTransmissionData(modelPath, transmission);
        }

        private static void ProcessStagedModel(
            Autodesk.Revit.ApplicationServices.Application application,
            DeliveryRecipe recipe,
            DeliverySource source,
            string stagedPath,
            DeliveryModelResult result)
        {
            Document document = application.OpenDocumentFile(stagedPath);
            try
            {
                if (document.IsWorkshared)
                {
                    throw new ModelDeliveryException("STAGING_MODEL_WORKSHARED", "Staging RVT is unexpectedly workshared: " + stagedPath);
                }

                List<LinkActionPlan> linkActions = BuildLinkActions(document, recipe, source, new List<DeliveryIssue>(), new List<DeliveryIssue>());
                ApplyLinkActions(document, recipe, source, linkActions, result);
                ApplyCleanup(document, recipe.Cleanup, source, result);
                RunExports(document, recipe, source, stagedPath, result);
                document.Save(new SaveOptions { Compact = true });
            }
            finally
            {
                document.Close(false);
            }
        }

        private static void ValidateStagedModel(
            Autodesk.Revit.ApplicationServices.Application application,
            DeliveryRecipe recipe,
            DeliverySource source,
            string stagedPath,
            DeliveryModelResult result,
            DeliveryModelPlan expectedPlan,
            string validationRoot)
        {
            if (!File.Exists(stagedPath))
            {
                result.Errors.Add("Output RVT does not exist.");
                return;
            }

            Document document = application.OpenDocumentFile(stagedPath);
            try
            {
                result.IsWorkshared = document.IsWorkshared;
                if (result.IsWorkshared) result.Errors.Add("Output RVT is still workshared.");
                string centralPath = GetCentralModelPath(document);
                result.CentralModelPath = centralPath;
                if (!string.IsNullOrWhiteSpace(centralPath)) result.Errors.Add("Output RVT still has a central model path: " + centralPath);
                if (!string.Equals(Path.GetFileName(document.PathName), source.TargetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add("Output RVT name does not match the approved target name.");
                }

                var validationBlockers = new List<DeliveryIssue>();
                var validationWarnings = new List<DeliveryIssue>();
                List<LinkActionPlan> actions = BuildLinkActions(document, recipe, source, validationBlockers, validationWarnings, validationMode: true, targetRoot: validationRoot);
                result.LinksValidated = actions.Count;
                foreach (DeliveryIssue blocker in validationBlockers) result.Errors.Add(blocker.Message);
                string[] actualLinkTransforms = actions
                    .Where(action => string.Equals(action.Action, "repath", StringComparison.Ordinal))
                    .SelectMany(action => action.InstanceTransforms)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                string[] expectedLinkTransforms = result.LinkTransforms.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (!actualLinkTransforms.SequenceEqual(expectedLinkTransforms, StringComparer.Ordinal))
                {
                    result.Errors.Add("One or more packaged Revit link transforms differ from the processed model.");
                }
                CleanupPlan remainingCleanup = BuildCleanupPlan(document, recipe.Cleanup, source);
                if (remainingCleanup.SheetIds.Count > 0) result.Errors.Add("Cleanup validation found sheets that should have been deleted.");
                if (remainingCleanup.ViewIds.Count > 0) result.Errors.Add("Cleanup validation found views that should have been deleted.");
                if (remainingCleanup.TemplateIds.Count > 0) result.Errors.Add("Cleanup validation found view templates that should have been deleted.");
                if (remainingCleanup.FilterIds.Count > 0) result.Errors.Add("Cleanup validation found filters that should have been deleted.");
                if (result.DeletedSheets != expectedPlan.SheetDeleteCount) result.Errors.Add("Deleted sheet count does not match the approved preview.");
                if (result.DeletedViews != expectedPlan.ViewDeleteCount) result.Errors.Add("Deleted view count does not match the approved preview.");
                if (result.DeletedTemplates != expectedPlan.TemplateDeleteCount) result.Errors.Add("Deleted template count does not match the approved preview.");
                if (result.DeletedFilters != expectedPlan.FilterDeleteCount) result.Errors.Add("Deleted filter count does not match the approved preview.");
                foreach (DeliveryExport export in recipe.Exports.Where(export => export.AppliesTo(source.Id) && export.Required))
                {
                    string exportDirectory = ExportDirectory(validationRoot, export);
                    if (!ExpectedExportExists(exportDirectory, Path.GetFileNameWithoutExtension(source.TargetFileName), export.Format))
                    {
                        result.Errors.Add("Required " + export.Format.ToUpperInvariant() + " export was not found.");
                    }
                }
                result.WarningsCount = document.GetWarnings().Count;
                if (recipe.Qa.MaxWarnings.HasValue && result.WarningsCount > recipe.Qa.MaxWarnings.Value)
                {
                    result.Errors.Add("Delivered model has " + result.WarningsCount.ToString(CultureInfo.InvariantCulture) + " warning(s), exceeding the approved maximum of " + recipe.Qa.MaxWarnings.Value.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
            finally
            {
                document.Close(false);
            }

            result.OutputSha256 = ComputeFileSha256(stagedPath);
            result.Success = result.Errors.Count == 0;
        }

        private static DeliveryModelPlan InspectSource(
            Autodesk.Revit.ApplicationServices.Application application,
            DeliveryRecipe recipe,
            DeliverySource source,
            List<DeliveryIssue> blockers,
            List<DeliveryIssue> warnings)
        {
            BasicFileInfo basic = BasicFileInfo.Extract(source.SourcePath);
            string isolatedInspectionPath = CreateIsolatedSourceCopy(source.SourcePath, "preview");
            try
            {
                Document document = OpenSource(application, isolatedInspectionPath, basic.IsWorkshared);
                try
                {
                    var protectedNames = ProtectedViewNames(recipe.Cleanup, source);
                    Dictionary<string, View> viewsByName = new FilteredElementCollector(document)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(view => !string.IsNullOrWhiteSpace(view.Name))
                        .GroupBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                    if (recipe.Cleanup.DeleteViews && string.IsNullOrWhiteSpace(source.StartViewName))
                    {
                        blockers.Add(new DeliveryIssue("START_VIEW_REQUIRED", "deleteViews requires an exact startViewName.", source.Id));
                    }
                    foreach (string protectedName in protectedNames)
                    {
                        if (!viewsByName.ContainsKey(protectedName))
                        {
                            blockers.Add(new DeliveryIssue("PROTECTED_VIEW_NOT_FOUND", "Protected view was not found: " + protectedName + ".", source.Id));
                        }
                    }

                    List<LinkActionPlan> linkActions = BuildLinkActions(document, recipe, source, blockers, warnings);
                    CleanupPlan cleanup = BuildCleanupPlan(document, recipe.Cleanup, source);
                    ValidateExports(document, recipe, source, blockers, warnings);
                    SourceFingerprint fingerprint = SourceFingerprint.Read(source.SourcePath);
                    int warningsCount = document.GetWarnings().Count;
                    if (recipe.Qa.MaxWarnings.HasValue && warningsCount > recipe.Qa.MaxWarnings.Value)
                    {
                        blockers.Add(new DeliveryIssue(
                            "WARNING_LIMIT_EXCEEDED",
                            "Source has " + warningsCount.ToString(CultureInfo.InvariantCulture) + " warning(s), exceeding the recipe maximum of " + recipe.Qa.MaxWarnings.Value.ToString(CultureInfo.InvariantCulture) + ".",
                            source.Id,
                            source.SourcePath));
                    }
                    return new DeliveryModelPlan
                    {
                        ModelId = source.Id,
                        Role = source.Role,
                        SourcePath = source.SourcePath,
                        OutputPath = Path.Combine(recipe.PackagePath, source.TargetFileName),
                        SourceBytes = fingerprint.Length,
                        SourceLastWriteUtc = new DateTimeOffset(fingerprint.LastWriteUtcTicks, TimeSpan.Zero).ToString("o"),
                        SourceSha256 = fingerprint.Sha256,
                        SourceIsWorkshared = basic.IsWorkshared,
                        LinkCount = linkActions.Sum(action => action.InstanceCount),
                        SheetDeleteCount = cleanup.SheetIds.Count,
                        ViewDeleteCount = cleanup.ViewIds.Count,
                        TemplateDeleteCount = cleanup.TemplateIds.Count,
                        FilterDeleteCount = cleanup.FilterIds.Count,
                        WarningsCount = warningsCount,
                        MatchedRuleIds = new HashSet<string>(linkActions.Where(action => !string.IsNullOrWhiteSpace(action.RuleId)).Select(action => action.RuleId), StringComparer.Ordinal)
                    };
                }
                finally
                {
                    document.Close(false);
                }
            }
            finally
            {
                DeleteTemporarySourceCopy(isolatedInspectionPath);
            }
        }

        private static string CreateIsolatedSourceCopy(string sourcePath, string purpose)
        {
            string directory = Path.Combine(Path.GetTempPath(), "RevitMcpNext", "model-delivery", purpose);
            Directory.CreateDirectory(directory);
            string isolatedPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".rvt");
            File.Copy(sourcePath, isolatedPath, overwrite: false);
            try
            {
                UnloadLinksInIsolatedSource(isolatedPath);
                return isolatedPath;
            }
            catch
            {
                DeleteTemporarySourceCopy(isolatedPath);
                throw;
            }
        }

        private static void DeleteTemporarySourceCopy(string isolatedPath)
        {
            if (string.IsNullOrWhiteSpace(isolatedPath)) return;
            try
            {
                if (File.Exists(isolatedPath)) File.Delete(isolatedPath);
                string backup = Path.ChangeExtension(isolatedPath, ".0001.rvt");
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch
            {
                // A unique temporary path cannot be mistaken for a published output; cleanup can be retried later.
            }
        }

        private static Document OpenSource(Autodesk.Revit.ApplicationServices.Application application, string sourcePath, bool workshared)
        {
            var options = new OpenOptions();
            if (workshared)
            {
                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;
            }
            return application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(sourcePath), options);
        }

        private static List<LinkActionPlan> BuildLinkActions(
            Document document,
            DeliveryRecipe recipe,
            DeliverySource source,
            List<DeliveryIssue> blockers,
            List<DeliveryIssue> warnings,
            bool validationMode = false,
            string targetRoot = null)
        {
            var actions = new List<LinkActionPlan>();
            var instancesByType = new FilteredElementCollector(document)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .GroupBy(instance => IdValue(instance.GetTypeId()))
                .ToDictionary(group => group.Key, group => group.ToList());
            var matchedRules = new HashSet<string>(StringComparer.Ordinal);
            foreach (RevitLinkType linkType in new FilteredElementCollector(document).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                string typeId = IdValue(linkType.Id);
                string existingPath = GetLinkPath(document, linkType);
                string existingFileName = Path.GetFileName(existingPath ?? string.Empty);
                List<RevitLinkInstance> instances = instancesByType.TryGetValue(typeId, out List<RevitLinkInstance> found)
                    ? found
                    : new List<RevitLinkInstance>();
                List<DeliveryLinkRule> explicitRules = recipe.LinkRules
                    .Where(rule => string.Equals(rule.SourceModelId, source.Id, StringComparison.Ordinal) &&
                                   (rule.Matches(existingPath, existingFileName) ||
                                    (validationMode && string.Equals(rule.Action, "repath", StringComparison.Ordinal) && RuleMatchesPackagedTarget(recipe, rule, existingPath, existingFileName))))
                    .ToList();
                if (explicitRules.Count > 1)
                {
                    blockers.Add(new DeliveryIssue("AMBIGUOUS_LINK_RULE", "Multiple link rules match " + existingFileName + ".", source.Id, existingPath));
                    continue;
                }

                DeliveryLinkRule rule = explicitRules.SingleOrDefault();
                DeliverySource automaticTarget = FindAutomaticLinkTarget(recipe, existingPath, existingFileName);
                string action;
                DeliverySource target = null;
                string ruleId = null;
                int? expectedCount = null;
                if (rule != null)
                {
                    matchedRules.Add(rule.Id);
                    action = rule.Action;
                    ruleId = rule.Id;
                    expectedCount = rule.ExpectedInstanceCount;
                    if (string.Equals(action, "repath", StringComparison.Ordinal))
                    {
                        target = recipe.Sources.SingleOrDefault(candidate => string.Equals(candidate.Id, rule.TargetModelId, StringComparison.Ordinal));
                        if (target == null)
                        {
                            blockers.Add(new DeliveryIssue("LINK_TARGET_NOT_FOUND", "Repath target model was not found: " + rule.TargetModelId + ".", source.Id));
                            continue;
                        }
                    }
                }
                else if (automaticTarget != null)
                {
                    action = "repath";
                    target = automaticTarget;
                }
                else if (recipe.Cleanup.RemoveUnmappedLinks)
                {
                    action = "remove";
                }
                else
                {
                    blockers.Add(new DeliveryIssue("UNMAPPED_LINK", "Link has no explicit rule and is not another packaged source: " + existingFileName + ".", source.Id, existingPath));
                    continue;
                }

                if (expectedCount.HasValue && expectedCount.Value != instances.Count)
                {
                    blockers.Add(new DeliveryIssue(
                        "LINK_INSTANCE_COUNT_MISMATCH",
                        "Expected " + expectedCount.Value.ToString(CultureInfo.InvariantCulture) + " instance(s) for " + existingFileName + " but found " + instances.Count.ToString(CultureInfo.InvariantCulture) + ".",
                        source.Id,
                        existingPath));
                }

                string targetPath = target == null ? null : Path.Combine(targetRoot ?? recipe.StagingPath, target.TargetFileName);
                if (validationMode && string.Equals(action, "repath", StringComparison.Ordinal) && !PathsEqual(existingPath, targetPath))
                {
                    blockers.Add(new DeliveryIssue("LINK_PATH_INVALID", "Delivered link does not point to its packaged target: " + existingFileName + ".", source.Id, existingPath));
                }
                if (validationMode && string.Equals(action, "repath", StringComparison.Ordinal))
                {
                    try
                    {
                        if (linkType.PathType != PathType.Relative)
                        {
                            blockers.Add(new DeliveryIssue("LINK_PATH_NOT_RELATIVE", "Packaged links must use relative paths so they remain valid after atomic publication: " + existingFileName + ".", source.Id, existingPath));
                        }
                    }
                    catch (Exception ex)
                    {
                        blockers.Add(new DeliveryIssue("LINK_PATH_TYPE_UNREADABLE", "Could not verify the packaged link path type: " + ex.Message, source.Id, existingPath));
                    }
                }
                if (validationMode && string.Equals(action, "remove", StringComparison.Ordinal))
                {
                    blockers.Add(new DeliveryIssue("REMOVED_LINK_REMAINS", "A link marked for removal remains in the delivered model: " + existingFileName + ".", source.Id, existingPath));
                }

                actions.Add(new LinkActionPlan
                {
                    LinkTypeId = linkType.Id,
                    ExistingPath = existingPath,
                    ExistingFileName = existingFileName,
                    Action = action,
                    TargetPath = targetPath,
                    RuleId = ruleId,
                    InstanceCount = instances.Count,
                    InstanceTransforms = instances.Select(TransformSignature).ToArray()
                });
            }

            foreach (DeliveryLinkRule requiredRule in recipe.LinkRules.Where(rule => rule.Required && string.Equals(rule.SourceModelId, source.Id, StringComparison.Ordinal)))
            {
                if (!matchedRules.Contains(requiredRule.Id))
                {
                    blockers.Add(new DeliveryIssue("REQUIRED_LINK_RULE_UNMATCHED", "Required link rule did not match: " + requiredRule.Id + ".", source.Id));
                }
            }
            return actions;
        }

        private static void ApplyLinkActions(Document document, DeliveryRecipe recipe, DeliverySource source, List<LinkActionPlan> actions, DeliveryModelResult result)
        {
            foreach (LinkActionPlan action in actions.Where(item => string.Equals(item.Action, "repath", StringComparison.Ordinal)))
            {
                if (!File.Exists(action.TargetPath)) throw new ModelDeliveryException("LINK_TARGET_OUTPUT_MISSING", "Packaged link target is missing: " + action.TargetPath);
                RevitLinkType type = document.GetElement(action.LinkTypeId) as RevitLinkType;
                if (type == null) throw new ModelDeliveryException("LINK_TYPE_MISSING", "Revit link type disappeared before repath.");
                using (var worksets = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets))
                {
                    type.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(action.TargetPath), worksets);
                }
                RunTransaction(document, "Store packaged Revit link as relative", () => type.PathType = PathType.Relative);
                VerifyTransforms(document, action);
            }

            result.LinkTransforms.Clear();
            result.LinkTransforms.AddRange(actions
                .Where(item => string.Equals(item.Action, "repath", StringComparison.Ordinal))
                .SelectMany(item => item.InstanceTransforms)
                .OrderBy(value => value, StringComparer.Ordinal));

            foreach (LinkActionPlan action in actions.Where(item => string.Equals(item.Action, "unload", StringComparison.Ordinal)))
            {
                RevitLinkType type = document.GetElement(action.LinkTypeId) as RevitLinkType;
                type?.Unload(null);
            }

            List<ElementId> removals = actions
                .Where(item => string.Equals(item.Action, "remove", StringComparison.Ordinal))
                .Select(item => item.LinkTypeId)
                .Where(id => id != null)
                .ToList();
            if (removals.Count > 0)
            {
                RunTransaction(document, "Remove non-delivery Revit links", () => document.Delete(removals));
            }
        }

        private static void VerifyTransforms(Document document, LinkActionPlan action)
        {
            string typeId = IdValue(action.LinkTypeId);
            string[] current = new FilteredElementCollector(document)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(instance => string.Equals(IdValue(instance.GetTypeId()), typeId, StringComparison.Ordinal))
                .Select(TransformSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            string[] expected = action.InstanceTransforms.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!current.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new ModelDeliveryException("LINK_TRANSFORM_CHANGED", "Repath changed one or more link instance transforms for " + action.ExistingFileName + ".");
            }
        }

        private static void ApplyCleanup(Document document, DeliveryCleanup cleanup, DeliverySource source, DeliveryModelResult result)
        {
            CleanupPlan plan = BuildCleanupPlan(document, cleanup, source);
            HashSet<string> protectedNames = ProtectedViewNames(cleanup, source);
            RunTransaction(document, "Prepare protected delivery views", () =>
            {
                foreach (View view in new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Where(view => protectedNames.Contains(view.Name)))
                {
                    if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
                }
                if (!string.IsNullOrWhiteSpace(source.StartViewName))
                {
                    View startView = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
                        .FirstOrDefault(view => string.Equals(view.Name, source.StartViewName, StringComparison.OrdinalIgnoreCase));
                    if (startView == null) throw new ModelDeliveryException("START_VIEW_NOT_FOUND", "Start view was not found: " + source.StartViewName + ".");
                    StartingViewSettings.GetStartingViewSettings(document).ViewId = startView.Id;
                }
            });

            result.DeletedSheets = DeleteElements(document, "Delete delivery sheets", plan.SheetIds);
            result.DeletedViews = DeleteElements(document, "Delete delivery views", plan.ViewIds);
            result.DeletedTemplates = DeleteElements(document, "Delete delivery view templates", plan.TemplateIds);
            result.DeletedFilters = DeleteElements(document, "Delete unused delivery filters", plan.FilterIds);
            result.PurgedElements = PurgeUnused(document, cleanup.PurgeUnusedPasses);
        }

        private static int PurgeUnused(Document document, int passes)
        {
            int deleted = 0;
            for (int pass = 0; pass < passes; pass++)
            {
                ICollection<ElementId> ids = GetUnusedElements(document);
                if (ids == null || ids.Count == 0) break;
                int passDeleted = 0;
                RunTransaction(document, "Purge unused delivery content " + (pass + 1).ToString(CultureInfo.InvariantCulture), () =>
                {
                    passDeleted = document.Delete(ids).Count;
                });
                deleted += passDeleted;
                if (passDeleted == 0) break;
            }
            return deleted;
        }

        private static ICollection<ElementId> GetUnusedElements(Document document)
        {
#if REVIT2021
            // Document.GetUnusedElements was added after Revit 2021. The
            // built-in Purge Unused performance-adviser rule exposes the same
            // candidate set without depending on a localized rule name.
            var ruleId = new PerformanceAdviserRuleId(new Guid("e8c63650-70b7-435a-9010-ec97660c1bda"));
            IList<FailureMessage> messages = PerformanceAdviser.GetPerformanceAdviser().ExecuteRules(
                document,
                new List<PerformanceAdviserRuleId> { ruleId });
            return (messages ?? new List<FailureMessage>())
                .SelectMany(message => message.GetFailingElements() ?? new List<ElementId>())
                .Where(id => id != null && id != ElementId.InvalidElementId)
                .GroupBy(IdValue, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
#else
            return document.GetUnusedElements(new HashSet<ElementId>());
#endif
        }

        private static CleanupPlan BuildCleanupPlan(Document document, DeliveryCleanup cleanup, DeliverySource source)
        {
            HashSet<string> protectedNames = ProtectedViewNames(cleanup, source);
            var plan = new CleanupPlan();
            foreach (View view in new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>())
            {
                if (protectedNames.Contains(view.Name)) continue;
                if (view is ViewSheet)
                {
                    if (cleanup.DeleteSheets) plan.SheetIds.Add(view.Id);
                    continue;
                }
                if (view.IsTemplate)
                {
                    if (cleanup.DeleteViewTemplates) plan.TemplateIds.Add(view.Id);
                    continue;
                }
                if (IsSystemView(view)) continue;
                if (view is ViewSchedule)
                {
                    if (cleanup.DeleteSchedules) plan.ViewIds.Add(view.Id);
                    continue;
                }
                if (view.ViewType == ViewType.Legend)
                {
                    if (cleanup.DeleteLegends) plan.ViewIds.Add(view.Id);
                    continue;
                }
                if (view.ViewType == ViewType.DraftingView)
                {
                    if (cleanup.DeleteDraftingViews) plan.ViewIds.Add(view.Id);
                    continue;
                }
                if (cleanup.DeleteViews) plan.ViewIds.Add(view.Id);
            }

            if (cleanup.DeleteUnusedFilters)
            {
                var retainedFilterIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (View retainedView in new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
                    .Where(view => protectedNames.Contains(view.Name) || IsSystemView(view)))
                {
                    try
                    {
                        foreach (ElementId filterId in retainedView.GetFilters()) retainedFilterIds.Add(IdValue(filterId));
                    }
                    catch
                    {
                        // Some internal views do not expose filters.
                    }
                }
                IEnumerable<Element> filters = new FilteredElementCollector(document)
                    .WherePasses(new LogicalOrFilter(
                        new ElementClassFilter(typeof(ParameterFilterElement)),
                        new ElementClassFilter(typeof(SelectionFilterElement))))
                    .WhereElementIsNotElementType();
                foreach (Element filter in filters)
                {
                    if (!retainedFilterIds.Contains(IdValue(filter.Id))) plan.FilterIds.Add(filter.Id);
                }
            }
            return plan;
        }

        private static void ValidateExports(Document document, DeliveryRecipe recipe, DeliverySource source, List<DeliveryIssue> blockers, List<DeliveryIssue> warnings)
        {
            HashSet<string> viewNames = new HashSet<string>(
                new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Select(view => view.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (DeliveryExport export in recipe.Exports.Where(item => item.AppliesTo(source.Id)))
            {
                foreach (string viewName in export.ViewNames)
                {
                    if (!viewNames.Contains(viewName))
                    {
                        (export.Required ? blockers : warnings).Add(new DeliveryIssue("EXPORT_VIEW_NOT_FOUND", export.Format.ToUpperInvariant() + " export view was not found: " + viewName + ".", source.Id));
                    }
                }
                if (string.Equals(export.Format, "dwg", StringComparison.Ordinal) && export.ViewNames.Count == 0)
                {
                    (export.Required ? blockers : warnings).Add(new DeliveryIssue("DWG_VIEW_SCOPE_REQUIRED", "DWG export requires explicit viewNames.", source.Id));
                }
                if (string.Equals(export.Format, "nwc", StringComparison.Ordinal) && !OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
                {
                    (export.Required ? blockers : warnings).Add(new DeliveryIssue("NWC_EXPORTER_UNAVAILABLE", "Navisworks exporter is not installed in this Revit host.", source.Id));
                }
            }
        }

        private static void RunExports(Document document, DeliveryRecipe recipe, DeliverySource source, string stagedPath, DeliveryModelResult result)
        {
            string baseName = Path.GetFileNameWithoutExtension(source.TargetFileName);
            foreach (DeliveryExport export in recipe.Exports.Where(item => item.AppliesTo(source.Id)))
            {
                string directory = ExportDirectory(recipe.StagingPath, export);
                Directory.CreateDirectory(directory);
                try
                {
                    if (string.Equals(export.Format, "ifc", StringComparison.Ordinal))
                    {
                        var options = new IFCExportOptions();
                        if (!string.IsNullOrWhiteSpace(export.SetupName)) options.AddOption("ConfigName", export.SetupName);
                        if (!document.Export(directory, baseName, options)) throw new InvalidOperationException("Revit returned false for IFC export.");
                    }
                    else if (string.Equals(export.Format, "dwg", StringComparison.Ordinal))
                    {
                        List<ElementId> viewIds = ResolveViews(document, export.ViewNames);
                        DWGExportOptions options = string.IsNullOrWhiteSpace(export.SetupName)
                            ? new DWGExportOptions()
                            : DWGExportOptions.GetPredefinedOptions(document, export.SetupName);
                        if (options == null) throw new InvalidOperationException("DWG export setup was not found: " + export.SetupName + ".");
                        if (!document.Export(directory, baseName, viewIds, options)) throw new InvalidOperationException("Revit returned false for DWG export.");
                    }
                    else if (string.Equals(export.Format, "nwc", StringComparison.Ordinal))
                    {
                        if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable()) throw new InvalidOperationException("Navisworks exporter is not available.");
                        var options = new NavisworksExportOptions();
                        if (export.ViewNames.Count == 1)
                        {
                            options.ExportScope = NavisworksExportScope.View;
                            options.ViewId = ResolveViews(document, export.ViewNames).Single();
                        }
                        document.Export(directory, baseName, options);
                    }
                    string[] exportedFiles = FindExpectedExportFiles(directory, baseName, export.Format);
                    if (exportedFiles.Length > 0)
                    {
                        result.Exports.AddRange(exportedFiles.Where(path => !result.Exports.Contains(path, StringComparer.OrdinalIgnoreCase)));
                    }
                    else if (export.Required)
                    {
                        result.Errors.Add("Required " + export.Format.ToUpperInvariant() + " export was not created.");
                    }
                }
                catch (Exception ex)
                {
                    if (export.Required) result.Errors.Add(export.Format.ToUpperInvariant() + " export failed: " + ex.Message);
                }
            }
        }

        private static List<ElementId> ResolveViews(Document document, IReadOnlyList<string> names)
        {
            var views = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().ToList();
            var ids = new List<ElementId>();
            foreach (string name in names)
            {
                View view = views.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (view == null) throw new InvalidOperationException("Export view was not found: " + name + ".");
                ids.Add(view.Id);
            }
            return ids;
        }

        private static bool ExpectedExportExists(string directory, string baseName, string format)
        {
            return FindExpectedExportFiles(directory, baseName, format).Length > 0;
        }

        private static string[] FindExpectedExportFiles(string directory, string baseName, string format)
        {
            if (!Directory.Exists(directory)) return Array.Empty<string>();
            string extension = "." + format.ToLowerInvariant();
            return Directory.EnumerateFiles(directory)
                .Where(path => Path.GetFileName(path).StartsWith(baseName, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void RebaseResultPaths(DeliveryModelResult result, string oldRoot, string newRoot)
        {
            result.OutputPath = Path.Combine(Path.GetFullPath(newRoot), Path.GetFileName(result.OutputPath));
            for (int index = 0; index < result.Exports.Count; index++)
            {
                result.Exports[index] = RebaseOwnedPath(result.Exports[index], oldRoot, newRoot);
            }
        }

        private static string RebaseOwnedPath(string value, string oldRoot, string newRoot)
        {
            string normalizedRoot = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedValue = Path.GetFullPath(value);
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            if (!normalizedValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelDeliveryException("DELIVERY_PATH_REBASE_FAILED", "Owned output path is outside the delivery root: " + value);
            }
            return Path.Combine(Path.GetFullPath(newRoot), normalizedValue.Substring(prefix.Length));
        }

        private static string ExportDirectory(string stagingPath, DeliveryExport export)
        {
            return string.IsNullOrWhiteSpace(export.OutputSubdirectory)
                ? stagingPath
                : Path.Combine(stagingPath, export.OutputSubdirectory);
        }

        private static int DeleteElements(Document document, string transactionName, List<ElementId> ids)
        {
            if (ids.Count == 0) return 0;
            RunTransaction(document, transactionName, () => document.Delete(ids));
            return ids.Count;
        }

        private static void RunTransaction(Document document, string name, Action action)
        {
            using (var transaction = new Transaction(document, name))
            {
                if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start Revit transaction: " + name + ".");
                try
                {
                    action();
                    if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Could not commit Revit transaction: " + name + ".");
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                    throw;
                }
            }
        }

        private static void PrepareStaging(DeliveryRecipe recipe)
        {
            Directory.CreateDirectory(recipe.DestinationRoot);
            string stagingRoot = Path.Combine(recipe.DestinationRoot, StagingDirectoryName);
            Directory.CreateDirectory(stagingRoot);
            if (!IsInside(stagingRoot, recipe.StagingPath)) throw new ModelDeliveryException("UNSAFE_STAGING_PATH", "Resolved staging path is outside the delivery staging root.");
            if (Directory.Exists(recipe.StagingPath))
            {
                bool empty = !Directory.EnumerateFileSystemEntries(recipe.StagingPath).Any();
                if (!empty && !IsOwnedStaging(recipe))
                {
                    throw new ModelDeliveryException("STAGING_NOT_OWNED", "Existing staging content is not owned by this project/delivery and will not be removed: " + recipe.StagingPath);
                }
                Directory.Delete(recipe.StagingPath, true);
            }
            Directory.CreateDirectory(recipe.StagingPath);
        }

        private static void WriteOwnershipMarker(DeliveryRecipe recipe)
        {
            var marker = new Dictionary<string, object>
            {
                ["projectId"] = recipe.ProjectId,
                ["deliveryId"] = recipe.DeliveryId,
                ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("o")
            };
            File.WriteAllText(Path.Combine(recipe.StagingPath, OwnershipFileName), JsonWireCodec.Serialize(marker), new UTF8Encoding(false));
        }

        private static bool IsOwnedStaging(DeliveryRecipe recipe)
        {
            string markerPath = Path.Combine(recipe.StagingPath, OwnershipFileName);
            if (!File.Exists(markerPath)) return false;
            try
            {
                var marker = JsonWireCodec.DeserializeObject(File.ReadAllText(markerPath)) as Dictionary<string, object>;
                return string.Equals(GetString(marker, "projectId"), recipe.ProjectId, StringComparison.Ordinal) &&
                       string.Equals(GetString(marker, "deliveryId"), recipe.DeliveryId, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteReports(
            DeliveryRecipe recipe,
            DeliveryPreviewToken token,
            IEnumerable<DeliveryModelResult> modelResults,
            IEnumerable<DeliveryIssue> errors,
            string directory,
            bool published)
        {
            Directory.CreateDirectory(directory);
            var manifest = new Dictionary<string, object>
            {
                ["projectId"] = recipe.ProjectId,
                ["recipeVersion"] = recipe.RecipeVersion,
                ["deliveryId"] = recipe.DeliveryId,
                ["planHash"] = token.PlanHash,
                ["published"] = published,
                ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["sources"] = recipe.Sources.Select(source => new Dictionary<string, object>
                {
                    ["modelId"] = source.Id,
                    ["path"] = source.SourcePath,
                    ["sha256"] = token.SourceFingerprints[source.Id].Sha256,
                    ["bytes"] = token.SourceFingerprints[source.Id].Length,
                    ["lastWriteUtc"] = new DateTime(token.SourceFingerprints[source.Id].LastWriteUtcTicks, DateTimeKind.Utc).ToString("o")
                }).ToArray(),
                ["models"] = modelResults.Select(result => result.ToDictionary()).ToArray()
            };
            var audit = new Dictionary<string, object>
            {
                ["deliveryId"] = recipe.DeliveryId,
                ["published"] = published,
                ["sourceInvariant"] = "Sources are read only; outputs must be standalone and non-workshared.",
                ["sourceHashesVerified"] = true,
                ["sources"] = recipe.Sources.Select(source => new Dictionary<string, object>
                {
                    ["modelId"] = source.Id,
                    ["path"] = source.SourcePath,
                    ["sha256Before"] = token.SourceFingerprints[source.Id].Sha256,
                    ["sha256After"] = SourceFingerprint.Read(source.SourcePath).Sha256
                }).ToArray(),
                ["errors"] = errors.Select(error => error.ToDictionary()).ToArray(),
                ["models"] = modelResults.Select(result => result.ToDictionary()).ToArray()
            };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonWireCodec.Serialize(manifest), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "audit.json"), JsonWireCodec.Serialize(audit), new UTF8Encoding(false));
        }

        private static Dictionary<string, object> BuildExecutionResult(
            DeliveryPreviewToken token,
            IEnumerable<DeliveryModelResult> models,
            IEnumerable<DeliveryIssue> errors,
            bool published,
            string manifestPath,
            string auditPath)
        {
            var data = new Dictionary<string, object>
            {
                ["previewId"] = token.PreviewId,
                ["planHash"] = token.PlanHash,
                ["deliveryId"] = token.Recipe.DeliveryId,
                ["applied"] = true,
                ["published"] = published,
                ["packagePath"] = token.Recipe.PackagePath,
                ["models"] = models.Select(model => model.ToDictionary()).ToArray(),
                ["errors"] = errors.Select(error => error.ToDictionary()).ToArray()
            };
            if (!string.IsNullOrWhiteSpace(manifestPath)) data["manifestPath"] = manifestPath;
            if (!string.IsNullOrWhiteSpace(auditPath)) data["auditPath"] = auditPath;
            return data;
        }

        private static void ValidateRecipePaths(DeliveryRecipe recipe, List<DeliveryIssue> blockers, List<DeliveryIssue> warnings)
        {
            if (Directory.Exists(recipe.PackagePath)) blockers.Add(new DeliveryIssue("FINAL_PACKAGE_EXISTS", "Final package already exists and will not be overwritten.", path: recipe.PackagePath));
            if (Directory.Exists(recipe.StagingPath) && Directory.EnumerateFileSystemEntries(recipe.StagingPath).Any())
            {
                if (IsOwnedStaging(recipe)) warnings.Add(new DeliveryIssue("STALE_STAGING_WILL_BE_RECONCILED", "Owned staging from an earlier failed attempt will be replaced on execute.", path: recipe.StagingPath));
                else blockers.Add(new DeliveryIssue("STAGING_NOT_OWNED", "Existing staging content is not owned by this delivery.", path: recipe.StagingPath));
            }
            foreach (DeliverySource source in recipe.Sources)
            {
                if (PathsEqual(source.SourcePath, Path.Combine(recipe.PackagePath, source.TargetFileName)))
                {
                    blockers.Add(new DeliveryIssue("SOURCE_OUTPUT_COLLISION", "Source and output paths resolve to the same file.", source.Id, source.SourcePath));
                }
                if (IsInside(recipe.PackagePath, source.SourcePath) || IsInside(recipe.StagingPath, source.SourcePath))
                {
                    blockers.Add(new DeliveryIssue("SOURCE_INSIDE_OUTPUT", "A source RVT is inside the final or staging package path.", source.Id, source.SourcePath));
                }
            }
        }

        private static DeliveryRecipe ParseRecipe(Dictionary<string, object> payload)
        {
            Dictionary<string, object> raw = GetDictionary(payload, "recipe");
            if (raw == null) throw new ModelDeliveryException("DELIVERY_RECIPE_REQUIRED", "A model delivery recipe is required.");
            string destinationRoot = NormalizeAbsolutePath(RequireString(raw, "destinationRoot"));
            string packageName = RequireString(raw, "packageName");
            if (!string.Equals(Path.GetFileName(packageName), packageName, StringComparison.Ordinal) || packageName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ModelDeliveryException("INVALID_PACKAGE_NAME", "packageName must be a directory name, not a path.");
            }
            string deliveryId = RequireString(raw, "deliveryId");
            if (deliveryId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ModelDeliveryException("INVALID_DELIVERY_ID", "deliveryId contains invalid path characters.");
            var recipe = new DeliveryRecipe
            {
                ProjectId = RequireString(raw, "projectId"),
                RecipeVersion = RequireString(raw, "recipeVersion"),
                DeliveryId = deliveryId,
                PackageName = packageName,
                DestinationRoot = destinationRoot,
                PackagePath = NormalizeAbsolutePath(Path.Combine(destinationRoot, packageName)),
                StagingPath = NormalizeAbsolutePath(Path.Combine(destinationRoot, StagingDirectoryName, deliveryId)),
                Cleanup = ParseCleanup(GetDictionary(raw, "cleanup")),
                Coordinates = ParseCoordinates(GetDictionary(raw, "coordinates")),
                Sources = GetDictionaryList(raw, "sourceModels").Select(ParseSource).ToList(),
                LinkRules = GetDictionaryList(raw, "linkRules").Select((item, index) => ParseLinkRule(item, index)).ToList(),
                Exports = GetDictionaryList(raw, "exports").Select(ParseExport).ToList(),
                Qa = ParseQa(GetDictionary(raw, "qa"))
            };
            if (recipe.Sources.Count == 0) throw new ModelDeliveryException("DELIVERY_SOURCES_REQUIRED", "At least one source model is required.");
            EnsureUnique(recipe.Sources.Select(source => source.Id), "DUPLICATE_MODEL_ID", "Source model IDs must be unique.");
            EnsureUnique(recipe.Sources.Select(source => source.SourcePath), "DUPLICATE_SOURCE_PATH", "Source model paths must be unique.");
            EnsureUnique(recipe.Sources.Select(source => source.TargetFileName), "DUPLICATE_TARGET_NAME", "Target RVT names must be unique.");
            HashSet<string> modelIds = new HashSet<string>(recipe.Sources.Select(source => source.Id), StringComparer.Ordinal);
            foreach (DeliveryLinkRule rule in recipe.LinkRules)
            {
                if (!modelIds.Contains(rule.SourceModelId)) throw new ModelDeliveryException("LINK_RULE_SOURCE_UNKNOWN", "Link rule references unknown sourceModelId: " + rule.SourceModelId + ".");
                if (string.Equals(rule.Action, "repath", StringComparison.Ordinal) && !modelIds.Contains(rule.TargetModelId)) throw new ModelDeliveryException("LINK_RULE_TARGET_UNKNOWN", "Link rule references unknown targetModelId: " + rule.TargetModelId + ".");
            }
            foreach (DeliveryExport export in recipe.Exports)
            {
                foreach (string modelId in export.ModelIds)
                {
                    if (!modelIds.Contains(modelId)) throw new ModelDeliveryException("EXPORT_MODEL_UNKNOWN", "Export references unknown modelId: " + modelId + ".");
                }
            }
            return recipe;
        }

        private static DeliverySource ParseSource(Dictionary<string, object> raw)
        {
            string target = RequireString(raw, "targetFileName");
            if (!string.Equals(Path.GetFileName(target), target, StringComparison.Ordinal) || !string.Equals(Path.GetExtension(target), ".rvt", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelDeliveryException("INVALID_TARGET_FILE_NAME", "targetFileName must be a file name ending with .rvt.");
            }
            return new DeliverySource
            {
                Id = RequireString(raw, "id"),
                SourcePath = NormalizeAbsolutePath(RequireString(raw, "sourcePath")),
                TargetFileName = target,
                Role = GetString(raw, "role"),
                StartViewName = GetString(raw, "startViewName"),
                Start3dViewName = GetString(raw, "start3dViewName")
            };
        }

        private static DeliveryCleanup ParseCleanup(Dictionary<string, object> raw)
        {
            if (raw == null) throw new ModelDeliveryException("CLEANUP_POLICY_REQUIRED", "An explicit cleanup policy is required.");
            return new DeliveryCleanup
            {
                DeleteSheets = RequireBool(raw, "deleteSheets"),
                DeleteViews = RequireBool(raw, "deleteViews"),
                DeleteSchedules = RequireBool(raw, "deleteSchedules"),
                DeleteLegends = RequireBool(raw, "deleteLegends"),
                DeleteDraftingViews = RequireBool(raw, "deleteDraftingViews"),
                DeleteViewTemplates = RequireBool(raw, "deleteViewTemplates"),
                DeleteUnusedFilters = RequireBool(raw, "deleteUnusedFilters"),
                RemoveUnmappedLinks = RequireBool(raw, "removeUnmappedLinks"),
                PurgeUnusedPasses = GetInt(raw, "purgeUnusedPasses", 0),
                ProtectedViewNames = new HashSet<string>(GetStringList(raw, "protectedViewNames"), StringComparer.OrdinalIgnoreCase)
            };
        }

        private static DeliveryCoordinatePolicy ParseCoordinates(Dictionary<string, object> raw)
        {
            if (raw == null) throw new ModelDeliveryException("COORDINATE_POLICY_REQUIRED", "An explicit coordinate policy is required.");
            if (!RequireBool(raw, "preserveLinkTransforms")) throw new ModelDeliveryException("COORDINATE_INVARIANT_REQUIRED", "preserveLinkTransforms must be true for reliable delivery.");
            string pathType = RequireString(raw, "packagedLinkPathType").ToLowerInvariant();
            if (!string.Equals(pathType, "relative", StringComparison.Ordinal)) throw new ModelDeliveryException("LINK_PATH_POLICY_INVALID", "packagedLinkPathType must be relative so the package remains portable.");
            return new DeliveryCoordinatePolicy { PreserveLinkTransforms = true, PackagedLinkPathType = pathType };
        }

        private static DeliveryQaPolicy ParseQa(Dictionary<string, object> raw)
        {
            if (raw == null) throw new ModelDeliveryException("QA_POLICY_REQUIRED", "An explicit delivery QA policy is required.");
            foreach (string invariant in new[] { "requireStandalone", "requireNoCentralPath", "requireSourceHashUnchanged", "requireCleanupMatchesPreview", "requireAllRequiredExports" })
            {
                if (!RequireBool(raw, invariant)) throw new ModelDeliveryException("QA_INVARIANT_REQUIRED", invariant + " must be true for production delivery.");
            }
            int? maxWarnings = GetNullableInt(raw, "maxWarnings");
            if (maxWarnings.HasValue && maxWarnings.Value < 0) throw new ModelDeliveryException("QA_WARNING_LIMIT_INVALID", "maxWarnings cannot be negative.");
            return new DeliveryQaPolicy { MaxWarnings = maxWarnings };
        }

        private static DeliveryLinkRule ParseLinkRule(Dictionary<string, object> raw, int index)
        {
            return new DeliveryLinkRule
            {
                Id = "link-rule-" + index.ToString(CultureInfo.InvariantCulture),
                SourceModelId = RequireString(raw, "sourceModelId"),
                MatchPath = NormalizeOptionalPath(GetString(raw, "matchPath")),
                MatchFileName = GetString(raw, "matchFileName"),
                Action = RequireString(raw, "action").ToLowerInvariant(),
                TargetModelId = GetString(raw, "targetModelId"),
                ExpectedInstanceCount = GetNullableInt(raw, "expectedInstanceCount"),
                Required = GetBool(raw, "required", false)
            };
        }

        private static DeliveryExport ParseExport(Dictionary<string, object> raw)
        {
            return new DeliveryExport
            {
                Format = RequireString(raw, "format").ToLowerInvariant(),
                ModelIds = new HashSet<string>(GetStringList(raw, "modelIds"), StringComparer.Ordinal),
                SetupName = GetString(raw, "setupName"),
                OutputSubdirectory = GetString(raw, "outputSubdirectory"),
                ViewNames = GetStringList(raw, "viewNames").ToList(),
                Required = RequireBool(raw, "required")
            };
        }

        private static HashSet<string> GetOpenDocumentPaths(Autodesk.Revit.ApplicationServices.Application application)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Document document in application.Documents)
            {
                if (document.IsLinked) continue;
                if (!string.IsNullOrWhiteSpace(document.PathName))
                {
                    try { paths.Add(NormalizeAbsolutePath(document.PathName)); } catch { }
                }
            }
            return paths;
        }

        private static Document FindOpenDocument(Autodesk.Revit.ApplicationServices.Application application, string path)
        {
            foreach (Document document in application.Documents)
            {
                if (document.IsLinked) continue;
                if (!string.IsNullOrWhiteSpace(document.PathName) && PathsEqual(document.PathName, path)) return document;
            }
            return null;
        }

        private static Dictionary<string, object> Decision(string key, string question)
        {
            return new Dictionary<string, object> { ["key"] = key, ["question"] = question };
        }

        private static string Slug(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "model";
            var builder = new StringBuilder();
            bool separator = false;
            foreach (char character in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                    separator = false;
                }
                else if (!separator && builder.Length > 0)
                {
                    builder.Append('-');
                    separator = true;
                }
            }
            string result = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(result) ? "model" : result;
        }

        private static DeliverySource FindAutomaticLinkTarget(DeliveryRecipe recipe, string existingPath, string existingFileName)
        {
            if (!string.IsNullOrWhiteSpace(existingPath))
            {
                DeliverySource byPath = recipe.Sources.SingleOrDefault(source => PathsEqual(source.SourcePath, existingPath));
                if (byPath != null) return byPath;
            }
            List<DeliverySource> byName = recipe.Sources.Where(source => string.Equals(Path.GetFileName(source.SourcePath), existingFileName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            List<DeliverySource> byTargetName = recipe.Sources.Where(source => string.Equals(source.TargetFileName, existingFileName, StringComparison.OrdinalIgnoreCase)).ToList();
            return byTargetName.Count == 1 ? byTargetName[0] : null;
        }

        private static bool RuleMatchesPackagedTarget(DeliveryRecipe recipe, DeliveryLinkRule rule, string existingPath, string existingFileName)
        {
            DeliverySource target = recipe.Sources.SingleOrDefault(source => string.Equals(source.Id, rule.TargetModelId, StringComparison.Ordinal));
            if (target == null) return false;
            return string.Equals(target.TargetFileName, existingFileName, StringComparison.OrdinalIgnoreCase) ||
                   PathsEqual(Path.Combine(recipe.StagingPath, target.TargetFileName), existingPath) ||
                   PathsEqual(Path.Combine(recipe.PackagePath, target.TargetFileName), existingPath);
        }

        private static string GetLinkPath(Document document, RevitLinkType linkType)
        {
            try
            {
                ExternalFileReference reference = ExternalFileUtils.GetExternalFileReference(document, linkType.Id);
                ModelPath path = reference?.GetAbsolutePath();
                return path == null ? string.Empty : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetCentralModelPath(Document document)
        {
            if (document == null || !document.IsWorkshared) return string.Empty;
            try
            {
                object value = document.GetType().GetMethod("GetWorksharingCentralModelPath", Type.EmptyTypes)?.Invoke(document, null);
                return value is ModelPath modelPath ? ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsSystemView(View view)
        {
            return view == null ||
                   view.ViewType == ViewType.ProjectBrowser ||
                   view.ViewType == ViewType.SystemBrowser ||
                   view.ViewType == ViewType.Internal ||
                   view.ViewType == ViewType.Undefined ||
                   view.ViewType == ViewType.DrawingSheet;
        }

        private static HashSet<string> ProtectedViewNames(DeliveryCleanup cleanup, DeliverySource source)
        {
            var names = new HashSet<string>(cleanup.ProtectedViewNames, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(source.StartViewName)) names.Add(source.StartViewName);
            if (!string.IsNullOrWhiteSpace(source.Start3dViewName)) names.Add(source.Start3dViewName);
            return names;
        }

        private static string TransformSignature(RevitLinkInstance instance)
        {
            Transform transform = instance.GetTotalTransform();
            double[] values =
            {
                transform.Origin.X, transform.Origin.Y, transform.Origin.Z,
                transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z,
                transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z,
                transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z
            };
            return string.Join(",", values.Select(value => Math.Round(value, 9).ToString("R", CultureInfo.InvariantCulture)));
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)).Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 hash = SHA256.Create())
            {
                return string.Concat(hash.ComputeHash(stream).Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static string PreviewKey(string sessionId, string previewId)
        {
            return (sessionId ?? string.Empty) + ":" + previewId;
        }

        private void RemoveExpiredUnsafe(DateTimeOffset now)
        {
            foreach (string key in _previews.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) _previews.Remove(key);
        }

        private static string NormalizeAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ModelDeliveryException("ABSOLUTE_PATH_REQUIRED", "Model delivery paths must be absolute or UNC paths.");
            string trimmed = path.Trim();
            if (trimmed.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("ACC://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("urn:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelDeliveryException(
                    "CLOUD_MODEL_UNSUPPORTED",
                    "ACC/BIM 360/cloud models are not supported by model delivery. Use a local, mapped-drive, or UNC RVT path.");
            }
            if (!Path.IsPathRooted(trimmed)) throw new ModelDeliveryException("ABSOLUTE_PATH_REQUIRED", "Model delivery paths must be absolute or UNC paths: " + trimmed);
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string NormalizeOptionalPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : NormalizeAbsolutePath(path);
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try { return string.Equals(NormalizeAbsolutePath(left), NormalizeAbsolutePath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool IsInside(string parent, string child)
        {
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child)) return false;
            string normalizedParent = NormalizeAbsolutePath(parent) + Path.DirectorySeparatorChar;
            string normalizedChild = NormalizeAbsolutePath(child);
            return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureUnique(IEnumerable<string> values, string code, string message)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                if (!seen.Add(value)) throw new ModelDeliveryException(code, message);
            }
        }

        private static string IdValue(ElementId id)
        {
#if REVIT2027
            return id?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
#else
#pragma warning disable CS0618
            return id?.IntegerValue.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
#pragma warning restore CS0618
#endif
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? value as Dictionary<string, object> : null;
        }

        private static List<Dictionary<string, object>> GetDictionaryList(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return new List<Dictionary<string, object>>();
            if (value is object[] array) return array.OfType<Dictionary<string, object>>().ToList();
            if (value is ArrayList arrayList) return arrayList.Cast<object>().OfType<Dictionary<string, object>>().ToList();
            if (value is IEnumerable enumerable) return enumerable.Cast<object>().OfType<Dictionary<string, object>>().ToList();
            return new List<Dictionary<string, object>>();
        }

        private static IReadOnlyList<string> GetStringList(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return Array.Empty<string>();
            if (value is string single) return new[] { single };
            if (value is IEnumerable enumerable) return enumerable.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return Array.Empty<string>();
        }

        private static string RequireString(Dictionary<string, object> root, string key)
        {
            string value = GetString(root, key)?.Trim();
            if (string.IsNullOrWhiteSpace(value)) throw new ModelDeliveryException("DELIVERY_FIELD_REQUIRED", "Required model delivery field is missing: " + key + ".");
            return value;
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static bool RequireBool(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) throw new ModelDeliveryException("DELIVERY_FIELD_REQUIRED", "Required model delivery boolean is missing: " + key + ".");
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(Dictionary<string, object> root, string key, bool defaultValue)
        {
            return root != null && root.TryGetValue(key, out object value) && value != null
                ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> root, string key, int defaultValue)
        {
            return root != null && root.TryGetValue(key, out object value) && value != null
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : defaultValue;
        }

        private static int? GetNullableInt(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) && value != null
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : (int?)null;
        }

        private sealed class DeliveryRecipe
        {
            public string ProjectId;
            public string RecipeVersion;
            public string DeliveryId;
            public string PackageName;
            public string DestinationRoot;
            public string PackagePath;
            public string StagingPath;
            public List<DeliverySource> Sources;
            public List<DeliveryLinkRule> LinkRules;
            public DeliveryCoordinatePolicy Coordinates;
            public DeliveryCleanup Cleanup;
            public List<DeliveryExport> Exports;
            public DeliveryQaPolicy Qa;
        }

        private sealed class DeliveryCoordinatePolicy
        {
            public bool PreserveLinkTransforms;
            public string PackagedLinkPathType;
        }

        private sealed class DeliveryQaPolicy
        {
            public int? MaxWarnings;
        }

        private sealed class DeliverySource
        {
            public string Id;
            public string SourcePath;
            public string TargetFileName;
            public string Role;
            public string StartViewName;
            public string Start3dViewName;
        }

        private sealed class DeliveryCleanup
        {
            public bool DeleteSheets;
            public bool DeleteViews;
            public bool DeleteSchedules;
            public bool DeleteLegends;
            public bool DeleteDraftingViews;
            public bool DeleteViewTemplates;
            public bool DeleteUnusedFilters;
            public bool RemoveUnmappedLinks;
            public int PurgeUnusedPasses;
            public HashSet<string> ProtectedViewNames;
        }

        private sealed class DeliveryLinkRule
        {
            public string Id;
            public string SourceModelId;
            public string MatchPath;
            public string MatchFileName;
            public string Action;
            public string TargetModelId;
            public int? ExpectedInstanceCount;
            public bool Required;

            public bool Matches(string path, string fileName)
            {
                return (!string.IsNullOrWhiteSpace(MatchPath) && PathsEqual(MatchPath, path)) ||
                       (!string.IsNullOrWhiteSpace(MatchFileName) && string.Equals(MatchFileName, fileName, StringComparison.OrdinalIgnoreCase));
            }
        }

        private sealed class DeliveryExport
        {
            public string Format;
            public HashSet<string> ModelIds;
            public string SetupName;
            public string OutputSubdirectory;
            public List<string> ViewNames;
            public bool Required;
            public bool AppliesTo(string modelId) { return ModelIds.Count == 0 || ModelIds.Contains(modelId); }
        }

        private sealed class DeliveryPreviewToken
        {
            public DeliveryPreviewToken(string previewId, string sessionId, string planHash, string canonicalRecipe, DeliveryRecipe recipe, DeliveryTargetBinding targetBinding, Dictionary<string, SourceFingerprint> sourceFingerprints, List<DeliveryModelPlan> modelPlans, bool ready, DateTimeOffset expiresAt)
            {
                PreviewId = previewId;
                SessionId = sessionId ?? string.Empty;
                PlanHash = planHash;
                CanonicalRecipe = canonicalRecipe;
                Recipe = recipe;
                TargetBinding = targetBinding;
                SourceFingerprints = sourceFingerprints;
                ModelPlans = modelPlans;
                Ready = ready;
                ExpiresAt = expiresAt;
            }
            public string PreviewId { get; }
            public string SessionId { get; }
            public string PlanHash { get; }
            public string CanonicalRecipe { get; }
            public DeliveryRecipe Recipe { get; }
            public DeliveryTargetBinding TargetBinding { get; }
            public Dictionary<string, SourceFingerprint> SourceFingerprints { get; }
            public List<DeliveryModelPlan> ModelPlans { get; }
            public bool Ready { get; }
            public DateTimeOffset ExpiresAt { get; }
        }

        private enum DeliveryJobPhase
        {
            Queued,
            Copying,
            Processing,
            Validating,
            VerifyingSources,
            Publishing,
            FinalValidating,
            Succeeded,
            Failed,
            Cancelled
        }

        private sealed class DeliveryJob
        {
            public DeliveryJob(string sessionId, DeliveryPreviewToken token)
            {
                JobId = "delivery-job-" + Guid.NewGuid().ToString("N");
                SessionId = sessionId ?? string.Empty;
                Token = token;
                CreatedAt = DateTimeOffset.UtcNow;
                UpdatedAt = CreatedAt;
                Phase = DeliveryJobPhase.Queued;
                Message = "Delivery accepted and queued for Revit processing.";
                Results = token.Recipe.Sources.ToDictionary(
                    source => source.Id,
                    source => new DeliveryModelResult(source.Id, source.SourcePath, Path.Combine(token.Recipe.PackagePath, source.TargetFileName)),
                    StringComparer.Ordinal);
                foreach (DeliverySource source in token.Recipe.Sources)
                {
                    if (token.SourceFingerprints.TryGetValue(source.Id, out SourceFingerprint fingerprint))
                    {
                        Results[source.Id].SourceSha256 = fingerprint.Sha256;
                    }
                }
                TotalUnits = token.Recipe.Sources.Count * 4 + 2;
            }

            public string JobId;
            public string SessionId;
            public DeliveryPreviewToken Token;
            public DeliveryJobPhase Phase;
            public int SourceIndex;
            public int CompletedUnits;
            public int TotalUnits;
            public bool StagingPrepared;
            public bool MovedToFinal;
            public bool Published;
            public bool CancellationRequested;
            public string CancellationReason;
            public string Message;
            public DateTimeOffset CreatedAt;
            public DateTimeOffset UpdatedAt;
            public DateTimeOffset? CompletedAt;
            public Dictionary<string, DeliveryModelResult> Results;
            public List<DeliveryIssue> Errors = new List<DeliveryIssue>();

            public bool IsTerminal => Phase == DeliveryJobPhase.Succeeded || Phase == DeliveryJobPhase.Failed || Phase == DeliveryJobPhase.Cancelled;

            public Dictionary<string, object> ToDictionary()
            {
                string state = Phase.ToString().ToLowerInvariant();
                var data = new Dictionary<string, object>
                {
                    ["jobId"] = JobId,
                    ["previewId"] = Token.PreviewId,
                    ["planHash"] = Token.PlanHash,
                    ["deliveryId"] = Token.Recipe.DeliveryId,
                    ["state"] = state,
                    ["phase"] = state,
                    ["terminal"] = IsTerminal,
                    ["cancellationRequested"] = CancellationRequested,
                    ["published"] = Published,
                    ["packagePath"] = Token.Recipe.PackagePath,
                    ["stagingPath"] = Token.Recipe.StagingPath,
                    ["targetBinding"] = Token.TargetBinding.ToDictionary(),
                    ["completedUnits"] = CompletedUnits,
                    ["totalUnits"] = TotalUnits,
                    ["progressPercent"] = TotalUnits == 0 ? 0 : Math.Min(100, (int)Math.Round(CompletedUnits * 100.0 / TotalUnits)),
                    ["message"] = Message,
                    ["createdAt"] = CreatedAt.ToUniversalTime().ToString("o"),
                    ["updatedAt"] = UpdatedAt.ToUniversalTime().ToString("o"),
                    ["models"] = Results.Values.Select(result => result.ToDictionary()).ToArray(),
                    ["errors"] = Errors.Select(error => error.ToDictionary()).ToArray()
                };
                if (CompletedAt.HasValue) data["completedAt"] = CompletedAt.Value.ToUniversalTime().ToString("o");
                if (StagingPrepared && Directory.Exists(Token.Recipe.StagingPath))
                {
                    data["manifestPath"] = Path.Combine(Token.Recipe.StagingPath, "manifest.json");
                    data["auditPath"] = Path.Combine(Token.Recipe.StagingPath, "audit.json");
                }
                if (Published)
                {
                    data["manifestPath"] = Path.Combine(Token.Recipe.PackagePath, "manifest.json");
                    data["auditPath"] = Path.Combine(Token.Recipe.PackagePath, "audit.json");
                }
                return data;
            }
        }

        private sealed class SourceFingerprint : IEquatable<SourceFingerprint>
        {
            public long Length;
            public long LastWriteUtcTicks;
            public string Sha256;
            public static SourceFingerprint Read(string path)
            {
                var file = new FileInfo(path);
                if (!file.Exists) throw new ModelDeliveryException("SOURCE_NOT_FOUND", "Source RVT was not found: " + path);
                string sha256;
                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (SHA256 hash = SHA256.Create())
                {
                    sha256 = string.Concat(hash.ComputeHash(stream).Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
                }
                return new SourceFingerprint { Length = file.Length, LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks, Sha256 = sha256 };
            }
            public bool Equals(SourceFingerprint other)
            {
                return other != null &&
                       Length == other.Length &&
                       LastWriteUtcTicks == other.LastWriteUtcTicks &&
                       string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class DeliveryModelPlan
        {
            public string ModelId;
            public string Role;
            public string SourcePath;
            public string OutputPath;
            public long SourceBytes;
            public string SourceLastWriteUtc;
            public string SourceSha256;
            public bool SourceIsWorkshared;
            public int LinkCount;
            public int SheetDeleteCount;
            public int ViewDeleteCount;
            public int TemplateDeleteCount;
            public int FilterDeleteCount;
            public int WarningsCount;
            public HashSet<string> MatchedRuleIds;
            public Dictionary<string, object> ToDictionary()
            {
                var data = new Dictionary<string, object>
                {
                    ["modelId"] = ModelId,
                    ["sourcePath"] = SourcePath,
                    ["outputPath"] = OutputPath,
                    ["sourceBytes"] = SourceBytes,
                    ["sourceLastWriteUtc"] = SourceLastWriteUtc,
                    ["sourceSha256"] = SourceSha256,
                    ["sourceIsWorkshared"] = SourceIsWorkshared,
                    ["targetIsWorkshared"] = false,
                    ["linkCount"] = LinkCount,
                    ["sheetDeleteCount"] = SheetDeleteCount,
                    ["viewDeleteCount"] = ViewDeleteCount,
                    ["templateDeleteCount"] = TemplateDeleteCount,
                    ["filterDeleteCount"] = FilterDeleteCount,
                    ["warningsCount"] = WarningsCount
                };
                if (!string.IsNullOrWhiteSpace(Role)) data["role"] = Role;
                return data;
            }
        }

        private sealed class DeliveryModelResult
        {
            public DeliveryModelResult(string modelId, string sourcePath, string outputPath)
            {
                ModelId = modelId;
                SourcePath = sourcePath;
                OutputPath = outputPath;
            }
            public string ModelId;
            public string SourcePath;
            public string OutputPath;
            public bool Success;
            public bool IsWorkshared;
            public string CentralModelPath;
            public string SourceSha256;
            public string OutputSha256;
            public int LinksValidated;
            public int DeletedSheets;
            public int DeletedViews;
            public int DeletedTemplates;
            public int DeletedFilters;
            public int PurgedElements;
            public int WarningsCount;
            public List<string> LinkTransforms = new List<string>();
            public List<string> Exports = new List<string>();
            public List<string> Errors = new List<string>();
            public Dictionary<string, object> ToDictionary()
            {
                var data = new Dictionary<string, object>
                {
                    ["modelId"] = ModelId,
                    ["sourcePath"] = SourcePath,
                    ["outputPath"] = OutputPath,
                    ["success"] = Success,
                    ["isWorkshared"] = IsWorkshared,
                    ["linksValidated"] = LinksValidated,
                    ["deletedSheets"] = DeletedSheets,
                    ["deletedViews"] = DeletedViews,
                    ["deletedTemplates"] = DeletedTemplates,
                    ["deletedFilters"] = DeletedFilters,
                    ["purgedElements"] = PurgedElements,
                    ["warningsCount"] = WarningsCount,
                    ["linkTransforms"] = LinkTransforms.ToArray(),
                    ["exports"] = Exports.ToArray(),
                    ["errors"] = Errors.ToArray()
                };
                if (!string.IsNullOrWhiteSpace(CentralModelPath)) data["centralModelPath"] = CentralModelPath;
                if (!string.IsNullOrWhiteSpace(SourceSha256)) data["sourceSha256"] = SourceSha256;
                if (!string.IsNullOrWhiteSpace(OutputSha256)) data["outputSha256"] = OutputSha256;
                return data;
            }
        }

        private sealed class DeliveryIssue
        {
            public DeliveryIssue(string code, string message, string modelId = null, string path = null)
            {
                Code = code;
                Message = message;
                ModelId = modelId;
                Path = path;
            }
            public string Code;
            public string Message;
            public string ModelId;
            public string Path;
            public Dictionary<string, object> ToDictionary()
            {
                var data = new Dictionary<string, object> { ["code"] = Code, ["message"] = Message };
                if (!string.IsNullOrWhiteSpace(ModelId)) data["modelId"] = ModelId;
                if (!string.IsNullOrWhiteSpace(Path)) data["path"] = Path;
                return data;
            }
        }

        private sealed class LinkActionPlan
        {
            public ElementId LinkTypeId;
            public string ExistingPath;
            public string ExistingFileName;
            public string Action;
            public string TargetPath;
            public string RuleId;
            public int InstanceCount;
            public string[] InstanceTransforms;
        }

        private sealed class CleanupPlan
        {
            public List<ElementId> SheetIds = new List<ElementId>();
            public List<ElementId> ViewIds = new List<ElementId>();
            public List<ElementId> TemplateIds = new List<ElementId>();
            public List<ElementId> FilterIds = new List<ElementId>();
        }
    }

    internal sealed class DeliveryTargetBinding : IEquatable<DeliveryTargetBinding>
    {
        public DeliveryTargetBinding(
            string instanceId,
            string documentFingerprint,
            long generation,
            string documentPath,
            string centralModelPath)
        {
            InstanceId = instanceId ?? string.Empty;
            DocumentFingerprint = documentFingerprint ?? string.Empty;
            Generation = generation;
            DocumentPath = NormalizePath(documentPath);
            CentralModelPath = NormalizePath(centralModelPath);
        }

        public string InstanceId { get; }
        public string DocumentFingerprint { get; }
        public long Generation { get; }
        public string DocumentPath { get; }
        public string CentralModelPath { get; }

        public string CanonicalIdentity =>
            InstanceId + "|" + DocumentFingerprint + "|" + Generation.ToString(CultureInfo.InvariantCulture) + "|" +
            DocumentPath + "|" + CentralModelPath;

        public bool Equals(DeliveryTargetBinding other)
        {
            return other != null &&
                   string.Equals(InstanceId, other.InstanceId, StringComparison.Ordinal) &&
                   string.Equals(DocumentFingerprint, other.DocumentFingerprint, StringComparison.OrdinalIgnoreCase) &&
                   Generation == other.Generation &&
                   string.Equals(DocumentPath, other.DocumentPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(CentralModelPath, other.CentralModelPath, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj) => Equals(obj as DeliveryTargetBinding);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(InstanceId);
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(DocumentFingerprint);
                hash = (hash * 397) ^ Generation.GetHashCode();
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(DocumentPath);
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(CentralModelPath);
                return hash;
            }
        }

        public string Describe()
        {
            string path = string.IsNullOrWhiteSpace(DocumentPath) ? "(unsaved document)" : DocumentPath;
            string central = string.IsNullOrWhiteSpace(CentralModelPath) ? "none" : CentralModelPath;
            return "instance " + InstanceId + ", document " + DocumentFingerprint + ", generation " +
                   Generation.ToString(CultureInfo.InvariantCulture) + ", path " + path + ", central " + central;
        }

        public Dictionary<string, object> ToDictionary()
        {
            var data = new Dictionary<string, object>
            {
                ["instanceId"] = InstanceId,
                ["documentFingerprint"] = DocumentFingerprint,
                ["generation"] = Generation
            };
            if (!string.IsNullOrWhiteSpace(DocumentPath)) data["documentPath"] = DocumentPath;
            if (!string.IsNullOrWhiteSpace(CentralModelPath)) data["centralModelPath"] = CentralModelPath;
            return data;
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Trim();
            if (trimmed.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return trimmed.TrimEnd('/');
            }
            try
            {
                return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return trimmed.Replace('/', '\\').TrimEnd('\\');
            }
        }
    }

    internal sealed class ModelDeliveryException : Exception
    {
        public ModelDeliveryException(string code, string message) : base(message) { Code = code; }
        public string Code { get; }
    }
}
