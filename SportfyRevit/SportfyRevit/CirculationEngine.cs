namespace SportfyRevit
{
    /// <summary>
    /// C# port of rules.js's computeCirculation (occupancy grid + multi-
    /// source BFS from every entry point), kept numerically identical on
    /// purpose so Fire Safety/Accessibility never disagree with the web
    /// app's own Combine rules checklist for the same layout. Only the
    /// distances are reproduced here, not the actual path polylines —
    /// rules.js needs those to draw the marching-ants on the canvas, but
    /// every Revit-side caller only ever reads path length.
    /// Placement footprints come straight from bounding_box (already
    /// final, post-rotation dimensions per the export format — see
    /// SportifyLayoutDto's PlacementDto), so unlike rules.js's
    /// getFootprint() there's no separate rotation swap needed here.
    /// </summary>
    internal static class CirculationEngine
    {
        private static readonly (int dr, int dc)[] Neighbors4 = { (-1, 0), (1, 0), (0, -1), (0, 1) };

        private static readonly Dictionary<string, (double nx, double ny)> EntryNormals = new()
        {
            ["top"] = (0, 1),
            ["bottom"] = (0, -1),
            ["left"] = (1, 0),
            ["right"] = (-1, 0),
        };

        private sealed class Grid
        {
            public double Cell;
            public int Cols;
            public int Rows;
            public int[] Owner = Array.Empty<int>();
        }

        private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

        /// <summary>Keeps the BFS grid small even for large sites (perf safety net) — mirrors circulationCellSize.</summary>
        private static double CellSize(double roofLengthM, double roofWidthM)
        {
            double cell = Clamp(Math.Min(roofLengthM, roofWidthM) / 24.0, 0.3, 1.0);
            const int maxCells = 4000;
            while ((roofLengthM / cell) * (roofWidthM / cell) > maxCells) cell *= 1.25;
            return cell;
        }

        /// <summary>owner[cell] == -1 means walkable; otherwise it's the index of the placement whose buffered footprint occupies that cell.</summary>
        private static Grid BuildOccupancyGrid(double roofLengthM, double roofWidthM, IReadOnlyList<PlacementDto> items, double bufferM)
        {
            double cell = CellSize(roofLengthM, roofWidthM);
            int cols = Math.Max(3, (int)Math.Ceiling(roofLengthM / cell));
            int rows = Math.Max(3, (int)Math.Ceiling(roofWidthM / cell));
            var owner = new int[cols * rows];
            Array.Fill(owner, -1);
            double half = bufferM / 2.0;

            for (int idx = 0; idx < items.Count; idx++)
            {
                var bb = items[idx].BoundingBox;
                if (bb == null) continue;

                int c0 = (int)Clamp(Math.Floor((bb.TopLeftXM - half) / cell), 0, cols - 1);
                int c1 = (int)Clamp(Math.Ceiling((bb.TopLeftXM + bb.WidthM + half) / cell) - 1, 0, cols - 1);
                int r0 = (int)Clamp(Math.Floor((bb.TopLeftYM - half) / cell), 0, rows - 1);
                int r1 = (int)Clamp(Math.Ceiling((bb.TopLeftYM + bb.HeightM + half) / cell) - 1, 0, rows - 1);

                for (int r = r0; r <= r1; r++)
                {
                    for (int c = c0; c <= c1; c++)
                    {
                        int k = r * cols + c;
                        if (owner[k] == -1) owner[k] = idx;
                    }
                }
            }

            return new Grid { Cell = cell, Cols = cols, Rows = rows, Owner = owner };
        }

        /// <summary>Walks inward from a boundary entry point until it finds free grid space.</summary>
        private static int EntryStartCell(EntryPointDto ep, double roofLengthM, double roofWidthM, Grid grid)
        {
            if (ep.Edge == null || !EntryNormals.TryGetValue(ep.Edge, out var normal)) return -1;

            double maxStep = Math.Max(roofLengthM, roofWidthM);
            for (double step = grid.Cell * 0.5; step < maxStep; step += grid.Cell)
            {
                double tx = Clamp(ep.XM + normal.nx * step, 0.0001, roofLengthM - 0.0001);
                double ty = Clamp(ep.YM + normal.ny * step, 0.0001, roofWidthM - 0.0001);
                int c = (int)Clamp(Math.Floor(tx / grid.Cell), 0, grid.Cols - 1);
                int r = (int)Clamp(Math.Floor(ty / grid.Cell), 0, grid.Rows - 1);
                int k = r * grid.Cols + c;
                if (grid.Owner[k] == -1) return k;
            }
            return -1;
        }

        private static (int[] dist, int[] parent) Bfs(Grid grid, List<int> startCells)
        {
            var dist = new int[grid.Cols * grid.Rows];
            var parent = new int[grid.Cols * grid.Rows];
            Array.Fill(dist, -1);
            Array.Fill(parent, -1);

            var queue = new List<int>(startCells);
            foreach (var k in startCells) dist[k] = 0;

            int qi = 0;
            while (qi < queue.Count)
            {
                int cur = queue[qi++];
                int cr = cur / grid.Cols, cc = cur % grid.Cols;
                foreach (var (dr, dc) in Neighbors4)
                {
                    int nr = cr + dr, nc = cc + dc;
                    if (nr < 0 || nc < 0 || nr >= grid.Rows || nc >= grid.Cols) continue;
                    int nk = nr * grid.Cols + nc;
                    if (grid.Owner[nk] != -1 || dist[nk] != -1) continue;
                    dist[nk] = dist[cur] + 1;
                    parent[nk] = cur;
                    queue.Add(nk);
                }
            }
            return (dist, parent);
        }

        /// <summary>Best (closest) walkable cell touching any cell owned by this item.</summary>
        private static int NearestAccessCell(Grid grid, int itemIdx, int[] dist)
        {
            int best = -1, bestDist = int.MaxValue;
            for (int k = 0; k < grid.Owner.Length; k++)
            {
                if (grid.Owner[k] != itemIdx) continue;
                int r = k / grid.Cols, c = k % grid.Cols;
                foreach (var (dr, dc) in Neighbors4)
                {
                    int nr = r + dr, nc = c + dc;
                    if (nr < 0 || nc < 0 || nr >= grid.Rows || nc >= grid.Cols) continue;
                    int nk = nr * grid.Cols + nc;
                    if (grid.Owner[nk] != -1 || dist[nk] == -1) continue;
                    if (dist[nk] < bestDist) { bestDist = dist[nk]; best = nk; }
                }
            }
            return best;
        }

        private static double PathLengthM(Grid grid, int[] parent, int endCell)
        {
            double len = 0;
            int cur = endCell;
            double? prevX = null, prevY = null;
            while (cur != -1)
            {
                int r = cur / grid.Cols, c = cur % grid.Cols;
                double x = (c + 0.5) * grid.Cell, y = (r + 0.5) * grid.Cell;
                if (prevX != null) len += Math.Sqrt(Math.Pow(x - prevX.Value, 2) + Math.Pow(y - prevY!.Value, 2));
                prevX = x; prevY = y;
                cur = parent[cur];
            }
            return len;
        }

        /// <summary>
        /// For every placement with an Id: its shortest walkable distance
        /// back to any entry point (in DistancesM), or added to Unreachable
        /// if the occupancy grid has no path — exactly rules.js's
        /// computeCirculation, minus the path polylines nothing here needs.
        /// </summary>
        public static (Dictionary<string, double> DistancesM, HashSet<string> Unreachable) ComputeTravelDistances(SportifyLayout layout)
        {
            var distances = new Dictionary<string, double>();
            var unreachable = new HashSet<string>();

            var items = layout.Placements ?? new List<PlacementDto>();
            var entries = layout.EntryPoints ?? new List<EntryPointDto>();
            double roofLengthM = layout.RoofContext?.LengthM ?? 0;
            double roofWidthM = layout.RoofContext?.WidthM ?? 0;
            double circulationWidthM = layout.DesignRules?.CirculationWidthM ?? 1.2;

            if (items.Count == 0) return (distances, unreachable);

            if (entries.Count == 0)
            {
                foreach (var it in items) if (it.Id != null) unreachable.Add(it.Id);
                return (distances, unreachable);
            }

            var grid = BuildOccupancyGrid(roofLengthM, roofWidthM, items, circulationWidthM);

            var starts = new List<int>();
            foreach (var ep in entries)
            {
                int k = EntryStartCell(ep, roofLengthM, roofWidthM, grid);
                if (k >= 0) starts.Add(k);
            }
            if (starts.Count == 0)
            {
                foreach (var it in items) if (it.Id != null) unreachable.Add(it.Id);
                return (distances, unreachable);
            }

            var (dist, parent) = Bfs(grid, starts);

            for (int idx = 0; idx < items.Count; idx++)
            {
                var it = items[idx];
                if (it.Id == null) continue;

                int best = NearestAccessCell(grid, idx, dist);
                if (best == -1) { unreachable.Add(it.Id); continue; }

                distances[it.Id] = PathLengthM(grid, parent, best);
            }

            return (distances, unreachable);
        }
    }
}
