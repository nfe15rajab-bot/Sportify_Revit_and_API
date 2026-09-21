using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    internal enum WorksharingChoice
    {
        /// <summary>The project is already workshared: nothing to decide.</summary>
        AlreadyWorkshared,
        /// <summary>The person agreed to turn worksharing on (it cannot be turned off again).</summary>
        Enable,
        /// <summary>Import without worksets: nothing about the project changes except the imported elements.</summary>
        NoWorksets,
        Cancel,
    }

    /// <summary>
    /// Sportify puts its geometry on worksets (Sports, Gardens, Combine), and worksets need worksharing. It used to switch worksharing on for any
    /// project that was not workshared, without a word, and that cannot be undone: it turns a single-user project into a central-model project.
    /// Now it asks first, once per project per Revit session, and offers to import without worksets instead. A person who is not at the machine
    /// to answer never gets worksharing switched on for them.
    /// </summary>
    internal static class WorksharingConsent
    {
        private static readonly Dictionary<string, WorksharingChoice> Remembered = new(StringComparer.OrdinalIgnoreCase);

        internal static string KeyOf(Document doc) => string.IsNullOrEmpty(doc.PathName) ? "untitled::" + doc.Title : doc.PathName;

        public static WorksharingChoice Decide(Document doc, bool interactive)
        {
            if (doc.IsWorkshared) return WorksharingChoice.AlreadyWorkshared;
            var key = KeyOf(doc);
            if (Remembered.TryGetValue(key, out var earlier)) return earlier;
            if (!interactive) return WorksharingChoice.NoWorksets;

            var ask = new TaskDialog("Sportify")
            {
                MainInstruction = "Put Sportify's geometry on worksets?",
                MainContent = "Sportify places what it builds on the worksets Sports, Gardens and Combine, and worksets need worksharing. " +
                              "This project is not workshared. Turning worksharing on cannot be undone: it makes this a workshared project that has to be saved as a central model.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Turn worksharing on and use the worksets", "Sports, Gardens and Combine are created. This cannot be undone.");
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Import without worksets", "The project stays as it is; only the imported elements are added.");
            // After the links exist: Revit throws "Corresponding button not found" for a default that is not there yet (the live import found this).
            ask.DefaultButton = TaskDialogResult.CommandLink2;

            var choice = ask.Show() switch
            {
                TaskDialogResult.CommandLink1 => WorksharingChoice.Enable,
                TaskDialogResult.CommandLink2 => WorksharingChoice.NoWorksets,
                _ => WorksharingChoice.Cancel,
            };
            if (choice != WorksharingChoice.Cancel) Remembered[key] = choice;
            SportifyLog.Info("worksharing", "project \"" + doc.Title + "\" is not workshared; the person chose: " + choice);
            return choice;
        }
    }
}
