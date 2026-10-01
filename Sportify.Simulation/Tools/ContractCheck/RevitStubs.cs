// Compile-time stand-ins for the few Revit types the commands' signatures mention. ContractCheck never runs Execute(); it only exercises the results parsing,
// publishing and report code. (RevitAPI.dll cannot be loaded outside Revit, and CI has no Revit.)
namespace Autodesk.Revit.Attributes { public enum TransactionMode { Manual, ReadOnly } [AttributeUsage(AttributeTargets.Class)] public sealed class TransactionAttribute : Attribute { public TransactionAttribute(TransactionMode m) { } } }
namespace Autodesk.Revit.DB { public class ElementSet { } public class Document { public string Title => ""; } }
namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded, Failed, Cancelled }
    public class UIDocument { public Autodesk.Revit.DB.Document Document { get; } = new Autodesk.Revit.DB.Document(); }
    public class UIApplication { public IntPtr MainWindowHandle => IntPtr.Zero; public UIDocument? ActiveUIDocument => null; }
    public class ExternalCommandData { public UIApplication Application { get; } = new UIApplication(); }
    public interface IExternalCommand { Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements); }
    [Flags] public enum TaskDialogCommonButtons { None = 0, Close = 8 }
    public enum TaskDialogResult { None, Close, CommandLink1, CommandLink2, CommandLink3, CommandLink4 }
    public enum TaskDialogCommandLinkId { CommandLink1 = 1001, CommandLink2 = 1002, CommandLink3 = 1003, CommandLink4 = 1004 }
    public class TaskDialog
    {
        public TaskDialog(string title) { }
        public string MainInstruction { get; set; } = ""; public string MainContent { get; set; } = ""; public string ExpandedContent { get; set; } = "";
        public TaskDialogCommonButtons CommonButtons { get; set; } public TaskDialogResult DefaultButton { get; set; }
        public void AddCommandLink(TaskDialogCommandLinkId id, string text, string? sub = null) { }
        public TaskDialogResult Show() => TaskDialogResult.Close;
        public static TaskDialogResult Show(string title, string message) => TaskDialogResult.Close;
    }
}
namespace SportfyRevit
{
    // LiveStructure reads the open model (Revit calls): here it hands the layout back unchanged. What it does with what it reads is
    // LiveStructureMerge, which Tools/AddinCheck checks on its own.
    internal static class LiveStructure { internal static string Apply(Autodesk.Revit.DB.Document? doc, string layoutJson, out string note) { note = ""; return layoutJson; } }
    // SportifyDiagramViews draws Revit views: nothing to draw here.
    internal static class SportifyDiagramViews { internal static void Refresh(Autodesk.Revit.DB.Document? doc, SportifyLayout? layout = null) { } }
    // ProjectLayout (ImportedLayoutStore.cs) also reads the layout kept in the project (Revit's extensible storage): here only the web app's is there.
    internal static class ProjectLayout
    {
        internal const string NoLayoutMessage = "No layout of this project to analyse yet.";
        internal static bool TryGet(Autodesk.Revit.DB.Document? doc, out string? json, out string source)
        {
            source = "the web app";
            return RoofBoundaryServer.TryGetLatestCombinedLayout(out json, out _) && json != null;
        }
    }
}
