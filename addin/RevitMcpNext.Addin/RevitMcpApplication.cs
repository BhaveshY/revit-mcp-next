using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Addin.Ipc;
using RevitMcpNext.Addin.Revit;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The add-in entry point (loaded by RevitMcpNext.Loader). Startup order (D2 §19.2): home → logger → settings →
    /// auth → crash guards → operation registry → document registry (events) → queue + pump + handler + ExternalEvent →
    /// dialog policy → FailuresProcessing safety net → Idling → UI monitor → pipe host → registration (state starting;
    /// ApplicationInitialized or the first Idling sets ready). Never shows a dialog and never blocks Revit.
    /// </summary>
    public sealed class RevitMcpApplication : IExternalApplication
    {
        private UIControlledApplication _application;
        private SettingsStore _settings;
        private DocumentRegistry _registry;
        private RevitRequestQueue _queue;
        private ExternalEventPump _pump;
        private RevitExternalEventHandler _handler;
        private ExternalEvent _externalEvent;
        private JobRunner _jobs;
        private DialogPolicy _dialogs;
        private UiStateMonitor _uiMonitor;
        private NamedPipeHost _pipeHost;
        private RuntimeInstanceRegistration _registration;
        private bool _failuresHooked;
        private bool _idlingHooked;
        private bool _dialogsHooked;

        public Result OnStartup(UIControlledApplication application)
        {
            _application = application;
            try
            {
                McpHome home = McpHome.Resolve();
                string layoutProblem = home.EnsureLayout();
                McpRuntime.Home = home;

                int year = 0;
                int.TryParse(application.ControlledApplication.VersionNumber, out year);
                _settings = new SettingsStore(home);
                McpRuntime.Settings = _settings;
                DiagnosticsLogger.Initialize(home, year, () => _settings.Current.Log);
                _settings.Start();
                if (layoutProblem != null) DiagnosticsLogger.Warn("startup", layoutProblem);
                if (home.LocationWarning != null) DiagnosticsLogger.Warn("startup", home.LocationWarning);

                var auth = new AuthTokenStore(home);
                auth.EnsureCreated();
                McpRuntime.Auth = auth;

                InstallCrashGuards();

                OperationRegistry ops = OperationRegistry.Build(typeof(RevitMcpApplication).Assembly, null, McpRuntime.TestOpsEnabled);
                McpRuntime.Ops = ops;

                InstanceIdentity identity = InstanceIdentity.Create(application.ControlledApplication, home, ops.CatalogHash);
                McpRuntime.Instance = identity;

                _registry = new DocumentRegistry(identity);
                McpRuntime.Registry = _registry;
                _registry.Attach(application);
                _registry.SetCodeExecution(_settings.Current.EnableCodeExecution, false, _settings.FileMtimeUtc);
                _settings.Changed += settings => _registry.SetCodeExecution(settings.EnableCodeExecution, _registry.CodeExecution.Consented, _settings.FileMtimeUtc);

                _jobs = new JobRunner(home, identity);
                _jobs.ResponseBuilder = (ctx, result) => Dispatcher.BuildResponse(ctx, result);
                McpRuntime.Jobs = _jobs;

                _queue = new RevitRequestQueue();
                McpRuntime.Queue = _queue;
                _pump = new ExternalEventPump(() => _settings.Current.Wake);
                McpRuntime.Pump = _pump;
                _queue.AttachPump(_pump);
                _handler = new RevitExternalEventHandler(_queue);
                _externalEvent = ExternalEvent.Create(_handler);
                _pump.Attach(_externalEvent);
                _pump.HasWork = () => _queue.HasPending || _queue.Deferred != null || _jobs.HasActiveJobs;
                _pump.IsExecuting = () => _handler.InExecute;

                _dialogs = new DialogPolicy(() => _settings.Current.DialogPolicy);
                McpRuntime.Dialogs = _dialogs;
                application.DialogBoxShowing += OnDialogBoxShowing;
                _dialogsHooked = true;

                application.ControlledApplication.FailuresProcessing += OnFailuresProcessing;
                _failuresHooked = true;

                application.Idling += OnIdling;
                _idlingHooked = true;

                _uiMonitor = new UiStateMonitor(_registry);
                McpRuntime.UiMonitor = _uiMonitor;
                try { _uiMonitor.RefreshHandle(application.MainWindowHandle); } catch { }
                _uiMonitor.Start();

                string ledgerPath = home.LedgerFile(identity.InstanceId);
                McpRuntime.Ledger = new RequestOutcomeLedger(ledgerPath);

                _pipeHost = new NamedPipeHost(identity.PipeName, identity.ControlPipeName, _queue, McpRuntime.Ledger, auth, _jobs);
                _pipeHost.Start();

                _registration = RuntimeInstanceRegistration.Start(home, identity, _registry);

                DiagnosticsLogger.Info("startup", "Revit MCP Next " + identity.AddinVersion + "+" + identity.GitSha + " started as " + identity.InstanceId +
                    " (Revit " + identity.Year + " " + identity.Build + ", " + identity.Language + ") in " + home.Root + " [" + home.Source + "].",
                    new Dictionary<string, object>
                    {
                        ["pipe"] = identity.PipeName,
                        ["payloadId"] = identity.PayloadId,
                        ["catalogHash"] = identity.CatalogHash,
                        ["authState"] = auth.State,
                        ["authFp"] = auth.Fingerprint,
                        ["boundKeys"] = ops.BoundKeys().Count,
                        ["mismatches"] = ops.Mismatches.Count,
                        ["testOps"] = McpRuntime.TestOpsEnabled
                    });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("startup", "Revit MCP Next failed to start; the add-in stays inactive in this Revit session.", ex);
                Cleanup("ADDIN_STARTUP_FAILED");
                DiagnosticsLogger.Flush();
                return Result.Succeeded;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                _registry?.SetState("stopping");
                Cleanup("shutdown");
                DiagnosticsLogger.Info("shutdown", "Revit MCP Next shut down.");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("shutdown", "Revit MCP Next shutdown failed.", ex);
            }
            DiagnosticsLogger.Flush();
            return Result.Succeeded;
        }

        private void Cleanup(string reason)
        {
            UIControlledApplication application = _application;
            SafeRun(() => _queue?.CancelAll(ErrorCodes.RevitExited, "Revit is shutting down (" + reason + ")."));
            SafeRun(() => _jobs?.CancelAll(ErrorCodes.RevitExited, "Revit is shutting down (" + reason + ")."));
            SafeRun(() => _pipeHost?.Dispose());
            SafeRun(() => _registration?.Dispose());
            SafeRun(() => _uiMonitor?.Dispose());
            if (application != null)
            {
                if (_idlingHooked) SafeRun(() => application.Idling -= OnIdling);
                if (_dialogsHooked) SafeRun(() => application.DialogBoxShowing -= OnDialogBoxShowing);
                if (_failuresHooked) SafeRun(() => application.ControlledApplication.FailuresProcessing -= OnFailuresProcessing);
                SafeRun(() => _registry?.Detach(application));
            }
            _idlingHooked = _dialogsHooked = _failuresHooked = false;
            SafeRun(() => _pump?.Dispose());
            SafeRun(() => _externalEvent?.Dispose());
            SafeRun(() => _settings?.Dispose());
            _pipeHost = null;
            _registration = null;
            _externalEvent = null;
        }

        private void OnIdling(object sender, IdlingEventArgs args)
        {
            try
            {
                if (!(sender is UIApplication app)) return;
                _registry?.OnIdling(app);
                try { _uiMonitor?.RefreshHandle(app.MainWindowHandle); } catch { }
                if (_pump != null && _pump.NeedsRecreate && _handler != null)
                {
                    // Idling is API context, so the ExternalEvent can be recreated here.
                    _externalEvent = ExternalEvent.Create(_handler);
                    _pump.Recreate(_externalEvent);
                    _pump.Raise();
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("idling", "Idling handler failed.", ex);
            }
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs args)
        {
            _dialogs?.Handle(sender, args);
        }

        private static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs args)
        {
            FailureCapture.OnFailuresProcessing(sender, args);
        }

        private static bool _crashGuardsInstalled;

        private static void InstallCrashGuards()
        {
            if (_crashGuardsInstalled) return;
            _crashGuardsInstalled = true;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                try
                {
                    DiagnosticsLogger.Error("crash", "Unhandled exception (terminating=" + args.IsTerminating + ").", args.ExceptionObject as Exception);
                    DiagnosticsLogger.Flush(1000);
                }
                catch
                {
                    // Never throw from the crash guard.
                }
            };
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                try
                {
                    args.SetObserved();
                    DiagnosticsLogger.Error("crash", "Unobserved task exception.", args.Exception);
                }
                catch
                {
                    // Never throw from the crash guard.
                }
            };
        }

        private static void SafeRun(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("cleanup", "Cleanup step failed.", ex);
            }
        }
    }
}
