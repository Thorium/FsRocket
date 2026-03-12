# FsRocket

A local multiplayer 2D arena combat game written in F#
Up to 4 players pilot ships in a split-screen arena, fighting with 21 different weapons over destructible VGA-style terrain.


Inspired heavily by the Finnish rocket game lineage: 
- TurboRaketti, Heikki Kosola, 1991
- AUTS (1995) by Jaakko Lyytinen (A-More) / The Kudos
- Tcpippeli (Ville Kujala, 199x)
- And so on... 

But mostly AUTS, because it had Shareware licence and excellent controls.
So there is a level-reader for AUTS levels included.


## Features

- **1-4 player local multiplayer** with split-screen viewports on a shared keyboard
- **21 weapons** — machinegun, homing missiles, mines, nukes, blackholes, sonicbooms, EMP, freezer, and more
- **33 terrain maps** loaded from the original AUTS `.LEV` format (RLE-compressed VGA data) plus a no-terrain arena mode
- **Destructible terrain** — projectiles carve out the map on impact
- **VGA Mode 13h aesthetic** — faithful 256-color palette rendering via GDI+
- **Pure functional core** — game logic is a single `GameState -> GameState` tick function with immutable records
- Gravity, thrust, water friction, knockback, shields, stun, and spawn invincibility

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (Windows)

No third-party NuGet packages are required.

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

| Player       | Thrust          | Left            | Right           | Brake  | Fire         |
|--------------|-----------------|-----------------|-----------------|--------|--------------|
| P1 (Blue)    | Up / NumPad8    | Left / NumPad4  | Right / NumPad6 | Down / NumPad5 | RShift / Enter |
| P2 (Green)   | W               | A               | D               | S      | Tab          |
| P3 (Red)     | I               | J               | L               | K      | B            |

**Global keys:** `1`-`4` set player count, `F1`-`F4` cycle weapon per player, `F5`/`F6` change level, `Space` starts a round, `Escape` quits.

## Project Structure

```
Physics.fs    — Constants: gravity, thrust, friction, velocities
Terrain.fs    — Level file loading and RLE decompression
Weapons.fs    — Weapon definitions, entity types, damage tables
Types.fs      — Immutable game-state records (Player, Entity, Particle, GameState)
Entities.fs   — Entity factories, collision detection, arena walls, spawning
Game.fs       — Core game tick logic (pure function)
Renderer.fs   — Split-screen WinForms/GDI+ renderer with VGA palette
Program.fs    — Entry point, keyboard input, game loop
test.fsx      — Smoke tests
*.LEV         — AUTS terrain maps
```

