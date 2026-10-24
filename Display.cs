using Raylib_cs;
using static Raylib_cs.Raylib;

namespace AchtungDieKurve;

public sealed record Resolution(int Width, int Height, string Label)
{
    public override string ToString() => Label.Length > 0 ? $"{Width} x {Height} ({Label})" : $"{Width} x {Height}";
}

/// <summary>Window size / fullscreen handling.</summary>
public static class Display
{
    public static readonly Resolution[] Presets =
    [
        new(1280, 800, "WXGA"),
        new(1600, 900, "HD+"),
        new(1920, 1080, "Full HD"),
        new(2560, 1440, "QHD"),
        new(3840, 2160, "4K UHD"),
    ];

    public static bool IsFullscreen => IsWindowState(ConfigFlags.BorderlessWindowMode);

    private static (int W, int H) MonitorSize()
    {
        var m = GetCurrentMonitor();
        int w = GetMonitorWidth(m), h = GetMonitorHeight(m);
        return w > 0 && h > 0 ? (w, h) : (int.MaxValue, int.MaxValue);
    }

    /// <summary>Presets that fit on the current monitor.</summary>
    private static Resolution[] Available()
    {
        var (mw, mh) = MonitorSize();
        var fitting = Presets.Where(p => p.Width <= mw && p.Height <= mh).ToArray();
        return fitting.Length > 0 ? fitting : [Presets[0]];
    }

    /// <summary>Largest preset that leaves some room for panels/decorations.</summary>
    public static Resolution DefaultWindowed()
    {
        var (mw, mh) = MonitorSize();
        return Presets.LastOrDefault(p => p.Width <= mw * 0.9 && p.Height <= mh * 0.9) ?? Presets[0];
    }

    public static void SetWindowed(int width, int height)
    {
        if (IsFullscreen) ToggleBorderlessWindowed();
        SetWindowSize(width, height);
        var (mw, mh) = MonitorSize();
        if (mw != int.MaxValue)
        {
            var monitorPos = GetMonitorPosition(GetCurrentMonitor());
            SetWindowPosition((int)monitorPos.X + Math.Max(0, (mw - width) / 2),
                              (int)monitorPos.Y + Math.Max(0, (mh - height) / 2));
        }
    }

    public static void ToggleFullscreen() => ToggleBorderlessWindowed();

    /// <summary>Switches to the next windowed preset that fits on the monitor.</summary>
    public static void CycleResolution()
    {
        var list = Available();
        var idx = Array.FindIndex(list, p => p.Width == GetScreenWidth() && p.Height == GetScreenHeight());
        var next = list[IsFullscreen ? list.Length - 1 : (idx + 1) % list.Length];
        SetWindowed(next.Width, next.Height);
    }

    public static string Describe()
    {
        int w = GetScreenWidth(), h = GetScreenHeight();
        var preset = Presets.FirstOrDefault(p => p.Width == w && p.Height == h);
        var text = preset?.ToString() ?? $"{w} x {h}";
        return IsFullscreen ? text + " fullscreen" : text;
    }
}
