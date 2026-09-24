using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The Sportify phase system: three phases, in this order.
    ///   Existing              the building and the roof as they stand (the project's first phase, whatever it is called)
    ///   Design and analysis   what an import from the web app brings: the layout being designed and analysed
    ///   Post analysis         what the analyses ask for afterwards: the dynamic furniture (Kinetics), placed from their results
    ///
    /// Revit's API can neither CREATE a phase nor rename one (found live), so this never adds one: it finds the phases a project already has, says what is missing
    /// and what to rename in Manage > Phasing, and reads and assigns elements to them. A phase is found by its name, else by its place in the order.
    /// </summary>
    internal static class SportifyPhases
    {
        internal const string Existing = "Existing";
        internal const string DesignAndAnalysis = "Design and analysis";
        internal const string PostAnalysis = "Post analysis";
        internal static readonly string[] Names = { Existing, DesignAndAnalysis, PostAnalysis };

        internal sealed class Status
        {
            public Phase? Existing, DesignAndAnalysis, PostAnalysis;
            public List<string> Phases = new();               // what the project has, in order
            public List<string> Missing = new();              // the Sportify phases that no phase stands for
            public List<string> ToRename = new();             // "current name -> Sportify name" a person may agree to
            public bool Complete => Missing.Count == 0 && ToRename.Count == 0;
        }

        static bool Is(Phase p, string name) => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase);

        /// <summary>Which of the three each of the project's phases stands for now, what is missing and what a rename would settle.</summary>
        internal static Status Read(Document doc)
        {
            var s = new Status();
            var phases = new List<Phase>();
            foreach (Phase p in doc.Phases) { phases.Add(p); s.Phases.Add(p.Name); }
            if (phases.Count == 0) { s.Missing.AddRange(Names); return s; }

            s.Existing = phases[0];                                                     // the first phase is always the one that stands for what exists
            s.DesignAndAnalysis = phases.FirstOrDefault(p => Is(p, DesignAndAnalysis));
            s.PostAnalysis = phases.FirstOrDefault(p => Is(p, PostAnalysis));

            var free = phases.Skip(1).Where(p => p != s.DesignAndAnalysis && p != s.PostAnalysis).ToList();
            if (s.DesignAndAnalysis == null)
            {
                if (free.Count > 0) { s.ToRename.Add(free[0].Name + " -> " + DesignAndAnalysis); s.DesignAndAnalysis = free[0]; free.RemoveAt(0); }
                else s.Missing.Add(DesignAndAnalysis);
            }
            if (s.PostAnalysis == null)
            {
                if (free.Count > 0) { s.ToRename.Add(free[0].Name + " -> " + PostAnalysis); s.PostAnalysis = free[0]; }
                else s.Missing.Add(PostAnalysis);
            }
            return s;
        }

        /// <summary>Renames the phases that stand in for Design and analysis / Post analysis (inside a transaction). Returns what it renamed; a phase that refuses is left and named in the list of failures.</summary>
        internal static List<string> Rename(Document doc, Status s, out List<string> failures)
        {
            failures = new List<string>();
            var done = new List<string>();
            foreach (var (phase, name) in new[] { (s.DesignAndAnalysis, DesignAndAnalysis), (s.PostAnalysis, PostAnalysis) })
            {
                if (phase == null || Is(phase, name)) continue;
                var was = phase.Name;
                try { phase.Name = name; done.Add(was + " -> " + name); }
                catch (Exception ex) { failures.Add(was + " could not be renamed: " + ex.Message); }
            }
            return done;
        }

        /// <summary>The phase for a name of the three, or null when the project has none to stand for it.</summary>
        internal static Phase? For(Document doc, string name)
        {
            var s = Read(doc);
            return name == Existing ? s.Existing : name == DesignAndAnalysis ? s.DesignAndAnalysis : name == PostAnalysis ? s.PostAnalysis : null;
        }

        /// <summary>
        /// Puts the elements in a phase (Phase Created) and makes the active view show them: an element created in a phase later than the view's own is not
        /// drawn in it. Inside a transaction. Returns how many elements were set, and says in <paramref name="note"/> why not when the phase does not exist.
        /// </summary>
        internal static int Assign(Document doc, IEnumerable<ElementId> ids, string phaseName, View? activeView, out string note)
        {
            note = "";
            var phase = For(doc, phaseName);
            if (phase == null) { note = "the project has no phase for \"" + phaseName + "\" (Manage > Phasing adds one), so they stay in the phase they were created in"; return 0; }
            var n = 0;
            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el == null || !el.ArePhasesModifiable()) continue;
                try { el.CreatedPhaseId = phase.Id; n++; }
                catch (Exception ex) { note = "an element could not be moved to \"" + phaseName + "\": " + ex.Message; }
            }
            if (activeView != null) ShowInView(doc, activeView, phase);
            return n;
        }

        /// <summary>The active view is put on this phase when its own comes earlier (a plan or a 3D view of an earlier phase would not draw the new elements).</summary>
        internal static void ShowInView(Document doc, View view, Phase phase)
        {
            try
            {
                var p = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (p == null || p.IsReadOnly) return;
                var order = new List<ElementId>();
                foreach (Phase ph in doc.Phases) order.Add(ph.Id);
                var current = order.IndexOf(p.AsElementId());
                var wanted = order.IndexOf(phase.Id);
                if (current >= 0 && wanted > current) p.Set(phase.Id);
            }
            catch (Exception ex) { SportifyLog.Warn("phases", "the view's phase could not be set: " + ex.Message); }
        }
    }
}
