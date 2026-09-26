namespace SportfyRevit
{
    /// <summary>One solid of a piece of furniture: a box, or a cylinder standing in the box x0..x1 / y0..y1 (a circle inscribed in it), from z0 to z1. Metres, the piece's centre at x = 0, y = 0, the ground at z = 0.</summary>
    internal readonly record struct ShapePart(string Kind, string Name, double X0, double Y0, double Z0, double X1, double Y1, double Z1)
    {
        public bool IsCylinder => Kind == "cylinder";
        public double Volume => IsCylinder ? Math.PI * Math.Pow(Math.Min(X1 - X0, Y1 - Y0) / 2, 2) * (Z1 - Z0) : (X1 - X0) * (Y1 - Y0) * (Z1 - Z0);
    }

    /// <summary>
    /// What a catalogue piece of furniture is made of, drawn from its category and its published size: a bench is a seat, a backrest and two end frames, a table a top on four legs,
    /// a bin a cylinder with a lid ring, a bollard a cylinder, a light a base, a pole and a head. Massing that is right about the dimensions anyone designs against (the footprint on
    /// the roof, the seat height, the overall height), not a product model; a firm's own family of the product, when the project has one, wins over this. No Revit in this file:
    /// SportifyFurnitureFamilyBuilder turns the parts into extrusions, and Tools/AddinCheck checks that every part stays inside the piece's box.
    /// </summary>
    internal static class FurnitureShape
    {
        public static IReadOnlyList<ShapePart> Parts(string? category, double lengthM, double widthM, double heightM)
        {
            double l = Math.Max(lengthM, 0.05), w = Math.Max(widthM, 0.05), h = Math.Max(heightM, 0.05);
            double x = l / 2, y = w / 2;
            double d = Math.Min(l, w);
            var parts = new List<ShapePart>();

            switch ((category ?? "").Trim().ToLowerInvariant())
            {
                case "bench":
                {
                    double seatTop = Math.Min(0.45, h * 0.55), seatThick = Math.Min(0.06, seatTop * 0.4), back = Math.Min(0.05, w * 0.2), leg = Math.Min(0.06, l * 0.2);
                    parts.Add(new("box", "seat", -x, -y + back, seatTop - seatThick, x, y, seatTop));
                    if (h > seatTop + 0.05) parts.Add(new("box", "backrest", -x, -y, seatTop, x, -y + back, h));
                    parts.Add(new("box", "left frame", -x, -y, 0, -x + leg, y, seatTop - seatThick));
                    parts.Add(new("box", "right frame", x - leg, -y, 0, x, y, seatTop - seatThick));
                    break;
                }
                case "table":
                {
                    double top = Math.Min(0.04, h * 0.2), leg = Math.Min(0.06, d * 0.2), inset = Math.Min(0.05, d * 0.1);
                    parts.Add(new("box", "top", -x, -y, h - top, x, y, h));
                    foreach (var (sx, sy, name) in new[] { (-1, -1, "leg 1"), (1, -1, "leg 2"), (1, 1, "leg 3"), (-1, 1, "leg 4") })
                    {
                        double cx = sx * (x - inset - leg / 2), cy = sy * (y - inset - leg / 2);
                        parts.Add(new("box", name, cx - leg / 2, cy - leg / 2, 0, cx + leg / 2, cy + leg / 2, h - top));
                    }
                    break;
                }
                case "bin":
                {
                    double r = d / 2, lid = Math.Min(0.06, h * 0.15);
                    parts.Add(new("cylinder", "body", -r, -r, 0, r, r, h - lid));
                    parts.Add(new("cylinder", "lid", -r, -r, h - lid, r, r, h));
                    break;
                }
                case "bollard":
                {
                    double r = d / 2;
                    parts.Add(new("cylinder", "post", -r, -r, 0, r, r, h));
                    break;
                }
                case "light":
                {
                    double r = d / 2, plate = Math.Min(0.05, h * 0.1), head = Math.Min(0.15, h * 0.2), pole = Math.Min(Math.Max(0.03, d * 0.12), r);
                    parts.Add(new("cylinder", "base", -r, -r, 0, r, r, plate));
                    parts.Add(new("cylinder", "pole", -pole, -pole, plate, pole, pole, h - head));
                    parts.Add(new("cylinder", "head", -r, -r, h - head, r, r, h));
                    break;
                }
                default:
                    parts.Add(new("box", "body", -x, -y, 0, x, y, h));
                    break;
            }
            return parts;
        }

        /// <summary>The family name for a product: the export's own (revit_family_name), else "Sportify - label", with the size in centimetres so that a product whose size changed in the catalogue is a new family, not the old one.</summary>
        public static string FamilyName(string? revitFamilyName, string? label, string? key, double lengthM, double widthM, double heightM)
        {
            var baseName = !string.IsNullOrWhiteSpace(revitFamilyName) ? revitFamilyName!.Trim() : "Sportify - " + (!string.IsNullOrWhiteSpace(label) ? label!.Trim() : key ?? "furniture");
            return $"{baseName} [{Math.Round(lengthM * 100):0}x{Math.Round(widthM * 100):0}x{Math.Round(heightM * 100):0} cm]";
        }
    }
}
