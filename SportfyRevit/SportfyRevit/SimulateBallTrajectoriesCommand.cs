using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Rigidbody physics (shots/serves/kicks), not a rule check — the point
    /// is to validate the FIBA/DIN clearance buffers per sport (see
    /// FIELDS in data.js) against what a real trajectory actually does,
    /// rather than trusting the static buffer distance alone.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SimulateBallTrajectoriesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Simulate Ball Trajectories",
                "Will simulate real ball trajectories (shots, serves, kicks) using rigidbody physics inside the placed layout, checking whether they cross into a neighboring court, over the roof edge, or into circulation space — a more rigorous, physics-based version of the static FIBA/DIN clearance buffers already used per sport.");
    }
}
