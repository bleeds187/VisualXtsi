using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace VisualXtsi.UI
{
    /// <summary>Help > About VisualXtsi: version, libclang version, paths and license; the details can be copied.</summary>
    internal sealed class AboutDialog : DialogWindow
    {
        private readonly TextBox _details;

        public AboutDialog()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Title = "About VisualXtsi";
            Width = 600;
            SizeToContent = SizeToContent.Height;
            MinWidth = 400;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            ShowInTaskbar = false;
            DialogTheme.Apply(this);

            var root = new StackPanel { Margin = new Thickness(14) };
            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            heading.Children.Add(new CrispImage { Moniker = KnownMonikers.AboutBox, Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0) });
            var title = new TextBlock
            {
                Text = $"VisualXtsi {SystemInfo.Version}",
                FontSize = FontSize * 1.6,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            heading.Children.Add(title);
            root.Children.Add(heading);
            root.Children.Add(new TextBlock
            {
                Text = "A C++ and Unreal Engine productivity extension for large, multi-platform codebases " +
                       "(UE 4.27 and UE5). Parsing by libclang (LLVM).",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
            root.Children.Add(new TextBlock
            {
                Text = "MIT License. Copyright (c) 2026 Jason Eubank. Provided \"as is\", without warranty of any kind.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            _details = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 260,
                Text = "Collecting...",
                Padding = new Thickness(4),
            };
            var detailsLabel = new Label { Content = "_Details", Target = _details, Padding = new Thickness(0, 12, 0, 4) };
            detailsLabel.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            AutomationProperties.SetLabeledBy(_details, detailsLabel);
            root.Children.Add(detailsLabel);
            root.Children.Add(_details);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var copy = new Button { Content = "_Copy Details", MinWidth = 90, Padding = new Thickness(10, 2, 10, 2) };
            AutomationProperties.SetHelpText(copy, "Copies the details above to the clipboard.");
            copy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(_details.Text);
                    copy.Content = "_Copied";
                }
                catch (Exception ex)
                {
                    Log.Write($"Copying the About details failed: {ex.Message}");
                }
            };
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            ok.Click += (s, e) => Close();
            buttons.Children.Add(copy);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            Content = root;

            string vsVersion = SystemInfo.VisualStudioVersion();
            Loaded += (s, e) =>
            {
                ok.Focus();
                _ = ShowDetailsAsync(vsVersion);
            };
        }

        private async Task ShowDetailsAsync(string vsVersion)
        {
            string text = await Task.Run(() => SystemInfo.Format(SystemInfo.Collect(vsVersion)));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _details.Text = text;
        }
    }
}
