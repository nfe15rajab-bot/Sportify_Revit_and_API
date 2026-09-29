using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The structure an analysis stands on, read from the open Revit model at the moment it runs.
    ///
    /// The layout an analysis gets (RoofBoundaryServer.TryGetLatestCombinedLayout) carries the grid, columns, beams and walls only as the web app last
    /// received them with a Push to Sportify. A layout that lost them on the way (a session loaded from a file, a push without the Structure part, a
    /// grid drawn after the push) was analysed on an assumed regular 8.4 m grid, with the real one standing in the open model (the professor's point,
    /// 2026-09-29: "Unity should pull the structure directly from the grid in Revit, not assume it"). So the structural, dynamic and sun analyses and
    /// Send All to Web App read it here, under the active pushed roof, with the same code a push uses (PushRoofBoundaryCommand.Build, Structure part
    /// only: read-only, nothing in the model changes). What is done with it is LiveStructureMerge (Revit-free, checked by Tools/AddinCheck).
    /// </summary>
    internal static class LiveStructure
    {
        /// <summary>The layout with the model's structure under the active roof in place of its own, and a sentence for the dialog saying what happened ("" when nothing was read).</summary>
        internal static string Apply(Document? doc, string layoutJson, out string note)
        {
            note = "";
            try
            {
                var roofId = RoofBoundaryServer.ActiveRoofId;
                if (doc == null || roofId == null || roofId.Value == 0) return layoutJson;

                var element = doc.GetElement(new ElementId(roofId.Value));
                if (element == null)
                {
                    note = "Structure: the pushed roof is not in the open model, so the structure the layout carries is used.";
                    return layoutJson;
                }

                var built = PushRoofBoundaryCommand.Build(doc, element, RoofPushScope.Structure, null, out _);
                if (built == null) return layoutJson;
                return LiveStructureMerge.Merge(layoutJson, built.Json, built.LengthM, built.WidthM, out note);
            }
            catch (Exception ex)
            {
                note = $"Structure: couldn't read the model ({ex.Message}), so the structure the layout carries is used.";
                return layoutJson;
            }
        }
    }
}
