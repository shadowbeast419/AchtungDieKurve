using Raylib_cs;
using static Raylib_cs.Raylib;

namespace AchtungDieKurve;

/// <summary>
/// Keyboard + gamepad input (Xbox layout). The N-th connected controller steers the N-th human player.
/// </summary>
public static class Input
{
    private const int MaxDevices = 8;
    private const float StickThreshold = 0.35f;
    private const float TriggerThreshold = 0.5f;   // trigger axes range -1 (released) .. 1 (pressed)

    // GLFW also reports mice, keyboards and RGB controllers as joysticks; raylib can't tell them
    // apart, so only devices whose name looks like a game controller are used.
    private static readonly string[] ControllerWords =
        ["controller", "pad", "xbox", "x-box", "joystick", "dualshock", "dualsense", "playstation", "8bitdo", "nintendo"];
    private static readonly string[] IgnoredWords = ["mouse", "keyboard"];

    private static readonly bool[] Seen = new bool[MaxDevices];
    private static readonly List<int> Pads = [];   // raylib device indices of usable controllers

    // GLFW only reads buttons of controllers whose exact SDL GUID (bus, vendor, product, firmware
    // version) is in its built-in database, so a firmware update can silently break a pad
    // (e.g. Xbox Wireless 0b22 on Bluetooth with firmware 0x0521). Register the known Xbox layouts
    // for every firmware version of these models.
    private const string BluetoothXboxLayout =
        "a:b0,b:b1,back:b10,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b6,leftstick:b13," +
        "lefttrigger:a5,leftx:a0,lefty:a1,rightshoulder:b7,rightstick:b14,righttrigger:a4,rightx:a2,righty:a3," +
        "start:b11,x:b3,y:b4,platform:Linux,";
    private const string UsbXboxLayout =
        "a:b0,b:b1,back:b6,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b8,leftshoulder:b4,leftstick:b9," +
        "lefttrigger:a2,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b10,righttrigger:a5,rightx:a3,righty:a4," +
        "start:b7,x:b2,y:b3,platform:Linux,";

    /// <summary>Adds controller mappings missing from GLFW's database. Call after InitWindow.</summary>
    public static void RegisterExtraMappings()
    {
        const int bluetooth = 0x05, usb = 0x03, microsoft = 0x045e;
        var sb = new System.Text.StringBuilder();
        for (var version = 0x0500; version <= 0x05ff; version++)
        {
            foreach (var product in (int[])[0x0b13, 0x0b20, 0x0b22])
                sb.Append(SdlGuid(bluetooth, microsoft, product, version)).Append(",Xbox Wireless Controller,").AppendLine(BluetoothXboxLayout);
            sb.Append(SdlGuid(usb, microsoft, 0x0b12, version)).Append(",Xbox Series Controller,").AppendLine(UsbXboxLayout);
        }
        SetGamepadMappings(sb.ToString());
    }

    private static string SdlGuid(int bus, int vendor, int product, int version)
    {
        static string Le16(int v) => $"{v & 0xff:x2}{(v >> 8) & 0xff:x2}0000";
        return Le16(bus) + Le16(vendor) + Le16(product) + Le16(version);
    }

    /// <summary>Raylib device index of the controller for this player, or -1.</summary>
    private static int PadFor(Player p) =>
        p.Kind == PlayerKind.Human && p.HumanIndex >= 0 && p.HumanIndex < Pads.Count ? Pads[p.HumanIndex] : -1;

    public static bool HasPad(Player p) => PadFor(p) >= 0;

    /// <summary>Refreshes the controller list; call once per frame. Logs (dis)connects.</summary>
    public static void UpdatePads()
    {
        Pads.Clear();
        for (var dev = 0; dev < MaxDevices; dev++)
        {
            var available = IsGamepadAvailable(dev);
            var name = available ? GetGamepadName_(dev) ?? "" : "";
            var usable = available && IsController(name);
            if (usable) Pads.Add(dev);

            if (available == Seen[dev]) continue;
            Seen[dev] = available;
            if (!available) Console.WriteLine($"Device {dev} disconnected");
            else Console.WriteLine(usable ? $"Controller connected: {name}" : $"Ignoring non-controller device: {name}");
        }

        UpdateMenuNavigation();
    }

    // ───────────────────────────── Menu navigation ─────────────────────────────

    private const float MenuStickThreshold = 0.6f;
    private static int _stickX, _stickY;

    /// <summary>-1/0/+1 per frame: arrow keys, D-pad, or a left-stick flick.</summary>
    public static int MenuDx { get; private set; }
    public static int MenuDy { get; private set; }

    private static void UpdateMenuNavigation()
    {
        int dx = 0, dy = 0;
        if (IsKeyPressed(KeyboardKey.Left) || AnyPadPressed(GamepadButton.LeftFaceLeft)) dx = -1;
        if (IsKeyPressed(KeyboardKey.Right) || AnyPadPressed(GamepadButton.LeftFaceRight)) dx = 1;
        if (IsKeyPressed(KeyboardKey.Up) || AnyPadPressed(GamepadButton.LeftFaceUp)) dy = -1;
        if (IsKeyPressed(KeyboardKey.Down) || AnyPadPressed(GamepadButton.LeftFaceDown)) dy = 1;

        // Sticks only count when they cross the threshold, not while held.
        int sx = 0, sy = 0;
        foreach (var pad in Pads)
        {
            var x = GetGamepadAxisMovement(pad, GamepadAxis.LeftX);
            var y = GetGamepadAxisMovement(pad, GamepadAxis.LeftY);
            if (MathF.Abs(x) > MenuStickThreshold) sx = MathF.Sign(x);
            if (MathF.Abs(y) > MenuStickThreshold) sy = MathF.Sign(y);
        }
        if (sx != 0 && sx != _stickX) dx = sx;
        if (sy != 0 && sy != _stickY) dy = sy;
        _stickX = sx;
        _stickY = sy;

        MenuDx = dx;
        MenuDy = dy;
    }

    /// <summary>One line per controller with pressed buttons and axes, for --padtest.</summary>
    public static string DescribePads()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var pad in Pads)
        {
            var buttons = Enum.GetValues<GamepadButton>()
                .Where(b => b != GamepadButton.Unknown && IsGamepadButtonDown(pad, b));
            var axes = Enum.GetValues<GamepadAxis>().Select(a => $"{a}={GetGamepadAxisMovement(pad, a):+0.00;-0.00}");
            sb.AppendLine($"[{GetGamepadName_(pad)}] buttons: {string.Join(" ", buttons)} | {string.Join(" ", axes)}");
        }
        return sb.Length > 0 ? sb.ToString() : "no controllers\n";
    }

    private static bool IsController(string name)
    {
        var n = name.ToLowerInvariant();
        return ControllerWords.Any(n.Contains) && !IgnoredWords.Any(n.Contains);
    }

    /// <summary>-1 = left, +1 = right, 0 = straight.</summary>
    public static float Steering(Player p)
    {
        var left = IsKeyDown(p.LeftKey);
        var right = IsKeyDown(p.RightKey);

        var pad = PadFor(p);
        if (pad >= 0)
        {
            var stick = GetGamepadAxisMovement(pad, GamepadAxis.LeftX);
            left |= IsGamepadButtonDown(pad, GamepadButton.LeftFaceLeft)
                    || IsGamepadButtonDown(pad, GamepadButton.LeftTrigger1)
                    || GetGamepadAxisMovement(pad, GamepadAxis.LeftTrigger) > TriggerThreshold
                    || stick < -StickThreshold;
            right |= IsGamepadButtonDown(pad, GamepadButton.LeftFaceRight)
                     || IsGamepadButtonDown(pad, GamepadButton.RightTrigger1)
                     || GetGamepadAxisMovement(pad, GamepadAxis.RightTrigger) > TriggerThreshold
                     || stick > StickThreshold;
        }

        return (right ? 1f : 0f) - (left ? 1f : 0f);
    }

    /// <summary>Start / continue: Space, Enter, pad A or Start.</summary>
    public static bool ConfirmPressed() =>
        IsKeyPressed(KeyboardKey.Space) || IsKeyPressed(KeyboardKey.Enter)
        || AnyPadPressed(GamepadButton.RightFaceDown) || AnyPadPressed(GamepadButton.MiddleRight);

    /// <summary>Back to menu / quit: Esc, pad B or View.</summary>
    public static bool BackPressed() =>
        IsKeyPressed(KeyboardKey.Escape)
        || AnyPadPressed(GamepadButton.RightFaceRight) || AnyPadPressed(GamepadButton.MiddleLeft);

    /// <summary>Pause during a round: Esc, pad Start or View.</summary>
    public static bool PausePressed() =>
        IsKeyPressed(KeyboardKey.Escape)
        || AnyPadPressed(GamepadButton.MiddleRight) || AnyPadPressed(GamepadButton.MiddleLeft);

    /// <summary>Toggle fullscreen: F11 or pad X.</summary>
    public static bool FullscreenPressed() =>
        IsKeyPressed(KeyboardKey.F11) || AnyPadPressed(GamepadButton.RightFaceLeft);

    private static bool AnyPadPressed(GamepadButton button) =>
        Pads.Any(pad => IsGamepadButtonPressed(pad, button));
}
