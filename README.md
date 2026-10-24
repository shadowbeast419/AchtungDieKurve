# Achtung, die Kurve!

A remake of the classic DOS game for Linux, written in C# / .NET 10 with [Raylib-cs](https://github.com/ChrisDill/Raylib-cs).
Up to 6 snakes (3 human + AI), Xbox controller support, Mario-Kart-style power-ups, QHD/4K scaling.

## Run

```sh
dotnet run                     # windowed, size picked to fit the monitor
dotnet run -- --fullscreen     # borderless fullscreen
dotnet run -- --qhd            # 2560x1440 window (also --fhd, --4k, --res WxH)
dotnet run -- --demo easy      # AI-only attract mode (easy|normal|hard)
dotnet run -- --padtest        # print live controller state for troubleshooting
```

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

Menu: ↑/↓ choose, ←/→ change, Space/A start. In game: Esc/Start pause, F11/X fullscreen.

## Rules

- Hitting a wall or any trail ends your round; every crash gives all survivors a point.
- Each crash makes the remaining lines 15 % faster.
- First to 10 × (players − 1) points with a 2-point lead wins.
- "?" boxes give a random item: Mushroom (speed), Star (invincible, wrap walls), Boo (invisible),
  Lightning (slows others), Banana (reverses others), Bob-omb (clears the board).

## Files

- Settings (player slots, AI level, power-ups): `~/.config/achtung-die-kurve/settings.json`
- A screenshot of every round end: `~/Pictures/AchtungDieKurve/` (disable with `--no-screenshots`)
