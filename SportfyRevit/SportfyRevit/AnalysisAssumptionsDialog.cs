using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Sportify.Simulation.Structure;

namespace SportfyRevit
{
    /// <summary>
    /// The window that opens before a structural analysis: for each value the analysis rests on that the layout cannot know, the
    /// designer enters their own, or accepts the built-in one knowingly (with where it comes from: the standard's clause, published
    /// guidance, or the author's judgement), or leaves it for later, in which case the results are marked PRELIMINARY.
    ///
    /// The same choice is offered in the web app's Site tab ("Analysis assumptions", assumptions.js); this dialog is for deciding at
    /// the moment of running. It knows nothing of Revit: the owner is just a window handle.
    /// </summary>
    internal sealed class AnalysisAssumptionsDialog : Window
    {
        sealed class RowControls
        {
            public AssumptionRow Row = null!;
            public RadioButton Accept = null!, Mine = null!, Later = null!;
            public TextBox? Box;
            public ComboBox? Combo;
            public TextBlock Error = null!;
        }

        readonly List<RowControls> _rows = new List<RowControls>();
        readonly TextBlock _footer = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

        /// <summary>What the designer decided, once the dialog has closed with OK.</summary>
        public List<AssumptionDecision> Decisions { get; private set; } = new List<AssumptionDecision>();

        static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
        static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x0E, 0x8A, 0x4B));
        static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x2F, 0x5F, 0xBF));
        static readonly Brush Violet = new SolidColorBrush(Color.FromRgb(0x8B, 0x3F, 0xC9));
        static readonly Brush Cyan = new SolidColorBrush(Color.FromRgb(0x0E, 0x74, 0x90));
        static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5B, 0x5F, 0x73));

        public AnalysisAssumptionsDialog(string title, IReadOnlyList<AssumptionRow> rows, string analysis)
        {
            Title = title + " — assumptions";
            Width = 720;
            SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height * 0.92);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var dock = new DockPanel { Margin = new Thickness(16) };

            var intro = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Text = "This analysis rests on a few values the layout cannot know. For each, enter your own value, or accept the built-in one knowingly. " +
                       "Anything left unconfirmed is named in the results, which are then marked PRELIMINARY: a red bay against a placeholder capacity is not a verdict. " +
                       "Values entered here last for this Revit session; enter them in the app's Site tab to keep them with the layout.",
            };
            DockPanel.SetDock(intro, Dock.Top);
            dock.Children.Add(intro);

            var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            var run = new Button { Content = "Run the analysis", IsDefault = true, MinWidth = 130, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            var all = new Button { Content = "Accept all built-in values", Padding = new Thickness(10, 5, 10, 5) };
            DockPanel.SetDock(cancel, Dock.Right);
            DockPanel.SetDock(run, Dock.Right);
            DockPanel.SetDock(all, Dock.Left);
            buttons.Children.Add(cancel);
            buttons.Children.Add(run);
            buttons.Children.Add(all);
            buttons.Children.Add(_footer);
            dock.Children.Add(buttons);

            var stack = new StackPanel();
            foreach (var row in rows) stack.Children.Add(BuildRow(row));
            stack.Children.Add(BuildFixed(analysis));
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
            dock.Children.Add(scroll);
            Content = dock;

            all.Click += (_, _) =>
            {
                foreach (var r in _rows) if (r.Row.Decision.State != AnalysisAssumptions.Entered || !r.Mine.IsChecked.GetValueOrDefault()) r.Accept.IsChecked = true;
                Refresh();
            };
            run.Click += (_, _) => { if (Collect()) DialogResult = true; };
            Refresh();
        }

        // ------------------------------------------------------------------ one assumption

        UIElement BuildRow(AssumptionRow row)
        {
            var def = row.Def;
            var rc = new RowControls { Row = row };
            var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

            var head = new DockPanel();
            var pill = new TextBlock
            {
                Text = StatusText(def.Status).ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = StatusBrush(def.Status),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(pill, Dock.Right);
            head.Children.Add(pill);
            head.Children.Add(new TextBlock { Text = def.Label, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            panel.Children.Add(head);

            panel.Children.Add(new TextBlock { Text = "Built-in: " + Nice(def.DefaultText) + ". " + Nice(def.Basis), TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 3, 0, 0), FontSize = 12 });
            panel.Children.Add(new TextBlock { Text = Nice(def.Reference), TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 6), FontSize = 12 });

            var group = "assump-" + def.Key;
            rc.Accept = new RadioButton { Content = "Accept the built-in value (" + Nice(def.DefaultText) + ")", GroupName = group, Margin = new Thickness(0, 2, 0, 2) };
            var mineRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            rc.Mine = new RadioButton { Content = "My value:", GroupName = group, VerticalAlignment = VerticalAlignment.Center };
            mineRow.Children.Add(rc.Mine);
            if (def.Kind == "choice")
            {
                rc.Combo = new ComboBox { MinWidth = 260, Margin = new Thickness(8, 0, 0, 0) };
                foreach (var c in def.Choices) rc.Combo.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Key });
                if (row.Decision.State == AnalysisAssumptions.Entered)
                    foreach (ComboBoxItem item in rc.Combo.Items) if ((string)item.Tag == row.Decision.Value) rc.Combo.SelectedItem = item;
                rc.Combo.SelectionChanged += (_, _) => { rc.Mine.IsChecked = true; Refresh(); };
                mineRow.Children.Add(rc.Combo);
            }
            else
            {
                rc.Box = new TextBox { MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(3), Text = row.Decision.State == AnalysisAssumptions.Entered ? row.Decision.Value : "" };
                rc.Box.TextChanged += (_, _) => { if (rc.Box.IsKeyboardFocusWithin) rc.Mine.IsChecked = true; Refresh(); };
                mineRow.Children.Add(rc.Box);
                mineRow.Children.Add(new TextBlock { Text = Nice(def.Unit), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Muted });
            }
            rc.Later = new RadioButton { Content = "Not confirmed yet (results will be marked PRELIMINARY)", GroupName = group, Margin = new Thickness(0, 2, 0, 0) };
            rc.Error = new TextBlock { Foreground = Brushes.Firebrick, FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

            switch (row.Decision.State)
            {
                case AnalysisAssumptions.Entered: rc.Mine.IsChecked = true; break;
                case AnalysisAssumptions.Accepted: rc.Accept.IsChecked = true; break;
                default: rc.Later.IsChecked = true; break;
            }
            rc.Accept.Checked += (_, _) => Refresh();
            rc.Mine.Checked += (_, _) => Refresh();
            rc.Later.Checked += (_, _) => Refresh();

            panel.Children.Add(rc.Accept);
            panel.Children.Add(mineRow);
            panel.Children.Add(rc.Later);
            panel.Children.Add(rc.Error);
            _rows.Add(rc);

            return new Border
            {
                Child = panel,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xD8, 0xE6)),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 8),
            };
        }

        UIElement BuildFixed(string analysis)
        {
            var list = AnalysisAssumptions.FixedConstants.Where(f => f.Analyses.Split(' ').Contains(analysis)).ToList();
            var stack = new StackPanel { Margin = new Thickness(8, 4, 0, 4) };
            stack.Children.Add(new TextBlock
            {
                Text = "Fixed in the model, listed so their source is visible. Changing one is a change to the model, not to this project.",
                TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
            });
            foreach (var f in list)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5), FontSize = 12 };
                line.Inlines.Add(new System.Windows.Documents.Run(f.Label + ": ") { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new System.Windows.Documents.Run(Nice(f.DefaultText) + "  "));
                line.Inlines.Add(new System.Windows.Documents.Run(StatusText(f.Status).ToUpperInvariant()) { Foreground = StatusBrush(f.Status), FontWeight = FontWeights.Bold, FontSize = 10.5 });
                line.Inlines.Add(new System.Windows.Documents.LineBreak());
                line.Inlines.Add(new System.Windows.Documents.Run(Nice(f.Reference)) { Foreground = Muted });
                stack.Children.Add(line);
            }
            return new Expander { Header = $"Built-in constants of the model ({list.Count})", Content = stack, Margin = new Thickness(0, 4, 0, 4) };
        }

        // ------------------------------------------------------------------ state

        void Refresh()
        {
            var open = 0;
            foreach (var r in _rows)
            {
                if (r.Later.IsChecked == true) open++;
                r.Error.Visibility = Visibility.Collapsed;
            }
            _footer.Text = open == 0
                ? "Every input is confirmed."
                : $"{open} input(s) unconfirmed: the results will be marked PRELIMINARY.";
            _footer.Foreground = open == 0 ? Green : Amber;
        }

        /// <summary>Reads the controls into <see cref="Decisions"/>; false (with the reason shown under the row) when a value is not valid.</summary>
        bool Collect()
        {
            var result = new List<AssumptionDecision>();
            var ok = true;
            foreach (var r in _rows)
            {
                var d = new AssumptionDecision { Key = r.Row.Def.Key };
                if (r.Accept.IsChecked == true) d.State = AnalysisAssumptions.Accepted;
                else if (r.Mine.IsChecked == true)
                {
                    var text = r.Combo != null ? (r.Combo.SelectedItem as ComboBoxItem)?.Tag as string : r.Box?.Text;
                    if (AnalysisAssumptionsPatcher.TryParse(r.Row.Def, text, out var normalised, out var error))
                    {
                        d.State = AnalysisAssumptions.Entered;
                        d.Value = normalised;
                    }
                    else
                    {
                        r.Error.Text = error;
                        r.Error.Visibility = Visibility.Visible;
                        ok = false;
                    }
                }
                result.Add(d);
            }
            if (ok) Decisions = result;
            return ok;
        }

        static string Nice(string? text) => (text ?? "").Replace("m2", "m²");

        static string StatusText(string status)
        {
            switch (status)
            {
                case AssumptionStatus.Standard: return "From a standard";
                case AssumptionStatus.Literature: return "Published guidance";
                case AssumptionStatus.Placeholder: return "Placeholder";
                case AssumptionStatus.Estimated: return "Estimated";
                default: return "Assumption";
            }
        }

        static Brush StatusBrush(string status)
        {
            switch (status)
            {
                case AssumptionStatus.Standard: return Green;
                case AssumptionStatus.Literature: return Blue;
                case AssumptionStatus.Placeholder: return Amber;
                case AssumptionStatus.Estimated: return Cyan;
                default: return Violet;
            }
        }

        // ------------------------------------------------------------------ the flow the commands use

        internal sealed class Outcome
        {
            public string Json = "";
            public bool Cancelled;
        }

        /// <summary>
        /// The layout with the designer's decisions in it, ready for the analysis. The dialog opens when some assumption is still
        /// unconfirmed, or when <paramref name="force"/> asks to review them; otherwise the layout comes back as it is (with this session's
        /// earlier decisions filled in). <paramref name="analysis"/> is "structural" or "dynamic".
        /// </summary>
        public static Outcome Prepare(string layoutJson, string analysis, string title, IntPtr owner, bool force)
        {
            var json = AssumptionsSession.ApplyRemembered(layoutJson);
            var rows = AnalysisAssumptionsPatcher.Read(json, analysis);
            if (rows.Count == 0 || (!force && !AnalysisAssumptionsPatcher.AnyUnconfirmed(rows)))
                return new Outcome { Json = json };

            var dialog = new AnalysisAssumptionsDialog(title, rows, analysis);
            if (owner != IntPtr.Zero) new WindowInteropHelper(dialog) { Owner = owner };
            if (dialog.ShowDialog() != true) return new Outcome { Json = json, Cancelled = true };

            AssumptionsSession.Remember(json, dialog.Decisions);
            return new Outcome { Json = AnalysisAssumptionsPatcher.Apply(json, dialog.Decisions) };
        }
    }
}
