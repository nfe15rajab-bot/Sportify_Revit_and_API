using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>The kinds of dynamic unit Kinetics can make: all of them the same two adaptive families (a bar and a membrane), placed on different points.</summary>
    internal enum KineticKind { Overhead, Slats, Fins, Sail, Fence }

    internal sealed class KineticKindInfo
    {
        public KineticKind Kind;
        public string Key = "", Label = "", Hint = "";
        public bool Built = true;
        public override string ToString() => Label;
    }

    internal static class KineticKinds
    {
        internal static readonly KineticKindInfo[] All =
        {
            new KineticKindInfo { Kind = KineticKind.Overhead, Key = "overhead", Label = "Overhead louvre (pergola)", Hint = "on the pergola(s) the Sun & Shade analysis recommends" },
            new KineticKindInfo { Kind = KineticKind.Slats, Key = "slats", Label = "Vertical slat screen (railing or wall)", Hint = "on the railings or walls you select: horizontal slats, tipped together by one rod" },
            new KineticKindInfo { Kind = KineticKind.Fins, Key = "fins", Label = "Vertical fin screen (railing or wall)", Hint = "on the railings or walls you select: fins turning about vertical axes" },
            new KineticKindInfo { Kind = KineticKind.Sail, Key = "sail", Label = "Tensile sail on movable pillars (ground rails)", Hint = "on the shade sail(s) the Sun & Shade analysis recommends: masts on carriages that slide on ground rails to grow or shrink the shade, a rectangle or a triangle, placed against the nearest garden and field" },
            new KineticKindInfo { Kind = KineticKind.Fence, Key = "fence", Label = "Roller fence (roof edge)", Hint = "on the roof edges the Ball Trajectory analysis fences: a curtain on guide rails, deployed only when needed" },
        };

        internal static KineticKindInfo Get(KineticKind kind) => Array.Find(All, k => k.Kind == kind) ?? All[0];
        internal static KineticKindInfo? ByKey(string? key) => Array.Find(All, k => k.Key == key);
    }

    /// <summary>
    /// The roof's plan (x right, y DOWN, metres, the frame of the last pushed layout) against the model: points and directions both ways. Every kinetic unit is planned in a
    /// local frame (KineticUnits) and put in the model through a UnitFrame; this is where those frames come from. All in metres, in the model's own axes.
    /// </summary>
    internal static class KineticsPlan
    {
        const double Metre = 0.3048;

        static V3 Model(double planX, double planY, double zM)
        {
            var (x, y) = SportifyLayoutBuilder.PlanToWorldFt(planX, planY);
            return new V3(x * Metre, y * Metre, zM);
        }

        /// <summary>The elevation of the roof's top face, metres.</summary>
        internal static double RoofZM => SportifyLayoutBuilder.CurrentOriginZFt * Metre;

        /// <summary>A plan point, at a height above the roof.</summary>
        internal static V3 ToWorld(double planX, double planY, double aboveRoofM = 0) => Model(planX, planY, RoofZM + aboveRoofM);

        /// <summary>The model directions of the plan's x axis and of its y axis (which points DOWN the plan, so against the model's own Y for a roof square to it).</summary>
        internal static (V3 X, V3 Y) PlanAxes()
        {
            var o = Model(0, 0, 0);
            return ((Model(1, 0, 0) - o).Unit(), (Model(0, 1, 0) - o).Unit());
        }

        /// <summary>A model direction as plan components (x right, y down).</summary>
        internal static (double X, double Y) DirToPlan(V3 world)
        {
            var (ex, ey) = PlanAxes();
            return (world.Dot(ex), world.Dot(ey));
        }

        internal static V3 DirFromPlan(double planX, double planY)
        {
            var (ex, ey) = PlanAxes();
            return ex * planX + ey * planY;
        }

        /// <summary>A plan direction in a unit's own frame (its x and y components; the frame's z is up, so a horizontal plan direction has none).</summary>
        internal static V3 PlanDirToLocal(UnitFrame frame, double planX, double planY)
        {
            var w = DirFromPlan(planX, planY);
            return new V3(w.Dot(frame.X), w.Dot(frame.Y), 0);
        }

        /// <summary>A frame that stands on the ground plane: x as given (horizontal), y = z cross x (so the frame is right-handed), z up.</summary>
        internal static UnitFrame Upright(V3 origin, V3 x)
        {
            var hx = new V3(x.X, x.Y, 0).Unit();
            return new UnitFrame { Origin = origin, X = hx, Y = V3.UnitZ.Cross(hx), Z = V3.UnitZ };
        }

        internal static V3 FromFeet(XYZ p) => new V3(p.X * Metre, p.Y * Metre, p.Z * Metre);
    }

    /// <summary>One place a dynamic unit goes: what it is, its frame in the model, its extent, and the analysis result (or the model element) that asked for it.</summary>
    internal sealed class KineticHost
    {
        public KineticKind Kind;
        public string Key = "", Name = "", From = "";
        public UnitFrame Frame;                                    // in the model, metres
        public double LengthM, DepthM, HeightM;                    // its extent along the frame's x, y and z
        public double PlanX, PlanY;                                // where the analysis put it (plan, m); 0 when it does not come from a plan piece
        public SunEquipmentDto? Piece;                             // overhead, sail
        public RoofFenceDto? Fence;                                // fence
        public string? Edge;
        public (double X, double Y) AxisPlan;                      // the blades' axis (overhead) or the run of the screen, in plan directions
        public (double X, double Y) NormalPlan;                    // the outward normal (screens, fences), in plan directions
        public ElementId? HostElement;
        public string? Note;
        public bool Triangle;                                      // sail: a right triangle on an L of tracks (else a rectangle on two parallel tracks)
        public double AnchorSide;                                  // sail: > 0 the masts at x = 0 stay put and the others run out; 0 they run out from the middle
        public string SiteNote = "";                               // what the sail was placed against: the nearest garden and field
    }

    /// <summary>The shape of a sail on ground rails: chosen, or left to the site (a triangle beside a garden, a rectangle elsewhere).</summary>
    internal enum SailShapeChoice { Auto, Rectangle, Triangle }

    /// <summary>Finds the hosts of each kind: pieces of the Sun &amp; Shade result, railings and walls of the model, the ball analysis's fences.</summary>
    internal static class KineticsHosts
    {
        /// <summary>The overhead pergolas or the sails of a sun and shade result: a rectangle on the roof, its blades (or its masts) standing at the piece's height.</summary>
        internal static List<KineticHost> FromPieces(KineticKind kind, IEnumerable<SunEquipmentDto> pieces, KineticEnvironment? env = null, SailShapeChoice shape = SailShapeChoice.Auto, IReadOnlyList<SiteRect>? site = null)
        {
            var list = new List<KineticHost>();
            var n = 0;
            var (ex, _) = KineticsPlan.PlanAxes();
            foreach (var p in pieces)
            {
                n++;
                if (kind == KineticKind.Sail) { list.Add(SailHost(p, n, env ?? new KineticEnvironment(), shape, site ?? Array.Empty<SiteRect>())); continue; }
                // local y runs from the piece's lower edge up the plan (the frame is right-handed), so the origin is the lower left corner
                var origin = KineticsPlan.ToWorld(p.XM, p.YM + p.DepthM);
                var frame = KineticsPlan.Upright(origin, ex);
                var axis = KineticsPlan.DirToPlan(frame.Y);
                list.Add(new KineticHost
                {
                    Kind = kind, Key = (kind == KineticKind.Sail ? "sail_" : "pergola_") + n, Name = p.Key == "canopy" ? (p.Name ?? "Solid canopy") + " (louvre pergola)" : (p.Name ?? kind.ToString()),
                    From = "the Sun & Shade result, at " + p.XM.ToString("0.#") + ", " + p.YM.ToString("0.#") + " m of the roof plan",
                    Frame = frame, LengthM = p.WidthM, DepthM = p.DepthM, HeightM = p.HeightM > 0.5 ? p.HeightM : 2.6,
                    PlanX = p.XM, PlanY = p.YM, Piece = p, AxisPlan = axis,
                });
            }
            return list;
        }

        /// <summary>
        /// A sail on ground rails at a piece of the sun and shade result. The rails run along the axis the shadow sweeps across through the day (the plan axis closest to it), so the masts
        /// run out along the direction the shade has to follow; a garden beside the piece is kept in mind: the masts that stay put are those farthest from it and the sail runs in away from
        /// it. A triangle sits with its right angle at the corner farthest from the garden and its slanted edge facing the garden. Local x is along the rails, y across, right-handed.
        /// </summary>
        static KineticHost SailHost(SunEquipmentDto p, int n, KineticEnvironment env, SailShapeChoice shape, IReadOnlyList<SiteRect> site)
        {
            var piece = new SiteRect { X = p.XM, Y = p.YM, W = p.WidthM, H = p.DepthM };
            var garden = KineticsSite.Nearest(site, true, piece, out var gardenGap, out var gx, out var gy);
            var field = KineticsSite.Nearest(site, false, piece, out var fieldGap, out _, out _);
            var triangle = shape == SailShapeChoice.Triangle || (shape == SailShapeChoice.Auto && garden != null && gardenGap < 4.0);

            // the plan directions (x right, y down) as unit vectors and their model directions
            V3 Dir(double px, double py) => KineticsPlan.DirFromPlan(px, py).Unit();
            var corners = new[] { (x: p.XM, y: p.YM), (x: p.XM + p.WidthM, y: p.YM), (x: p.XM + p.WidthM, y: p.YM + p.DepthM), (x: p.XM, y: p.YM + p.DepthM) };
            UnitFrame frame; double w, d, anchor = 0;
            string layoutNote;
            if (!triangle)
            {
                var sun = SunModel.SunAt(env.LatitudeDeg, SunModel.DayOfYear[0], (SunModel.WindowFromH + SunModel.WindowToH) / 2.0);
                double tx, ty; SunModel.TowardSun(sun, env.NorthDeg, out tx, out ty);
                var sweepX = -ty; var sweepY = tx;                                              // the shadow sweeps across the sun's bearing
                var axisIsX = Math.Abs(sweepX) >= Math.Abs(sweepY);
                double sign = 1;
                if (garden != null)
                {
                    var along = axisIsX ? gx : gy;
                    if (Math.Abs(along) > 0.3) { sign = along >= 0 ? 1 : -1; anchor = 1; }       // the garden lies along the rails: it is at +x, the masts at x = 0 stay
                }
                double xpx = axisIsX ? sign : 0, xpy = axisIsX ? 0 : sign;
                var xWorld = Dir(xpx, xpy);
                var yWorld = V3.UnitZ.Cross(xWorld).Unit();
                var yp = KineticsPlan.DirToPlan(yWorld);
                // the corner with the least x and the least y in the sail's own axes is where its origin sits
                var origin = corners.OrderBy(c => c.x * xpx + c.y * xpy + c.x * yp.X + c.y * yp.Y).First();
                frame = KineticsPlan.Upright(KineticsPlan.ToWorld(origin.x, origin.y), xWorld);
                w = axisIsX ? p.WidthM : p.DepthM; d = axisIsX ? p.DepthM : p.WidthM;
                layoutNote = "two parallel tracks along the " + (axisIsX ? "plan's x" : "plan's y") + " axis (the way the shadow sweeps)" + (anchor > 0 ? ", the masts at the end away from the garden stay put" : ", the masts run out from the middle");
            }
            else
            {
                // the corner farthest from the garden (else the lower left one) is the right angle; the legs run along the piece's edges into the piece
                (double x, double y) f = corners[3];
                if (garden != null) f = corners.OrderByDescending(c => (c.x - garden.CentreX) * (c.x - garden.CentreX) + (c.y - garden.CentreY) * (c.y - garden.CentreY)).First();
                double sx = Math.Abs(f.x - p.XM) < 1e-9 ? 1 : -1, sy = Math.Abs(f.y - p.YM) < 1e-9 ? 1 : -1;
                var d1 = Dir(sx, 0); var d2 = Dir(0, sy);
                var xIsPlanX = V3.UnitZ.Cross(d1).Dot(d2) > 0;                                   // the leg whose left-hand normal points along the other leg is the x axis
                var xWorld = xIsPlanX ? d1 : d2;
                frame = KineticsPlan.Upright(KineticsPlan.ToWorld(f.x, f.y), xWorld);
                w = xIsPlanX ? p.WidthM : p.DepthM; d = xIsPlanX ? p.DepthM : p.WidthM;
                layoutNote = "an L of two tracks meeting at the right angle, which stays put; the slanted edge faces " + (garden != null ? "the garden" : "away from the corner");
            }
            var site1 = new List<string>();
            if (garden != null) site1.Add("garden " + (garden.Label.Length > 0 ? "\"" + garden.Label + "\" " : "") + gardenGap.ToString("0.#") + " m away");
            if (field != null) site1.Add("field " + (field.Label.Length > 0 ? "\"" + field.Label + "\" " : "") + fieldGap.ToString("0.#") + " m away");
            var siteNote = site1.Count > 0 ? "beside " + string.Join(" and ", site1) : "no garden or field in the layout to place it against";
            return new KineticHost
            {
                Kind = KineticKind.Sail, Key = (triangle ? "sailtri_" : "sail_") + n, Name = (p.Name ?? "Shade sail") + (triangle ? " (triangle)" : " (rectangle)"),
                From = "the Sun & Shade result, at " + p.XM.ToString("0.#") + ", " + p.YM.ToString("0.#") + " m of the roof plan; " + siteNote + "; " + layoutNote,
                Frame = frame, LengthM = w, DepthM = d, HeightM = p.HeightM > 0.5 ? p.HeightM : 3.5, PlanX = p.XM, PlanY = p.YM, Piece = p,
                AxisPlan = KineticsPlan.DirToPlan(frame.X), Triangle = triangle, AnchorSide = anchor, SiteNote = siteNote,
            };
        }

        /// <summary>
        /// A piece the person places by hand (the analysis recommended none): the centre is a point picked in the active view, the size the catalogue's own. The point is turned into the
        /// roof's plan coordinates through the plan's axes, so it lands where it was picked on a roof turned against the model. Null when the pick was cancelled.
        /// </summary>
        internal static SunEquipmentDto? PickPiece(UIDocument uidoc, KineticKind kind, EquipmentType type)
        {
            XYZ picked;
            try { picked = uidoc.Selection.PickPoint("Pick the centre of the " + (kind == KineticKind.Sail ? "sail" : "louvre pergola")); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
            var p = KineticsPlan.FromFeet(picked);
            var origin = KineticsPlan.ToWorld(0, 0);
            var (ex, ey) = KineticsPlan.PlanAxes();
            var d = p - origin;
            double cx = d.Dot(ex), cy = d.Dot(ey);
            double w = type.Sizes[0][0], depth = type.Sizes[0][1];
            return new SunEquipmentDto
            {
                Key = type.Key, Name = (kind == KineticKind.Sail ? "Shade sail" : "Louvre pergola") + " (placed by hand)",
                XM = Math.Round(cx - w / 2, 2), YM = Math.Round(cy - depth / 2, 2), WidthM = w, DepthM = depth, HeightM = type.HeightM,
            };
        }

        /// <summary>The roller fences the ball analysis proposes: a stretch of a roof edge, its outward normal, standing on the roof.</summary>
        internal static List<KineticHost> FromFences(IEnumerable<RoofFenceDto> fences, double roofLengthM, double roofWidthM)
        {
            var list = new List<KineticHost>();
            var n = 0;
            foreach (var f in fences)
            {
                n++;
                double x0, y0, x1, y1, nx, ny;
                switch (f.Edge)
                {
                    case "top": x0 = f.FromM; x1 = f.ToM; y0 = y1 = 0; nx = 0; ny = -1; break;
                    case "bottom": x0 = f.FromM; x1 = f.ToM; y0 = y1 = roofWidthM; nx = 0; ny = 1; break;
                    case "left": y0 = f.FromM; y1 = f.ToM; x0 = x1 = 0; nx = -1; ny = 0; break;
                    case "right": y0 = f.FromM; y1 = f.ToM; x0 = x1 = roofLengthM; nx = 1; ny = 0; break;
                    default: continue;
                }
                var a = KineticsPlan.ToWorld(x0, y0);
                var b = KineticsPlan.ToWorld(x1, y1);
                var outward = KineticsPlan.DirFromPlan(nx, ny).Unit();
                var run = outward.Cross(V3.UnitZ).Unit();                 // z cross run = outward: right-handed
                var origin = (b - a).Dot(run) >= 0 ? a : b;
                var frame = KineticsPlan.Upright(origin, run);
                list.Add(new KineticHost
                {
                    Kind = KineticKind.Fence, Key = "fence_" + f.Edge + "_" + n, Name = "Roller fence, " + f.Edge + " edge",
                    From = "the Ball Trajectory result: " + f.Edge + " edge from " + f.FromM.ToString("0.#") + " to " + f.ToM.ToString("0.#") + " m",
                    Frame = frame, LengthM = Math.Abs(f.ToM - f.FromM), DepthM = 0.3, HeightM = f.HeightM > 0.5 ? f.HeightM : 3.0,
                    Fence = f, Edge = f.Edge, NormalPlan = (nx, ny),
                });
            }
            return list;
        }

        sealed class ScreenFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Wall || e is Railing;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        /// <summary>A wall's location curve, or the runs of a railing's path.</summary>
        static Curve[] PathOf(Element e)
        {
            if (e is Railing railing) return railing.GetPath().ToArray();
            var curve = (e.Location as LocationCurve)?.Curve;
            return curve == null ? Array.Empty<Curve>() : new[] { curve };
        }

        /// <summary>
        /// Railings and walls of the model as hosts of a vertical screen: the selection, else the person picks them. A straight run of a wall or of a railing's path is
        /// one host; a curved one is taken as its chord (and says so). The screen faces away from the roof's centre, the side the sun has to come from to matter.
        /// Returns null when the person cancelled the pick; the list is empty (with the reason) when nothing usable was chosen.
        /// </summary>
        internal static List<KineticHost>? FromSelection(UIDocument uidoc, KineticKind kind, out string problem)
        {
            problem = "";
            var doc = uidoc.Document;
            var picked = uidoc.Selection.GetElementIds().Select(doc.GetElement).Where(e => e is Wall || e is Railing).ToList();
            if (picked.Count == 0)
            {
                try
                {
                    var refs = uidoc.Selection.PickObjects(ObjectType.Element, new ScreenFilter(),
                        "Pick the railings or walls to fit with the " + (kind == KineticKind.Fins ? "fin" : "slat") + " screen, then press Finish");
                    picked = refs.Select(r => doc.GetElement(r.ElementId)).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
            }
            if (picked.Count == 0) { problem = "No railing or wall was chosen."; return new List<KineticHost>(); }
            return FromElements(picked, kind, out problem);
        }

        /// <summary>The hosts of the given walls and railings (the part of <see cref="FromSelection"/> that needs no person, so a test can run it).</summary>
        internal static List<KineticHost> FromElements(IEnumerable<Element> picked, KineticKind kind, out string problem)
        {
            problem = "";
            var (roofL, roofW) = SportifyLayoutBuilder.CurrentRoofSizeM;
            V3? centre = roofL > 0 && roofW > 0 ? KineticsPlan.ToWorld(roofL / 2, roofW / 2) : null;
            var list = new List<KineticHost>();
            var skipped = 0; var curved = false;
            foreach (var e in picked)
            {
                var bb = e.get_BoundingBox(null);
                if (bb == null) { skipped++; continue; }
                var baseZ = bb.Min.Z * 0.3048;
                var height = Math.Max(0.5, (bb.Max.Z - bb.Min.Z) * 0.3048);
                var curves = PathOf(e);
                var n = 0;
                foreach (var curve in curves)
                {
                    var a = KineticsPlan.FromFeet(curve.GetEndPoint(0)); var b = KineticsPlan.FromFeet(curve.GetEndPoint(1));
                    a.Z = baseZ; b.Z = baseZ;
                    if (!(curve is Line)) curved = true;
                    var len = new V3(b.X - a.X, b.Y - a.Y, 0).Length;
                    if (len < 0.3) { skipped++; continue; }
                    n++;
                    var along = new V3(b.X - a.X, b.Y - a.Y, 0).Unit();
                    var mid = (a + b) * 0.5;
                    var candidate = along.Cross(V3.UnitZ).Unit();                          // the right of the run
                    var outward = candidate;
                    if (centre != null && (mid - centre.Value).Dot(new V3(candidate.X, candidate.Y, 0)) < 0) outward = candidate * -1;
                    var run = outward.Cross(V3.UnitZ).Unit();                              // z cross run = outward
                    var origin = (b - a).Dot(run) >= 0 ? a : b;
                    var frame = KineticsPlan.Upright(origin, run);
                    var np = KineticsPlan.DirToPlan(outward);
                    list.Add(new KineticHost
                    {
                        Kind = kind, Key = (kind == KineticKind.Fins ? "fins_" : "slats_") + e.Id.Value + (curves.Length > 1 ? "_" + n : ""),
                        Name = (e is Wall ? "Wall " : "Railing ") + e.Id.Value + (curves.Length > 1 ? " (run " + n + ")" : ""),
                        From = "the model: " + (e is Wall ? "wall " : "railing ") + e.Id.Value + ", " + len.ToString("0.0") + " m long",
                        Frame = frame, LengthM = len, DepthM = 0.15, HeightM = height, NormalPlan = np, AxisPlan = KineticsPlan.DirToPlan(run), HostElement = e.Id,
                        Note = curve is Line ? null : "a curved run, taken as its straight chord",
                    });
                }
            }
            if (list.Count == 0) problem = "Nothing usable in what was chosen: a run shorter than 0.3 m, or an element without geometry" + (skipped > 0 ? " (" + skipped + " skipped)" : "") + ".";
            else if (curved) SportifyLog.Info("kinetics", "a curved railing or wall was taken as its straight chord");
            return list;
        }
    }
}
