using System.Globalization;
using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// Which of the .rft files in Revit's family-template folders can be the "Generic Model" template, best first, judged by file name alone.
    ///
    /// The generator builds every family from Revit's Generic Model template. It used to know four English paths (RVT 2024/2025/2026, "English"
    /// and "English-Imperial" folders) and to open a file dialog in the middle of an import when none existed: on a German, French, Spanish...
    /// Revit the template is called something else and lives in another folder, so generation could never work there and the first import
    /// stopped to ask about a file most people cannot name. The names are localised, the host-based variants ("wall based", "face based"...)
    /// must not be taken for the plain one, so this ranks; GenericFamilyTemplateLocator then opens the best candidates in Revit and keeps the
    /// first whose category really is Generic Models, which does not depend on any language.
    ///
    /// Free of Revit types on purpose: AddinCheck tests it on the names of the templates of several languages.
    /// </summary>
    internal static class FamilyTemplateRanking
    {
        /// <summary>The name of the plain Generic Model template in the languages Revit ships in, after Normalise (lower case, no accents).</summary>
        private static readonly string[] GenericModel =
        {
            "generic model",              // English
            "allgemeines modell",         // German
            "modele generique",           // French
            "modelo generico",            // Spanish, Portuguese
            "modello generico",           // Italian
            "generiek model",             // Dutch
            "model ogolny",               // Polish
            "obecny model",               // Czech
            "altalanos modell",           // Hungarian
            "常规模型", "一般モデル", "일반 모델", "типовая модель", "обобщенная модель", // Chinese, Japanese, Korean, Russian
        };

        /// <summary>Words that mean "this variant hangs on something": a wall, a face, a line, a work plane, a ceiling, a floor; or is adaptive / pattern based.</summary>
        private static readonly string[] HostWords =
        {
            "based", "wall", "face", "line", "work plane", "workplane", "ceiling", "floor", "adaptive", "pattern",     // English
            "basiert", "wand", "flache", "linie", "arbeitsebene", "decke", "boden", "adaptiv", "muster",              // German (flächenbasiert -> flachenbasiert)
            "base sur", "sur un", "sur une", "adaptatif", "mur", "plafond",                                         // French
            "basado", "adaptativo", "pared", "techo",                                                              // Spanish
            "basato", "adattivo", "parete", "soffitto",                                                            // Italian
            "baseado", "parede",                                                                                    // Portuguese
        };

        /// <summary>Drops what cannot be the plain template and orders the rest: metric first, then the shortest name (the plain one has the fewest extra words).</summary>
        public static List<string> Rank(IEnumerable<string> rftPaths)
        {
            var scored = new List<(string Path, int Score, int Length)>();
            foreach (var path in rftPaths)
            {
                var name = Normalise(System.IO.Path.GetFileNameWithoutExtension(path));
                if (!GenericModel.Any(g => name.Contains(Normalise(g)))) continue;
                if (HostWords.Any(w => ContainsWord(name, w) && !GenericModel.Any(g => Normalise(g).Contains(w)))) continue;

                int score = 100;
                if (name.Contains("metric") || name.Contains("metrisch") || name.Contains("metrique") || name.Contains("metrico") || name.Contains("metrica")) score += 20;
                // A folder called Imperial (English-Imperial) is the same template in feet: usable, but the metric one is preferred.
                if (path.IndexOf("imperial", StringComparison.OrdinalIgnoreCase) >= 0 && score < 120) score -= 10;
                scored.Add((path, score, name.Length));
            }
            return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Length).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase).Select(s => s.Path).ToList();
        }

        /// <summary>Lower case, accents removed, separators turned into spaces: "Modèle_générique-métrique" becomes "modele generique metrique".</summary>
        internal static string Normalise(string text)
        {
            var decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c == '_' || c == '-' || c == ',' || c == '.' || c == '(' || c == ')' ? ' ' : char.ToLowerInvariant(c));
            }
            return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>The word (or phrase) occurs in the name, as part of a word too: "wandbasiert" holds "basiert" and "wand".</summary>
        private static bool ContainsWord(string name, string word) => name.Contains(word);
    }
}
