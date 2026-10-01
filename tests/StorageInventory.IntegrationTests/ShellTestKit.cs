using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StorageInventory.App;
using StorageInventory.App.Services;
using StorageInventory.App.ViewModels;
using StorageInventory.App.Views;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>TEST CODE ONLY. Wraps the real scanner: counts calls, can report scripted progress, and can hold a scan
/// at a gate before Core starts (even when cancelled, as a scan busy listing one huge folder would), so tests can look
/// at a running scan for as long as they need without a large tree.</summary>
public sealed class GatedScanner(IStorageInventoryScanner inner) : IStorageInventoryScanner
{
    private TaskCompletionSource _gate = Opened();
    private int _calls;

    public GatedScanner() : this(new InventoryScanner())
    {
    }

    public int Calls => Volatile.Read(ref _calls);
    public IProgress<StorageScanProgress>? LastProgress { get; private set; }
    public CancellationToken LastToken { get; private set; }

    /// <summary>Reported as soon as a scan starts, before the gate.</summary>
    public StorageScanProgress? Script { get; set; }

    /// <summary>The next scans wait at the gate until <see cref="Open"/>.</summary>
    public void Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _gate.TrySetResult();

    public async Task<StorageScanResult> ScanAsync(StorageScanOptions options, IProgress<StorageScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        LastProgress = progress;
        LastToken = cancellationToken;
        if (Script is { } scripted) progress?.Report(scripted);
        await _gate.Task.ConfigureAwait(false);
        return await inner.ScanAsync(options, progress, cancellationToken).ConfigureAwait(false);
    }

    private static TaskCompletionSource Opened()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }
}

/// <summary>TEST CODE ONLY. v1's main window, loaded from v1's own markup (TEST-U4's reference).</summary>
public static class V1Reference
{
    /// <summary>The frozen C1 base the reference comes from.</summary>
    public const string BaseCommit = "bf3fa36c7c9f8f710e86762bbc20faaf9755ca02";

    /// <summary><c>git rev-parse bf3fa36:src/StorageInventory.App/MainWindow.xaml</c>.</summary>
    public const string BlobId = "d29fd70170a137dafb1981b8a24c3cbfccfd377d";

    public static string FilePath => Path.Combine(TestEnvironment.RepoRoot, "tests", "StorageInventory.IntegrationTests", "V1Baseline", "MainWindow.v1.xaml");

    public static byte[] Bytes() => File.ReadAllBytes(FilePath);

    /// <summary>Git's object id of a file's contents ("blob &lt;length&gt;\0" + bytes, SHA-1).</summary>
    public static string GitBlobId(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexStringLower(SHA1.HashData([.. header, .. bytes]));
    }

    /// <summary>
    /// v1's window as loose XAML. Two mechanical edits, neither of which reaches the visual tree: the x:Class directive
    /// is dropped (loose XAML has no code-behind) and the view-model namespace names its assembly. A test can also
    /// replace one piece of text, to show that the comparison notices.
    /// </summary>
    public static Window Load(string? replace = null, string? with = null)
    {
        var xaml = Encoding.UTF8.GetString(Bytes());
        xaml = ReplaceOnce(xaml, "x:Class=\"StorageInventory.App.MainWindow\"", "");
        xaml = ReplaceOnce(xaml, "\"clr-namespace:StorageInventory.App.ViewModels\"", "\"clr-namespace:StorageInventory.App.ViewModels;assembly=StorageInventory\"");
        if (replace is not null) xaml = ReplaceOnce(xaml, replace, with!);
        return (Window)XamlReader.Parse(xaml);
    }

    /// <summary>Shows v1's window over a view model, doing what v1's constructor did that affects what is shown: the
    /// view model as DataContext and focus on the source box once loaded.</summary>
    public static Window Open(MainViewModel vm, string? replace = null, string? with = null, double? width = null, double? height = null)
    {
        var w = Load(replace, with);
        if (width is { } wide) w.Width = wide;
        if (height is { } high) w.Height = high;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = 0;
        w.DataContext = vm;
        w.Loaded += (_, _) => ((UIElement)w.FindName("SourceBox")).Focus();
        w.Show();
        return w;
    }

    /// <summary>v1's page: the scroller's content.</summary>
    public static FrameworkElement Page(Window v1) => (FrameworkElement)((ScrollViewer)v1.Content).Content;

    private static string ReplaceOnce(string text, string from, string to)
    {
        var at = text.IndexOf(from, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(from, at + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException($"expected exactly one '{from}' in v1's markup");
        return text[..at] + to + text[(at + from.Length)..];
    }
}

/// <summary>TEST CODE ONLY. Opening shell windows and finding things in them.</summary>
public static class ShellHost
{
    public static MainWindow Open(ScanSession session) => UiHost.Invoke(() =>
    {
        var w = new MainWindow(session) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
        w.Show();
        return w;
    });

    public static ContentControl PageHost(MainWindow w) => (ContentControl)w.FindName("PageHost");

    public static ListBox NavigationList(MainWindow w) => (ListBox)w.FindName("NavigationList");

    public static Grid Root(MainWindow w) => (Grid)w.FindName("ShellRoot");

    public static ScanPage ScanPage(MainWindow w) => Single<ScanPage>(PageHost(w));

    /// <summary>The C2 page: the Scan page's scroller content, which is v1's page.</summary>
    public static FrameworkElement Page(ScanPage page) => (FrameworkElement)((ScrollViewer)page.Content).Content;

    public static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var deeper in All<T>(child)) yield return deeper;
        }
    }

    public static T Single<T>(DependencyObject root) where T : DependencyObject
    {
        var found = All<T>(root).ToList();
        return found.Count == 1 ? found[0] : throw new AssertionException($"expected one {typeof(T).Name}, found {found.Count}");
    }

    /// <summary>Logical descendants: unlike the visual tree, they exist inside a stage view that has never been shown.</summary>
    public static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var deeper in Logical<T>(child)) yield return deeper;
        }
    }

    public static Button Button(DependencyObject root, string automationName) =>
        Logical<Button>(root).Single(b => AutomationProperties.GetName(b) == automationName);

    /// <summary>Lets pending work (bindings, layout, weak-event clean-up) run, then collects garbage fully.</summary>
    public static void CollectGarbage()
    {
        for (var i = 0; i < 3; i++)
        {
            UiHost.Settle();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    /// <summary>Number of handlers attached to an ObservableObject's PropertyChanged event.</summary>
    public static int Subscribers(object observable)
    {
        var field = typeof(StorageInventory.App.Mvvm.ObservableObject).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ObservableObject.PropertyChanged backing field not found");
        return (field.GetValue(observable) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}

/// <summary>TEST CODE ONLY. A rendered image as raw BGRA pixels.</summary>
public sealed record RenderedImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>Renders an element on the window's own themed background, the way <see cref="UiHost.Render"/> does.
    /// With <paramref name="withMargin"/> the element's margin is included (a whole scrollable page).</summary>
    public static RenderedImage Of(FrameworkElement element, Window window, bool withMargin)
    {
        var m = withMargin ? element.Margin : new Thickness(0);
        var width = (int)Math.Ceiling(element.ActualWidth + m.Left + m.Right);
        var height = (int)Math.Ceiling(element.ActualHeight + m.Top + m.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = window.Background is SolidColorBrush { Color.A: > 0 } b ? b
                : window.TryFindResource("ApplicationBackgroundBrush") as Brush ?? SystemColors.WindowBrush;
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            // An explicit viewbox: the element's own layout box, whatever its content draws outside it.
            var box = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = box },
                null, new Rect(m.Left, m.Top, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return new RenderedImage(width, height, pixels);
    }

    public RenderedImage Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x + width > Width || y + height > Height) throw new ArgumentOutOfRangeException(nameof(width), $"crop {x},{y} {width}x{height} outside {Width}x{Height}");
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++) Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, pixels, row * width * 4, width * 4);
        return new RenderedImage(width, height, pixels);
    }

    /// <summary>Pixels that differ in any channel, and the box around them.</summary>
    public (int Count, Int32Rect Box) Differences(RenderedImage other)
    {
        if (other.Width != Width || other.Height != Height) return (int.MaxValue, Int32Rect.Empty);
        int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var i = 0; i < Width * Height; i++)
        {
            var o = i * 4;
            if (Pixels[o] == other.Pixels[o] && Pixels[o + 1] == other.Pixels[o + 1] && Pixels[o + 2] == other.Pixels[o + 2] && Pixels[o + 3] == other.Pixels[o + 3]) continue;
            count++;
            int x = i % Width, y = i / Width;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return (count, count == 0 ? Int32Rect.Empty : new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
    }

    /// <summary>The differing pixels in magenta over a faded copy of this frame.</summary>
    public RenderedImage DiffImage(RenderedImage other)
    {
        var pixels = new byte[Pixels.Length];
        for (var i = 0; i < Width * Height; i++)
        {
            var o = i * 4;
            var same = Pixels[o] == other.Pixels[o] && Pixels[o + 1] == other.Pixels[o + 1] && Pixels[o + 2] == other.Pixels[o + 2];
            pixels[o] = same ? (byte)(Pixels[o] / 4 + 160) : (byte)255;
            pixels[o + 1] = same ? (byte)(Pixels[o + 1] / 4 + 160) : (byte)0;
            pixels[o + 2] = same ? (byte)(Pixels[o + 2] / 4 + 160) : (byte)255;
            pixels[o + 3] = 255;
        }
        return new RenderedImage(Width, Height, pixels);
    }

    public void Save(string path)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, Pixels, Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(file);
    }
}

/// <summary>TEST CODE ONLY. What UI Automation clients and keyboard users see of a window.</summary>
public static class Accessibility
{
    /// <summary>UI Automation's control view of a window, one line per element, indented by depth. Elements for which
    /// <paramref name="skip"/> is true are left out together with everything inside them.</summary>
    public static List<string> ControlView(Window window, Func<AutomationPeer, bool> skip)
    {
        var lines = new List<string>();
        var root = UIElementAutomationPeer.CreatePeerForElement(window);
        Walk(root, 0, lines, skip);
        return lines;
    }

    private static void Walk(AutomationPeer peer, int depth, List<string> lines, Func<AutomationPeer, bool> skip)
    {
        foreach (var child in peer.GetChildren() ?? [])
        {
            if (skip(child)) continue;
            if (child.IsControlElement())
            {
                lines.Add(new string(' ', depth * 2) + Describe(child));
                Walk(child, depth + 1, lines, skip);
            }
            else
            {
                Walk(child, depth, lines, skip);   // not in the control view: its children take its place
            }
        }
    }

    private static string Describe(AutomationPeer p) =>
        $"{Try(() => p.GetAutomationControlType())} name='{Try(p.GetName)}' id='{Try(p.GetAutomationId)}' help='{Try(p.GetHelpText)}' " +
        $"live={(p is UIElementAutomationPeer u ? AutomationProperties.GetLiveSetting(u.Owner) : default)} enabled={Try(() => p.IsEnabled())} " +
        $"keyboard={Try(() => p.IsKeyboardFocusable())} key='{Try(p.GetAccessKey)}' label='{Try(() => p.GetLabeledBy() is UIElementAutomationPeer l ? l.GetName() : "")}'";

    /// <summary>Some peers (virtualised items, for example) throw for a property; that is recorded, not fatal, so the
    /// same peer in both windows still compares equal.</summary>
    private static string Try<T>(Func<T> read)
    {
        try { return read()?.ToString() ?? ""; }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or ElementNotAvailableException) { return "<" + ex.GetType().Name + ">"; }
    }

    /// <summary>The Tab order of a window: keyboard focus goes to the window's first tab stop, then moves Next (what
    /// the Tab key does) until it comes back round. Elements for which <paramref name="skip"/> is true are left out.
    /// Keyboard focus is cleared again afterwards.</summary>
    public static List<string> TabOrder(Window window, Func<DependencyObject, bool> skip)
    {
        var order = new List<string>();
        var seen = new HashSet<DependencyObject>();
        Keyboard.ClearFocus();
        window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        var current = Keyboard.FocusedElement as DependencyObject;
        while (current is UIElement element && seen.Add(current) && seen.Count < 500)
        {
            if (!skip(current)) order.Add(DescribeStop(current));
            element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            current = Keyboard.FocusedElement as DependencyObject;
        }
        Keyboard.ClearFocus();
        return order;
    }

    private static string DescribeStop(DependencyObject e)
    {
        var peer = e is UIElement u ? UIElementAutomationPeer.CreatePeerForElement(u) : null;
        return $"{e.GetType().Name} '{peer?.GetName() ?? ""}'";
    }

    public static bool IsInside(DependencyObject e, DependencyObject ancestor)
    {
        for (var d = e; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, ancestor)) return true;
        }
        return false;
    }
}

/// <summary>TEST CODE ONLY. Collects WPF's data-binding diagnostics ("System.Windows.Data Error: 40 : ...") while it
/// is installed. Messages are normalised (hash codes removed) so two windows can be compared.</summary>
public sealed class BindingErrorRecorder : TraceListener
{
    private static readonly Regex HashCode = new(@"HashCode=-?\d+", RegexOptions.Compiled);
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly StringBuilder _pending = new();

    private BindingErrorRecorder()
    {
    }

    public static BindingErrorRecorder Start()
    {
        var recorder = new BindingErrorRecorder();
        UiHost.Invoke(() =>
        {
            PresentationTraceSources.Refresh();   // WPF traces bindings only after a refresh (or under a debugger)
            var source = PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(recorder);
            source.Switch.Level = SourceLevels.Warning;
        });
        return recorder;
    }

    public void Stop() => UiHost.Invoke(() =>
    {
        PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Off;
    });

    public IReadOnlyList<string> Messages => [.. _messages];

    public IReadOnlyList<string> Normalised => [.. _messages.Select(m => HashCode.Replace(m, "HashCode=#")).Distinct().Order(StringComparer.Ordinal)];

    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
    }

    public override void Write(string? message)
    {
        lock (_pending) _pending.Append(message);
    }

    public override void WriteLine(string? message)
    {
        lock (_pending)
        {
            _pending.Append(message);
            _messages.Enqueue(_pending.ToString());
            _pending.Clear();
        }
    }
}

/// <summary>TEST CODE ONLY. The synthetic tree of the docs screenshot (same folder layout), under the shared fixture's
/// base so it is removed with it: no real drive, no real names.</summary>
public static class SyntheticTree
{
    private static readonly (string Folder, int Files, long BytesPerFile)[] Layout =
    [
        ("Videos", 6, 9_000_000),
        ("Photos", 240, 180_000),
        ("Music", 120, 150_000),
        ("Projects", 400, 12_000),
        ("Documents", 150, 20_000),
        ("Archive", 3, 700_000),
        ("Downloads", 12, 45_000),
    ];

    private static readonly Lazy<string> Tree = new(() =>
    {
        var tree = Path.Combine(PhaseAFixture.Shared.Base, "synthetic", "Demo");
        foreach (var (folder, files, bytes) in Layout)
        {
            var dir = Directory.CreateDirectory(Path.Combine(tree, folder, "2025"));
            for (var i = 0; i < files; i++)
            {
                using var f = new FileStream(Path.Combine(dir.FullName, $"{folder.ToLowerInvariant()}_{i:D3}.dat"), FileMode.CreateNew);
                f.SetLength(bytes + i * 97);
            }
        }
        return tree;
    });

    public static string Root => Tree.Value;

    public static long FileCount => Layout.Sum(l => l.Files);

    /// <summary>A report folder next to the tree (outside it) that does not exist yet.</summary>
    public static string NewReportFolder() => Path.Combine(PhaseAFixture.Shared.Base, "synthetic", "reports-" + Guid.NewGuid().ToString("N")[..6]);
}
