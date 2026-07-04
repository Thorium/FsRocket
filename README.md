# FsRocket

A local multiplayer 2D arena combat game written in F#
Up to 4 players pilot ships in a split-screen arena, fighting with 21 different weapons over destructible VGA-style terrain.


Inspired heavily by the Finnish rocket game lineage: 
- TurboRaketti, Heikki Kosola, 1991
- AUTS (1995) by Jaakko Lyytinen (A-More) / The Kudos
- Tcpippeli (Ville Kujala, 199x)
- Kops, Fuse, and so on... 

But mostly AUTS, because it had Shareware licence and excellent controls.
So there is a level-reader for AUTS levels included.

Separate MonoGame / WinForms / Fable branches are available.
You can try the Fable version online here: https://thorium.github.io/FsRocket/

## Features

- **1-4 player local multiplayer** with split-screen viewports on a shared keyboard (on ultrawide windows — width ≥ 2× height — 3/4-player mode uses a single side-by-side row ordered by keyboard position: P1 | P3 | P4 | P2)
- **21 weapons** — machinegun, homing missiles, mines, nukes, blackholes, sonicbooms, EMP, freezer, and more
- **10 bundled terrain maps** in the original AUTS `.LEV` format (RLE-compressed VGA data) — HUNAJA, JAATIKKO, KARKKI, KIERTO, KORALLI, NEBULA, SIENI, TEHDAS, TULIVUOR, VIIDAKKO — plus a no-terrain arena mode; any extra `.LEV` dropped next to the exe is picked up at startup
- **Destructible terrain** — projectiles carve out the map on impact
- **Bases / landing pads** — land on a pad to come to a stop, recharge energy, and switch your special weapon (by default weapons can only be changed while parked on a base)
- **VGA Mode 13h aesthetic** — faithful 256-color palette rendering via MonoGame (hardware-accelerated)
- **Cross-platform** — runs on Windows, macOS, and Linux via MonoGame DesktopGL
- **Pure functional core** — game logic is a single `GameState -> GameState` tick function with immutable records
- Gravity, thrust, water friction, knockback, shields, stun, and spawn invincibility

## Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) or later (Windows, macOS, or Linux)

Uses [MonoGame Framework (DesktopGL)](https://www.monogame.net/) for hardware-accelerated 2D rendering.

## Build & Run

```
dotnet build
dotnet run
```

## Tests

```
dotnet fsi test.fsx
```

## Controls

| Player       | Thrust          | Left            | Right           | Special (Down) | Fire           | Switch weapon      |
|--------------|-----------------|-----------------|-----------------|----------------|----------------|--------------------|
| P1 (Blue)    | W               | A               | D               | S              | Tab            | `1` back / `2` fwd |
| P2 (Green)   | Up / NumPad8    | Left / NumPad4  | Right / NumPad6 | Down / NumPad5 | RShift / Enter | `8` back / `9` fwd |
| P3 (Red)     | T               | F               | H               | G              | Y              | `4` back / `5` fwd |
| P4 (Yellow)  | I               | J               | L               | K              | B              | `6` back / `7` fwd |

The main gun fires with the **Fire** key; the **Special** weapon fires with the **Down** key. The weapon-switch keys (placed near each player's hands) cycle the special weapon — by default **only while parked on a base**.

**Global keys:** `1`-`4` set the player count (in the menu), `F4` toggles "respawn on death" (in the menu — when off, destroyed ships stay dead and the round ends when at most one ship is left flying), `F5`/`F6` change level, `F7`/`F8` add/remove CPU players, `F9` toggles the "switch weapons only on a base" rule, `F11` toggles fullscreen, `Space` starts a round, `Escape` returns to the menu / quits.

## Project Structure

```
Physics.fs    — Constants: gravity, thrust, friction, velocities
Terrain.fs    — Level file loading and RLE decompression
Weapons.fs    — Weapon definitions, entity types, damage tables
Types.fs      — Immutable game-state records (Player, Entity, Particle, GameState)
Entities.fs   — Entity factories, collision detection, arena walls, spawning
Game.fs       — Core game tick logic (pure function)
Renderer.fs   — Split-screen MonoGame/SpriteBatch renderer with VGA palette
Program.fs    — MonoGame entry point, keyboard input, game loop
test.fsx      — Smoke tests
*.LEV         — AUTS terrain maps
```

<img width="1185" height="717" alt="image" src="https://github.com/user-attachments/assets/052d8c29-2cda-4dc2-a88b-ebf61fe9d4d9" />
