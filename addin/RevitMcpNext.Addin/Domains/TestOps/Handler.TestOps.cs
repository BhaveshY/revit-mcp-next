using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// e2e-only test ops (SPEC §13.4). They are compiled into the release DLL but registered only when the home is not
    /// %USERPROFILE%\.revit-mcp-next and &lt;home&gt;\config\e2e-test-ops.enable exists (OperationRegistry.Build is passed
    /// McpRuntime.TestOpsEnabled), so users never see them. They are reached through the raw pipe only (the broker does not
    /// advertise test.* keys).
    /// <list type="bullet">
    /// <item><c>test.sleep_ui {ms}</c> blocks the Revit UI thread (R5 busy); honours cancel_request between 100 ms slices.</item>
    /// <item><c>test.show_dialog {id, title, text, buttons}</c> shows a TaskDialog with that id at the next Idling (outside
    /// any MCP request, so the auto-responder treats it as the user's) and returns at once (R7, V20).</item>
    /// <item><c>test.enter_command {command, escapeAfterMs}</c> PostCommands a placement tool so Revit sits in a command
    /// (R12); Escape is posted twice after escapeAfterMs (default 20 s, 0 = never) so the session recovers.</item>
    /// <item><c>test.inproc_call {key, args, doc?}</c> calls through RevitMcpInProcessBridge (S99).</item>
    /// <item><c>test.window {state: minimize|restore}</c> minimizes or restores the Revit main window (R6 wake latency).</item>
    /// <item><c>test.state</c> returns the last test dialog result and the pump/dialog counters.</item>
    /// </list>
    /// </summary>
    internal static class TestOps
    {
        private const int MaxSleepMs = 120000;
        private static readonly ConcurrentQueue<Action<UIApplication>> IdleActions = new ConcurrentQueue<Action<UIApplication>>();
        private static readonly object StateGate = new object();
        private static Dictionary<string, object> _lastDialogResult;
        private static Timer _escapeTimer;

        /// <summary>True when an action waits for the next Idling (the Idling handler then asks for a prompt raise).</summary>
        public static bool HasIdleActions => !IdleActions.IsEmpty;

        /// <summary>Runs the queued idle actions (RevitMcpApplication.OnIdling, UI thread, never inside an MCP request).</summary>
        public static void RunIdleActions(UIApplication app)
        {
            if (!McpRuntime.TestOpsEnabled) return;
            int guard = 0;
            while (guard++ < 4 && IdleActions.TryDequeue(out Action<UIApplication> action))
            {
                try
                {
                    action(app);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Error("testops", "A test idle action failed.", ex);
                }
            }
        }

        [Op("test.sleep_ui", Kind = "read", Scope = "none", Idle = false)]
        public static OpResult SleepUi(RequestContext ctx)
        {
            int ms = ctx.Args.IntClamped("ms", 30000, 0, MaxSleepMs);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            DiagnosticsLogger.Info("testops", "test.sleep_ui blocks the UI thread for " + ms.ToString(CultureInfo.InvariantCulture) + " ms.");
            while (stopwatch.ElapsedMilliseconds < ms)
            {
                if (ctx.CancelRequested)
                {
                    return OpResult.Fail(ErrorCodes.RequestCancelled, "test.sleep_ui was cancelled after " +
                        stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
                }
                Thread.Sleep((int)Math.Min(100, Math.Max(1, ms - stopwatch.ElapsedMilliseconds)));
            }
            return OpResult.Success(new Dictionary<string, object> { ["sleptMs"] = stopwatch.ElapsedMilliseconds },
                "the UI thread slept " + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        [Op("test.show_dialog", Kind = "read", Scope = "none", Idle = false)]
        public static OpResult ShowDialog(RequestContext ctx)
        {
            string id = ctx.Args.Str("id", "E2E_Test_Dialog");
            string title = ctx.Args.Str("title", "E2E dialog");
            string text = ctx.Args.Str("text", "Dialog shown by the revit-mcp-next e2e test op test.show_dialog.");
            List<string> buttons = ctx.Args.Strs("buttons");
            if (buttons == null || buttons.Count == 0) buttons = new List<string> { "ok", "cancel" };

            TaskDialogCommonButtons common = TaskDialogCommonButtons.None;
            var links = new List<string>();
            foreach (string raw in buttons)
            {
                string button = (raw ?? string.Empty).Trim();
                switch (button.ToLowerInvariant())
                {
                    case "ok": common |= TaskDialogCommonButtons.Ok; break;
                    case "yes": common |= TaskDialogCommonButtons.Yes; break;
                    case "no": common |= TaskDialogCommonButtons.No; break;
                    case "cancel": common |= TaskDialogCommonButtons.Cancel; break;
                    case "retry": common |= TaskDialogCommonButtons.Retry; break;
                    case "close": common |= TaskDialogCommonButtons.Close; break;
                    default:
                        if (button.StartsWith("link:", StringComparison.OrdinalIgnoreCase) && links.Count < 4) links.Add(button.Substring(5).Trim());
                        else throw OpException.InvalidArgs("buttons", "unknown button '" + button + "' (ok, yes, no, cancel, retry, close or link:<text>)");
                        break;
                }
            }

            lock (StateGate) _lastDialogResult = new Dictionary<string, object> { ["id"] = id, ["state"] = "scheduled" };
            IdleActions.Enqueue(app =>
            {
                var dialog = new TaskDialog(title)
                {
                    Id = id,
                    MainInstruction = title,
                    MainContent = text,
                    CommonButtons = common,
                    AllowCancellation = (common & (TaskDialogCommonButtons.Cancel | TaskDialogCommonButtons.Close)) != 0,
                    TitleAutoPrefix = false
                };
                for (int index = 0; index < links.Count; index++)
                {
                    dialog.AddCommandLink((TaskDialogCommandLinkId)((int)TaskDialogCommandLinkId.CommandLink1 + index), links[index]);
                }
                lock (StateGate) _lastDialogResult = new Dictionary<string, object> { ["id"] = id, ["state"] = "showing", ["atUtc"] = DocumentRegistry.Utc(DateTime.UtcNow) };
                TaskDialogResult result = dialog.Show();
                lock (StateGate)
                {
                    _lastDialogResult = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["state"] = "closed",
                        ["result"] = result.ToString(),
                        ["atUtc"] = DocumentRegistry.Utc(DateTime.UtcNow)
                    };
                }
                DiagnosticsLogger.Info("testops", "test dialog " + id + " closed with " + result + ".");
            });
            return OpResult.Success(new Dictionary<string, object> { ["scheduled"] = true, ["id"] = id, ["title"] = title, ["buttons"] = buttons },
                "the test dialog " + id + " opens at the next Idling");
        }

        [Op("test.enter_command", Kind = "read", Scope = "none", Idle = false)]
        public static OpResult EnterCommand(RequestContext ctx)
        {
            string command = ctx.Args.Str("command", "Wall");
            int escapeAfterMs = ctx.Args.IntClamped("escapeAfterMs", 20000, 0, 300000);
            RevitCommandId id = ResolveCommand(command);
            if (id == null)
            {
                throw OpException.NotFound("command", "command", command,
                    Enum.GetNames(typeof(PostableCommand)).Where(n => n.IndexOf(command ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0).Take(10));
            }
            if (!ctx.App.CanPostCommand(id))
            {
                return OpResult.Fail(ErrorCodes.RevitRefused, "Revit cannot post the command " + command + " right now.",
                    new Dictionary<string, object> { ["apiMessage"] = "CanPostCommand returned false", ["command"] = command });
            }
            ctx.App.PostCommand(id);
            if (escapeAfterMs > 0) ScheduleEscape(escapeAfterMs);
            return OpResult.Success(new Dictionary<string, object> { ["posted"] = command, ["escapeAfterMs"] = escapeAfterMs },
                "posted " + command + "; Revit enters the command after this request");
        }

        [Op("test.inproc_call", Kind = "read", Scope = "none", Idle = false)]
        public static OpResult InprocCall(RequestContext ctx)
        {
            string key = ctx.Args.ReqStr("key");
            IDictionary<string, object> args = Wire.AsMap(Wire.Get(ctx.Request.Args, "args"));
            var request = new BridgeRequest
            {
                V = BridgeProtocol.Version,
                RequestId = Guid.NewGuid().ToString("N"),
                ClientKey = "test.inproc_call",
                Op = key,
                Kind = string.Empty,
                TimeoutMs = (int)Math.Max(1000, Math.Min(int.MaxValue, ctx.Remaining.TotalMilliseconds)),
                Doc = ctx.Request.Doc,
                Mode = Wire.GetString(ctx.Request.Args, "mode")
            };
            if (args != null) foreach (KeyValuePair<string, object> pair in args) request.Args[pair.Key] = pair.Value;
            BridgeResponse response = RevitMcpInProcessBridge.Execute(ctx.App, request);
            return OpResult.Success(new Dictionary<string, object> { ["key"] = key, ["response"] = response.ToWire() },
                key + " through the in-process bridge: " + (response.Ok ? "ok" : response.Code));
        }

        [Op("test.window", Kind = "read", Scope = "none", Idle = false)]
        public static OpResult Window(RequestContext ctx)
        {
            string state = ctx.Args.Enum("state", "restore", "minimize", "restore");
            IntPtr main = IntPtr.Zero;
            try { main = ctx.App.MainWindowHandle; } catch { main = IntPtr.Zero; }
            if (main == IntPtr.Zero) main = McpRuntime.UiMonitor?.MainWindow ?? IntPtr.Zero;
            if (main == IntPtr.Zero) return OpResult.Fail(ErrorCodes.RevitRefused, "The Revit main window handle is unknown.");
            bool ok = NativeMethods.ShowWindowAsync(main, state == "minimize" ? NativeMethods.SW_MINIMIZE : NativeMethods.SW_RESTORE);
            return OpResult.Success(new Dictionary<string, object> { ["state"] = state, ["posted"] = ok }, "main window " + state);
        }

        [Op("test.state", Kind = "read", Scope = "none", Idle = true)]
        public static OpResult State(RequestContext ctx)
        {
            Dictionary<string, object> dialog;
            lock (StateGate) dialog = _lastDialogResult == null ? null : new Dictionary<string, object>(_lastDialogResult);
            var data = new Dictionary<string, object>
            {
                ["lastTestDialog"] = dialog,
                ["pendingIdleActions"] = IdleActions.Count,
                ["pump"] = McpRuntime.Pump?.GetCounters(),
                ["dialogs"] = McpRuntime.Dialogs?.GetCounters(),
                ["queue"] = McpRuntime.Queue?.GetCounters(),
                ["ui"] = McpRuntime.UiMonitor?.GetCounters(),
                ["codeExecutionPreapproved"] = McpRuntime.CodeExecutionPreapproved
            };
            return OpResult.Success(data, "test state");
        }

        private static RevitCommandId ResolveCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            try
            {
                if (Enum.TryParse(command.Trim(), true, out PostableCommand postable) && Enum.IsDefined(typeof(PostableCommand), postable))
                {
                    return RevitCommandId.LookupPostableCommandId(postable);
                }
                return RevitCommandId.LookupCommandId(command.Trim());
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("testops", "Could not resolve the command " + command + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Posts Escape twice to the focused window of the Revit UI thread (and the main window) after a delay.</summary>
        private static void ScheduleEscape(int delayMs)
        {
            lock (StateGate)
            {
                _escapeTimer?.Dispose();
                _escapeTimer = new Timer(_ =>
                {
                    try
                    {
                        IntPtr main = McpRuntime.UiMonitor?.MainWindow ?? IntPtr.Zero;
                        if (!NativeMethods.SafeIsWindow(main)) return;
                        IntPtr focus = NativeMethods.FocusOfThread(NativeMethods.ThreadOf(main));
                        IntPtr target = focus != IntPtr.Zero ? focus : main;
                        for (int i = 0; i < 2; i++)
                        {
                            NativeMethods.Post(target, NativeMethods.WM_KEYDOWN, new IntPtr(NativeMethods.VK_ESCAPE), new IntPtr(0x00010001));
                            NativeMethods.Post(target, NativeMethods.WM_KEYUP, new IntPtr(NativeMethods.VK_ESCAPE), new IntPtr(unchecked((int)0xC0010001)));
                            Thread.Sleep(150);
                        }
                        DiagnosticsLogger.Info("testops", "test.enter_command posted Escape twice.");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLogger.Warn("testops", "Posting Escape failed: " + ex.Message);
                    }
                }, null, delayMs, Timeout.Infinite);
            }
        }
    }
}
