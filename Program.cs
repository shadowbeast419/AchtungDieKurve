using AchtungDieKurve;
using AchtungDieKurve.Net;
using Raylib_cs;
using static Raylib_cs.Raylib;

// Usage: AchtungDieKurve [--res WIDTHxHEIGHT | --qhd | --fhd | --4k] [--fullscreen] [--padtest]
//                        [--demo [easy|normal|hard]] [--demo-rounds N] [--turbo N]
//                        [--screenshots | --no-screenshots]
//                        [--host | --connect HOST[:PORT]] [--local N] [--name NAME] [--port N]
//        AchtungDieKurve --server [--port N] [--ai easy|normal|hard] [--no-powerups]   (headless)
(int W, int H)? requested = null;
var fullscreen = false;
var padTest = false;
AiLevel? demo = null;
var demoRounds = 0;
var turbo = 1f;
bool? screenshots = null;
var dedicated = false;
var host = false;
string? connect = null;
int? port = null, localPlayers = null;
string? name = null;
AiLevel? serverAi = null;
var noPowerUps = false;
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
        case "--server" or "--dedicated": dedicated = true; break;
        case "--host": host = true; break;
        case "--connect" or "--join" when i + 1 < args.Length: connect = args[++i]; break;
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536: port = p; i++; break;
        case "--local" when i + 1 < args.Length && int.TryParse(args[i + 1], out var l): localPlayers = Math.Clamp(l, 1, Settings.MaxLocalPlayers); i++; break;
        case "--name" when i + 1 < args.Length: name = args[++i]; break;
        case "--ai" when i + 1 < args.Length && Enum.TryParse<AiLevel>(args[i + 1], ignoreCase: true, out var ai): serverAi = ai; i++; break;
        case "--no-powerups": noPowerUps = true; break;
        default: Console.Error.WriteLine($"Unknown option '{args[i]}'"); break;
    }
}

var settings = Settings.Load();

if (dedicated)
{
    // Headless: no window, just the lobby + simulation. Stop with Ctrl+C.
    using var server = new GameServer(port ?? settings.Port, serverAi ?? AiLevel.Normal, !noPowerUps);
    try
    {
        server.Start();
    }
    catch (System.Net.Sockets.SocketException e)
    {
        Console.Error.WriteLine($"Cannot listen on port {server.Port}: {e.Message}");
        return 1;
    }
    var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();
    Console.WriteLine("Dedicated server running - the first PC to join is the host. Press Ctrl+C to stop.");
    stop.Wait();
    return 0;
}

if (port is { } customPort) settings.Port = customPort;
if (localPlayers is { } lp) settings.LocalPlayers = lp;
if (name is { Length: > 0 }) settings.PlayerName = name;

SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint | ConfigFlags.ResizableWindow);
InitWindow(Game.BaseWidth, Game.BaseHeight, "Achtung, die Kurve!");
SetWindowMinSize(640, 400);
SetExitKey(KeyboardKey.Null);
SetTargetFPS(60);
Input.RegisterExtraMappings();

var start = requested ?? (Display.DefaultWindowed().Width, Display.DefaultWindowed().Height);
Display.SetWindowed(start.W, start.H);
if (fullscreen) Display.ToggleFullscreen();

var netStart = host || connect is not null ? new NetStart(host, connect) : null;
var game = new Game(settings, demo, demoRounds, turbo, screenshots, netStart);
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
return 0;
