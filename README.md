# FsRocket

A local multiplayer 2D arena combat game written in F#
Up to 4 players pilot ships in a split-screen arena, fighting with 21 different weapons over destructible VGA-style terrain.

**▶ Play online: https://thorium.github.io/FsRocket/** — runs entirely in the browser, with the F# game logic compiled to JavaScript by [Fable](https://fable.io/) 5.6.0 and rendered on an HTML5 canvas.


Inspired heavily by the Finnish rocket game lineage: 
- TurboRaketti, Heikki Kosola, 1991
- AUTS (1995) by Jaakko Lyytinen (A-More) / The Kudos
- Tcpippeli (Ville Kujala, 199x)
- Kops, Fuse, and so on... 

But mostly AUTS, because it had Shareware licence and excellent controls.
So there is a level-reader for AUTS levels included.

This is the **`FableWeb`** branch — the browser version (Fable + HTML5 Canvas). Separate **`MonoGame`** and **`WinForms`** branches provide native desktop builds from the same shared game logic.

## Features

- **1-4 player local multiplayer** with split-screen viewports on a shared keyboard (on ultrawide windows — width ≥ 2× height — 3/4-player mode uses a single side-by-side row ordered by keyboard position: P1 | P3 | P4 | P2)
- **21 weapons** — machinegun, homing missiles, mines, nukes, blackholes, sonicbooms, EMP, freezer, and more
- **Original AUTS `.LEV` maps** (RLE-compressed VGA data) — ships with 10 maps (`HUNAJA`, `JAATIKKO`, `KARKKI`, `KIERTO`, `KORALLI`, `NEBULA`, `SIENI`, `TEHDAS`, `TULIVUOR`, `VIIDAKKO`; `F5`/`F6` to cycle), and you can **upload your own `.LEV` files** in the browser to replace the rotation
- **Destructible terrain** — projectiles carve out the map on impact; the landing pads and the grey frame along the map edges are indestructible
- **VGA Mode 13h aesthetic** — faithful 256-color palette rendering on an HTML5 canvas
- **Bases / landing pads** — land on a pad to recharge energy and switch your special weapon
- **Pure functional core** — game logic is a single `GameState -> GameState` tick function with immutable records
- Gravity, thrust, water friction, knockback, shields, stun, and spawn invincibility

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) — for the [Vite](https://vitejs.dev/) dev server / bundler

## Build & Run

The browser front-end is built with [Fable](https://fable.io/) **5.6.0** (F# → JavaScript) and Vite.

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

| Player       | Thrust          | Left            | Right           | Special (Down) | Fire           | Switch weapon      |
|--------------|-----------------|-----------------|-----------------|----------------|----------------|--------------------|
| P1 (Blue)    | W               | A               | D               | S              | Tab            | `1` back / `2` fwd |
| P2 (Green)   | Up / NumPad8    | Left / NumPad4  | Right / NumPad6 | Down / NumPad5 | RShift / Enter | `8` back / `9` fwd |
| P3 (Red)     | T               | F               | H               | G              | Y              | `4` back / `5` fwd |
| P4 (Yellow)  | I               | J               | L               | K              | B              | `6` back / `7` fwd |

**Gamepads:** connected gamepads (W3C standard mapping) claim human players in order — pad 1 drives P1, pad 2 drives P2, and so on; remaining players keep their keyboard mappings, and the keyboard also stays active for pad-driven players. Left stick / D-Pad = turn, `A` (or D-Pad up) = thrust, `B`/`RT` = fire, `X`/`LT` = special, `LB`/`RB` = cycle weapon, `Start` = begin round. Useful on membrane keyboards, whose limited key rollover can't report 3-4 players' simultaneous keys. `F10` toggles gamepad input off/on entirely (e.g. to ignore a drifting controller that is left connected).

Cannon-type rounds (cannon, multicannon, machinegun, rubber bullets, rear turret) carry your ship's own motion: shots fired ahead while flying go faster and further, shots fired while backing away fall short. They also drop quickly under gravity, so long shots need to be lobbed.

**Global keys:** `1`-`4` set the player count (in the menu), `F4` toggles "respawn on death" (in the menu — when off, destroyed ships stay dead and the round ends when at most one ship is left flying), `F5`/`F6` cycle the loaded levels, `F7`/`F8` add/remove CPU players, `F2` toggles the CPU level (in the menu — **Hard**, the default, is the full AI; **Easy** is the same AI firing only about once a second), `F9` cycles the weapon-switch rule (on a base / anytime / game start), `F10` toggles gamepad input, `Space` starts a round, `Escape` resets to the menu.

**Menu (web, desktop):** between rounds a clickable menu sets players (1-4), level, CPU ships, CPU level (Hard / Easy), respawn, the weapon-switch rule and gamepads, uploads a `.LEV` map and lists every player's keys. Each control shows its keyboard shortcut, and the keys keep working.

**CPU ships** (both levels) now and then break off to repair: at 20% health or less (the health bar turns red earlier, at 30%), a CPU stops chasing; once no enemy is within close range (about half a screen) it finds its way — around terrain, on any level including your own — to the nearest free base it can reach and stays parked, still turning and firing, until it is back to full health. At most one repair trip per CPU a minute (dying resets the count); the trip is dropped the moment an enemy comes close, or if it hasn't reached a pad within 15 seconds (a ship knocked off its pad gets the full 15 seconds again). When every other ship is dead, a CPU below half health uses the lull to go and repair too; such a trip doesn't count toward the once-a-minute limit. In a fight, when terrain blocks the straight line to its enemy, a CPU looks in the 8 compass directions for open space (clear for 60 pixels) and flies the open way nearest its enemy instead of pushing into the rock. CPUs aim the cannon along the bullet's falling arc and hold fire when the target is out of its reach, never fire a shell that would fall back on themselves, and drop mines, blackholes and toxic pools only while moving fast enough to clear them.

**Touch (phones / tablets):** on touch devices the game switches to single-player against CPU ships (1 by default) in one full-window landscape viewport. A tap menu picks 1 or 2 players, the level, CPU count, CPU level, respawn and weapon-switch rules. With 2 players the screen splits (P1 left, P2 right) and each half gets its own control set, rimmed in that player's ship colour. In a round, `◀`/`▶` on the left turn, the right side has `THRUST`, `FIRE`, `SPECIAL` and the weapon-cycle buttons, and `QUIT` ends the round and returns to the menu. `SPECIAL` greys out while the special weapon can't fire (reloading or stunned) and fills up from the bottom as it reloads; a player's controls dim while their ship is down. Android goes fullscreen in landscape on `START`; in portrait a prompt asks to turn the device. `?touch=1` / `?touch=0` in the URL forces the touch layout on or off.

**Install as an app:** the web build is an installable app (web app manifest + offline service worker). On Android Chrome use *Install app* / *Add to Home screen*; it then launches fullscreen in landscape and starts offline after one online launch. On iPhone/iPad use Safari's *Share → Add to Home Screen*. Desktop Chrome/Edge offer the install button in the address bar. On touch devices the portrait "Turn your device sideways" screen also offers it: an *Install as app* button where the browser supports one, the *Add to Home Screen* steps on an iPhone (whose Safari can't show a web page full screen otherwise). The service worker is only registered in production builds (`npm run build`), not under the Vite dev server.

By default the special weapon can only be changed while **parked on a base**; the per-player keys above cycle it. With the **Game start** rule nobody switches during a round (CPU ships included): START first opens a "Choose your weapon" dialog where each human picks a special — or **Random**, drawn when the round starts — with the mouse, the drop-down, or their weapon keys. Use the **Upload a .LEV map** button under the canvas to load your own maps.

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

<img width="961" height="628" alt="image" src="https://github.com/user-attachments/assets/aee02f7f-6e38-4e2a-a3b8-99d1252aa060" />
