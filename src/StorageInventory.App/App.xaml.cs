using System.Windows;

namespace StorageInventory.App;

public partial class App : Application
{
    static App()
    {
        // The window paints its own opaque background. The Fluent theme's Mica backdrop (Windows 11 22H2 and later)
        // would make the window background and WPF's composition target transparent and leave the Desktop Window
        // Manager to draw the backdrop behind the content. Where that composition fails, v1.0.0 showed a blank white
        // window. Without the backdrop the theme paints its own solid background, as it already does on Windows 10.
        // WPF reads the switch once, when the first window is styled, so it is set before any window exists.
        AppContext.SetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", true);
    }
}
