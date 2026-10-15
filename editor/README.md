# FsRocket Level Editor

A standalone WinForms editor for the AUTS `.LEV` terrain maps used by FsRocket.
It loads, edits, and saves the original RLE-compressed `.LEV` format **byte-for-byte
compatibly** with the game (the encoder round-trips the bundled `.LEV` maps identically), so
anything you make here drops straight into the game's level list.

```
dotnet run --project editor/FsRocketEditor.fsproj
```

(Windows only — like the game, the editor targets `net10.0-windows` / WinForms.)

---

## The four terrain colours — what every pixel MEANS

The game only cares about four kinds of pixel. This is the key to making imported
images behave correctly:

| Material  | Palette value        | Behaviour in game                                                        |
|-----------|----------------------|--------------------------------------------------------------------------|
| **Void**  | `0x00` (black)       | Empty sky / flyable space. Carve caves and tunnels with this. Ships and bullets pass through freely. |
| **Wall**  | **any other colour** | Solid, destructible terrain. Ships crash into it; ammo erases it.        |
| **Water** | `0x27`               | Penetrable, but applies friction/drag to ships moving through it.        |
| **Base**  | `0x5C`–`0x5F`         | Landing pad. **Indestructible.** Ships land to stop, recharge energy and switch weapons. |

Two important consequences:

- **Every colour that isn't `0x00`, `0x27`, or `0x5C–0x5F` is a wall.** That is why
  an imported photo becomes solid ground — all its colours read as wall.
- **Spawn points are not stored** in the file. The game derives them at load time
  from each **base** pixel that has **void directly above it**. Toggle **Spawns**
  (`G`) to preview exactly where ships will start as you paint pads.

The editor renders bases with a fixed green bar (so pads are easy to spot), void as
black, water as the palette's blue, and walls in their own colours.

---

## Importing an image (jpg / png / bmp / gif → .LEV)

`File → Import image…` (or the [command-line converter](#command-line-converter))
maps any image onto the 320×400 map. **Fit modes:** *Stretch* (distort to fill),
*Fit* (keep aspect ratio, letterbox with void), *Center* (native size, centred).

There are two **colour modes**:

### 1. Walls only — for textures you'll carve

Every source pixel maps to the **nearest solid wall colour** — never water, base, or
pure black. The **whole image becomes solid terrain**, always visually distinct from
carved-out caves. Optionally tick **"Carve dark pixels into caves"** so pixels darker
than a threshold become Void on import.

Recommended workflow:

1. **Import** a texture — e.g. a brick-wall photo — as solid terrain.
2. **Carve** caves and tunnels with the **Void** brush (or right-click, which always
   paints Void).
3. **Place landing pads** with the **Base** brush. Leave empty void just above each pad
   so it registers as a spawn point (check with **Spawns**).
4. Add **Water** where you want drag, then `File → Save`.

### 2. Keep colours — for levels you painted by hand

Paint a complete level in any image editor using these **key colours**, then import it
ready to play. Pixels close to a key colour become that functional material; everything
else keeps its appearance as solid wall:

| Paint this colour | Becomes      |
|-------------------|--------------|
| black `#000000`   | Void / caves |
| blue  `#0099FF`   | Water        |
| green `#30C060`   | Base / landing pad |
| any other colour  | Solid wall (colour preserved) |

(These are exactly the colours the editor itself draws, so what you paint is what you
get. A base still needs empty void directly above it to yield a spawn point.)

The **Key-colour tolerance** in the dialog controls how close a pixel must be to a key
colour to snap to it (default 24 per channel — strict, so only colours you genuinely
meant as void/water/base convert). Raise it to absorb JPEG noise; set it to 0 to match
the key colours exactly.

---

## Good levels: check and clean up

A ship needs about **9 px** (map pixels) of free space to turn — its sprite reaches about
4 px from its centre. Space narrower than that looks like a tunnel on screen but can't be
flown, and a player may have no digging weapon to open it. Toggle **Check** (`C`, or
`Level → Check flyability`) to colour what the ship can't use:

| Colour   | Problem |
|----------|---------|
| red      | **pocket** — enclosed space the ship can't enter |
| yellow   | **slit** — a dead end or corner too narrow to fly into |
| cyan     | **narrow gap** — the only way between two open areas, too tight to pass |
| magenta  | **small sealed cave** — open space with no pad and no way in |
| orange   | **speck** — a loose bit of wall of 20 px or less |
| white    | under a pad: **no ship-sized route** from it to the other pads |

The status bar sums them up, and a green ring at the cursor shows the ship's turning room.

`Level → Clean up…` (`Ctrl+K`) fixes them, the same way the bundled levels were cleaned:
it fills pockets, slits and small caves (copying the nearby terrain's texture and outline
colour), widens the narrow gaps, tunnels to cut-off pads through the thinnest wall and
removes specks; optionally it also trims thin wall spikes. **Pads, the space just above
them and the map's grey frame are never changed**, so spawn points stay where they were.
`Ctrl+Z` takes the whole cleanup back. **Publish to game** warns when the check finds
problems.

Tips: prefer big terrain pieces over many small ones, keep thin outlined frames solid (or
open them wide), and give every pad free sky above it. `Help → Good levels` has the same
list in the editor.

## Command-line converter

Run the editor with arguments to convert an image to a `.LEV` headlessly (no GUI),
handy for scripting or batch jobs:

```
dotnet run --project editor/FsRocketEditor.fsproj -- <input-image> <output.LEV> [fit] [colours] [caveThreshold]
```

- `fit` — `stretch` (default) · `fit` · `center`
- `colours` — `solid` (default, everything wall) · `keep` (black/blue/green = void/water/base)
- `caveThreshold` — *(solid only)* `0`–`255`; pixels darker than this become void caves (`-1` = off)

Examples:

```
# A brick texture as solid terrain, to carve later:
dotnet run --project editor/FsRocketEditor.fsproj -- bricks.png CAVES.LEV stretch solid

# A level you painted with the key colours, ready to play:
dotnet run --project editor/FsRocketEditor.fsproj -- mylevel.png MYLEVEL.LEV fit keep
```

## Level commands (for scripts and agents)

Every level tool also works from the command line, so a script or a coding agent can
check, fix, look at and compare levels without the GUI. Each command takes one `.LEV`
or a whole folder of them (a folder in writes a folder out, one file per level). Below,
`ed` stands for `dotnet run --project editor/FsRocketEditor.fsproj --` or the built
`FsRocketEditor.exe`.

| Command | What it does |
|---------|--------------|
| `ed --check <level\|folder> [--json]` | What the ship can't fly, one line per level. `--json` lists every problem spot: kind, bounding box, centre and size in map pixels. |
| `ed --clean <in> <out> [flags]` | Make the level flyable (as **Clean up** in the editor). Flags turn steps off — `--no-fill`, `--no-widen`, `--no-connect`, `--keep-specks` — or on: `--spikes <r>` trims wall spikes up to 2*r+1 px wide. |
| `ed --render <level\|folder> <out.png\|folder> [--check] [--scale <n>]` | The level as a PNG (scale 1-8, default 2); `--check` adds the check overlay. |
| `ed --compare <before> <after> <out.png\|folder> [--scale <n>]` | Before \| after \| changes side by side: green = new wall, red = opened. Folders pair levels by file name. |
| `ed --clear <in.LEV> <out.LEV> <area>...` | Clear terrain for more flight space and tidy the cut (stubs, crumbs and slivers go, new edges get the outline colour; pads and the frame stay). An area is `rect:x0,y0,x1,y1` or `ellipse:x0,y0,x1,y1` in map pixels (0-319, 0-399). Run `--clean` afterwards to fill any new dead space. |

**Exit codes:** `0` done, `1` the check found problems, `2` bad arguments or files.

```
# Which levels need work, and where exactly:
ed --check levels --json > report.json

# Clean them all into a new folder and look at what changed:
ed --clean levels cleaned
ed --compare levels cleaned review

# Clear two spots in one level, then tidy what that leaves:
ed --clear KORALLI.LEV KORALLI2.LEV ellipse:23,169,86,207 rect:122,177,150,210
ed --clean KORALLI2.LEV KORALLI2.LEV
```

The `--json` report looks like this (one entry per level):

```json
[
  {
    "level": "KORALLI",
    "flyable": false,
    "pads": 4,
    "cutOffPads": 1,
    "counts": { "pockets": 12, "slits": 125, "narrowGaps": 4, "smallCaves": 1, "specks": 3 },
    "problems": [
      { "kind": "pocket", "x0": 66, "y0": 282, "x1": 74, "y1": 300, "centerX": 69, "centerY": 291, "pixels": 109 }
    ]
  }
]
```

Problem kinds: `pocket`, `slit`, `narrow-gap`, `small-cave`, `speck`, `cut-off-pad`.

With a single `.LEV` argument the editor opens that level in the GUI. Run with `--help`
for the full argument list.

## Opening, saving & publishing to the game

The editor keeps its own working `.LEV` files separate from the game's copy, so you
edit freely and decide when to push a level into the game:

- **Open** — `File → Open…` (`Ctrl+O`) loads any `.LEV` (the game's maps, or your own)
  into the canvas, fully decompressed and editable.
- **Save / Save As** — `Ctrl+S` writes the standard RLE-compressed `.LEV`.
- **Publish to game** — `File → Publish to game` (`Ctrl+P`) saves the level, then copies
  it into the game's level folder. The first time it asks you to pick that folder (the
  one next to the FsRocket game, where its `.LEV` files live) and remembers it for next
  time. Change it later with `File → Set game folder…`.
  **Restart the game** after publishing — it scans for levels at startup, so a freshly
  copied map shows up in the level list (switch with `F5`/`F6`) on the next launch.

> Prefer to do it by hand? A `.LEV` is fully self-contained — just copy your saved file
> into the same folder as the game executable (alongside the bundled `.LEV` maps) and restart the
> game. In a source checkout that folder is the repo root, from where the build copies
> `*.LEV` into the game's output.

The remembered game folder is stored in `FsRocketEditor.settings` next to the editor.

## Tools & shortcuts

| Action       | Keys                                             |
|--------------|--------------------------------------------------|
| Tools        | `B` Brush · `F` Fill · `L` Line · `R` Rectangle  |
| Materials    | `1` Void · `2` Wall · `3` Water · `4` Base       |
| Brush size   | `[` smaller · `]` larger                         |
| Zoom         | `-` out · `+` in                                 |
| Spawns       | `G` toggle spawn-point preview                   |
| Check        | `C` toggle the flyability check · `Ctrl+K` clean up |
| Undo / Redo  | `Ctrl+Z` / `Ctrl+Y`                              |
| File         | `Ctrl+N` new · `Ctrl+O` open · `Ctrl+S` save     |
| Publish      | `Ctrl+P` copy the level into the game's folder   |
| Colour guide | `F1`                                             |

**Mouse:** left button paints the current material; **right button always erases
(paints Void)**. For Line and Rectangle, press–drag–release.

- **Fill** is *class-aware*: it floods the connected region of the same material
  (so a wall area made of many shades fills as one).

---

## Notes on the `.LEV` format

A `.LEV` file is two RLE-compressed 320×200 VGA pages (top and bottom halves of the
320×400 map) followed by 8 trailing bytes — four little-endian `uint16` "viewport"
words. Those words are metadata only; FsRocket does not use them for gameplay. The
editor preserves them when you edit an existing map and writes a neutral full-map
default (`0, 0, 319, 399`) for brand-new maps.

The compressor lives in `Terrain.fs` (`compressLevel` / `saveLevel`) as the exact
inverse of the game's existing `decompressLevel`, and is covered by round-trip tests.

The level check and cleanup (`LevelCheck.fs`) are covered by `editor/test-editor.fsx`:

```
dotnet fsi editor/test-editor.fsx [folder of .LEV files to clean]
```
