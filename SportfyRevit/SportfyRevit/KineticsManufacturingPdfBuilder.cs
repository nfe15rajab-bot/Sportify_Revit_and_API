using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SportfyRevit
{
    /// <summary>
    /// The Kinetics ribbon's "Bill of Materials" button, as a PDF a machinist can be handed directly: one section
    /// per placed unit, each with its real parts (KineticsBillOfMaterials.RowsFull — structure, blade/panel/
    /// curtain, and the mechanism's own hardware together) and, above the parts, a plain-language account of how
    /// that unit actually moves, built from the exact same Findings the Improve tab and the web app's Kinetics
    /// results already show — no separate narrative invented for this document. Same QuestPDF house style as
    /// AnalysisReportPdfBuilder (A4, 2cm margins, tone-neutral here since nothing here is pass/fail).
    /// </summary>
    internal static class KineticsManufacturingPdfBuilder
    {
        public static void Generate(string outputPath, List<KineticUnit> units, bool preliminary, string? preliminaryNote)
        {
            QuestPDF.Settings.License = LicenseType.Community;

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken3));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("Sportify — Kinetics Manufacturing Sheet").FontSize(20).Bold().FontColor(Colors.Blue.Darken2);
                        col.Item().Text($"{units.Count} assembly(ies), for fabrication and assembly").FontSize(11).SemiBold();
                        col.Item().Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(9).FontColor(Colors.Grey.Medium);
                        col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                    });

                    page.Content().PaddingTop(10).Column(col =>
                    {
                        col.Spacing(16);
                        col.Item().Element(c => NotesSection(c, preliminary, preliminaryNote));
                        for (var i = 0; i < units.Count; i++)
                        {
                            var unit = units[i];
                            col.Item().Element(c => UnitSection(c, unit, i + 1));
                            if (i < units.Count - 1) col.Item().PageBreak();
                        }
                    });

                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.Span("Sportify — kinetics manufacturing sheet · page ");
                        x.CurrentPageNumber();
                        x.Span(" / ");
                        x.TotalPages();
                    });
                });
            }).GeneratePdf(outputPath);
        }

        static void NotesSection(IContainer container, bool preliminary, string? preliminaryNote)
        {
            container.Background("#fff4d6").Padding(8).Column(col =>
            {
                col.Item().Text("Before cutting").FontSize(10).Bold().FontColor("#7a4b00");
                col.Item().Text("Dimensions and mechanism sizing below come from the same calculation Revit's Kinetics ribbon builds from, not a rough estimate. Every \"assumed\" material entry is an honest placeholder, not a specification — confirm the real section, alloy/grade and finish before ordering. Once a real part is drawn in SOLIDWORKS, enter its own mass and section back into SportifyKineticsInputs.json (blade_mass_per_m_kg / blade_inertia_m4) so the deflection and actuator checks use it instead of the built-in estimate.")
                    .FontSize(8).FontColor("#7a4b00");
                if (preliminary)
                    col.Item().PaddingTop(3).Text("PRELIMINARY — " + (preliminaryNote ?? "some inputs are built-in values nobody has confirmed yet."))
                        .FontSize(8).Bold().FontColor("#7a4b00");
            });
        }

        static void UnitSection(IContainer container, KineticUnit unit, int index)
        {
            var info = KineticKinds.Get(unit.Host.Kind);
            var findings = FindingsFor(unit);
            var rows = KineticsBillOfMaterials.RowsFull(unit);

            container.Column(col =>
            {
                col.Spacing(6);
                col.Item().BorderBottom(1).BorderColor(Colors.Grey.Darken1).PaddingBottom(4).Row(row =>
                {
                    row.RelativeItem().Text($"{index}. {info.Label}").FontSize(15).Bold();
                    row.ConstantItem(220).AlignRight().Text(unit.Host.Name).FontSize(9).FontColor(Colors.Grey.Medium);
                });
                col.Item().Text($"Build size {unit.Host.LengthM:0.0} x {unit.Host.DepthM:0.0} m, {unit.Host.HeightM:0.0} m high — shown at its {unit.StateLabel} state")
                    .FontSize(9).FontColor(Colors.Grey.Darken2);

                if (findings.Count > 0)
                {
                    col.Item().PaddingTop(6).Text("MOTION & MECHANISM").FontSize(10).Bold().FontColor(Colors.Blue.Darken2);
                    col.Item().Column(fc =>
                    {
                        fc.Spacing(2);
                        foreach (var f in findings) fc.Item().Text($"•  {f}").FontSize(8.5f);
                    });
                }

                if (rows.Count > 0)
                {
                    col.Item().PaddingTop(6).Text("PARTS FOR MANUFACTURE").FontSize(10).Bold().FontColor(Colors.Blue.Darken2);
                    col.Item().PaddingTop(2).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(1.5f);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(2.5f);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(5);
                        });
                        table.Header(header =>
                        {
                            header.Cell().Element(HeaderCell).Text("Part");
                            header.Cell().Element(HeaderCell).Text("Count");
                            header.Cell().Element(HeaderCell).Text("Length (m)");
                            header.Cell().Element(HeaderCell).Text("Section (mm)");
                            header.Cell().Element(HeaderCell).Text("Area (m²)");
                            header.Cell().Element(HeaderCell).Text("Material");
                        });
                        foreach (var r in rows)
                        {
                            table.Cell().Element(BodyCell).Text(r.Part);
                            table.Cell().Element(BodyCell).Text(r.Count);
                            table.Cell().Element(BodyCell).Text(r.LengthM);
                            table.Cell().Element(BodyCell).Text(r.SectionMm);
                            table.Cell().Element(BodyCell).Text(r.AreaM2);
                            table.Cell().Element(BodyCell).Text(r.Material).FontSize(7.5f);
                        }
                    });
                }
            });

            static IContainer HeaderCell(IContainer c) => c.DefaultTextStyle(x => x.Bold().FontSize(8.5f)).PaddingVertical(4).BorderBottom(1).BorderColor(Colors.Grey.Darken1);
            static IContainer BodyCell(IContainer c) => c.DefaultTextStyle(x => x.FontSize(8.5f)).PaddingVertical(3).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
        }

        static List<string> FindingsFor(KineticUnit unit)
        {
            if (unit.Fence != null) return unit.Fence.Findings;
            if (unit.Sail != null) return unit.Sail.Findings;
            if (unit.Louvre != null) return unit.Louvre.Findings;
            return new List<string>();
        }
    }
}
