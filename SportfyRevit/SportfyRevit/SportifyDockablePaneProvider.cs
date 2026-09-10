using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Registers the browser pane as a real Revit dockable pane (like the
    /// built-in Properties/Project Browser panels) rather than a floating
    /// WPF window, so it behaves like part of Revit's own UI — dockable,
    /// tabbable, persisted across sessions. Must be registered exactly
    /// once in OnStartup with a GUID that never changes between builds,
    /// or Revit treats a changed GUID as a different pane and orphans the
    /// old registration.
    /// </summary>
    internal class SportifyDockablePaneProvider : IDockablePaneProvider
    {
        public static readonly DockablePaneId PaneId = new(new System.Guid("E7C1F9D2-3A44-4B8E-9F21-6D5C0A1B2E3F"));

        private readonly SportifyBrowserPane _pane = new();

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _pane;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right,
            };
        }
    }
}
