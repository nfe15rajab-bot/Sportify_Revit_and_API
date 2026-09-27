using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace SportfyRevit
{
    /// <summary>
    /// The small dialog every Kinetics command opens with: which analysis is driving the dynamic family this time, and (for the commands that place something)
    /// which kind of dynamic unit to make. The "What to make" combo is filtered to the kinds that priority actually offers (KineticKinds.For) — Sun offers the
    /// overhead louvre, slat/fin screens, sail and roller fence; Wind & Erosion offers the wind-break screen. A kind not yet built (KineticKindInfo.Built) still
    /// appears — a designer can place it in the web app's Kinetics tab for spatial planning either way — but says so plainly once picked here (KineticsCommands.Ask).
    /// "Structural" was removed rather than kept as a third, empty option: unlike Wind & Erosion it names no concrete dynamic unit yet, so listing it invited a
    /// pick that could only ever dead-end.
    /// </summary>
    internal sealed class KineticsPriorityDialog : Window
    {
        internal sealed class PriorityOption
        {
            public string Key = "";       // "sun" | "wind_erosion"
            public string Label = "";
            public override string ToString() => Label;
        }

        internal sealed class Choice
        {
            public string Priority = "sun";
            public KineticKind Kind = KineticKind.Overhead;
            public SailShapeChoice SailShape = SailShapeChoice.Auto;
        }

        internal static readonly PriorityOption[] Options =
        {
            new PriorityOption { Key = "sun", Label = "Sun — louvres, screens, sails, fences" },
            new PriorityOption { Key = "wind_erosion", Label = "Wind & Erosion — wind-break screen" },
        };

        readonly ComboBox _combo;
        readonly ComboBox? _kind;
        readonly ComboBox? _shape;
        static SailShapeChoice _lastShape = SailShapeChoice.Auto;

        public Choice? Chosen { get; private set; }

        public KineticsPriorityDialog(string title, string lastPriorityKey, KineticKind lastKind, bool askKind)
        {
            Title = title + (askKind ? " — what to make" : " — priority");
            Width = 500;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var dock = new DockPanel { Margin = new Thickness(16) };

            var intro = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Text = "Which analysis should Kinetics answer? Its own recommendation (position, size, the values driving the family) comes straight from that analysis's last published result.",
            };
            DockPanel.SetDock(intro, Dock.Top);
            dock.Children.Add(intro);

            var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(cancel, Dock.Right);
            DockPanel.SetDock(ok, Dock.Right);
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            dock.Children.Add(buttons);

            var stack = new StackPanel();
            _combo = new ComboBox { Margin = new Thickness(0, 0, 0, 4), ItemsSource = Options, DisplayMemberPath = null };
            _combo.SelectedItem = Array.Find(Options, o => o.Key == lastPriorityKey) ?? Options[0];
            stack.Children.Add(_combo);

            if (askKind)
            {
                stack.Children.Add(new TextBlock { Text = "What to make", Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold });
                var kind = new ComboBox();
                stack.Children.Add(kind);
                var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)) };
                void Refresh() => hint.Text = kind.SelectedItem is KineticKindInfo info ? "Goes " + info.Hint + "." : "";

                // The kind picker is filtered to what the chosen priority actually offers (Sun: louvre, screens, sail,
                // fence; Wind & Erosion: the wind-break screen) — repopulated whenever the priority combo changes, so
                // picking Wind & Erosion never shows a Sun-only kind and vice versa.
                void RepopulateKind()
                {
                    var priorityKey = (_combo.SelectedItem as PriorityOption)?.Key ?? Options[0].Key;
                    var offered = KineticKinds.For(priorityKey);
                    kind.ItemsSource = offered;
                    var remembered = Array.Find(offered, k => k.Kind == lastKind);
                    kind.SelectedItem = remembered ?? (offered.Length > 0 ? offered[0] : null);
                    Refresh();
                }
                _combo.SelectionChanged += (_, _) => RepopulateKind();
                RepopulateKind();
                kind.SelectionChanged += (_, _) => Refresh();
                stack.Children.Add(hint);
                _kind = kind;
                var shapeLabel = new TextBlock { Text = "Sail shape", Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold };
                var shape = new ComboBox { ItemsSource = new[] { "Automatic: a triangle beside a garden, else a rectangle", "Rectangle: four masts on two parallel tracks", "Triangle: three masts on an L of two tracks" } };
                shape.SelectedIndex = (int)_lastShape;
                stack.Children.Add(shapeLabel); stack.Children.Add(shape);
                void ShowShape() { var v = kind.SelectedItem is KineticKindInfo k && k.Kind == KineticKind.Sail ? Visibility.Visible : Visibility.Collapsed; shapeLabel.Visibility = v; shape.Visibility = v; }
                kind.SelectionChanged += (_, _) => ShowShape();
                ShowShape();
                _shape = shape;
            }
            dock.Children.Add(stack);

            Content = dock;

            ok.Click += (_, _) =>
            {
                var picked = (PriorityOption?)_combo.SelectedItem;
                Chosen = new Choice
                {
                    Priority = picked?.Key ?? Options[0].Key,
                    Kind = (_kind?.SelectedItem as KineticKindInfo)?.Kind ?? lastKind,
                    SailShape = _shape != null && _shape.SelectedIndex >= 0 ? (SailShapeChoice)_shape.SelectedIndex : SailShapeChoice.Auto,
                };
                _lastShape = Chosen.SailShape;
                DialogResult = true;
            };
        }

        /// <summary>The unattended walk-through (KineticsWalkthrough) answers for the person: what it returns is what the dialog would have chosen. Null in normal use.</summary>
        internal static Func<bool, Choice?>? AutoAnswer;

        /// <summary>Shows the dialog; returns what was chosen, or null if cancelled.</summary>
        public static Choice? Ask(string title, IntPtr owner, string lastPriorityKey, KineticKind lastKind, bool askKind)
        {
            if (AutoAnswer != null) return AutoAnswer(askKind);
            var dialog = new KineticsPriorityDialog(title, lastPriorityKey, lastKind, askKind);
            if (owner != IntPtr.Zero) new WindowInteropHelper(dialog) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.Chosen : null;
        }
    }
}
