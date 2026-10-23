using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.PlatformUI;

namespace VisualXtsi.UI
{
    /// <summary>One entry of a <see cref="ColorPicker"/>: a named color, the default, or "Custom...".</summary>
    internal sealed class ColorChoice
    {
        public ColorChoice(string name, Color? color, bool custom = false)
        {
            Name = name;
            Color = color;
            IsCustom = custom;
        }

        public string Name { get; }
        public Color? Color { get; }
        public bool IsCustom { get; }
        public Brush Swatch => Color is Color c ? new SolidColorBrush(c) : Brushes.Transparent;

        /// <summary>The name with the hex value, so a color never has to be told by sight.</summary>
        public string Display => IsCustom ? Name : Color is Color c ? $"{Name} ({ColorPicker.Hex(c)})" : Name;

        public override string ToString() => Display;
    }

    /// <summary>
    /// A color box: a swatch plus an editable list of theme-aware named colors, the default (or
    /// "None") and "Custom..." (the Windows color dialog). A hex value ("#4EC9B0") or a palette
    /// name can be typed. The text always names the color, so its meaning never rests on the
    /// swatch alone.
    /// </summary>
    internal sealed class ColorPicker : DockPanel
    {
        private static readonly (string Name, uint Dark, uint Light)[] Palette =
        {
            ("Light blue", 0x9CDCFE, 0x1F377F), ("Steel blue", 0x8FB8DE, 0x3B6EA5), ("Lavender", 0xC5A5F5, 0x6F3FA0),
            ("Gold", 0xD7BA7D, 0x8A5A00), ("Teal", 0x4EC9B0, 0x2B91AF), ("Sage", 0xB8D7A3, 0x2F7F4F),
            ("Silver", 0xC8C8C8, 0x4F4F4F), ("Pale yellow", 0xDCDCAA, 0x74531F), ("Apricot", 0xFFD68F, 0x8A6100),
            ("Orange", 0xF2B86B, 0xA0522D), ("Rust", 0xE06C2B, 0xB04A00), ("Orchid", 0xC586C0, 0x8B2D8B),
            ("Pink", 0xF48FB1, 0xAD1457), ("Red", 0xF44747, 0xA31515), ("Green", 0x6A9955, 0x008000),
            ("White", 0xFFFFFF, 0x000000),
        };

        private readonly Rectangle _swatch;
        private readonly ComboBox _combo;
        private readonly ColorChoice _default;
        private Color? _value;
        private bool _updating;

        /// <param name="defaultLabel">The null entry: "Default" (shown with <paramref name="defaultColor"/>) or "None".</param>
        public ColorPicker(string accessibleName, string defaultLabel, Color? defaultColor, bool lightTheme)
        {
            _default = new ColorChoice(defaultLabel, defaultColor);
            _swatch = new Rectangle { Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
            _swatch.SetResourceReference(Shape.StrokeProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            AutomationProperties.SetName(_swatch, "Swatch");
            SetDock(_swatch, Dock.Left);
            Children.Add(_swatch);

            var items = new List<ColorChoice> { _default };
            items.AddRange(Palette.Select(p => new ColorChoice(p.Name, FromRgb(lightTheme ? p.Light : p.Dark))));
            items.Add(new ColorChoice("Custom...", null, custom: true));
            _combo = new ComboBox
            {
                IsEditable = true,
                ItemsSource = items,
                ItemTemplate = ItemTemplate(),
                MinWidth = 150,
            };
            TextSearch.SetTextPath(_combo, nameof(ColorChoice.Display));
            AutomationProperties.SetName(_combo, accessibleName);
            AutomationProperties.SetHelpText(_combo, "Pick a named color, type a hex value such as #4EC9B0, or choose Custom... for the color dialog.");
            _combo.SelectionChanged += (s, e) => OnSelected();
            _combo.LostKeyboardFocus += (s, e) => ParseTyped();
            _combo.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && !_combo.IsDropDownOpen)
                    ParseTyped();
            };
            Children.Add(_combo);
            // The editable part is what gets focus: give it the box's name and help.
            _combo.Loaded += (s, e) =>
            {
                if (_combo.Template?.FindName("PART_EditableTextBox", _combo) is TextBox edit)
                {
                    AutomationProperties.SetName(edit, accessibleName);
                    AutomationProperties.SetHelpText(edit, AutomationProperties.GetHelpText(_combo));
                }
            };
            Show();
        }

        public event Action Changed;

        /// <summary>null: the default / none.</summary>
        public Color? Value
        {
            get => _value;
            set
            {
                _value = value;
                Show();
            }
        }

        public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private void OnSelected()
        {
            if (_updating || !(_combo.SelectedItem is ColorChoice choice))
                return;
            if (choice.IsCustom)
            {
                Color? picked = PickCustom();
                if (picked != null)
                    _value = picked;
            }
            else
            {
                _value = choice == _default ? null : choice.Color;
            }
            Show();
            Changed?.Invoke();
        }

        private void ParseTyped()
        {
            if (_updating)
                return;
            string text = _combo.Text.Trim();
            if (text == Describe(_value))
                return;
            Color? parsed = Parse(text, out bool valid);
            if (valid)
            {
                _value = parsed;
                Changed?.Invoke();
            }
            Show(); // also restores the text after an invalid entry
        }

        private Color? Parse(string text, out bool valid)
        {
            valid = true;
            if (text.Length == 0 || text.StartsWith(_default.Name, StringComparison.OrdinalIgnoreCase))
                return null;
            foreach (ColorChoice choice in _combo.ItemsSource.Cast<ColorChoice>())
                if (!choice.IsCustom && choice != _default && (text.Equals(choice.Name, StringComparison.OrdinalIgnoreCase) || text == choice.Display))
                    return choice.Color;
            string hex = text.TrimStart('#');
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                return FromRgb(rgb);
            valid = false;
            return _value;
        }

        private Color? PickCustom()
        {
            using (var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true })
            {
                Color start = _value ?? _default.Color ?? Colors.Gray;
                dialog.Color = System.Drawing.Color.FromArgb(start.R, start.G, start.B);
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    ? Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)
                    : (Color?)null;
            }
        }

        private void Show()
        {
            _updating = true;
            try
            {
                ColorChoice match = _value == null ? _default
                    : _combo.ItemsSource.Cast<ColorChoice>().FirstOrDefault(c => !c.IsCustom && c != _default && c.Color == _value);
                _combo.SelectedItem = match;
                _combo.Text = Describe(_value);
                Color? shown = _value ?? _default.Color;
                _swatch.Fill = shown is Color c ? new SolidColorBrush(c) : Brushes.Transparent;
                _swatch.StrokeDashArray = shown == null ? new DoubleCollection { 2, 2 } : null;
            }
            finally
            {
                _updating = false;
            }
        }

        private string Describe(Color? value)
        {
            if (value == null)
                return _default.Display;
            ColorChoice named = _combo.ItemsSource.Cast<ColorChoice>().FirstOrDefault(c => !c.IsCustom && c != _default && c.Color == value);
            return named?.Display ?? Hex(value.Value);
        }

        private static Color FromRgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static DataTemplate ItemTemplate()
        {
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var swatch = new FrameworkElementFactory(typeof(Rectangle));
            swatch.SetValue(WidthProperty, 14.0);
            swatch.SetValue(HeightProperty, 14.0);
            swatch.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
            swatch.SetValue(Shape.StrokeThicknessProperty, 1.0);
            swatch.SetResourceReference(Shape.StrokeProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            swatch.SetBinding(Shape.FillProperty, new Binding(nameof(ColorChoice.Swatch)));
            panel.AppendChild(swatch);
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ColorChoice.Display)));
            panel.AppendChild(text);
            return new DataTemplate { VisualTree = panel };
        }
    }
}
