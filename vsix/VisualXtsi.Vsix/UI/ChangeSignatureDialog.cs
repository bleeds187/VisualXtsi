using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Refactoring;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Change Signature: the parameters as rows (type, name, default, value at calls for new ones),
    /// reordered, added and removed with the buttons; the new signature is shown as it will read.
    /// </summary>
    internal sealed class ChangeSignatureDialog : DialogWindow
    {
        private readonly string _function;
        private readonly List<SignatureParameter> _parameters;
        private readonly Grid _rows = new Grid();
        private readonly TextBox _preview;
        private readonly TextBlock _problem;
        private readonly Button _ok;
        private int _selected = -1;

        public IReadOnlyList<SignatureParameter> Parameters => _parameters;

        public ChangeSignatureDialog(string function, string label, IEnumerable<SignatureParameter> parameters, string note)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _function = function;
            _parameters = parameters.ToList();
            Title = "Change Signature - VisualXtsi";
            Width = 760;
            ResizeMode = ResizeMode.NoResize;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            ShowInTaskbar = false;
            DialogTheme.Apply(this);
            SizeToContent = SizeToContent.Height;

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            if (!string.IsNullOrEmpty(note))
                root.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Opacity = 0.8 });

            foreach (double width in new[] { 2.2, 1.3, 1.2, 1.2 })
                _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            var body = new DockPanel();
            var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(side, Dock.Right);
            side.Children.Add(SideButton("_Add", Add));
            side.Children.Add(SideButton("_Remove", Remove));
            side.Children.Add(SideButton("Move _Up", () => Move(-1)));
            side.Children.Add(SideButton("Move _Down", () => Move(1)));
            body.Children.Add(side);
            body.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            root.Children.Add(body);

            _preview = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(4, 3, 4, 3),
            };
            AutomationProperties.SetName(_preview, "New signature");
            root.Children.Add(_preview);
            _problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetLiveSetting(_problem, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(_problem, "Signature problem");
            root.Children.Add(_problem);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
            _ok.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;
            Build();
        }

        private static Button SideButton(string text, System.Action click)
        {
            var b = new Button { Content = text, MinWidth = 96, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += (s, e) => click();
            return b;
        }

        private void Add()
        {
            int at = _selected >= 0 ? _selected + 1 : _parameters.Count;
            _parameters.Insert(at, new SignatureParameter { Type = "int32", Name = "NewParameter" + (_parameters.Count(p => p.IsNew) + 1) });
            _selected = at;
            Build();
        }

        private void Remove()
        {
            if (_selected < 0 || _selected >= _parameters.Count)
                return;
            _parameters.RemoveAt(_selected);
            _selected = System.Math.Min(_selected, _parameters.Count - 1);
            Build();
        }

        private void Move(int by)
        {
            int to = _selected + by;
            if (_selected < 0 || to < 0 || to >= _parameters.Count)
                return;
            (_parameters[_selected], _parameters[to]) = (_parameters[to], _parameters[_selected]);
            _selected = to;
            Build();
        }

        // The rows: a header, then per parameter its type, name, default and (new ones) value at calls.
        private void Build()
        {
            _rows.Children.Clear();
            _rows.RowDefinitions.Clear();
            _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string[] headers = { "Type", "Name", "Default", "Value at calls (new)" };
            for (int c = 0; c < headers.Length; c++)
                Place(new TextBlock { Text = headers[c], Margin = new Thickness(2, 0, 4, 4), Opacity = 0.8 }, 0, c);
            TextBox focus = null;
            for (int i = 0; i < _parameters.Count; i++)
            {
                SignatureParameter p = _parameters[i];
                int row = i + 1, index = i;
                _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var type = Cell(p.Type, $"Parameter {row} type", v => p.Type = v, index);
                var name = Cell(p.Name, $"Parameter {row} name", v => p.Name = v, index);
                var def = Cell(p.Default, $"Parameter {row} default", v => p.Default = v, index);
                var value = Cell(p.CallValue, $"Parameter {row} value at calls", v => p.CallValue = v, index);
                value.IsEnabled = p.IsNew;
                Place(type, row, 0);
                Place(name, row, 1);
                Place(def, row, 2);
                Place(value, row, 3);
                if (i == _selected)
                    focus = p.IsNew ? type : name;
            }
            Update();
            if (focus != null)
                focus.Loaded += (s, e) => focus.Focus(); // the row the buttons acted on keeps the focus
        }

        private TextBox Cell(string text, string name, System.Action<string> set, int index)
        {
            var box = new TextBox { Text = text, Margin = new Thickness(2, 0, 4, 4), Padding = new Thickness(3, 2, 3, 2), FontFamily = new FontFamily("Consolas") };
            AutomationProperties.SetName(box, name);
            box.TextChanged += (s, e) =>
            {
                set(box.Text.Trim());
                Update();
            };
            box.GotKeyboardFocus += (s, e) => _selected = index;
            return box;
        }

        private void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            _rows.Children.Add(element);
        }

        private void Update()
        {
            _preview.Text = SignatureSupport.Describe(_function, _parameters);
            string why = Problem();
            _problem.Text = why ?? "";
            _ok.IsEnabled = why == null;
        }

        private string Problem()
        {
            var names = new HashSet<string>();
            bool defaults = false;
            foreach (SignatureParameter p in _parameters)
            {
                if (p.Type.Length == 0)
                    return "Every parameter needs a type.";
                if (p.Name.Length > 0 && RenameSupport.Validate(p.Name, null) != null)
                    return $"\"{p.Name}\": " + RenameSupport.Validate(p.Name, null);
                if (p.Name.Length > 0 && !names.Add(p.Name))
                    return $"Two parameters are named {p.Name}.";
                if (p.IsNew && p.Name.Length == 0)
                    return "A new parameter needs a name.";
                if (p.IsNew && p.CallValue.Length == 0 && p.Default.Length == 0)
                    return $"Give {p.Name} a value for the existing calls, or a default.";
                if (defaults && p.Default.Length == 0)
                    return "Parameters after one with a default need defaults too.";
                defaults |= p.Default.Length > 0;
            }
            return null;
        }
    }
}
