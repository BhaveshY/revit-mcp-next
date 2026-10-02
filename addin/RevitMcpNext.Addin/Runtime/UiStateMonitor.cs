using System;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Samples the Revit main window state off the UI thread (D2 §8.5). Wave-1 stub: reports defaults only
    /// (foreground false, not minimized, enabled, not hung, no popup). P-REL-ADDIN implements the 500 ms sampler with
    /// NativeMethods and pushes changes through DocumentRegistry.SetUi.
    /// </summary>
    internal sealed class UiStateMonitor : IDisposable
    {
        private readonly DocumentRegistry _registry;
        private IntPtr _mainWindow = IntPtr.Zero;

        public UiStateMonitor(DocumentRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>The Revit main window handle (captured at startup and refreshed from Execute/Idling).</summary>
        public IntPtr MainWindow => _mainWindow;

        public void RefreshHandle(IntPtr handle)
        {
            if (handle != IntPtr.Zero) _mainWindow = handle;
        }

        public void Start()
        {
            _registry?.SetUi(new SnapshotUi());
        }

        /// <summary>The current UI health (defaults in wave 1).</summary>
        public UiHealth GetHealth()
        {
            SnapshotUi ui = _registry?.Ui ?? new SnapshotUi();
            return new UiHealth
            {
                MainWindowEnabled = ui.MainWindowEnabled,
                Hung = ui.Hung,
                Minimized = ui.Minimized,
                Foreground = ui.Foreground,
                Popup = ui.Popup
            };
        }

        public void Dispose()
        {
        }
    }
}
