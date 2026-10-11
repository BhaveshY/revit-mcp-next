using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Win32 calls used by the wake-up pump, the UI state monitor and the dialog tools (D2 §5.2, SPEC §8.5). All of them
    /// are safe on background threads and never call the Revit API. Calls that would send a message to the Revit UI
    /// thread (GetWindowText, SendMessage) are avoided or bounded: window text is read with InternalGetWindowText, and
    /// messages to dialogs are posted or sent with SendMessageTimeout(SMTO_ABORTIFHUNG). Works on net48 and net10.
    /// </summary>
    internal static class NativeMethods
    {
        internal const uint WM_NULL = 0x0000;
        internal const uint WM_CLOSE = 0x0010;
        internal const uint WM_KEYDOWN = 0x0100;
        internal const uint WM_KEYUP = 0x0101;
        internal const uint WM_COMMAND = 0x0111;
        internal const uint WM_USER = 0x0400;
        /// <summary>TaskDialog: click a button by id (wParam = id).</summary>
        internal const uint TDM_CLICK_BUTTON = WM_USER + 102;
        internal const uint BM_CLICK = 0x00F5;
        internal const int VK_ESCAPE = 0x1B;

        internal const uint GA_PARENT = 1;
        internal const uint GA_ROOT = 2;
        internal const uint GA_ROOTOWNER = 3;
        internal const uint GW_OWNER = 4;

        internal const uint SMTO_ABORTIFHUNG = 0x0002;
        internal const uint SMTO_BLOCK = 0x0001;

        internal const int IDOK = 1;
        internal const int IDCANCEL = 2;
        internal const int IDABORT = 3;
        internal const int IDRETRY = 4;
        internal const int IDIGNORE = 5;
        internal const int IDYES = 6;
        internal const int IDNO = 7;
        internal const int IDCLOSE = 8;

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        /// <summary>False when an owned modal dialog disabled the window.</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowEnabled(IntPtr hWnd);

        /// <summary>True when the window's thread has not pumped messages for about 5 s.</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsHungAppWindow(IntPtr hWnd);

        /// <summary>Minimized.</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>The most recently active popup owned by the window (the window itself when there is none).</summary>
        [DllImport("user32.dll")]
        internal static extern IntPtr GetLastActivePopup(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        /// <summary>Reads the window text without sending WM_GETTEXT (GetWindowText would block on a busy UI thread).</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int InternalGetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern int GetDlgCtrlID(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const long WS_EX_TOOLWINDOW = 0x00000080L;
        internal const long BS_TYPEMASK = 0x0000000FL;
        internal const long BS_PUSHBUTTON = 0x0L;
        internal const long BS_DEFPUSHBUTTON = 0x1L;
        internal const long BS_COMMANDLINK = 0xEL;
        internal const long BS_DEFCOMMANDLINK = 0xFL;
        internal const int SW_MINIMIZE = 6;
        internal const int SW_RESTORE = 9;
        internal const int BN_CLICKED = 0;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int index);

        /// <summary>Asynchronous ShowWindow (never waits for the target thread).</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindowAsync(IntPtr hWnd, int cmdShow);

        /// <summary>GetWindowLong(Ptr) without a message to the owning thread; 0 on failure.</summary>
        internal static long WindowLong(IntPtr hWnd, int index)
        {
            try
            {
                return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index).ToInt64() : GetWindowLong32(hWnd, index);
            }
            catch
            {
                return 0;
            }
        }

        internal static bool IsToolWindow(IntPtr hWnd)
        {
            return (WindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0;
        }

        /// <summary>True for push buttons and command links (not check boxes, radio buttons or group boxes).</summary>
        internal static bool IsPushButton(IntPtr hWnd)
        {
            long type = WindowLong(hWnd, GWL_STYLE) & BS_TYPEMASK;
            return type == BS_PUSHBUTTON || type == BS_DEFPUSHBUTTON || type == BS_COMMANDLINK || type == BS_DEFCOMMANDLINK;
        }

        /// <summary>MAKEWPARAM(low, high).</summary>
        internal static IntPtr MakeWParam(int low, int high)
        {
            return new IntPtr((low & 0xFFFF) | ((high & 0xFFFF) << 16));
        }

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentProcessId();

        // ------------------------------------------------------------------------------------------------------------
        // Helpers (never throw)
        // ------------------------------------------------------------------------------------------------------------

        internal static readonly uint CurrentPid = SafeCurrentPid();

        private static uint SafeCurrentPid()
        {
            try
            {
                return GetCurrentProcessId();
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Window text through InternalGetWindowText (no message to the owning thread); empty on failure.</summary>
        internal static string WindowText(IntPtr hWnd, int max = 512)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            try
            {
                var builder = new StringBuilder(max + 1);
                int length = InternalGetWindowText(hWnd, builder, builder.Capacity);
                return length <= 0 ? string.Empty : builder.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string ClassName(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            try
            {
                var builder = new StringBuilder(257);
                int length = GetClassName(hWnd, builder, builder.Capacity);
                return length <= 0 ? string.Empty : builder.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static bool SafeIsWindow(IntPtr hWnd)
        {
            try
            {
                return hWnd != IntPtr.Zero && IsWindow(hWnd);
            }
            catch
            {
                return false;
            }
        }

        internal static uint ProcessOf(IntPtr hWnd)
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                return pid;
            }
            catch
            {
                return 0;
            }
        }

        internal static uint ThreadOf(IntPtr hWnd)
        {
            try
            {
                return GetWindowThreadProcessId(hWnd, out _);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>All descendant windows (EnumChildWindows walks the whole child tree; no messages are sent).</summary>
        internal static List<IntPtr> Descendants(IntPtr parent, int max = 512)
        {
            var list = new List<IntPtr>();
            if (parent == IntPtr.Zero) return list;
            try
            {
                EnumChildWindows(parent, (hWnd, _) =>
                {
                    list.Add(hWnd);
                    return list.Count < max;
                }, IntPtr.Zero);
            }
            catch
            {
                // Best effort.
            }
            return list;
        }

        /// <summary>Visible top-level windows of this process (no messages are sent).</summary>
        internal static List<IntPtr> TopLevelWindowsOfThisProcess(int max = 512)
        {
            var list = new List<IntPtr>();
            uint pid = CurrentPid;
            try
            {
                EnumWindows((hWnd, _) =>
                {
                    if (ProcessOf(hWnd) == pid) list.Add(hWnd);
                    return list.Count < max;
                }, IntPtr.Zero);
            }
            catch
            {
                // Best effort.
            }
            return list;
        }

        /// <summary>True when <paramref name="hWnd"/> is owned (directly or through its owner chain) by <paramref name="owner"/>.</summary>
        internal static bool IsOwnedBy(IntPtr hWnd, IntPtr owner)
        {
            if (hWnd == IntPtr.Zero || owner == IntPtr.Zero || hWnd == owner) return false;
            try
            {
                IntPtr current = hWnd;
                for (int depth = 0; depth < 16; depth++)
                {
                    IntPtr next = GetWindow(current, GW_OWNER);
                    if (next == IntPtr.Zero) break;
                    if (next == owner) return true;
                    current = next;
                }
                return GetAncestor(hWnd, GA_ROOTOWNER) == owner;
            }
            catch
            {
                return false;
            }
        }

        internal static bool Post(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return hWnd != IntPtr.Zero && PostMessage(hWnd, msg, wParam, lParam);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>SendMessageTimeout with SMTO_ABORTIFHUNG; false when it timed out or failed.</summary>
        internal static bool SendWithTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint timeoutMs)
        {
            try
            {
                IntPtr ok = SendMessageTimeout(hWnd, msg, wParam, lParam, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out _);
                return ok != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The focused window of a GUI thread (zero when unknown).</summary>
        internal static IntPtr FocusOfThread(uint threadId)
        {
            try
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
                return GetGUIThreadInfo(threadId, ref info) ? (info.hwndFocus != IntPtr.Zero ? info.hwndFocus : info.hwndActive) : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
