# FsRocket

A local multiplayer 2D arena combat game written in F#
Up to 4 players pilot ships in a split-screen arena, fighting with 21 different weapons over destructible VGA-style terrain.

**▶ Play online: https://thorium.github.io/FsRocket/** — runs entirely in the browser, with the F# game logic compiled to JavaScript by [Fable](https://fable.io/) 5.0.0 and rendered on an HTML5 canvas.


Inspired heavily by the Finnish rocket game lineage: 
- TurboRaketti, Heikki Kosola, 1991
- AUTS (1995) by Jaakko Lyytinen (A-More) / The Kudos
- Tcpippeli (Ville Kujala, 199x)
- Kops, Fuse, and so on... 

But mostly AUTS, because it had Shareware licence and excellent controls.
So there is a level-reader for AUTS levels included.

This is the **`FableWeb`** branch — the browser version (Fable + HTML5 Canvas). Separate **`MonoGame`** and **`WinForms`** branches provide native desktop builds from the same shared game logic.

## Features

- **1-4 player local multiplayer** with split-screen viewports on a shared keyboard
- **21 weapons** — machinegun, homing missiles, mines, nukes, blackholes, sonicbooms, EMP, freezer, and more
- **Original AUTS `.LEV` maps** (RLE-compressed VGA data) — ships with `CLASSIC`, and you can **upload your own `.LEV` files** in the browser to build a level rotation (`F5`/`F6` to cycle)
- **Destructible terrain** — projectiles carve out the map on impact
- **VGA Mode 13h aesthetic** — faithful 256-color palette rendering on an HTML5 canvas
- **Bases / landing pads** — land on a pad to recharge energy and switch your special weapon
- **Pure functional core** — game logic is a single `GameState -> GameState` tick function with immutable records
- Gravity, thrust, water friction, knockback, shields, stun, and spawn invincibility

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) — for the [Vite](https://vitejs.dev/) dev server / bundler

## Build & Run

The browser front-end is built with [Fable](https://fable.io/) **5.0.0** (F# → JavaScript) and Vite.

```
dotnet tool restore     # installs the Fable compiler (pinned in .config/dotnet-tools.json)
npm install
npm run dev             # Fable watch + Vite dev server (http://localhost:5173)
```

Production build — emits static files to `dist/`:

```
npm run build           # dotnet fable && vite build
```

Deployment to GitHub Pages (https://thorium.github.io/FsRocket/) is automated by
`.github/workflows/deploy.yml` on pushes to the `FableWeb` branch.

## Controls

| Player       | Thrust          | Left            | Right           | Special (Down) | Fire           | Switch weapon |
|--------------|-----------------|-----------------|-----------------|----------------|----------------|---------------|
| P1 (Blue)    | Up / NumPad8    | Left / NumPad4  | Right / NumPad6 | Down / NumPad5 | RShift / Enter | `9`           |
| P2 (Green)   | W               | A               | D               | S              | Tab            | `1`           |
| P3 (Red)     | I               | J               | L               | K              | B              | `6`           |

**Global keys:** `1`-`4` set the player count (in the menu), `F5`/`F6` cycle the loaded levels, `F7`/`F8` add/remove CPU players, `F9` toggles the "switch weapons only on a base" rule, `Space` starts a round, `Escape` resets to the menu.

By default the special weapon can only be changed while **parked on a base**; the per-player keys above (`9`/`1`/`6`) cycle it. Use the **Upload a .LEV map** button under the canvas to load your own maps.

## Project Structure

```
Physics.fs    — Constants: gravity, thrust, friction, velocities
Terrain.fs    — Level file loading and RLE decompression
Weapons.fs    — Weapon definitions, entity types, damage tables
Types.fs      — Immutable game-state records (Player, Entity, Particle, GameState)
Entities.fs   — Entity factories, collision detection, arena walls, spawning
Game.fs       — Core game tick logic (pure function)
Renderer.fs   — Split-screen HTML5 Canvas 2D renderer with VGA palette
Program.fs    — Browser entry point: canvas, requestAnimationFrame loop, keyboard input, level upload
index.html    — Page shell (canvas + upload control)
vite.config.ts— Vite config (GitHub Pages base path)
public/*.LEV  — AUTS terrain maps served as static assets
```

`Physics.fs`, `Terrain.fs`, `Weapons.fs`, `Types.fs`, `Entities.fs` and `Game.fs` are the
shared, framework-agnostic game logic used by all three front-ends (Fable, MonoGame, WinForms).

<img width="1185" height="717" alt="image" src="https://github.com/user-attachments/assets/052d8c29-2cda-4dc2-a88b-ebf61fe9d4d9" />
