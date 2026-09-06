using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Starts/stops RoofBoundaryServer alongside Revit itself (so it's
    /// already listening by the time anyone opens the frontend's Combine
    /// tab) and adds a dedicated "Sportify" ribbon tab with the push button
    /// on it — a real top-level tab (not a panel tucked under Add-Ins), so
    /// "Sportify" is what you actually see in the ribbon.
    /// </summary>
    public class SportfyRevitApp : IExternalApplication
    {
        private const string TabName = "Sportify";

        public Result OnStartup(UIControlledApplication application)
        {
            RoofBoundaryServer.Start();

            application.CreateRibbonTab(TabName);
            var panel = application.CreateRibbonPanel(TabName, "Roof Sync");

            var buttonData = new PushButtonData(
                "PushRoofBoundary",
                "Push Roof\nto Sportify",
                typeof(SportfyRevitApp).Assembly.Location,
                typeof(PushRoofBoundaryCommand).FullName)
            {
                ToolTip = "Select a roof or floor and send its footprint to the Sportify web app's Combine tab.",
            };
            panel.AddItem(buttonData);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            RoofBoundaryServer.Stop();
            return Result.Succeeded;
        }
    }
}
