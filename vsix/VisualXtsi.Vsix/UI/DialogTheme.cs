using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.Win32;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Makes a dialog follow the Visual Studio theme (including the high-contrast themes, which
    /// map these colors to the Windows system colors), the environment font and the Windows
    /// "Make text bigger" setting. Standard controls get Visual Studio's themed dialog styles;
    /// list views and their column headers get the styles below.
    /// </summary>
    internal static class DialogTheme
    {
        private const string ListStyles = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                    xmlns:vsui='clr-namespace:Microsoft.VisualStudio.PlatformUI;assembly=Microsoft.VisualStudio.Shell.15.0'>
  <Style x:Key='VxFocusVisual'>
    <Setter Property='Control.Template'>
      <Setter.Value>
        <ControlTemplate>
          <Rectangle Margin='1' StrokeThickness='1' StrokeDashArray='1 2' SnapsToDevicePixels='True'
                     Stroke='{DynamicResource {x:Static vsui:EnvironmentColors.ToolWindowTextBrushKey}}' />
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ListView'>
    <Setter Property='Background' Value='{DynamicResource {x:Static vsui:EnvironmentColors.ToolWindowBackgroundBrushKey}}' />
    <Setter Property='Foreground' Value='{DynamicResource {x:Static vsui:EnvironmentColors.ToolWindowTextBrushKey}}' />
    <Setter Property='BorderBrush' Value='{DynamicResource {x:Static vsui:EnvironmentColors.ToolWindowBorderBrushKey}}' />
    <Setter Property='BorderThickness' Value='1' />
    <Setter Property='FocusVisualStyle' Value='{StaticResource VxFocusVisual}' />
    <!-- The grid view's own scroll viewer, which holds the column headers (Visual Studio's themed
         dialog styles give list views a template without them). -->
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ListView'>
          <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='{TemplateBinding BorderThickness}' SnapsToDevicePixels='True'>
            <ScrollViewer Style='{DynamicResource {x:Static GridView.GridViewScrollViewerStyleKey}}' Focusable='False'>
              <ItemsPresenter />
            </ScrollViewer>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='GridViewColumnHeader'>
    <Setter Property='Background' Value='{DynamicResource {x:Static vsui:HeaderColors.DefaultBrushKey}}' />
    <Setter Property='Foreground' Value='{DynamicResource {x:Static vsui:HeaderColors.DefaultTextBrushKey}}' />
    <Setter Property='BorderBrush' Value='{DynamicResource {x:Static vsui:HeaderColors.SeparatorLineBrushKey}}' />
    <Setter Property='HorizontalContentAlignment' Value='Left' />
    <Setter Property='Padding' Value='6,3,6,3' />
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='GridViewColumnHeader'>
          <Grid>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                    BorderThickness='0,0,1,1' Padding='{TemplateBinding Padding}'>
              <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' RecognizesAccessKey='True'
                                VerticalAlignment='Center' />
            </Border>
            <Thumb x:Name='PART_HeaderGripper' HorizontalAlignment='Right' Width='6' Margin='0,0,-3,0' Cursor='SizeWE'>
              <Thumb.Template>
                <ControlTemplate TargetType='Thumb'><Border Background='Transparent' /></ControlTemplate>
              </Thumb.Template>
            </Thumb>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource {x:Static vsui:HeaderColors.MouseOverBrushKey}}' />
              <Setter Property='Foreground' Value='{DynamicResource {x:Static vsui:HeaderColors.MouseOverTextBrushKey}}' />
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource {x:Static vsui:HeaderColors.MouseDownBrushKey}}' />
              <Setter Property='Foreground' Value='{DynamicResource {x:Static vsui:HeaderColors.MouseDownTextBrushKey}}' />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <!-- Selected rows are highlighted and outlined, so selection does not depend on color alone,
       and stay highlighted while typing in the search box. -->
  <Style TargetType='ListViewItem'>
    <Setter Property='Foreground' Value='{DynamicResource {x:Static vsui:EnvironmentColors.ToolWindowTextBrushKey}}' />
    <Setter Property='FocusVisualStyle' Value='{StaticResource VxFocusVisual}' />
    <Setter Property='Padding' Value='0,1,0,1' />
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ListViewItem'>
          <Border x:Name='Bd' Background='Transparent' BorderBrush='Transparent' BorderThickness='1'
                  Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <GridViewRowPresenter Columns='{TemplateBinding GridView.ColumnCollection}' Content='{TemplateBinding Content}' />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource {x:Static vsui:TreeViewColors.SelectedItemActiveBrushKey}}' />
              <Setter TargetName='Bd' Property='BorderBrush' Value='{DynamicResource {x:Static vsui:TreeViewColors.SelectedItemActiveTextBrushKey}}' />
              <Setter Property='Foreground' Value='{DynamicResource {x:Static vsui:TreeViewColors.SelectedItemActiveTextBrushKey}}' />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        private static ResourceDictionary _listStyles;

        public static void Apply(Window window)
        {
            ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(window, true);
            window.SetResourceReference(Window.BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            window.SetResourceReference(Window.ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            window.SetResourceReference(TextElement.FontFamilyProperty, Microsoft.VisualStudio.Shell.VsFonts.EnvironmentFontFamilyKey);
            window.FontSize = FontSize(window);
            window.SetResourceReference(Microsoft.VisualStudio.PlatformUI.ImageThemingUtilities.ImageBackgroundColorProperty,
                                        EnvironmentColors.ToolWindowBackgroundColorKey);
            window.UseLayoutRounding = true;
            // Center over the owner ourselves, once the size is known (SizeToContent dialogs), unless
            // a remembered position was restored.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Loaded += (s, e) =>
            {
                if (DialogLayout.WasRestored(window))
                    return; // placed already (remembered position)
                // Over Visual Studio's main window (the dialog owner VS assigns is not always it).
                IntPtr ownerHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                Rect owner = SystemParameters.WorkArea;
                if (ownerHandle != IntPtr.Zero && GetWindowRect(ownerHandle, out RECT r))
                {
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                    owner = new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY,
                                     (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
                }
                window.Left = owner.Left + Math.Max(0, (owner.Width - window.ActualWidth) / 2);
                window.Top = owner.Top + Math.Max(0, (owner.Height - window.ActualHeight) / 2);
            };
            KeepBoundsWhileOpening(window);
        }

        /// <summary>
        /// Right after a dialog opens, something outside its thread moves and resizes it to an
        /// unrelated size (seen in Visual Studio 17.14). Moves and resizes sent from other threads
        /// are ignored for the first two seconds; the user's own resizing runs on the dialog's
        /// thread and is never affected.
        /// </summary>
        private static void KeepBoundsWhileOpening(Window window)
        {
            int shownAt = 0;
            window.ContentRendered += (s, e) => shownAt = Environment.TickCount;
            window.SourceInitialized += (s, e) =>
            {
                var source = (HwndSource)PresentationSource.FromVisual(window);
                source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    const int WM_WINDOWPOSCHANGING = 0x0046, ISMEX_SEND = 1, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;
                    int flagsOffset = 2 * IntPtr.Size + 16; // WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags
                    if (msg == WM_WINDOWPOSCHANGING && (InSendMessageEx(IntPtr.Zero) & ISMEX_SEND) != 0 &&
                        (shownAt == 0 || unchecked(Environment.TickCount - shownAt) < 2000))
                    {
                        int flags = Marshal.ReadInt32(lParam, flagsOffset);
                        Marshal.WriteInt32(lParam, flagsOffset, flags | SWP_NOSIZE | SWP_NOMOVE);
                    }
                    return IntPtr.Zero;
                });
            };
        }

        [DllImport("user32.dll")]
        private static extern uint InSendMessageEx(IntPtr reserved);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        /// <summary>
        /// Gives list views under <paramref name="root"/> themed styles with column headers. Put on
        /// the content, not the window: the themed dialog styles are added to the window later and
        /// would win there.
        /// </summary>
        public static void UseListStyles(FrameworkElement root)
        {
            try
            {
                root.Resources.MergedDictionaries.Add(_listStyles ?? (_listStyles = (ResourceDictionary)XamlReader.Parse(ListStyles)));
            }
            catch (Exception ex)
            {
                Log.Write($"Loading the dialog styles failed: {ex.Message}");
            }
        }

        /// <summary>The environment font size, scaled by the Windows text size setting (100-225%).</summary>
        public static double FontSize(FrameworkElement element)
        {
            double size = element.TryFindResource(Microsoft.VisualStudio.Shell.VsFonts.EnvironmentFontSizeKey) is double s && s > 0 ? s : 12;
            return Math.Round(size * TextScale(), 1);
        }

        private static double TextScale()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility"))
                    if (key?.GetValue("TextScaleFactor") is int percent && percent >= 100 && percent <= 225)
                        return percent / 100.0;
            }
            catch (Exception)
            {
                // No setting: 100%.
            }
            return 1;
        }
    }
}
