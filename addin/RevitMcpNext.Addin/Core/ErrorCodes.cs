using System;
using System.Collections.Generic;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Every error code of the ErrorCatalog (SPEC §4.3; contracts/src/errors.ts is the source of fix text).
    /// The add-in returns only code + message (concrete names/values) + details; the broker renders fix:.
    /// </summary>
    internal static class ErrorCodes
    {
        // Instances, transport, auth
        public const string NoRevitRunning = "NO_REVIT_RUNNING";
        public const string RevitStarting = "REVIT_STARTING";
        public const string RevitExited = "REVIT_EXITED";
        public const string AddinPipeMissing = "ADDIN_PIPE_MISSING";
        public const string AddinOutdated = "ADDIN_OUTDATED";
        public const string AddinNewerThanBroker = "ADDIN_NEWER_THAN_BROKER";
        public const string AuthMismatch = "AUTH_MISMATCH";
        public const string AuthNotConfigured = "AUTH_NOT_CONFIGURED";
        public const string BridgeAccessDenied = "BRIDGE_ACCESS_DENIED";
        public const string BridgeBusy = "BRIDGE_BUSY";
        public const string RevitQueueFull = "REVIT_QUEUE_FULL";
        public const string RevitBusy = "REVIT_BUSY";
        public const string RevitDialogOpen = "REVIT_DIALOG_OPEN";
        public const string RevitNotResponding = "REVIT_NOT_RESPONDING";
        public const string RevitEditModeOrCommand = "REVIT_EDIT_MODE_OR_COMMAND";
        public const string RequestCancelled = "REQUEST_CANCELLED";

        // Targeting
        public const string NoOpenDocument = "NO_OPEN_DOCUMENT";
        public const string TargetAmbiguous = "TARGET_AMBIGUOUS";
        public const string TargetClosed = "TARGET_CLOSED";
        public const string DocNotOpen = "DOC_NOT_OPEN";
        public const string TargetChanged = "TARGET_CHANGED";
        /// <summary>Add-in to broker: the {rid,key} pair does not match an open document; refresh the snapshot and retry.</summary>
        public const string TargetStale = "TARGET_STALE";
        public const string FamilyDocRequired = "FAMILY_DOC_REQUIRED";
        public const string ProjectDocRequired = "PROJECT_DOC_REQUIRED";
        public const string DocReadOnly = "DOC_READ_ONLY";
        public const string LinkReadOnly = "LINK_READ_ONLY";
        public const string NeedsActiveDoc = "NEEDS_ACTIVE_DOC";

        // Execution outcomes
        public const string WriteStillRunning = "WRITE_STILL_RUNNING";
        public const string ReadStillRunning = "READ_STILL_RUNNING";
        public const string WriteOutcomeUnknown = "WRITE_OUTCOME_UNKNOWN";
        public const string RevitTransactionRolledBack = "REVIT_TRANSACTION_ROLLED_BACK";
        public const string RevitRefused = "REVIT_REFUSED";

        // Arguments and names
        public const string InvalidArgs = "INVALID_ARGS";
        public const string UnknownOp = "UNKNOWN_OP";
        public const string NotFound = "NOT_FOUND";
        public const string AmbiguousName = "AMBIGUOUS_NAME";
        public const string NameTaken = "NAME_TAKEN";
        public const string UnsupportedVersion = "UNSUPPORTED_VERSION";
        public const string UnsupportedOp = "UNSUPPORTED_OP";
        public const string NotEditable = "NOT_EDITABLE";
        public const string TemplateControlled = "TEMPLATE_CONTROLLED";
        public const string NoTagFamily = "NO_TAG_FAMILY";
        public const string LegendNeedsSeed = "LEGEND_NEEDS_SEED";
        public const string CannotCloseActive = "CANNOT_CLOSE_ACTIVE";
        public const string LastView = "LAST_VIEW";
        public const string SaveAsRequired = "SAVE_AS_REQUIRED";
        public const string ExporterMissing = "EXPORTER_MISSING";
        public const string CaptureGpuUnavailable = "CAPTURE_GPU_UNAVAILABLE";
        public const string CaptureNotExportable = "CAPTURE_NOT_EXPORTABLE";

        // Confirm tokens and session caches
        public const string ConfirmStale = "CONFIRM_STALE";
        public const string ConfirmExpired = "CONFIRM_EXPIRED";
        public const string ConfirmMismatch = "CONFIRM_MISMATCH";
        public const string PageExpired = "PAGE_EXPIRED";
        public const string HandleExpired = "HANDLE_EXPIRED";
        public const string CaptureGone = "CAPTURE_GONE";
        public const string JobUnknown = "JOB_UNKNOWN";
        public const string OutcomeExpired = "OUTCOME_EXPIRED";

        // Undo
        public const string UndoBlocked = "UNDO_BLOCKED";
        public const string UndoUnconfirmed = "UNDO_UNCONFIRMED";
        public const string Irreversible = "IRREVERSIBLE";

        // Family editing (D3 §3.7)
        public const string NeedsTypes = "NEEDS_TYPES";
        public const string NotEditableFamily = "NOT_EDITABLE_FAMILY";
        public const string FormulaInvalid = "FORMULA_INVALID";
        public const string ParamInUse = "PARAM_IN_USE";
        public const string ParamHasFormula = "PARAM_HAS_FORMULA";
        public const string SharedParamFileMissing = "SHARED_PARAM_FILE_MISSING";
        public const string SharedParamRename = "SHARED_PARAM_RENAME";

        // Code execution (SPEC §11)
        public const string CodeExecutionDisabled = "CODE_EXECUTION_DISABLED";
        public const string CodeExecutionDeclined = "CODE_EXECUTION_DECLINED";
        public const string CodeExecutionUnavailable = "CODE_EXECUTION_UNAVAILABLE";
        public const string CodeCompileError = "CODE_COMPILE_ERROR";
        public const string CodeDeniedApi = "CODE_DENIED_API";
        public const string CodeTimeout = "CODE_TIMEOUT";
        public const string CodeRuntimeError = "CODE_RUNTIME_ERROR";

        // Dialogs (ui press)
        public const string DialogNotFound = "DIALOG_NOT_FOUND";
        public const string DialogProtected = "DIALOG_PROTECTED";
        public const string ButtonNotFound = "BUTTON_NOT_FOUND";

        // Generic
        public const string InternalError = "INTERNAL_ERROR";
        public const string ResponseTooLarge = "RESPONSE_TOO_LARGE";

        // Add-in/transport internals (not rendered to models as-is; the broker maps them)
        public const string ResponseSerializationFailed = "RESPONSE_SERIALIZATION_FAILED";
        public const string RequestIdConflict = "REQUEST_ID_CONFLICT";
        /// <summary>Status finding: the add-in was built without artifacts/catalog/catalog.json.</summary>
        public const string CatalogMissing = "CATALOG_MISSING";
        /// <summary>Status finding: settings.json could not be parsed; the last good values stay in effect.</summary>
        public const string SettingsInvalid = "SETTINGS_INVALID";

        /// <summary>Codes whose responses guarantee that nothing changed in the model.</summary>
        public static bool NothingChanged(string code)
        {
            switch (code)
            {
                case WriteStillRunning:
                case WriteOutcomeUnknown:
                case InternalError:
                    return false;
                default:
                    return true;
            }
        }
    }

    /// <summary>warn: line codes (SPEC §4.3).</summary>
    internal static class WarningCodes
    {
        public const string RevitWarning = "REVIT_WARNING";
        public const string RevitDialogAnswered = "REVIT_DIALOG_ANSWERED";
        public const string ValueClamped = "VALUE_CLAMPED";
        public const string ParamIgnored = "PARAM_IGNORED";
        public const string ParamRenamed = "PARAM_RENAMED";
        public const string PartialResult = "PARTIAL_RESULT";
        public const string FittingFailed = "FITTING_FAILED";
        public const string IdsGone = "IDS_GONE";
        public const string NonFiniteNumber = "NON_FINITE_NUMBER";
        public const string BridgeResponseRecovered = "BRIDGE_RESPONSE_RECOVERED";
        public const string SettingsInvalid = "SETTINGS_INVALID";
        public const string LateResult = "LATE_RESULT";
    }

    /// <summary>notice: line codes (SPEC §4.3).</summary>
    internal static class NoticeCodes
    {
        public const string AutoPinned = "AUTO_PINNED";
        public const string TargetNow = "TARGET_NOW";
        public const string Rebound = "REBOUND";
        public const string SavedAs = "SAVED_AS";
        public const string PinMoved = "PIN_MOVED";
        public const string ActiveDiffers = "ACTIVE_DIFFERS";
        public const string FamilyEditing = "FAMILY_EDITING";
        public const string RecoveredWrite = "RECOVERED_WRITE";
        public const string CodeExecutionEnabled = "CODE_EXECUTION_ENABLED";
        public const string SelectionStale = "SELECTION_STALE";
    }

    /// <summary>
    /// Thrown by handlers (ctx.Error / c.Error / PayloadReader) to return a cataloged error. The dispatcher turns it into
    /// an error response; inside the ChangeEngine it rolls the group back first.
    /// </summary>
    internal sealed class OpException : Exception
    {
        public OpException(string code, string message, object details = null)
            : base(message ?? code)
        {
            Code = string.IsNullOrWhiteSpace(code) ? ErrorCodes.InternalError : code;
            Details = details;
        }

        public string Code { get; }

        /// <summary>Structured details (anonymous objects are not allowed on the wire; use dictionaries or wire DTOs).</summary>
        public object Details { get; }

        public static OpException InvalidArgs(string param, string reason, object example = null)
        {
            var details = new Dictionary<string, object> { ["param"] = param, ["reason"] = reason };
            if (example != null) details["example"] = example;
            return new OpException(ErrorCodes.InvalidArgs, string.IsNullOrEmpty(param) ? reason : param + ": " + reason, details);
        }

        public static OpException NotFound(string param, string kind, string value, IEnumerable<string> candidates = null)
        {
            var list = candidates == null ? new List<string>() : new List<string>(candidates);
            var details = new Dictionary<string, object>
            {
                ["param"] = param,
                ["kind"] = kind,
                ["value"] = value,
                ["candidates"] = list
            };
            string message = kind + " '" + value + "' was not found" +
                             (list.Count > 0 ? "; closest: " + string.Join(", ", list.GetRange(0, Math.Min(5, list.Count))) : string.Empty) + ".";
            return new OpException(ErrorCodes.NotFound, message, details);
        }

        public static OpException Ambiguous(string param, string kind, string value, IEnumerable<string> candidates)
        {
            var list = candidates == null ? new List<string>() : new List<string>(candidates);
            var details = new Dictionary<string, object>
            {
                ["param"] = param,
                ["kind"] = kind,
                ["value"] = value,
                ["candidates"] = list
            };
            string message = kind + " '" + value + "' matches several elements: " +
                             string.Join(", ", list.GetRange(0, Math.Min(5, list.Count))) + ". Use an exact name or an id.";
            return new OpException(ErrorCodes.AmbiguousName, message, details);
        }
    }
}
