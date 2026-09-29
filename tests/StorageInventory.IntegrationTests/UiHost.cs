using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StorageInventory.IntegrationTests;

/// <summary>TEST CODE ONLY. Hosts the real WPF application objects on one STA thread so tests can drive the actual
/// windows and render them to PNG (the test session cannot capture the screen).</summary>
public static class UiHost
{
    private static readonly Lazy<Dispatcher> UiDispatcher = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new StorageInventory.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();   // theme and resources from App.xaml (StartupUri is not used: Run() is never called)
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "UI tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static T Invoke<T>(Func<T> func) => UiDispatcher.Value.Invoke(func);

    public static void Invoke(Action action) => UiDispatcher.Value.Invoke(action);

    /// <summary>Waits (without blocking the UI thread) until the condition holds on the UI thread.</summary>
    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (Invoke(condition)) return true;
            Thread.Sleep(50);
        }
        return Invoke(condition);
    }

    /// <summary>Lets pending layout and rendering finish.</summary>
    public static void Settle() => UiDispatcher.Value.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    public static void SetTheme(ThemeMode mode) => Invoke(() => Application.Current.ThemeMode = mode);

    /// <summary>Renders a window's content to a PNG.</summary>
    public static void Render(Window window, string path)
    {
        Settle();
        Invoke(() =>
        {
            // Render the whole scrollable page (not just the visible part) on the window's own themed background.
            var content = window.Content is System.Windows.Controls.ScrollViewer { Content: FrameworkElement page } ? page : (FrameworkElement)window.Content;
            var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
            var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var background = window.Background is SolidColorBrush { Color.A: > 0 } b ? b
                    : window.TryFindResource("ApplicationBackgroundBrush") as Brush ?? SystemColors.WindowBrush;
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            encoder.Save(file);
        });
    }
}
