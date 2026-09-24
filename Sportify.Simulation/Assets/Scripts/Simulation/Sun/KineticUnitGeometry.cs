using System;
using System.Collections.Generic;

namespace Sportify.Simulation.Sun
{
    /// <summary>A point or a direction, metres.</summary>
    public struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double k) => new V3(a.X * k, a.Y * k, a.Z * k);
        public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public V3 Cross(V3 b) => new V3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public V3 Unit() { var l = Length; return l < 1e-12 ? new V3(0, 0, 1) : new V3(X / l, Y / l, Z / l); }
        public static readonly V3 UnitX = new V3(1, 0, 0), UnitY = new V3(0, 1, 0), UnitZ = new V3(0, 0, 1);
    }

    /// <summary>
    /// A straight member: a bar with a rectangular section from P0 to P1. The section is <see cref="SizeU"/> wide along <see cref="U"/> (a unit vector square to the axis) and
    /// <see cref="SizeV"/> along the other section direction. In Revit it is one instance of the 8-point adaptive bar (four corner points at each end), in Unity a box,
    /// in SOLIDWORKS an extrusion. Metres.
    /// </summary>
    public sealed class BarPlan
    {
        public string Role;                        // blade | fin | post | rail | rod | mast | housing | bottombar | crank | piston | motor | track | carriage | curtainslat
        public V3 P0, P1;
        public double SizeU, SizeV;
        public V3 U;
        /// <summary>The moving parts (blades, fins, curtains, masts, carriages): dynamic furniture. The rest carries them: structure.</summary>
        public bool Dynamic;
        /// <summary>The mechanism's hardware (crank arms, actuator piston and housing, drive motors): the CAD model and the videos show it, the BIM model does not place it.</summary>
        public bool Detail;
        /// <summary>Only the CAD model has it (the slats a roller curtain is made of, which the videos and the BIM model draw as one membrane).</summary>
        public bool CadOnly;

        public V3 V => P1.Equals(P0) ? V3.UnitY : (P1 - P0).Unit().Cross(U).Unit();

        /// <summary>The eight corners: the section at P0 (four, in order around it) then at P1.</summary>
        public V3[] Corners()
        {
            var a = (P1 - P0).Unit();
            var u = (U - a * U.Dot(a)).Unit();
            var v = a.Cross(u).Unit();
            var hu = u * (SizeU / 2); var hv = v * (SizeV / 2);
            V3[] Section(V3 p) => new[] { p - hu - hv, p + hu - hv, p + hu + hv, p - hu + hv };
            var s0 = Section(P0); var s1 = Section(P1);
            return new[] { s0[0], s0[1], s0[2], s0[3], s1[0], s1[1], s1[2], s1[3] };
        }
    }

    /// <summary>A membrane: four corners in order around it (A, B, C, D), a ruled surface between the edges AB and DC (a hyperbolic paraboloid when the corners are not level): a sail, a curtain.</summary>
    public sealed class SurfacePlan
    {
        public string Role;                        // sail | curtain
        public V3 A, B, C, D;
        public bool Dynamic = true;
    }

    /// <summary>What a kinetic unit is made of at one moment, in its own local frame.</summary>
    public sealed class UnitPlan
    {
        public string Kind, Label;
        public List<BarPlan> Bars = new List<BarPlan>();
        public List<SurfacePlan> Surfaces = new List<SurfacePlan>();
        public double LengthM, DepthM, HeightM;    // the unit's own extent in x, y, z
    }

    /// <summary>The unit's own frame in the world: a point and three unit vectors (x along the unit, y its outward or depth direction, z up).</summary>
    public struct UnitFrame
    {
        public V3 Origin, X, Y, Z;
        public static UnitFrame Identity => new UnitFrame { Origin = new V3(0, 0, 0), X = V3.UnitX, Y = V3.UnitY, Z = V3.UnitZ };
        public V3 ToWorld(V3 local) => Origin + X * local.X + Y * local.Y + Z * local.Z;
        public V3 DirToWorld(V3 local) => X * local.X + Y * local.Y + Z * local.Z;
    }

    /// <summary>
    /// Plans for the kinds of kinetic unit, as bars and membranes in a local frame: an overhead louvre, a slat screen and a fin screen on a wall or railing, a tensile sail on
    /// masts that slide on ground rails, and a roller fence. Pure geometry: the mechanics say how many blades, how open, how far the masts have run; this puts them in space.
    /// Every louvre has its mechanism: a crank arm on each blade, a push-rod hanging from the arms' pins (it moves on an arc as they turn, a parallelogram linkage), an actuator
    /// piston on the rod and its housing on the frame. A sail has its tracks, carriages and drive motors; a fence its motor and the slats of its curtain.
    /// </summary>
    public static class KineticUnits
    {
        /// <summary>The room a blade leaves at the end that carries its crank, between it and the rail: the crank arm, the pin and the rod live in it.</summary>
        public const double HardwareGapM = 0.12;
        /// <summary>How far a pin sits past the end of its blade, along the blade's axis.</summary>
        public const double PinStubM = 0.05;

        static BarPlan Bar(string role, V3 p0, V3 p1, double sizeU, double sizeV, V3 uHint, bool dynamic, bool detail = false, bool cadOnly = false)
        {
            var a = (p1 - p0).Unit();
            var u = uHint - a * uHint.Dot(a);
            if (u.Length < 1e-9) u = a.Cross(Math.Abs(a.Z) > 0.9 ? V3.UnitX : V3.UnitZ);
            return new BarPlan { Role = role, P0 = p0, P1 = p1, SizeU = sizeU, SizeV = sizeV, U = u.Unit(), Dynamic = dynamic, Detail = detail, CadOnly = cadOnly };
        }

        /// <summary>
        /// A blade's bar: its axis from p0 to p1, the section's thickness direction the blade's face normal <paramref name="normal"/> (so the chord runs square to both).
        /// </summary>
        static BarPlan Blade(string role, V3 p0, V3 p1, double chord, double thickness, V3 normal)
        {
            var a = (p1 - p0).Unit();
            var n = (normal - a * normal.Dot(a)).Unit();
            var chordDir = a.Cross(n).Unit();
            return new BarPlan { Role = role, P0 = p0, P1 = p1, SizeU = chord, SizeV = thickness, U = chordDir, Dynamic = true };
        }

        /// <summary>Equal bays for a run: the post positions from 0 to length.</summary>
        static double[] Stations(double length, int bays)
        {
            var s = new double[bays + 1];
            for (var i = 0; i <= bays; i++) s[i] = length * i / bays;
            return s;
        }

        static readonly V3 CrankSection = new V3(0.02, 0.006, 0);

        /// <summary>The crank arm of one blade: from a point on its axis to the pin, rigid with the blade.</summary>
        static BarPlan Crank(V3 onAxis, V3 pin, V3 uHint) => Bar("crank", onAxis, pin, CrankSection.X, CrankSection.Y, uHint, true, detail: true);

        // ------------------------------------------------------------------ the kinds

        /// <summary>
        /// An OVERHEAD louvre. Local x runs across the width (the blades are stacked along it), y along the depth (each blade's axis), z up. Posts at the corners
        /// and at every support line, rails along x at each support line carrying the blade bearings. In each bay the blades stop <see cref="HardwareGapM"/> short of the rail
        /// that ends the bay; there a crank arm on each blade reaches a pin, a push-rod along x hangs from the pins (a parallelogram linkage: it moves on an arc as the blades
        /// turn), an actuator piston runs from the rod's end into a housing on the frame beyond the last post.
        /// <paramref name="normalOfBlade"/> is the blades' face normal at this moment (LouvreActuationModel: cos(open) z + sin(open) toward the sun).
        /// </summary>
        public static UnitPlan Overhead(double widthM, double depthM, double heightM, int count, double chordM, double thicknessM, int bays, double postSizeM, double railSizeM, V3 normalOfBlade, double crankM = 0.06)
        {
            var plan = new UnitPlan { Kind = "overhead", Label = "Overhead louvre", LengthM = widthM, DepthM = depthM, HeightM = heightM };
            var ys = Stations(depthM, Math.Max(1, bays));
            var pitch = widthM / count;
            var n = new V3(normalOfBlade.X, 0, normalOfBlade.Z);                                     // a blade turns about its own axis (y): its normal has no y
            for (var k = 0; k < ys.Length; k++)
            {
                plan.Bars.Add(Bar("rail", new V3(0, ys[k], heightM - railSizeM / 2), new V3(widthM, ys[k], heightM - railSizeM / 2), railSizeM, railSizeM, V3.UnitZ, false));
                foreach (var x in new[] { 0.0, widthM })
                    plan.Bars.Add(Bar("post", new V3(x, ys[k], 0), new V3(x, ys[k], heightM - railSizeM), postSizeM, postSizeM, V3.UnitX, false));
            }
            for (var b = 0; b + 1 < ys.Length; b++)
            {
                var bay = ys[b + 1] - ys[b];
                var hardware = crankM > 0 && bay > 0.5;
                var y0 = ys[b] + 0.03; var y1 = hardware ? ys[b + 1] - HardwareGapM : ys[b + 1] - 0.03;
                for (var i = 0; i < count; i++)
                {
                    var x = (i + 0.5) * pitch;
                    plan.Bars.Add(Blade("blade", new V3(x, y0, heightM), new V3(x, y1, heightM), chordM, thicknessM, normalOfBlade));
                }
                if (!hardware) continue;
                var yPin = y1 + PinStubM;
                V3 Pin(int i) => new V3((i + 0.5) * pitch, yPin, heightM) - n * crankM;
                for (var i = 0; i < count; i++) plan.Bars.Add(Crank(new V3((i + 0.5) * pitch, y1, heightM), Pin(i), V3.UnitX));
                V3 first = Pin(0), last = Pin(count - 1);
                var rodEnd = new V3(last.X + 0.06, yPin, last.Z);
                plan.Bars.Add(Bar("rod", new V3(first.X - 0.06, yPin, first.Z), rodEnd, 0.02, 0.02, V3.UnitZ, true));
                plan.Bars.Add(Bar("piston", rodEnd, rodEnd + new V3(0.32, 0, 0), 0.02, 0.02, V3.UnitZ, true, detail: true));
                plan.Bars.Add(Bar("housing", new V3(widthM + 0.05, yPin, heightM - crankM / 2), new V3(widthM + 0.45, yPin, heightM - crankM / 2), 0.10, 0.14, V3.UnitZ, false, detail: true));
            }
            return plan;
        }

        /// <summary>
        /// A SLAT SCREEN on a railing or wall (photo: horizontal timber slats between posts, tipped open together by one rod hanging beside them). Local x along the screen,
        /// y the outward normal, z up. Posts at every bay line, rails along the top and the bottom, slats stacked up the height. In each bay the slats start
        /// <see cref="HardwareGapM"/> from the post; on each slat's end a crank arm reaches a pin, a vertical push-rod hangs from the pins, an actuator piston rises from the
        /// rod into a housing above the top rail.
        /// </summary>
        public static UnitPlan SlatScreen(double lengthM, double heightM, int count, double chordM, double thicknessM, int bays, double postSizeM, double railSizeM, V3 normalOfBlade, double crankM = 0.06)
        {
            var plan = new UnitPlan { Kind = "slats", Label = "Vertical slat screen", LengthM = lengthM, DepthM = chordM, HeightM = heightM };
            var xs = Stations(lengthM, Math.Max(1, bays));
            var pitch = (heightM - 2 * railSizeM) / count;
            var n = new V3(0, normalOfBlade.Y, normalOfBlade.Z);                                     // a slat turns about its own axis (x): its normal has no x
            foreach (var x in xs) plan.Bars.Add(Bar("post", new V3(x, 0, 0), new V3(x, 0, heightM), postSizeM, postSizeM, V3.UnitY, false));
            plan.Bars.Add(Bar("rail", new V3(0, 0, railSizeM / 2), new V3(lengthM, 0, railSizeM / 2), railSizeM, railSizeM, V3.UnitY, false));
            plan.Bars.Add(Bar("rail", new V3(0, 0, heightM - railSizeM / 2), new V3(lengthM, 0, heightM - railSizeM / 2), railSizeM, railSizeM, V3.UnitY, false));
            for (var b = 0; b + 1 < xs.Length; b++)
            {
                var bay = xs[b + 1] - xs[b];
                var hardware = crankM > 0 && bay > 0.5;
                var x0 = xs[b] + postSizeM / 2 + (hardware ? HardwareGapM : 0.03); var x1 = xs[b + 1] - postSizeM / 2 - 0.03;
                double Z(int i) => railSizeM + (i + 0.5) * pitch;
                for (var i = 0; i < count; i++) plan.Bars.Add(Blade("blade", new V3(x0, 0, Z(i)), new V3(x1, 0, Z(i)), chordM, thicknessM, normalOfBlade));
                if (!hardware) continue;
                var xPin = x0 - PinStubM;
                V3 Pin(int i) => new V3(xPin, 0, Z(i)) - n * crankM;
                for (var i = 0; i < count; i++) plan.Bars.Add(Crank(new V3(x0, 0, Z(i)), Pin(i), V3.UnitY));
                V3 first = Pin(0), last = Pin(count - 1);
                var rodTop = new V3(xPin, last.Y, last.Z + 0.06);
                plan.Bars.Add(Bar("rod", new V3(xPin, first.Y, first.Z - 0.06), rodTop, 0.02, 0.02, V3.UnitX, true));
                plan.Bars.Add(Bar("piston", rodTop, rodTop + new V3(0, 0, 0.55), 0.02, 0.02, V3.UnitX, true, detail: true));
                plan.Bars.Add(Bar("housing", new V3(xPin, -crankM / 2, heightM + 0.02), new V3(xPin, -crankM / 2, heightM + 0.45), 0.10, 0.14, V3.UnitX, false, detail: true));
            }
            return plan;
        }

        /// <summary>
        /// A FIN SCREEN (photo: full-height timber fins turning on vertical axes inside a frame). Local x along the screen, y the outward normal, z up. Posts at the
        /// ends, rails at the top and the bottom (and at each support line up the height), fins side by side. Each fin stops <see cref="HardwareGapM"/> below the rail above it;
        /// there a crank arm on its top reaches a pin, a push-rod along x hangs from the pins, an actuator piston runs from its end into a housing beyond the last post.
        /// </summary>
        public static UnitPlan FinScreen(double lengthM, double heightM, int count, double chordM, double thicknessM, int bays, double postSizeM, double railSizeM, V3 normalOfBlade, double crankM = 0.06)
        {
            var plan = new UnitPlan { Kind = "fins", Label = "Vertical fin screen", LengthM = lengthM, DepthM = chordM, HeightM = heightM };
            var zs = Stations(heightM, Math.Max(1, bays));
            var pitch = lengthM / count;
            var n = new V3(normalOfBlade.X, normalOfBlade.Y, 0);                                     // a fin turns about its own axis (z): its normal has no z
            foreach (var x in new[] { 0.0, lengthM }) plan.Bars.Add(Bar("post", new V3(x, 0, 0), new V3(x, 0, heightM), postSizeM, postSizeM, V3.UnitY, false));
            foreach (var z in zs) plan.Bars.Add(Bar("rail", new V3(0, 0, Math.Min(Math.Max(z, railSizeM / 2), heightM - railSizeM / 2)), new V3(lengthM, 0, Math.Min(Math.Max(z, railSizeM / 2), heightM - railSizeM / 2)), railSizeM, railSizeM, V3.UnitY, false));
            for (var b = 0; b + 1 < zs.Length; b++)
            {
                var bay = zs[b + 1] - zs[b];
                var hardware = crankM > 0 && bay > 0.6;
                var z0 = zs[b] + (b == 0 ? railSizeM : railSizeM / 2);
                var z1 = zs[b + 1] - (b + 2 == zs.Length ? railSizeM : railSizeM / 2) - (hardware ? HardwareGapM : 0);
                for (var i = 0; i < count; i++)
                {
                    var x = (i + 0.5) * pitch;
                    plan.Bars.Add(Blade("fin", new V3(x, 0, z0), new V3(x, 0, z1), chordM, thicknessM, normalOfBlade));
                }
                if (!hardware) continue;
                var zPin = z1 + PinStubM;
                V3 Pin(int i) => new V3((i + 0.5) * pitch, 0, zPin) - n * crankM;
                for (var i = 0; i < count; i++) plan.Bars.Add(Crank(new V3((i + 0.5) * pitch, 0, z1), Pin(i), V3.UnitY));
                V3 first = Pin(0), last = Pin(count - 1);
                var rodEnd = new V3(last.X + 0.06, last.Y, zPin);
                plan.Bars.Add(Bar("rod", new V3(first.X - 0.06, first.Y, zPin), rodEnd, 0.02, 0.02, V3.UnitZ, true));
                plan.Bars.Add(Bar("piston", rodEnd, rodEnd + new V3(0.40, 0, 0), 0.02, 0.02, V3.UnitZ, true, detail: true));
                plan.Bars.Add(Bar("housing", new V3(lengthM + 0.05, -crankM / 2, zPin), new V3(lengthM + 0.50, -crankM / 2, zPin), 0.10, 0.14, V3.UnitZ, false, detail: true));
            }
            return plan;
        }

        /// <summary>
        /// A TENSILE SAIL on MOVABLE PILLARS that slide on ground rails (photo: a fabric membrane hung between the tops of steel masts). Local x along the rails, y across them,
        /// z up. The sail is a rectangle (four masts on two parallel tracks along x) or a right triangle (three masts on an L of two tracks, along x and along y): the shape
        /// traced on the ground. <paramref name="scale"/> is how far the moving masts have run: 1 is the analysis's footprint (<paramref name="widthM"/> x <paramref name="depthM"/>),
        /// less shrinks the shaded area, more grows it, up to <paramref name="maxScale"/> where the tracks end. A rectangle grows along x (the tracks' axis) from an anchored end
        /// (<paramref name="anchorSide"/> &gt; 0: the masts at x = 0 stay put) or from its middle (0); a triangle grows in both axes from the corner at the origin, which stays put.
        /// Each mast stands on a carriage on its track; a drive motor sits at the end of each track. The masts stay upright; the two diagonals of a rectangle differ in height
        /// by <paramref name="twistM"/> so the fabric is a hyperbolic paraboloid, not a flat sheet.
        /// </summary>
        public static UnitPlan Sail(bool triangle, double widthM, double depthM, double scale, double anchorSide, double heightM, double mastDiameterM, double railSizeM, double minScale, double maxScale, double twistM, double carriageM = 0.30)
        {
            var plan = new UnitPlan { Kind = triangle ? "sail_triangle" : "sail", Label = triangle ? "Tensile sail on ground rails (triangle)" : "Tensile sail on ground rails (rectangle)", LengthM = widthM * maxScale, DepthM = depthM * (triangle ? maxScale : 1), HeightM = heightM };
            var railH = railSizeM * 0.6;                                                             // a low profile track, half sunk in the roof
            double x1, x2;
            if (anchorSide > 0) { x1 = 0; x2 = scale * widthM; }
            else { x1 = widthM / 2 - scale * widthM / 2; x2 = widthM / 2 + scale * widthM / 2; }
            var carriageTop = railH + 0.10;
            var mastD = mastDiameterM;
            V3 Base(double x, double y) => new V3(x, y, 0);

            void Mast(V3 baseAt, double topZ, bool moves)
            {
                plan.Bars.Add(Bar("carriage", new V3(baseAt.X - carriageM / 2, baseAt.Y, railH + 0.05), new V3(baseAt.X + carriageM / 2, baseAt.Y, railH + 0.05), 0.22, 0.10, V3.UnitZ, moves));
                plan.Bars.Add(Bar("mast", new V3(baseAt.X, baseAt.Y, carriageTop), new V3(baseAt.X, baseAt.Y, topZ), mastD, mastD, V3.UnitX, moves));
            }
            void Track(V3 a, V3 b) => plan.Bars.Add(Bar("track", new V3(a.X, a.Y, railH / 2), new V3(b.X, b.Y, railH / 2), railSizeM, railH, V3.UnitZ, false));
            void Motor(V3 at, V3 along) => plan.Bars.Add(Bar("motor", at, at + along, 0.24, 0.24, V3.UnitZ, false, detail: true));

            if (!triangle)
            {
                double xa, xb;
                if (anchorSide > 0) { xa = -0.25; xb = widthM * maxScale + 0.25; }
                else { xa = widthM / 2 - widthM * maxScale / 2 - 0.25; xb = widthM / 2 + widthM * maxScale / 2 + 0.25; }
                foreach (var y in new[] { 0.0, depthM })
                {
                    Track(Base(xa, y), Base(xb, y));
                    Motor(new V3(xb + 0.03, y, 0.12), new V3(0.30, 0, 0));
                    if (anchorSide <= 0) Motor(new V3(xa - 0.03, y, 0.12), new V3(-0.30, 0, 0));
                }
                var moves1 = anchorSide <= 0;
                var hi = heightM + twistM / 2; var lo = heightM - twistM / 2;
                Mast(Base(x1, 0), hi, moves1);
                Mast(Base(x2, 0), lo, true);
                Mast(Base(x2, depthM), hi, true);
                Mast(Base(x1, depthM), lo, moves1);
                plan.Surfaces.Add(new SurfacePlan { Role = "sail", A = new V3(x1, 0, hi), B = new V3(x2, 0, lo), C = new V3(x2, depthM, hi), D = new V3(x1, depthM, lo) });
            }
            else
            {
                var w = scale * widthM; var d = scale * depthM;
                Track(Base(-0.25, 0), Base(widthM * maxScale + 0.25, 0));
                Track(Base(0, -0.25), Base(0, depthM * maxScale + 0.25));
                Motor(new V3(widthM * maxScale + 0.28, 0, 0.12), new V3(0.30, 0, 0));
                Motor(new V3(0, depthM * maxScale + 0.28, 0.12), new V3(0, 0.30, 0));
                Mast(Base(0, 0), heightM, false);
                Mast(Base(w, 0), heightM, true);
                Mast(Base(0, d), heightM, true);
                // the apex is two points 2 cm apart, so that the ruled surface between the edges AB and DC has an edge at DC
                plan.Surfaces.Add(new SurfacePlan { Role = "sail", A = new V3(0, 0, heightM), B = new V3(w, 0, heightM), C = new V3(0.02, d, heightM), D = new V3(0, d, heightM) });
            }
            return plan;
        }

        /// <summary>
        /// A ROLLER FENCE (a rouleau on vertical guide rails, deployed only when it is needed). Local x along the fence, y the outward normal, z up. The roller housing
        /// lies along the base, a drive motor at its end; two or more guide rails stand in the Z axis; the curtain runs from the roller up to the bottom bar, which the roller
        /// lets rise along the rails: <paramref name="deployedM"/> is how high the top of the curtain stands now (0 stored, the recommended height in play). For the CAD model
        /// the curtain is also made of slats (as a roller shutter is), stacked at the roller when stored and spread up the rails as the bar rises.
        /// </summary>
        public static UnitPlan RollerFence(double lengthM, double fullHeightM, double deployedM, int bays, double railSizeM, double rollerDiameterM)
        {
            var plan = new UnitPlan { Kind = "fence", Label = "Roller fence", LengthM = lengthM, DepthM = rollerDiameterM, HeightM = fullHeightM };
            var xs = Stations(lengthM, Math.Max(1, bays));
            var barTop = rollerDiameterM + Math.Max(0, Math.Min(deployedM, fullHeightM - rollerDiameterM));
            plan.Bars.Add(Bar("housing", new V3(0, 0, rollerDiameterM / 2), new V3(lengthM, 0, rollerDiameterM / 2), rollerDiameterM, rollerDiameterM, V3.UnitY, false));
            plan.Bars.Add(Bar("motor", new V3(lengthM + 0.03, 0, rollerDiameterM / 2), new V3(lengthM + 0.33, 0, rollerDiameterM / 2), rollerDiameterM * 1.4, rollerDiameterM * 1.4, V3.UnitY, false, detail: true));
            foreach (var x in xs) plan.Bars.Add(Bar("rail", new V3(x, 0, rollerDiameterM), new V3(x, 0, fullHeightM), railSizeM, railSizeM, V3.UnitY, false));
            plan.Bars.Add(Bar("bottombar", new V3(0, 0, barTop), new V3(lengthM, 0, barTop), 0.05, 0.05, V3.UnitY, true));
            plan.Surfaces.Add(new SurfacePlan { Role = "curtain", A = new V3(0, 0, rollerDiameterM), B = new V3(lengthM, 0, rollerDiameterM), C = new V3(lengthM, 0, barTop), D = new V3(0, 0, barTop) });

            const double slatH = 0.10;
            var n = Math.Max(1, (int)Math.Round((fullHeightM - rollerDiameterM) / slatH));
            for (var i = 0; i < n; i++)
            {
                var z = rollerDiameterM + (i + 0.5) / n * (barTop - rollerDiameterM);
                plan.Bars.Add(Bar("curtainslat", new V3(0.03, 0, z), new V3(lengthM - 0.03, 0, z), slatH * 0.95, 0.012, V3.UnitZ, true, cadOnly: true));
            }
            return plan;
        }

        // ------------------------------------------------------------------ normals from the states

        /// <summary>The blade face normal of an OVERHEAD louvre: (cos open) up plus (sin open) toward the sun (a horizontal unit vector in the local frame).</summary>
        public static V3 NormalOverhead(double openDeg, V3 leanLocal) { var o = openDeg * Math.PI / 180.0; return V3.UnitZ * Math.Cos(o) + leanLocal.Unit() * Math.Sin(o); }

        /// <summary>The slat normal on a wall: (cos open) out along the wall's normal plus (sin open) up.</summary>
        public static V3 NormalSlat(double openDeg) { var o = openDeg * Math.PI / 180.0; return V3.UnitY * Math.Cos(o) + V3.UnitZ * Math.Sin(o); }

        /// <summary>The fin normal on a wall: (cos open) out plus (sin open) along the wall toward the sun (+1 or -1 along local x).</summary>
        public static V3 NormalFin(double openDeg, double alongSign) { var o = openDeg * Math.PI / 180.0; return V3.UnitY * Math.Cos(o) + V3.UnitX * (alongSign >= 0 ? 1 : -1) * Math.Sin(o); }
    }
}
