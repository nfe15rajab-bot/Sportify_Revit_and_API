namespace SportfyRevit
{
    /// <summary>
    /// The names of what one of Revit's own templates brings into a project (a BuiltIn...Template class, generated from the template itself): the German BIM template's and the English
    /// multi-discipline template's. A Sportify project is made from one of them, so "Hide Revit templates" knows which items are Revit's, and "Show Revit templates" where to copy them back from.
    /// </summary>
    internal sealed class BuiltInSet
    {
        public string Name { get; }
        /// <summary>Path of the template below Revit's Templates folder.</summary>
        public string TemplateFile { get; }
        public HashSet<string> ViewTemplates { get; }
        public HashSet<string> Filters { get; }
        public HashSet<string> Schedules { get; }
        public HashSet<string> SheetNumbers { get; }
        /// <summary>"ViewType|Name" of each view.</summary>
        public HashSet<string> Views { get; }

        private BuiltInSet(string name, string templateFile, string[] viewTemplates, string[] filters, string[] schedules, string[] sheetNumbers, string[] views)
        {
            Name = name; TemplateFile = templateFile;
            ViewTemplates = new(viewTemplates); Filters = new(filters); Schedules = new(schedules); SheetNumbers = new(sheetNumbers); Views = new(views);
        }

        public static readonly BuiltInSet German = new("German", BuiltInGermanTemplate.TemplateFile, BuiltInGermanTemplate.ViewTemplates, BuiltInGermanTemplate.Filters, BuiltInGermanTemplate.Schedules,
            BuiltInGermanTemplate.SheetNumbers, BuiltInGermanTemplate.Views);

        public static readonly BuiltInSet English = new("English", BuiltInEnglishTemplate.TemplateFile, BuiltInEnglishTemplate.ViewTemplates, BuiltInEnglishTemplate.Filters, BuiltInEnglishTemplate.Schedules,
            BuiltInEnglishTemplate.SheetNumbers, BuiltInEnglishTemplate.Views);

        public static IReadOnlyList<BuiltInSet> All => new[] { German, English };

        public static BuiltInSet For(TemplateLanguage language) => language == TemplateLanguage.De ? German : English;
    }
}
