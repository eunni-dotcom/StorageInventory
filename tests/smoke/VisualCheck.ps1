<#
.SYNOPSIS
    Visual rendering checks for the StorageInventory window (TEST CODE ONLY). Dot-source this file.
.DESCRIPTION
    UI Automation reads WPF's element tree, which exists whether or not anything reaches the screen: v1.0.0 passed every
    UI Automation check while users saw a blank white window. Assert-WindowPainted looks at pixels instead. It places the
    window fully on the primary screen, in front of everything else, waits until its picture stops changing, and checks:

      1. ON SCREEN (what the Desktop Window Manager shows): the client area is not a flat field (at least 1.5% of it
         contrasts strongly with the dominant colour, at most 97% is close to it; a healthy setup screen measures about
         3-5% and 70-95%, a blank window about 0% and 100%), and every landmark UI Automation reports has ink inside
         its own on-screen rectangle.
      2. OWN SURFACE (what the window paints itself, read with PrintWindow without the compositor's backdrop): no fully
         transparent area, and every landmark still visible when that surface is laid over plain white and over plain
         black. v1.0.0 painted no background at all and relied on the compositor's Mica backdrop behind it: in the dark
         theme its white text over white, which is what an affected machine showed, is a blank white field.
      3. NO COMPOSITOR BACKDROP: the window does not ask the Desktop Window Manager for backdrop material
         (DWMWA_SYSTEMBACKDROP_TYPE Mica, Acrylic or Tabbed) behind its client area.

    Checks 2 and 3 are deterministic on every Windows 11 22H2+ or Server 2025 machine, including hosted CI runners
    whose compositor happens to draw the backdrop correctly; check 1 catches a blank or invisible window from any
    other cause. Ink is measured as luma difference >= 60 (of 255) from each region's own dominant colour, so light,
    dark and contrast themes, DPI scaling and anti-aliasing pass without reference images. The only file written is an
    optional PNG of the client area: the application window only, no other desktop content.
#>

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
if (-not ('SiVisual.Native' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace SiVisual {
public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    /// <summary>The window's own client-area pixels (PW_CLIENTONLY, without PW_RENDERFULLCONTENT, so without the
    /// compositor's backdrop) as raw 32-bit BGRA values, or null if PrintWindow fails.</summary>
    public static int[] PrintClient(IntPtr hwnd, out int width, out int height) {
        RECT r; GetClientRect(hwnd, out r); width = r.Right; height = r.Bottom;
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), bits;
        IntPtr hbm = CreateDIBSection(dc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, hbm);
        var px = new int[width * height];
        try {
            if (!PrintWindow(hwnd, dc, 1)) return null;   // PW_CLIENTONLY
            Marshal.Copy(bits, px, 0, px.Length);
            return px;
        } finally { SelectObject(dc, old); DeleteObject(hbm); DeleteDC(dc); }
    }
}

/// <summary>Luma (Rec. 709, 0-255) of an image, and contrast statistics over rectangles of it.</summary>
public sealed class LumaImage {
    public readonly int Width, Height;
    private readonly byte[] luma;
    public LumaImage(Bitmap bmp) {
        Width = bmp.Width; Height = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var raw = new int[Width * Height];
        for (int y = 0; y < Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), raw, y * Width, Width);
        bmp.UnlockBits(data);
        luma = ToLuma(raw, -1, false);
    }
    private LumaImage(int w, int h, byte[] l) { Width = w; Height = h; luma = l; }

    /// <summary>The window's own surface laid over a plain underlay (0 = black, 255 = white). A surface that carries
    /// alpha is premultiplied; a surface whose alpha bytes are all zero is a legacy opaque surface and ignores the underlay.</summary>
    public static LumaImage Over(int[] bgra, int w, int h, int underlay) {
        bool hasAlpha = false;
        foreach (int p in bgra) if ((p >> 24 & 255) != 0) { hasAlpha = true; break; }
        return new LumaImage(w, h, ToLuma(bgra, underlay, hasAlpha));
    }
    /// <summary>Share of pixels that are fully transparent (all four bytes zero): nothing painted there. A legacy opaque
    /// surface (no alpha anywhere) counts as painted, unless every pixel is zero, which means nothing was painted at all.</summary>
    public static double TransparentShare(int[] bgra) {
        bool hasAlpha = false, anyPixel = false;
        foreach (int p in bgra) { if ((p >> 24 & 255) != 0) hasAlpha = true; if (p != 0) anyPixel = true; }
        if (!anyPixel) return 1;
        if (!hasAlpha) return 0;
        long n = 0; foreach (int p in bgra) if (p == 0) n++;
        return (double)n / bgra.Length;
    }
    private static byte[] ToLuma(int[] raw, int underlay, bool premultiplied) {
        var l = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++) {
            int p = raw[i], a = p >> 24 & 255, r = p >> 16 & 255, g = p >> 8 & 255, b = p & 255;
            if (premultiplied) { int k = (255 - a) * underlay / 255; r = Math.Min(255, r + k); g = Math.Min(255, g + k); b = Math.Min(255, b + k); }
            l[i] = (byte)((2126 * r + 7152 * g + 722 * b) / 10000);
        }
        return l;
    }
    /// <summary>[dominant luma, share within 12 of it, share differing by at least 60] over the rectangle.</summary>
    public double[] Stats(int x0, int y0, int x1, int y1) {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(Width, x1); y1 = Math.Min(Height, y1);
        if (x1 <= x0 || y1 <= y0) return new double[] { -1, 1, 0 };
        var hist = new int[256]; long n = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { hist[luma[y * Width + x]]++; n++; }
        int best = 0, bestCount = -1;
        for (int c = 0; c < 256; c++) { int s = 0; for (int d = Math.Max(0, c - 2); d <= Math.Min(255, c + 2); d++) s += hist[d]; if (s > bestCount) { bestCount = s; best = c; } }
        long flat = 0, ink = 0;
        for (int c = 0; c < 256; c++) { int diff = Math.Abs(c - best); if (diff <= 12) flat += hist[c]; if (diff >= 60) ink += hist[c]; }
        return new double[] { best, (double)flat / n, (double)ink / n };
    }
}
}
'@
}

# Physical pixels for every window, rectangle and capture on this thread, whatever the display scaling.
[void][SiVisual.Native]::SetThreadDpiAwarenessContext([IntPtr]-4)

function Get-ClientScreenRectangle([IntPtr] $Hwnd) {
    $c = New-Object SiVisual.Native+RECT; [void][SiVisual.Native]::GetClientRect($Hwnd, [ref]$c)
    $p = New-Object SiVisual.Native+POINT; [void][SiVisual.Native]::ClientToScreen($Hwnd, [ref]$p)
    New-Object System.Drawing.Rectangle $p.X, $p.Y, $c.Right, $c.Bottom
}

function Get-ScreenCapture([System.Drawing.Rectangle] $Rect) {
    $bmp = New-Object System.Drawing.Bitmap $Rect.Width, $Rect.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($Rect.Left, $Rect.Top, 0, 0, $bmp.Size) } finally { $g.Dispose() }
    $bmp
}

# Puts the window on the primary screen's work area (shrunk to fit if needed), topmost and in front.
function Show-WindowForCapture([IntPtr] $Hwnd) {
    [void][SiVisual.Native]::ShowWindow($Hwnd, 9)   # SW_RESTORE
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $r = New-Object SiVisual.Native+RECT; [void][SiVisual.Native]::GetWindowRect($Hwnd, [ref]$r)
    $w = [Math]::Min($r.Right - $r.Left, $work.Width); $h = [Math]::Min($r.Bottom - $r.Top, $work.Height)
    [void][SiVisual.Native]::SetWindowPos($Hwnd, [IntPtr]-1, $work.Left, $work.Top, $w, $h, 0x40)   # HWND_TOPMOST, SWP_SHOWWINDOW
    [void][SiVisual.Native]::SetForegroundWindow($Hwnd)
}

function Clear-Topmost([IntPtr] $Hwnd) { [void][SiVisual.Native]::SetWindowPos($Hwnd, [IntPtr]-2, 0, 0, 0, 0, 0x13) }   # HWND_NOTOPMOST, NOSIZE|NOMOVE|NOACTIVATE

# Captures the client area until two consecutive frames agree (the window has finished painting), up to $TimeoutSeconds.
function Get-SettledClientCapture([IntPtr] $Hwnd, [int] $TimeoutSeconds = 15) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $previous = $null; $bmp = $null
    while ($true) {
        Start-Sleep -Milliseconds 400
        if ($bmp) { $bmp.Dispose() }
        $bmp = Get-ScreenCapture (Get-ClientScreenRectangle $Hwnd)
        $s = ([SiVisual.LumaImage]::new($bmp)).Stats(0, 0, $bmp.Width, $bmp.Height)
        $now = '{0}|{1:N3}|{2:N3}' -f $s[0], $s[1], $s[2]
        if (($now -eq $previous -and $sw.Elapsed.TotalSeconds -ge 1.5) -or $sw.Elapsed.TotalSeconds -ge $TimeoutSeconds) { return $bmp }
        $previous = $now
    }
}

# UI Automation rectangles can be logical or physical depending on the client; map them through the window's own
# UI Automation rectangle onto the physical window rectangle, then into client coordinates.
function Convert-UiaRectangle($UiaRect, $UiaWindowRect, [IntPtr] $Hwnd, [System.Drawing.Rectangle] $Client) {
    $wr = New-Object SiVisual.Native+RECT; [void][SiVisual.Native]::GetWindowRect($Hwnd, [ref]$wr)
    $sx = ($wr.Right - $wr.Left) / [Math]::Max(1.0, $UiaWindowRect.Width); $sy = ($wr.Bottom - $wr.Top) / [Math]::Max(1.0, $UiaWindowRect.Height)
    $x = $wr.Left + ($UiaRect.Left - $UiaWindowRect.Left) * $sx - $Client.Left
    $y = $wr.Top + ($UiaRect.Top - $UiaWindowRect.Top) * $sy - $Client.Top
    New-Object System.Drawing.Rectangle ([int][Math]::Round($x)), ([int][Math]::Round($y)), ([int][Math]::Round($UiaRect.Width * $sx)), ([int][Math]::Round($UiaRect.Height * $sy))
}

# The landmarks sit at the top of the page. Driving the window through UI Automation (Invoke on a button at the bottom)
# can leave the page scrolled down, so scroll it back to the top first, the way a user would.
function Reset-PageScroll($Root) {
    $A = [Windows.Automation.AutomationElement]
    $page = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($A::IsScrollPatternAvailableProperty, $true)))
    if (-not $page) { return }
    $scroll = $page.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if ($scroll.Current.VerticallyScrollable -and $scroll.Current.VerticalScrollPercent -gt 0) {
        $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, 0)
    }
}

function Find-Landmark($Root, [string] $Name, $Type) {
    $A = [Windows.Automation.AutomationElement]
    $c = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition($A::NameProperty, $Name)),
        (New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $Type)))
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
}

<#
.SYNOPSIS
    Asserts that the window shows its interface. Returns result objects (Name, Ok, Detail).
.PARAMETER Landmarks
    @( @{ Name = '...'; Type = [Windows.Automation.ControlType]::Text; MinInk = 0.02 }, ... ). A landmark that is not
    entirely inside the visible client area fails; it is never passed silently.
#>
function Assert-WindowPainted {
    param(
        [Parameter(Mandatory)] [IntPtr] $Hwnd,
        [Parameter(Mandatory)] [object[]] $Landmarks,
        [string] $Label = 'window',
        [string] $SavePng,
        [double] $MinInk = 0.015,
        [double] $MaxFlat = 0.97,
        [double] $MaxTransparent = 0.005
    )
    $results = New-Object System.Collections.Generic.List[object]
    function Add-Result([string] $n, [bool] $ok, [string] $d) { $results.Add([pscustomobject]@{ Name = $n; Ok = $ok; Detail = $d }) }

    Show-WindowForCapture $Hwnd
    try {
        $root = [Windows.Automation.AutomationElement]::FromHandle($Hwnd)
        Reset-PageScroll $root
        $client = Get-ClientScreenRectangle $Hwnd
        $bmp = Get-SettledClientCapture $Hwnd
        if ($SavePng) { $bmp.Save($SavePng, [System.Drawing.Imaging.ImageFormat]::Png) }
        $screen = [SiVisual.LumaImage]::new($bmp)
        $bmp.Dispose()

        # Where UI Automation says each landmark is, in client pixels.
        $uiaWindow = $root.Current.BoundingRectangle
        $marks = foreach ($l in $Landmarks) {
            $e = Find-Landmark $root $l.Name $l.Type
            $r = if ($e) { Convert-UiaRectangle $e.Current.BoundingRectangle $uiaWindow $Hwnd $client } else { $null }
            [pscustomobject]@{ Name = $l.Name; Rect = $r; MinInk = $(if ($l.MinInk) { $l.MinInk } else { 0.02 }) }
        }
        function Test-Landmarks([SiVisual.LumaImage] $img, [string] $where) {
            foreach ($m in $marks) {
                $what = "$Label shows '$($m.Name)' $where"
                if (-not $m.Rect) { Add-Result $what $false '(not found by UI Automation)'; continue }
                $r = $m.Rect
                if ($r.Left -lt 0 -or $r.Top -lt 0 -or $r.Right -gt $img.Width -or $r.Bottom -gt $img.Height -or $r.Width -lt 4 -or $r.Height -lt 4) {
                    Add-Result $what $false "(rectangle $r is not inside the $($img.Width)x$($img.Height) client area)"; continue
                }
                $s = $img.Stats($r.Left, $r.Top, $r.Right, $r.Bottom)
                Add-Result $what ($s[2] -ge $m.MinInk) ('({0:P1} of its {1}x{2} rectangle contrasts with its background; need >= {3:P0})' -f $s[2], $r.Width, $r.Height, $m.MinInk)
            }
        }

        # 1. on screen
        $s = $screen.Stats(0, 0, $screen.Width, $screen.Height)
        Add-Result "$Label paints a picture on screen, not a flat field" ($s[2] -ge $MinInk -and $s[1] -le $MaxFlat) `
            ('({0}x{1} client: background luma {2}, {3:P1} near background, {4:P1} contrasting; need <= {5:P0} and >= {6:P1})' -f $screen.Width, $screen.Height, $s[0], $s[1], $s[2], $MaxFlat, $MinInk)
        Test-Landmarks $screen 'on screen'

        # 2. own surface, without the compositor's backdrop
        $w = 0; $h = 0
        $own = [SiVisual.Native]::PrintClient($Hwnd, [ref]$w, [ref]$h)
        if (-not $own -or $w -ne $screen.Width -or $h -ne $screen.Height) {
            Add-Result "$Label own surface can be read" $false "(PrintWindow returned $(if ($own) { "${w}x$h" } else { 'nothing' }), client is $($screen.Width)x$($screen.Height))"
        }
        else {
            $t = [SiVisual.LumaImage]::TransparentShare($own)
            Add-Result "$Label paints its own background (no transparent area)" ($t -le $MaxTransparent) ('({0:P1} of the client area is fully transparent, left for the compositor to fill; need <= {1:P1})' -f $t, $MaxTransparent)
            Test-Landmarks ([SiVisual.LumaImage]::Over($own, $w, $h, 255)) 'over a white underlay'
            Test-Landmarks ([SiVisual.LumaImage]::Over($own, $w, $h, 0)) 'over a black underlay'
        }

        # 3. no compositor backdrop material behind the client area
        $type = 0
        $hr = [SiVisual.Native]::DwmGetWindowAttribute($Hwnd, 38, [ref]$type, 4)   # DWMWA_SYSTEMBACKDROP_TYPE
        Add-Result "$Label asks the compositor for no backdrop material" ($hr -ne 0 -or $type -notin @(2, 3, 4)) "(DWMWA_SYSTEMBACKDROP_TYPE $(if ($hr -eq 0) { $type } else { 'not supported on this Windows' }); 2-4 are Mica, Acrylic, Tabbed)"
    }
    finally { Clear-Topmost $Hwnd }
    $results
}
