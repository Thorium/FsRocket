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
- **Destructible terrain** — projectiles carve out the map on impact; the landing pads and the grey frame along the map edges are indestructible
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

The main gun fires with the **Fire** key; the **Special** weapon fires with the **Down** key. The weapon-switch keys (placed near each player's hands) cycle the special weapon — by default **only while parked on a base**. Cannon-type rounds (cannon, multicannon, machinegun, rubber bullets, rear turret) carry your ship's own motion: shots fired ahead while flying go faster and further, shots fired while backing away fall short. They also drop quickly under gravity, so long shots need to be lobbed.

**Gamepads:** connected controllers claim human players in order — pad 1 drives P1, pad 2 drives P2, and so on; remaining players keep their keyboard mappings, and the keyboard also stays active for pad-driven players. Left stick / D-Pad = turn, `A` (or D-Pad up) = thrust, `B`/`RT` = fire, `X`/`LT` = special, `LB`/`RB` = cycle weapon, `Start` = begin round. `F10` toggles gamepad input off/on entirely (e.g. to ignore a drifting controller). Useful on membrane keyboards, whose limited key rollover can't report 3-4 players' simultaneous keys.

**Global keys:** `1`-`4` set the player count (in the menu), `F4` toggles "respawn on death" (in the menu — when off, destroyed ships stay dead and the round ends when at most one ship is left flying), `F5`/`F6` change level, `F7`/`F8` add/remove CPU players, `F2` toggles the CPU level (in the menu — **Hard**, the default, is the full AI; **Easy** is the same AI firing only about once a second), `F9` cycles the weapon-switch rule (on a base / anytime / game start), `F10` toggles gamepad input, `F11` toggles fullscreen, `F1` shows every player's keys, `Space` starts a round, `Escape` returns to the menu / quits.

On Windows the accessibility shortcuts (Shift pressed five times for Sticky Keys, right Shift held for Filter Keys, Num Lock held for Toggle Keys) are off while the game window is in front, so their dialogs can't interrupt a round; they come back when you switch away or quit. Players who have the feature itself turned on keep it. No admin rights are needed, and nothing is saved to your settings.

**Menu:** between rounds a menu panel sets players (1-4), level, CPU ships, CPU level (Hard / Easy), respawn, the weapon-switch rule and gamepads; it is clickable with the mouse, and each control shows its keyboard shortcut. The game opens on VIIDAKKO.

**CPU ships** (both levels) now and then break off to repair: at 20% health or less (the health bar turns red earlier, at 30%), a CPU stops chasing; once no enemy is within close range (about half a screen) it finds its way — around terrain, on any level including your own — to the nearest free base it can reach and stays parked, still turning and firing, until it is back to full health. At most one repair trip per CPU a minute (dying resets the count); the trip is dropped the moment an enemy comes close, or if it hasn't reached a pad within 15 seconds (a ship knocked off its pad gets the full 15 seconds again). When every other ship is dead, a CPU below half health uses the lull to go and repair too; such a trip doesn't count toward the once-a-minute limit. In a fight, when terrain blocks the straight line to its enemy, a CPU looks in the 8 compass directions for open space (clear for 60 pixels) and flies the open way nearest its enemy instead of pushing into the rock. CPUs aim the cannon along the bullet's falling arc and hold fire when the target is out of its reach, never fire a shell that would fall back on themselves, and drop mines, blackholes and toxic pools only while moving fast enough to clear them.

With the **Game start** weapon-switch rule nobody switches during a round (CPU ships included): START first opens a "Choose your weapon" dialog where each human picks a special — or **Random**, drawn when the round starts — with the mouse, their weapon keys or `LB`/`RB`.

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

<img width="961" height="628" alt="image" src="https://github.com/user-attachments/assets/aee02f7f-6e38-4e2a-a3b8-99d1252aa060" />
