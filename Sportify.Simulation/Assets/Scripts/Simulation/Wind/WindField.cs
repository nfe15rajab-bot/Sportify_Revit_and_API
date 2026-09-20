using System;

namespace Sportify.Simulation.Wind
{
    /// <summary>
    /// The wind over the whole roof, per direction, on a 0.5 m grid: which EN 1991-1-4 zone each cell
    /// is in and so how much the roof speeds the wind up there. Drives the wind lines and the
    /// zone map in the video; the analysis itself never reads it.
    /// </summary>
    public sealed class RoofField
    {
        readonly RoofZone[][] _zones;
        readonly float[][] _speed;

        public readonly int Nx, Ny;
        public readonly float Length, Width, Cell;

        public RoofField(WindInputs inputs, WindSite site)
        {
            Length = (float)inputs.RoofLength;
            Width = (float)inputs.RoofWidth;
            Nx = Math.Max(1, (int)Math.Round(inputs.RoofLength / WindModel.CellSizeM));
            Ny = Math.Max(1, (int)Math.Round(inputs.RoofWidth / WindModel.CellSizeM));
            Cell = (float)WindModel.CellSizeM;

            var dirs = WindModel.DirectionsDeg;
            _zones = new RoofZone[dirs.Length][];
            _speed = new float[dirs.Length][];
            for (var d = 0; d < dirs.Length; d++)
            {
                _zones[d] = new RoofZone[Nx * Ny];
                _speed[d] = new float[Nx * Ny];
                for (var iy = 0; iy < Ny; iy++)
                {
                    for (var ix = 0; ix < Nx; ix++)
                    {
                        var x = (ix + 0.5) * inputs.RoofLength / Nx;
                        var y = (iy + 0.5) * inputs.RoofWidth / Ny;
                        var z = WindModel.Classify(inputs.Shape, x, y, dirs[d], site.RoofElevation);
                        _zones[d][iy * Nx + ix] = z;
                        _speed[d][iy * Nx + ix] = (float)WindModel.SpeedUp(z);
                    }
                }
            }
        }

        public RoofZone ZoneAtCell(int dir, int ix, int iy)
        {
            return _zones[dir][Math.Max(0, Math.Min(Ny - 1, iy)) * Nx + Math.Max(0, Math.Min(Nx - 1, ix))];
        }

        /// <summary>Speed-up at a layout point; 1 (undisturbed wind) off the roof.</summary>
        public float SpeedFactorAt(int dir, float x, float y)
        {
            if (x < 0f || y < 0f || x >= Length || y >= Width) return 1f;
            var ix = (int)(x / Length * Nx);
            var iy = (int)(y / Width * Ny);
            return _speed[dir][Math.Max(0, Math.Min(Ny - 1, iy)) * Nx + Math.Max(0, Math.Min(Nx - 1, ix))];
        }
    }

    /// <summary>
    /// Cell-by-cell numbers for one green-roof zone, on the same cells the analysis used, so the map
    /// in the video and the percentages in the report agree by construction.
    /// </summary>
    public sealed class ZoneField
    {
        public readonly ZoneInput Zone;
        public readonly int Nx, Ny;
        public readonly bool Known, Paved;
        public readonly double DryKgM2;
        public readonly CoverKind Cover;

        /// <summary>Worst uplift utilisation over all directions, per cell (row-major, top row first).</summary>
        public readonly float[] Uplift;

        /// <summary>Lowest 10 m wind at which the cell erodes, bare and once the planting has closed, over all directions.</summary>
        public readonly float[] BareOnset, EstablishedOnset;

        /// <summary>The direction index that gives the bare onset: where the blown substrate goes.</summary>
        public readonly byte[] BareWorstDirection;

        public ZoneField(ZoneInput zone, WindInputs inputs, WindSite site)
        {
            Zone = zone;
            string source;
            DryKgM2 = WindModel.DryWeightKgM2(zone.Assembly, out source);
            Known = DryKgM2 > 0;
            Cover = WindModel.Cover(zone.Assembly);
            Paved = Cover == CoverKind.Paved;

            Nx = Math.Max(1, (int)Math.Round(zone.Width / WindModel.CellSizeM));
            Ny = Math.Max(1, (int)Math.Round(zone.Height / WindModel.CellSizeM));
            Uplift = new float[Nx * Ny];
            BareOnset = new float[Nx * Ny];
            EstablishedOnset = new float[Nx * Ny];
            BareWorstDirection = new byte[Nx * Ny];

            var dirs = WindModel.DirectionsDeg;
            var cw = zone.Width / Nx;
            var ch = zone.Height / Ny;

            for (var iy = 0; iy < Ny; iy++)
            {
                for (var ix = 0; ix < Nx; ix++)
                {
                    var x = zone.X + (ix + 0.5) * cw;
                    var y = zone.Y + (iy + 0.5) * ch;
                    double maxU = 0, minBare = double.PositiveInfinity, minEst = double.PositiveInfinity;
                    var worstDir = 0;

                    for (var d = 0; d < dirs.Length; d++)
                    {
                        var rz = WindModel.Classify(inputs.Shape, x, y, dirs[d], site.RoofElevation);
                        if (Known) maxU = Math.Max(maxU, WindModel.UpliftUtilisation(rz, site.QRoof, DryKgM2));
                        if (!Paved)
                        {
                            var bare = WindModel.ErosionOnsetMs(Cover, false, rz, site);
                            var est = WindModel.ErosionOnsetMs(Cover, true, rz, site);
                            if (bare < minBare) { minBare = bare; worstDir = d; }
                            if (est < minEst) minEst = est;
                        }
                    }

                    var i = iy * Nx + ix;
                    Uplift[i] = (float)Math.Min(maxU, 999.0);
                    BareOnset[i] = Paved ? float.PositiveInfinity : (float)minBare;
                    EstablishedOnset[i] = Paved ? float.PositiveInfinity : (float)minEst;
                    BareWorstDirection[i] = (byte)worstDir;
                }
            }
        }
    }
}
