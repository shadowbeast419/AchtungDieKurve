using AchtungDieKurve;
using Raylib_cs;
using static Raylib_cs.Raylib;

// Usage: AchtungDieKurve [--res WIDTHxHEIGHT | --qhd | --fhd | --4k] [--fullscreen] [--padtest]
//                        [--demo [easy|normal|hard]] [--demo-rounds N] [--turbo N]
//                        [--screenshots | --no-screenshots]
(int W, int H)? requested = null;
var fullscreen = false;
var padTest = false;
AiLevel? demo = null;
var demoRounds = 0;
var turbo = 1f;
bool? screenshots = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--res" when i + 1 < args.Length:
            var parts = args[++i].ToLowerInvariant().Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                requested = (w, h);
            else
                Console.Error.WriteLine($"Invalid resolution '{args[i]}', expected e.g. 2560x1440");
            break;
        case "--qhd": requested = (2560, 1440); break;
        case "--fhd": requested = (1920, 1080); break;
        case "--4k": requested = (3840, 2160); break;
        case "--fullscreen" or "-f": fullscreen = true; break;
        case "--padtest": padTest = true; break;
        case "--demo":
            demo = AiLevel.Normal;
            if (i + 1 < args.Length && Enum.TryParse<AiLevel>(args[i + 1], ignoreCase: true, out var level))
            {
                demo = level;
                i++;
            }
            break;
        case "--demo-rounds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): demoRounds = n; i++; break;
        case "--turbo" when i + 1 < args.Length && float.TryParse(args[i + 1], out var t): turbo = Math.Clamp(t, 0.1f, 50f); i++; break;
        case "--screenshots": screenshots = true; break;
        case "--no-screenshots": screenshots = false; break;
    }
}

SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint | ConfigFlags.ResizableWindow);
InitWindow(Game.BaseWidth, Game.BaseHeight, "Achtung, die Kurve!");
SetWindowMinSize(640, 400);
SetExitKey(KeyboardKey.Null);
SetTargetFPS(60);
Input.RegisterExtraMappings();

var start = requested ?? (Display.DefaultWindowed().Width, Display.DefaultWindowed().Height);
Display.SetWindowed(start.W, start.H);
if (fullscreen) Display.ToggleFullscreen();

var game = new Game(Settings.Load(), demo, demoRounds, turbo, screenshots);
var nextPadReport = 0.0;
while (!WindowShouldClose() && !game.QuitRequested)
{
    if (padTest && GetTime() >= nextPadReport)
    {
        Console.Write(Input.DescribePads());
        nextPadReport = GetTime() + 0.5;
    }

    game.Update(GetFrameTime());
    game.Draw();
}

game.Dispose();
CloseWindow();
