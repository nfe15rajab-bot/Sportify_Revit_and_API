using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sportify.Simulation.Sun
{
    /// <summary>One input of the louvre's mechanical model: what it is, its built-in value, and how far that value can be trusted.</summary>
    public sealed class LouvreInputDef
    {
        public string Key, Label, Unit, Status, Reference;   // Status: standard | literature | assumed | placeholder | optional
        public double Default;
    }

    /// <summary>What the report says about one input: the value used, and whether the mechanical engineer entered it, or the built-in one stands.</summary>
    public sealed class LouvreInputUse
    {
        public string Key, Label, Value, State, Status, Reference;   // State: entered | accepted | unconfirmed
    }

    /// <summary>
    /// The mechanical inputs of a louvre. Every built-in value is a screening figure with its source, and the ones that are assumed or placeholders
    /// (not standard constants or published coefficients) keep the result PRELIMINARY until the mechanical engineer enters them: the add-in reads a small
    /// JSON file of overrides (SportifyKineticsInputs.json), so a SolidWorks mass property or a bench-test friction figure goes in without touching code.
    /// </summary>
    public sealed class LouvreDesign
    {
        public static readonly LouvreInputDef[] Inputs =
        {
            new LouvreInputDef { Key = "chord_m", Label = "Blade chord", Unit = "m", Default = 0.15, Status = "assumed", Reference = "a common extruded-aluminium louvre profile; replace with the designed section" },
            new LouvreInputDef { Key = "thickness_m", Label = "Blade thickness", Unit = "m", Default = 0.03, Status = "assumed", Reference = "hollow section, as above" },
            new LouvreInputDef { Key = "wall_m", Label = "Blade wall thickness", Unit = "m", Default = 0.002, Status = "assumed", Reference = "extruded hollow section; from the profile drawing" },
            new LouvreInputDef { Key = "pitch_ratio", Label = "Blade pitch / chord, when no spacing is recommended", Unit = "-", Default = 0.95, Status = "assumed", Reference = "below 1 the blades overlap when closed, so a closed louvre is a roof (the spacing recommendation replaces it)" },
            new LouvreInputDef { Key = "max_pitch_ratio", Label = "Widest blade pitch / chord the spacing may recommend", Unit = "-", Default = 3.0, Status = "assumed", Reference = "beyond about three chords the blades stop reading as a louvre and the closed position leaves large gaps" },
            new LouvreInputDef { Key = "density_kg_m3", Label = "Blade material density", Unit = "kg/m3", Default = 2700, Status = "standard", Reference = "aluminium alloy (EN AW-6063)" },
            new LouvreInputDef { Key = "youngs_modulus_pa", Label = "Blade material Young's modulus", Unit = "Pa", Default = 70e9, Status = "standard", Reference = "aluminium alloy" },
            new LouvreInputDef { Key = "normal_force_coeff", Label = "Normal-force coefficient, blade face-on to the wind", Unit = "-", Default = 1.2, Status = "literature", Reference = "flat plate normal to the flow, about 1.1 to 1.3 in fluid-dynamic drag handbooks" },
            new LouvreInputDef { Key = "aero_centre_frac", Label = "Aerodynamic centre from the leading edge, at small incidence", Unit = "x chord", Default = 0.25, Status = "literature", Reference = "thin flat plate: quarter chord; it moves to mid-chord as the plate turns face-on" },
            new LouvreInputDef { Key = "friction_torque_nm", Label = "Bearing and seal friction, per blade", Unit = "N m", Default = 0.3, Status = "placeholder", Reference = "measure on the prototype" },
            new LouvreInputDef { Key = "crank_radius_m", Label = "Actuator crank arm", Unit = "m", Default = 0.06, Status = "placeholder", Reference = "from the mechanism design (push-pull rod driving every blade)" },
            new LouvreInputDef { Key = "linkage_efficiency", Label = "Linkage efficiency", Unit = "-", Default = 0.80, Status = "assumed", Reference = "rod-and-crank with plain bearings" },
            new LouvreInputDef { Key = "safety_factor", Label = "Safety factor on the actuator", Unit = "-", Default = 1.5, Status = "assumed", Reference = "screening value; the mechanical engineer's standard applies" },
            new LouvreInputDef { Key = "stow_wind_speed_ms", Label = "Wind speed above which the blades stow", Unit = "m/s", Default = 14, Status = "assumed", Reference = "a typical maker's operating limit, about 50 km/h (Beaufort 7)" },
            new LouvreInputDef { Key = "full_swing_s", Label = "Time for a full swing, closed to open", Unit = "s", Default = 60, Status = "assumed", Reference = "louvre pergolas take about a minute" },
            new LouvreInputDef { Key = "deflection_limit_ratio", Label = "Blade deflection limit (span / n)", Unit = "-", Default = 200, Status = "assumed", Reference = "serviceability limit for a cladding-type member" },
            new LouvreInputDef { Key = "air_density_kg_m3", Label = "Air density", Unit = "kg/m3", Default = 1.25, Status = "standard", Reference = "EN 1991-1-4" },
            new LouvreInputDef { Key = "post_size_m", Label = "Railing / frame post, outer size (square hollow section)", Unit = "m", Default = 0.06, Status = "assumed", Reference = "the supports the blades pivot in; from the railing design" },
            new LouvreInputDef { Key = "post_wall_m", Label = "Railing / frame post wall thickness", Unit = "m", Default = 0.003, Status = "assumed", Reference = "square hollow section" },
            new LouvreInputDef { Key = "rail_size_m", Label = "Railing / frame rail, outer size (square hollow section)", Unit = "m", Default = 0.04, Status = "assumed", Reference = "the rails that carry the blade bearings and tie the posts" },
            // The tensile sail on movable pillars (SailMechanics): steel masts on carriages that run on ground tracks, fabric between their tops.
            new LouvreInputDef { Key = "mast_diameter_m", Label = "Sail mast, outer diameter (steel tube)", Unit = "m", Default = 0.14, Status = "assumed", Reference = "the yellow masts of a shade-sail structure are 114 to 219 mm tubes; replace with the designed section" },
            new LouvreInputDef { Key = "mast_wall_m", Label = "Sail mast wall thickness", Unit = "m", Default = 0.006, Status = "assumed", Reference = "circular hollow section" },
            new LouvreInputDef { Key = "steel_youngs_pa", Label = "Steel Young's modulus", Unit = "Pa", Default = 210e9, Status = "standard", Reference = "structural steel" },
            new LouvreInputDef { Key = "steel_yield_pa", Label = "Steel yield strength", Unit = "Pa", Default = 275e6, Status = "standard", Reference = "S275 structural steel" },
            new LouvreInputDef { Key = "fabric_mass_kg_m2", Label = "Sail fabric mass", Unit = "kg/m2", Default = 0.35, Status = "assumed", Reference = "HDPE shade cloth about 0.3 kg/m2, PVC-coated polyester about 0.6" },
            new LouvreInputDef { Key = "sail_wind_coeff", Label = "Sail wind force coefficient (uplift / suction)", Unit = "-", Default = 1.5, Status = "literature", Reference = "the sun and shade catalogue's own value for a shade sail" },
            new LouvreInputDef { Key = "sail_pretension_kn_m", Label = "Sail fabric pretension", Unit = "kN/m", Default = 0.5, Status = "assumed", Reference = "membrane pretension of a shade sail is of the order of 0.3 to 1 kN/m; the designer's value applies" },
            new LouvreInputDef { Key = "sail_min_scale", Label = "Smallest size the sail is run in to (share of its analysed footprint)", Unit = "-", Default = 0.5, Status = "assumed", Reference = "the masts run in along their tracks; the freed area gets the sun (a garden nearby)" },
            new LouvreInputDef { Key = "sail_max_scale", Label = "Largest size the sail is run out to (share of its analysed footprint)", Unit = "-", Default = 1.4, Status = "assumed", Reference = "where the ground tracks end" },
            new LouvreInputDef { Key = "sail_cover_target_percent", Label = "Share of the wanted shade the sail keeps covered as the sun moves", Unit = "%", Default = 95, Status = "assumed", Reference = "the masts run out only as far as this needs" },
            new LouvreInputDef { Key = "sail_rail_size_m", Label = "Ground track, outer size", Unit = "m", Default = 0.10, Status = "assumed", Reference = "the rail the mast carriages run on" },
            new LouvreInputDef { Key = "sail_carriage_len_m", Label = "Mast carriage length", Unit = "m", Default = 0.30, Status = "assumed", Reference = "from the carriage design" },
            new LouvreInputDef { Key = "sail_drive_speed_ms", Label = "Carriage drive speed", Unit = "m/s", Default = 0.05, Status = "assumed", Reference = "a rack or screw drive: a few centimetres a second" },
            new LouvreInputDef { Key = "sail_rolling_friction", Label = "Carriage rolling friction coefficient", Unit = "-", Default = 0.05, Status = "placeholder", Reference = "steel wheels on a track; measure on the prototype" },
            new LouvreInputDef { Key = "sail_twist_ratio", Label = "Sail twist: the height between its two diagonals, as a share of its shorter side", Unit = "-", Default = 0.06, Status = "assumed", Reference = "a tensile membrane is anticlastic (a hyperbolic paraboloid): about 5 to 10% keeps it taut" },
            new LouvreInputDef { Key = "sail_storm_height_m", Label = "Height the sail is lowered to in a storm", Unit = "m", Default = 0.6, Status = "assumed", Reference = "masts telescope down and the sail is taken in; nothing is left catching the wind" },
            // The roller fence (RollerFenceMechanics): a curtain on a roller with vertical guide rails, deployed only when it is needed.
            new LouvreInputDef { Key = "fence_curtain_kg_m2", Label = "Fence curtain mass", Unit = "kg/m2", Default = 0.6, Status = "assumed", Reference = "a coated-polyester ball-stop mesh: 0.3 to 0.8 kg/m2" },
            new LouvreInputDef { Key = "fence_solidity", Label = "Fence curtain solidity (the share of the area that stops the wind)", Unit = "-", Default = 0.35, Status = "assumed", Reference = "an open ball-stop mesh; a solid curtain is 1" },
            new LouvreInputDef { Key = "fence_wind_coeff", Label = "Fence wind force coefficient", Unit = "-", Default = 1.2, Status = "literature", Reference = "a porous screen normal to the wind, per unit of its solid area" },
            new LouvreInputDef { Key = "fence_roller_diameter_m", Label = "Fence roller housing diameter", Unit = "m", Default = 0.12, Status = "assumed", Reference = "the roller tube with the stored curtain rolled on it" },
            new LouvreInputDef { Key = "fence_rail_size_m", Label = "Fence guide rail, outer size (square hollow section)", Unit = "m", Default = 0.08, Status = "assumed", Reference = "the rails the bottom bar runs in; replaced by the recommended size when it is too light" },
            new LouvreInputDef { Key = "fence_rail_wall_m", Label = "Fence guide rail wall thickness", Unit = "m", Default = 0.004, Status = "assumed", Reference = "square hollow section" },
            new LouvreInputDef { Key = "fence_max_bay_m", Label = "Furthest apart two guide rails may stand", Unit = "m", Default = 3.0, Status = "assumed", Reference = "the curtain spans between rails; a longer fence gets a rail every bay" },
            new LouvreInputDef { Key = "ball_mass_kg", Label = "Ball mass, for the impact on the fence", Unit = "kg", Default = 0.45, Status = "assumed", Reference = "a size-5 football; the ball simulation's own ball is a shot from the courts" },
            new LouvreInputDef { Key = "ball_speed_ms", Label = "Ball speed at the fence", Unit = "m/s", Default = 22, Status = "assumed", Reference = "a hard kick: 20 to 30 m/s" },
            new LouvreInputDef { Key = "net_stretch_m", Label = "How far the curtain gives when a ball hits", Unit = "m", Default = 0.6, Status = "assumed", Reference = "the stopping distance of the mesh: the softer, the smaller the force on the rails" },
            new LouvreInputDef { Key = "fence_deploy_s", Label = "Time to deploy or stow the fence", Unit = "s", Default = 20, Status = "assumed", Reference = "roller shutters take 10 to 30 s" },
            // Optional: from the CAD model (Sportify.Mechanical writes them from SOLIDWORKS). Default 0 = not entered, the value follows from the section above.
            new LouvreInputDef { Key = "blade_mass_per_m_kg", Label = "Blade mass per metre (from the CAD model)", Unit = "kg/m", Default = 0, Status = "optional", Reference = "SOLIDWORKS mass properties of the designed section; replaces the hollow-section estimate" },
            new LouvreInputDef { Key = "blade_inertia_m4", Label = "Blade area moment about the chord axis (from the CAD model)", Unit = "m4", Default = 0, Status = "optional", Reference = "SOLIDWORKS section of the designed blade; replaces the hollow-section estimate in the deflection check" },
        };

        readonly Dictionary<string, double> _values = new Dictionary<string, double>();
        readonly HashSet<string> _entered = new HashSet<string>();

        /// <param name="overrides">The mechanical engineer's own values by key; unknown keys and values that are not positive numbers are ignored.</param>
        public LouvreDesign(IDictionary<string, double> overrides = null)
        {
            foreach (var d in Inputs) _values[d.Key] = d.Default;
            if (overrides == null) return;
            foreach (var kv in overrides)
                if (_values.ContainsKey(kv.Key) && kv.Value > 0 && !double.IsNaN(kv.Value) && !double.IsInfinity(kv.Value))
                {
                    _values[kv.Key] = kv.Value;
                    _entered.Add(kv.Key);
                }
        }

        public double this[string key] { get { return _values[key]; } }

        public List<LouvreInputUse> Uses()
        {
            var uses = new List<LouvreInputUse>();
            foreach (var d in Inputs)
            {
                var entered = _entered.Contains(d.Key);
                var trusted = d.Status == "standard" || d.Status == "literature" || d.Status == "optional";
                uses.Add(new LouvreInputUse
                {
                    Key = d.Key, Label = d.Label,
                    Value = d.Status == "optional" && !entered ? "not entered: from the section" : _values[d.Key].ToString("G6", CultureInfo.InvariantCulture) + " " + d.Unit,
                    State = entered ? "entered" : trusted ? "accepted" : "unconfirmed",
                    Status = d.Status, Reference = d.Reference,
                });
            }
            return uses;
        }

        /// <summary>True while an assumed or placeholder value stands that the mechanical engineer has not entered: the numbers are a screening, not a design.</summary>
        public bool Preliminary { get { foreach (var u in Uses()) if (u.State == "unconfirmed") return true; return false; } }

        public string PreliminaryNote()
        {
            var names = new List<string>();
            foreach (var u in Uses()) if (u.State == "unconfirmed") names.Add(u.Label.ToLowerInvariant());
            return names.Count == 0 ? "" : "PRELIMINARY: built-in values stand for " + string.Join(", ", names) + ". Enter them in SportifyKineticsInputs.json.";
        }
    }

    /// <summary>What one actuation state asks of the blades.</summary>
    public sealed class LouvreStateLoad
    {
        public string Label;
        public double SolarTimeH, SunElevationDeg, ProfileAngleDeg, OpenAngleDeg;
        public double SunStoppedPercent;         // of the direct sun on the louvre
        public double WindTorqueOperatingNm;     // per blade, at the operating wind limit
        public double WindTorqueGustNm;          // per blade, at the design peak pressure
    }

    /// <summary>How many blades, and how far apart, the analysis recommends: the widest pitch that still stops the shade target when the blades track the sun.</summary>
    public sealed class LouvreSpacingReport
    {
        public LouvreHost Host;
        public double StackLengthM;              // what the blades are arrayed across: the width of an overhead unit, the height of a vertical screen
        public double PitchM;
        public int Count;
        public double TargetStoppedPercent;      // the shade target the analysis carries
        public string BindingLabel;              // the state that limits the pitch: the sun that is hardest to stop
        public double BindingProfileDeg;
        public double StoppedAtBindingPercent;   // what the recommended spacing stops in that state, blades tracking
        public double ClosedStoppedPercent;      // what it stops closed (the storm position of an overhead unit): the louvre is a roof only if this is 100
        public bool CappedAtMaxPitch;
        public string Reason;
    }

    /// <summary>The railing or frame that carries the blades: how many bays and how far apart the posts are, so no blade spans more than its section allows.</summary>
    public sealed class LouvreSupportReport
    {
        public double SpanM;                     // the full length a blade would have to span unsupported
        public double AllowedSpanM;              // the most one blade section may span in the design peak
        public int Bays;
        public double BayLengthM;                // the blade span between posts
        public int IntermediatePosts;
        public double DeflectionMm;              // face-on in the design peak, over one bay
    }

    public sealed class LouvreMechanicsReport
    {
        public LouvreHost Host;
        public double StackLengthM;
        public int BladeCount;
        public double ChordM, ThicknessM, SpanM, PitchM, BladeMassKg, TotalMassKg;
        public double DesignPressurePa, DesignWindMs, OperatingWindMs, OperatingPressurePa;
        public double PeakTorqueOperatingNm, PeakTorqueAngleDeg, PeakTorqueGustNm;    // per blade; the angle is the blade opening at which the peak occurs
        public double FaceOnForceGustN;                                               // per blade, the load on its two bearings together
        public double ActuatorTorqueNm, ActuatorForceN, HoldingTorqueNm;              // the shaft that drives every blade, and the rod at the crank
        public double DeflectionMm, DeflectionRatio, AllowedSpanM;
        public bool DeflectionOk, StowRequired;
        public double StowOpenAngleDeg;                                               // where the blades go and latch in a storm: closed (0) overhead, feathered (90) on a wall
        public double SwingSeconds, ActuatorPowerW, EnergyWhPerDay;
        public LouvreSpacingReport Spacing;                                           // null when the pitch came from the pitch ratio
        public LouvreSupportReport Supports;
        public List<LouvreStateLoad> States = new List<LouvreStateLoad>();
        public List<string> Findings = new List<string>();
    }

    /// <summary>
    /// The mechanics of a louvre's blades: blade count and mass from the geometry, the wind torque a blade puts on its pivot at every angle, the
    /// actuator that has to turn them all (and hold them in a gust), how far a blade bends face-on to the wind, the wind speed above which they stow, how far
    /// apart the blades should be for the shade the analysis asks for, and how far apart the posts of the railing or frame must be.
    /// Plain arithmetic on the sun and shade result (the design peak pressure at the roof, the shade target, the uplift on the anchors); no Unity,
    /// no Revit. A screening model with every assumption named (LouvreDesign.Inputs), meant to be replaced input by input with the real mechanism's numbers.
    ///
    /// Model. A blade is a flat plate of chord c and length L (its span between supports) that pivots about its mid-chord; the wind is horizontal and square
    /// to the blade axis. Overhead, a blade tilted o degrees from flat meets it at incidence o; on a wall, a blade tilted o degrees from the wall's plane meets
    /// it at incidence 90 - o. Normal force N(a) = q C_N sin(a) c L; the centre of pressure runs from the quarter chord (a small) to mid-chord (a = 90), so the
    /// pivot arm is c (1/2 - a0)(1 - sin a) and the torque about the pivot is M(a) = q C_N c^2 L (1/2 - a0) sin(a) (1 - sin(a)): nothing flat, nothing
    /// face-on, a peak at a = 30 degrees. Every blade is driven by one actuator through a push-pull rod, so the shaft carries N blades at once (the
    /// conservative reading: all at their peak together).
    /// </summary>
    public static class LouvreMechanics
    {
        public const double Deg = Math.PI / 180.0;

        /// <summary>Share of the direct sun an OVERHEAD louvre of chord c and pitch p stops when its blades are open o degrees from flat and tilted toward the sun (profile angle = the sun's elevation across the blades).</summary>
        public static double SunStoppedShare(double chordM, double pitchM, double elevationDeg, double openDeg)
        {
            var s = Math.Sin(elevationDeg * Deg);
            if (s < 1e-3) return 1.0;
            return Math.Max(0.0, Math.Min(1.0, chordM * Math.Sin((openDeg + elevationDeg) * Deg) / (pitchM * s)));
        }

        /// <summary>
        /// The same for a louvre on a WALL: blades stacked at vertical pitch p, open o degrees from the wall's plane and tipped toward a sun whose profile angle
        /// over the wall is gamma. Adjacent rays are p cos(gamma) apart, and a blade shows c cos(o - gamma) to them.
        /// </summary>
        public static double SunStoppedShareVertical(double chordM, double pitchM, double profileDeg, double openDeg)
        {
            var c = Math.Cos(profileDeg * Deg);
            if (c < 1e-3) return 1.0;
            return Math.Max(0.0, Math.Min(1.0, chordM * Math.Cos((openDeg - profileDeg) * Deg) / (pitchM * c)));
        }

        /// <summary>The share stopped, for the host.</summary>
        public static double SunStopped(LouvreHost host, double chordM, double pitchM, double profileDeg, double openDeg) =>
            host == LouvreHost.Horizontal ? SunStoppedShare(chordM, pitchM, profileDeg, openDeg) : SunStoppedShareVertical(chordM, pitchM, profileDeg, openDeg);

        /// <summary>The angle between the horizontal wind and a blade's chord: the blade's opening overhead, its complement on a wall.</summary>
        public static double IncidenceDeg(LouvreHost host, double openDeg) => host == LouvreHost.Horizontal ? openDeg : 90.0 - openDeg;

        /// <summary>The rod stroke that swings the blades through <paramref name="sweepDeg"/>: the crank arm times the angle (a rod and crank, small angles: the exact stroke is r sin(angle) about the mid-position).</summary>
        public static double RodStrokeMm(LouvreDesign d, double sweepDeg) => d["crank_radius_m"] * sweepDeg * Deg * 1000.0;

        /// <summary>Wind torque on one blade's pivot, N m, at design pressure q (Pa), for a blade meeting the wind at this incidence (degrees).</summary>
        public static double BladeTorqueNm(LouvreDesign d, double pressurePa, double spanM, double incidenceDeg)
        {
            var sin = Math.Sin(incidenceDeg * Deg);
            return pressurePa * d["normal_force_coeff"] * d["chord_m"] * d["chord_m"] * spanM * (0.5 - d["aero_centre_frac"]) * sin * (1.0 - sin);
        }

        /// <summary>
        /// How many blades, how far apart. With the blades tracking the sun each stops c / (p s) of the direct sun, s being how far apart the rays are per unit of
        /// pitch (sin of the profile angle overhead, cos of it on a wall); the sun that is hardest to stop, the one with the largest s, limits the pitch. The
        /// widest pitch that still stops the shade target there means the fewest blades (the lightest louvre, the least to turn), never wider than the pitch
        /// ratio input allows. The analysis's own shade target is the input.
        /// </summary>
        public static LouvreSpacingReport RecommendSpacing(LouvreDesign d, LouvreHost host, double stackLengthM, double targetStoppedPercent, IList<LouvreActuationModel.ActuationState> states)
        {
            var c = d["chord_m"];
            var target = Math.Max(0.05, Math.Min(1.0, targetStoppedPercent / 100.0));
            double sepMax = 0, bindingProfile = 0; string bindingLabel = "";
            if (states != null)
                foreach (var s in states)
                {
                    var sep = host == LouvreHost.Horizontal ? Math.Sin(s.ProfileAngleDeg * Deg) : Math.Cos(s.ProfileAngleDeg * Deg);
                    if (sep > sepMax) { sepMax = sep; bindingProfile = s.ProfileAngleDeg; bindingLabel = s.Label; }
                }
            if (sepMax <= 0.05) { sepMax = 1.0; bindingLabel = "worst case"; bindingProfile = host == LouvreHost.Horizontal ? 90 : 0; }

            var pmax = c / (target * sepMax);
            var widest = c * d["max_pitch_ratio"];
            var capped = pmax > widest;
            var pitch = Math.Min(pmax, widest);
            var count = Math.Max(2, (int)Math.Ceiling(stackLengthM / pitch - 1e-9));
            pitch = stackLengthM / count;

            var r = new LouvreSpacingReport
            {
                Host = host, StackLengthM = stackLengthM, PitchM = pitch, Count = count, TargetStoppedPercent = Math.Round(target * 100, 1),
                BindingLabel = bindingLabel, BindingProfileDeg = bindingProfile, CappedAtMaxPitch = capped,
                StoppedAtBindingPercent = Math.Round(100.0 * Math.Min(1.0, c / (pitch * sepMax)), 1),
                ClosedStoppedPercent = Math.Round(100.0 * Math.Min(1.0, c / pitch), 1),
            };
            r.Reason = string.Format(CultureInfo.InvariantCulture,
                "{0} blades of {1:0} mm at a {2:0} mm pitch across {3:0.##} m: tracking the sun they stop {4:0}% at the hardest sun ({5}, {6:0} deg profile), against the {7:0}% asked for" +
                "{8}; closed they stop {9:0}%{10}.",
                count, c * 1000, pitch * 1000, stackLengthM, r.StoppedAtBindingPercent, bindingLabel, bindingProfile, r.TargetStoppedPercent,
                capped ? " (the widest pitch allowed, " + d["max_pitch_ratio"].ToString("0.#", CultureInfo.InvariantCulture) + " chords, gives more than that)" : "",
                r.ClosedStoppedPercent, r.ClosedStoppedPercent >= 99.5 ? ", so the closed louvre is a roof" : ", so the closed louvre is not a roof: it leaves gaps");
            return r;
        }

        /// <summary>
        /// The railing or frame: the most one blade may span between posts is the span at which its face-on deflection in the design peak just meets the limit;
        /// a longer run is cut into equal bays no longer than that. Nothing to add when the run is short enough.
        /// </summary>
        public static LouvreSupportReport RecommendSupports(LouvreDesign d, double spanM, double allowedSpanM, double deflectionAtSpanMm)
        {
            var allowed = double.IsInfinity(allowedSpanM) || allowedSpanM <= 0 ? spanM : allowedSpanM;
            var bays = Math.Max(1, (int)Math.Ceiling(spanM / allowed - 1e-9));
            var bay = spanM / bays;
            return new LouvreSupportReport
            {
                SpanM = spanM, AllowedSpanM = allowed, Bays = bays, BayLengthM = bay, IntermediatePosts = bays - 1,
                DeflectionMm = deflectionAtSpanMm * Math.Pow(bay / spanM, 4),
            };
        }

        /// <param name="widthM">The overhead unit's width: the blades are arrayed across it.</param>
        /// <param name="depthM">Its depth: each blade's length, unsupported between its two end bearings.</param>
        /// <param name="designPressurePa">The peak wind pressure at the roof (the sun and shade result carries it).</param>
        /// <param name="stowedUpliftKn">The wind uplift on the pergola's anchors, from the sun and shade analysis, that the stowed (closed, latched) pergola sees.</param>
        public static LouvreMechanicsReport Analyse(LouvreDesign d, double widthM, double depthM, double designPressurePa, double stowedUpliftKn, IList<LouvreActuationModel.ActuationState> states) =>
            Analyse(d, LouvreHost.Horizontal, widthM, depthM, designPressurePa, stowedUpliftKn, states, null);

        /// <param name="host">Overhead, or on a wall.</param>
        /// <param name="stackLengthM">What the blades are arrayed across: the width of an overhead unit, the height of a wall screen.</param>
        /// <param name="spanM">Each blade's length: the depth of an overhead unit, the length of a wall screen.</param>
        /// <param name="spacing">The recommended spacing (RecommendSpacing); null takes the pitch from the pitch ratio input.</param>
        public static LouvreMechanicsReport Analyse(LouvreDesign d, LouvreHost host, double stackLengthM, double spanM, double designPressurePa, double stowedUpliftKn,
                                                    IList<LouvreActuationModel.ActuationState> states, LouvreSpacingReport spacing)
        {
            var r = new LouvreMechanicsReport { Host = host, StackLengthM = stackLengthM, Spacing = spacing };
            double c = d["chord_m"], t = d["thickness_m"], w = Math.Min(d["wall_m"], Math.Min(c, t) / 2.0 - 1e-6);
            var eta = d["linkage_efficiency"];
            var sf = d["safety_factor"];
            var rho = d["air_density_kg_m3"];
            var depthM = spanM;

            r.ChordM = c; r.ThicknessM = t; r.SpanM = spanM;
            if (spacing != null) { r.BladeCount = spacing.Count; r.PitchM = spacing.PitchM; }
            else
            {
                r.BladeCount = Math.Max(2, (int)Math.Ceiling(stackLengthM / (c * d["pitch_ratio"])));
                r.PitchM = stackLengthM / r.BladeCount;
            }

            var sectionM2 = c * t - (c - 2 * w) * (t - 2 * w);                       // hollow rectangle
            // the CAD model's mass per metre, when the mechanical engineer has entered it, replaces the estimate from the section
            r.BladeMassKg = (d["blade_mass_per_m_kg"] > 0 ? d["blade_mass_per_m_kg"] : d["density_kg_m3"] * sectionM2) * depthM;
            r.TotalMassKg = r.BladeMassKg * r.BladeCount;

            r.DesignPressurePa = Math.Max(0.0, designPressurePa);
            r.DesignWindMs = Math.Sqrt(2.0 * r.DesignPressurePa / rho);
            r.OperatingWindMs = d["stow_wind_speed_ms"];
            r.OperatingPressurePa = 0.5 * rho * r.OperatingWindMs * r.OperatingWindMs;

            // the wind torque on a blade, over the whole swing (by incidence, then reported as the blade's opening)
            double peakIncidence = 0;
            for (var a = 0.0; a <= 90.0 + 1e-9; a += 0.5)
            {
                var m = BladeTorqueNm(d, r.OperatingPressurePa, depthM, a);
                if (m > r.PeakTorqueOperatingNm) { r.PeakTorqueOperatingNm = m; peakIncidence = a; }
            }
            r.PeakTorqueAngleDeg = host == LouvreHost.Horizontal ? peakIncidence : 90.0 - peakIncidence;
            r.PeakTorqueGustNm = 0;
            for (var a = 0.0; a <= 90.0 + 1e-9; a += 0.5) r.PeakTorqueGustNm = Math.Max(r.PeakTorqueGustNm, BladeTorqueNm(d, r.DesignPressurePa, depthM, a));
            r.FaceOnForceGustN = r.DesignPressurePa * d["normal_force_coeff"] * c * depthM;

            // the actuator: sized to move the blades at the wind limit, and to hold them at the design peak if they are caught open
            var friction = d["friction_torque_nm"];
            r.ActuatorTorqueNm = sf * r.BladeCount * (r.PeakTorqueOperatingNm + friction) / eta;
            r.HoldingTorqueNm = sf * r.BladeCount * (r.PeakTorqueGustNm + friction) / eta;
            r.ActuatorForceN = r.ActuatorTorqueNm / d["crank_radius_m"];

            // the blade bends face-on to the design peak: a simply supported beam under a uniform load
            var loadNPerM = r.DesignPressurePa * d["normal_force_coeff"] * c;
            var inertia = d["blade_inertia_m4"] > 0 ? d["blade_inertia_m4"]                       // the CAD model's, when entered
                          : (c * t * t * t - (c - 2 * w) * Math.Pow(t - 2 * w, 3)) / 12.0;        // else the hollow section's, bending across the thickness, the wind's direction
            var ei = d["youngs_modulus_pa"] * inertia;
            var limit = d["deflection_limit_ratio"];
            r.DeflectionMm = loadNPerM > 0 ? 5.0 * loadNPerM * Math.Pow(depthM, 4) / (384.0 * ei) * 1000.0 : 0;
            r.DeflectionRatio = r.DeflectionMm > 0 ? depthM * 1000.0 / r.DeflectionMm : double.PositiveInfinity;
            r.AllowedSpanM = loadNPerM > 0 ? Math.Pow(384.0 * ei / (5.0 * loadNPerM * limit), 1.0 / 3.0) : double.PositiveInfinity;
            r.DeflectionOk = r.DeflectionRatio >= limit;
            r.Supports = RecommendSupports(d, depthM, r.AllowedSpanM, r.DeflectionMm);

            r.StowRequired = r.DesignWindMs > r.OperatingWindMs;
            r.StowOpenAngleDeg = host == LouvreHost.Horizontal ? 0 : 90;      // overhead: flat and wind-parallel; on a wall: feathered, edge-on to the wind (closed it faces it square)

            // motion: a full swing takes FullSwingSeconds; the power is the operating torque at that speed, the energy is the day's sweeps
            r.SwingSeconds = d["full_swing_s"];
            var omega = (Math.PI / 2.0) / r.SwingSeconds;
            r.ActuatorPowerW = r.ActuatorTorqueNm / sf * omega;
            double sweepRad = 0, prev = 0;
            if (states != null)
                foreach (var s in states)
                {
                    sweepRad += Math.Abs(s.LouvreOpenAngleDeg - prev) * Deg;
                    prev = s.LouvreOpenAngleDeg;
                    var incidence = IncidenceDeg(host, s.LouvreOpenAngleDeg);
                    r.States.Add(new LouvreStateLoad
                    {
                        Label = s.Label, SolarTimeH = s.SolarTimeH, SunElevationDeg = s.SunElevationDeg, ProfileAngleDeg = s.ProfileAngleDeg, OpenAngleDeg = s.LouvreOpenAngleDeg,
                        SunStoppedPercent = Math.Round(100.0 * SunStopped(host, c, r.PitchM, s.ProfileAngleDeg, s.LouvreOpenAngleDeg), 1),
                        WindTorqueOperatingNm = BladeTorqueNm(d, r.OperatingPressurePa, depthM, incidence),
                        WindTorqueGustNm = BladeTorqueNm(d, r.DesignPressurePa, depthM, incidence),
                    });
                }
            r.EnergyWhPerDay = r.ActuatorTorqueNm / sf * sweepRad / 3600.0;

            if (r.DesignPressurePa <= 0)
                r.Findings.Add("No design wind pressure is available (run Wind Uplift & Erosion or the Sun & Shade Analysis with a wind zone): the wind loads below are zero, not safe.");
            if (spacing != null) r.Findings.Add(spacing.Reason);
            r.Findings.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} blades of {1:0} x {2:0} mm across {3:0.#} m at a {4:0} mm pitch, {5:0.0} kg each, {6:0} kg for the {7}.",
                r.BladeCount, c * 1000, t * 1000, stackLengthM, r.PitchM * 1000, r.BladeMassKg, r.TotalMassKg, host == LouvreHost.Horizontal ? "unit" : "screen"));
            r.Findings.Add(string.Format(CultureInfo.InvariantCulture,
                "Actuator: {0:0.#} N m at the shaft ({1:0} N at a {2:0} mm crank) moves every blade in wind up to {3:0} m/s; wind torque peaks at {4:0.0} N m per blade at {5:0} deg open.",
                r.ActuatorTorqueNm, r.ActuatorForceN, d["crank_radius_m"] * 1000, r.OperatingWindMs, r.PeakTorqueOperatingNm, r.PeakTorqueAngleDeg));
            if (r.DesignPressurePa > 0)
            {
                r.Findings.Add(string.Format(CultureInfo.InvariantCulture,
                    "If the blades are caught open in the design peak ({0:0} Pa, {1:0} m/s) the drive must hold {2:0.#} N m: a self-locking (worm) drive or a brake, not the {3:0.#} N m that moves them.",
                    r.DesignPressurePa, r.DesignWindMs, r.HoldingTorqueNm, r.ActuatorTorqueNm));
                r.Findings.Add(r.DeflectionOk
                    ? string.Format(CultureInfo.InvariantCulture, "Blade deflection face-on to the design peak: {0:0.#} mm, span/{1:0} (limit span/{2:0}).", r.DeflectionMm, r.DeflectionRatio, limit)
                    : string.Format(CultureInfo.InvariantCulture, "Blade deflection face-on to the design peak is {0:0.#} mm, span/{1:0}, past the span/{2:0} limit over the {3:0.0} m span: a blade of this section may span {4:0.0} m, so the railing needs {5} post(s) between the ends, {6:0.0} m apart, and then bends {7:0.0} mm.",
                        r.DeflectionMm, r.DeflectionRatio, limit, depthM, r.AllowedSpanM, r.Supports.IntermediatePosts, r.Supports.BayLengthM, r.Supports.DeflectionMm));
            }
            if (r.StowRequired)
                r.Findings.Add(host == LouvreHost.Horizontal
                    ? string.Format(CultureInfo.InvariantCulture,
                        "Wind stow: the design peak ({0:0} m/s) is past the {1:0} m/s operating limit, so the pergola needs an anemometer and a controller that drives the blades closed (0 deg) and latches them; stowed it is a roof, with {2:0.#} kN of uplift on its anchors (sun and shade analysis).",
                        r.DesignWindMs, r.OperatingWindMs, stowedUpliftKn)
                    : string.Format(CultureInfo.InvariantCulture,
                        "Wind stow: the design peak ({0:0} m/s) is past the {1:0} m/s operating limit, so the screen needs an anemometer and a controller that feathers the blades (open 90 deg, edge-on to the wind) and latches them: closed on a wall they face the wind square, {2:0} N a blade.",
                        r.DesignWindMs, r.OperatingWindMs, r.FaceOnForceGustN));
            r.Findings.Add(string.Format(CultureInfo.InvariantCulture,
                "Motion: a full swing takes {0:0} s at about {1:0.0} W; the day's sweeps through these states cost about {2:0.000} Wh, negligible against the shade.",
                r.SwingSeconds, r.ActuatorPowerW, r.EnergyWhPerDay));
            return r;
        }
    }
}
