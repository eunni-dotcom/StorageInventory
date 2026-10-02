using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// The main window paints its own opaque background in every theme. v1.0.0 used the Fluent theme's Mica window backdrop:
/// on Windows 11 22H2 and later that makes the window background and WPF's composition target transparent and leaves the
/// Desktop Window Manager to draw the backdrop behind the content. Where that composition failed, users saw a blank white
/// window, while UI Automation (and so every existing test) still found all the controls.
/// </summary>
public static class UiBackgroundTests
{
    private const int DwmwaSystemBackdropType = 38;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [Test]
    public static void The_window_paints_its_own_opaque_background_in_light_and_dark()
    {
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            UiHost.SetTheme(mode);
            var (w, _) = UiSetupTests.OpenWindow();
            try
            {
                UiHost.Settle();
                var (background, composition, hr, backdrop) = UiHost.Invoke(() =>
                {
                    var hwnd = new WindowInteropHelper(w).Handle;
                    var result = DwmGetWindowAttribute(hwnd, DwmwaSystemBackdropType, out var type, sizeof(int));
                    return (w.Background, HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor, result, type);
                });
                Assert.True(background is SolidColorBrush { Color.A: 255, Opacity: 1.0 }, $"{mode}: the window background is an opaque solid brush, was {Describe(background)}");
                Assert.Equal((byte)255, composition.A, $"{mode}: WPF's composition target is opaque ({composition})");
                // DWMSBT_MAINWINDOW (Mica) = 2, DWMSBT_TRANSIENTWINDOW (Acrylic) = 3, DWMSBT_TABBEDWINDOW = 4. Windows before 11 22H2
                // do not know the attribute (hr != 0), which also means no backdrop.
                Assert.True(hr != 0 || backdrop is not (2 or 3 or 4), $"{mode}: no Desktop Window Manager backdrop behind the client area (DWMWA_SYSTEMBACKDROP_TYPE = {backdrop})");
            }
            finally
            {
                UiSetupTests.Close(w);
                UiHost.SetTheme(ThemeMode.System);
            }
        }
    }

    private static string Describe(Brush? b) => b is SolidColorBrush s ? $"SolidColorBrush {s.Color} at opacity {s.Opacity}" : b?.GetType().Name ?? "null";
}
