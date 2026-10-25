# Achtung, die Kurve!

A remake of the classic DOS game for Linux, written in C# / .NET 10 with [Raylib-cs](https://github.com/ChrisDill/Raylib-cs).
Up to 6 snakes (3 human + AI), Xbox controller support, Mario-Kart-style power-ups, QHD/4K scaling,
network play (host from the menu, join by IP/name, or run a dedicated server).

## Run

```sh
dotnet run                     # windowed, size picked to fit the monitor
dotnet run -- --fullscreen     # borderless fullscreen
dotnet run -- --qhd            # 2560x1440 window (also --fhd, --4k, --res WxH)
dotnet run -- --demo easy      # AI-only attract mode (easy|normal|hard)
dotnet run -- --padtest        # print live controller state for troubleshooting
```

## Network play

```sh
dotnet run -- --server                    # headless dedicated server on TCP port 7777 (Ctrl+C stops it)
dotnet run -- --server --port 8000 --ai hard --no-powerups
dotnet run -- --host                      # host from this PC and go straight to the lobby
dotnet run -- --connect 192.168.1.20      # join (also kurve.example.org:8000, [::1]:7777)
dotnet run -- --connect myserver --local 2 --name Ward-3
```

- In the game: **Host network game** or **Join network game** in the main menu. "Players on this PC" (1-3)
  sets how many people play on each PC; they use the keys `1/Q`, `M/,`, `←/→` and controllers 1-3.
- **Lobby**: the host (the PC that hosts, or the first PC to join a dedicated server) gives each colour to
  a player of any connected PC, to the AI, or turns it off, and sets AI difficulty and power-ups.
  Only the host can start, pause, skip to the next round, or go back to the lobby.
- If a PC drops out mid-game, the AI takes over its colours. If the host's PC leaves, a dedicated server
  makes the next PC the host; a game hosted from the menu ends.
- Open the port in the firewall of the hosting PC: `sudo ufw allow 7777/tcp`. Over the internet, forward
  TCP 7777 on the host's router.

Standalone build (no .NET needed on the target PC):

```sh
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
./publish/AchtungDieKurve --fullscreen
```

## Controls

| Player | Keyboard | Controller |
|---|---|---|
| Fred (red) | `1` / `Q` | N-th connected pad goes to the N-th human player: D-pad, left stick, LB/RB or LT/RT |
| Greenlee (green) | `M` / `,` | |
| Bluebell (blue) | `←` / `→` | |

Menu: ↑/↓ choose, ←/→ change, Space/A select, Esc/B back. In game: Esc/Start pause, F11/X fullscreen.

## Rules

- Hitting a wall or any trail ends your round; every crash gives all survivors a point.
- Each crash makes the remaining lines 15 % faster.
- First to 10 × (players − 1) points with a 2-point lead wins.
- "?" boxes give a random item: Mushroom (speed), Star (invincible, wrap walls), Boo (invisible),
  Lightning (slows others), Banana (reverses others), Bob-omb (clears the board).

## Files

- Settings (player slots, AI level, power-ups, last server, players on this PC): `~/.config/achtung-die-kurve/settings.json`
- A screenshot of every round end: `~/Pictures/AchtungDieKurve/` (disable with `--no-screenshots`)
