/// FsRocket - Terrain System
/// RLE decompression and pixel-based terrain from .LEV files
/// Decompression matches LoadAndDecompressBackground at 0000:11AF
/// Spawn scanning matches LoadMapAndFindSpawns at 0000:141F
module FsRocket.Terrain

open System
#if !FABLE_COMPILER
open System.IO
#endif
open FsRocket.Physics

// ─── Constants ─────────────────────────────────────────────────────────

/// Arena is two stacked 320x200 VGA pages = 320x400
let PageSize = MapWidth * 200  // 64000 bytes per VGA page

/// Terrain color constants
/// New model: only totally black is penetrable (flyable).
/// Water is also penetrable but applies friction.
/// Everything else is solid wall.
/// Dark gray pixels are indestructible walls that cannot be erased by ammo.
[<Literal>]
let WaterColor = 0x27uy          // Water surface — penetrable, friction applies
// Base / landing pad colour range — shaded grey bars (0x5C..0x5F), drawn as small
// horizontal rectangles in the AUTS maps. Ships land here without taking collision
// damage, recharge energy, and may switch weapons. Bases are also indestructible:
// ammo cannot erase them. (Earlier code mislabelled this range as "indestructible
// walls" and used 0x28 for bases — but real AUTS maps colour bases 0x5C..0x5F.)
/// Base/landing pad range low
[<Literal>]
let BaseColorMin = 0x5Cuy
/// Base/landing pad range high
[<Literal>]
let BaseColorMax = 0x5Fuy
/// Empty/sky — the only freely flyable space
[<Literal>]
let VoidColor = 0x00uy

// ─── Spawn Point ───────────────────────────────────────────────────────
[<Struct>]
type SpawnPoint =
    { X: int; Y: int; Width: int }

// ─── Level Data ────────────────────────────────────────────────────────

type LevelData =
    { Pixels: byte array           // 320x400 flat pixel array (row-major)
      Viewports: int * int * int * int  // (vx0, vy0, vx1, vy1)
      SpawnPoints: SpawnPoint array
      Name: string }

// ─── RLE Decompression ────────
//
// Algorithm per VGA page (two passes, 64000 bytes each):
//   1. first_flag = true
//   2. Loop until dest_offset >= 64000:
//      a. If first_flag: prev = 0xFFFF (impossible match), clear flag
//         Else: prev = current_byte
//      b. Read next source byte -> current_byte
//      c. Write current_byte to dest[dest_offset]
//      d. If current_byte == prev (RLE match):
//           - Read run_count byte from source
//           - Write (run_count - 2) additional copies at dest[dest_offset+1..]
//           - Advance dest_offset past the run
//           - Set first_flag = true (next byte starts fresh)
//      e. dest_offset++

let decompressPage (src: byte array) (srcOffset: int) (dest: byte array) (destOffset: int) : int =
    let mutable si = srcOffset
    let mutable di = destOffset
    let destEnd = destOffset + PageSize
    let mutable firstFlag = true
    let mutable prev = 0xFFFF  // Impossible match on first byte

    while di < destEnd do
        if firstFlag then
            prev <- 0xFFFF
            firstFlag <- false
        else
            prev <- int dest[di - 1]  // prev = last written byte

        // Read next source byte
        let cur = int src[si]
        si <- si + 1

        // Write to destination
        dest[di] <- byte cur

        // RLE check: current == previous?
        if cur = prev then
            // Read run count
            let runCount = int src[si]
            si <- si + 1
            // Fill (runCount - 2) additional copies
            // The "2" accounts for the two trigger bytes already written
            let extra = runCount - 2
            if extra >= 0 then
                for _ in 0 .. extra do
                    di <- di + 1
                    if di < destEnd then
                        dest[di] <- byte cur
            firstFlag <- true  // Reset: next byte has no valid predecessor

        di <- di + 1

    si  // Return new source offset (where we stopped reading)

/// Read a little-endian uint16 from a byte array (avoids BitConverter so the
/// code transpiles cleanly under Fable as well as on .NET).
let inline private readU16LE (data: byte array) (offset: int) : int =
    int data[offset] ||| (int data[offset + 1] <<< 8)

/// Decompress a .LEV file into a 320x400 pixel array + 4 viewport words
let decompressLevel (data: byte array) : byte array * (int * int * int * int) =
    // Last 8 bytes are 4 LE uint16 viewport words
    let compressedSize = data.Length - 8

    // Read viewport words from last 8 bytes
    let vp0 = readU16LE data compressedSize
    let vp1 = readU16LE data (compressedSize + 2)
    let vp2 = readU16LE data (compressedSize + 4)
    let vp3 = readU16LE data (compressedSize + 6)

    let pixels = Array.zeroCreate<byte> (MapWidth * MapHeight)

    // Pass 1: decompress first 320x200 page (rows 0..199)
    let srcAfterPage1 = decompressPage data 0 pixels 0

    // Pass 2: decompress second 320x200 page (rows 200..399)
    decompressPage data srcAfterPage1 pixels PageSize |> ignore

    pixels, (vp0, vp1, vp2, vp3)

// ─── Pixel Access ──────────────────────────────────────────────────────

/// Get terrain pixel at (x, y). Returns 0x00 for out-of-bounds.
let inline getPixel (pixels: byte array) (x: int) (y: int) =
    if x < 0 || x >= MapWidth || y < 0 || y >= MapHeight then VoidColor
    else pixels[y * MapWidth + x]

/// Is this pixel a solid wall? Everything that is NOT black (0x00), NOT water,
/// and NOT base is treated as solid — ships and most projectiles cannot pass through.
let inline isWall (pixel: byte) =
    pixel <> VoidColor && pixel <> WaterColor && not (pixel >= BaseColorMin && pixel <= BaseColorMax)

/// Is this pixel water/ground? 
let inline isWater (pixel: byte) =
    pixel = WaterColor

/// Is this pixel a base/landing pad? (shaded grey bar range)
let inline isBase (pixel: byte) =
    pixel >= BaseColorMin && pixel <= BaseColorMax

/// Is this pixel void/empty/flyable? (0x00)
let inline isVoid (pixel: byte) =
    pixel = VoidColor

/// Is this pixel passable (flyable)? Black, water, or base.
let inline isPassable (pixel: byte) =
    pixel = VoidColor || pixel = WaterColor || isBase pixel

/// Is this pixel indestructible (cannot be erased by ammo)? Bases are the only
/// indestructible pixels — the shaded grey landing-pad bars.
let inline isIndestructible (pixel: byte) =
    isBase pixel

/// Colour of the grey frame the maps draw along their edges.
[<Literal>]
let FrameColor = 0x07uy
/// How far in from the map edge the frame reaches (2-3 px in the bundled maps,
/// thicker in places).
[<Literal>]
let FrameDepth = 6

/// Is this pixel the map's edge frame — "bedrock" that ammo can't erase,
/// like the pads?
let inline isFrame (x: int) (y: int) (pixel: byte) =
    pixel = FrameColor
    && (x < FrameDepth || y < FrameDepth || x >= MapWidth - FrameDepth || y >= MapHeight - FrameDepth)

// ─── Spawn Point Scanning  ─────
//
// Scan rows 2..398, cols 0..319:
//   If pixel(col, row) == 0x28 (base) AND pixel(col, row-1) == 0x00 (void above):
//     Found base spawn. Measure width rightward while same condition holds.
//     Record {x=col, y=row-1, width=measured-1}. Skip col past the base.

let findSpawnPoints (pixels: byte array) : SpawnPoint array =
    let spawns = ResizeArray<SpawnPoint>()
    let mutable row = 2

    while row <= 398 do
        let mutable col = 0

        while col < 320 do
            let current = getPixel pixels col row

            if isBase current then
                let above = getPixel pixels col (row - 1)

                if above = VoidColor then
                    // Found base spawn — measure width rightward
                    let mutable width = 0

                    let mutable measuring = true
                    while measuring do
                        width <- width + 1
                        let rightCur = getPixel pixels (col + width) row
                        let rightAbove = getPixel pixels (col + width) (row - 1)
                        if not (isBase rightCur) || rightAbove <> VoidColor then
                            measuring <- false

                    spawns.Add { X = col; Y = row - 1; Width = width - 1 }
                    col <- col + width  // Skip past this base
                else
                    col <- col + 1
            else
                col <- col + 1

        row <- row + 1

    // Merge spawn points belonging to the same pad. A single base bar is drawn
    // with shaded/beveled rows, so the scan reports the wide top row plus a few
    // tiny stray points along the bevel. Collapse points that overlap in x
    // (within a small margin) and sit within a few rows into one base, so two
    // ships are never told to spawn on what is really the same pad.
    let xMargin = 6
    let yMargin = 6
    let merged = ResizeArray<SpawnPoint>()
    for sp in spawns |> Seq.sortBy (fun s -> s.Y, s.X) do
        let mutable hit = -1
        for i in 0 .. merged.Count - 1 do
            let m = merged[i]
            let overlapX = sp.X <= m.X + m.Width + xMargin && m.X <= sp.X + sp.Width + xMargin
            if hit < 0 && overlapX && abs (sp.Y - m.Y) <= yMargin then hit <- i
        if hit < 0 then
            merged.Add sp
        else
            let m = merged[hit]
            let x0 = min m.X sp.X
            let x1 = max (m.X + m.Width) (sp.X + sp.Width)
            merged[hit] <- { X = x0; Y = min m.Y sp.Y; Width = x1 - x0 }

    merged.ToArray()

// ─── Load a .LEV File ──────────────────────────────────────────────────

/// Build level data from raw .LEV bytes. Pure (no file IO) so it works both on
/// .NET and under Fable, where the bytes are fetched over HTTP instead.
let loadLevelFromBytes (name: string) (data: byte array) : LevelData =
    let pixels, viewports = decompressLevel data
    let spawns = findSpawnPoints pixels
    { Pixels = pixels
      Viewports = viewports
      SpawnPoints = spawns
      Name = name }

#if !FABLE_COMPILER
/// Read a .LEV file from disk (desktop builds only — Fable has no file system).
let loadLevel (filePath: string) : LevelData =
    let data = File.ReadAllBytes filePath
    loadLevelFromBytes (Path.GetFileNameWithoutExtension filePath) data
#endif

// ─── Terrain Queries for Game Logic ────────────────────────────────────

/// Check terrain at a world position (float coords, pre-divided by PositionScale)
let terrainAt (pixels: byte array) (x: float) (y: float) : byte =
    getPixel pixels (int (round x)) (int (round y))

/// Is position hitting a wall? (for collision)
let hitsTerrainWall (pixels: byte array) (x: float) (y: float) : bool =
    isWall (terrainAt pixels x y)

/// Is position on water? (for friction)
let isOnWater (pixels: byte array) (x: float) (y: float) : bool =
    isWater (terrainAt pixels x y)

/// Is position on a base? (for spawn marking)
let isOnBase (pixels: byte array) (x: float) (y: float) : bool =
    isBase (terrainAt pixels x y)

/// Pick a spawn point avoiding every currently-occupied base index, so two ships
/// never start on the same base. Falls back to allowing reuse only when all bases
/// are occupied (more players than bases), and to a random arena position when the
/// level has no bases at all.
let randomSpawnExcluding (spawns: SpawnPoint array) (rng: Random) (occupied: int list) : int * int * int =
    if spawns.Length = 0 then
        rng.Next MapWidth, rng.Next MapHeight, -1
    else
        let free = [| for i in 0 .. spawns.Length - 1 do if not (List.contains i occupied) then yield i |]
        let idx =
            if free.Length > 0 then free[rng.Next free.Length]
            else rng.Next spawns.Length   // all bases taken — unavoidable reuse
        let sp = spawns[idx]
        sp.X + sp.Width / 2, sp.Y, idx

// ─── Terrain Modification (ammo erasing walls) ────────────────────────

// Dirty-region tracking: eraseTerrainCircle accumulates a bounding rect of
// the pixels it changes so a renderer can repaint just that region instead
// of rebuilding the whole 320x400 bitmap. Empty is encoded as x1 < x0.
let mutable private dirtyX0 = 0
let mutable private dirtyY0 = 0
let mutable private dirtyX1 = -1
let mutable private dirtyY1 = -1

let private markDirty (x0: int) (y0: int) (x1: int) (y1: int) =
    let x0 = max 0 x0
    let y0 = max 0 y0
    let x1 = min (MapWidth - 1) x1
    let y1 = min (MapHeight - 1) y1
    if x1 >= x0 && y1 >= y0 then
        if dirtyX1 < dirtyX0 then
            dirtyX0 <- x0; dirtyY0 <- y0; dirtyX1 <- x1; dirtyY1 <- y1
        else
            dirtyX0 <- min dirtyX0 x0
            dirtyY0 <- min dirtyY0 y0
            dirtyX1 <- max dirtyX1 x1
            dirtyY1 <- max dirtyY1 y1

/// A queued water fill: pixel indices that turn to water in order, a few per tick.
type private WaterFlow = { Cells: int array; Next: int }

let mutable private waterFlows: WaterFlow list = []

/// Index of the pixels the queued flows will fill (kept in step with them):
/// they already count as water when a later hole plans its flow, so digging
/// faster than the water flows still connects.
let private queuedWater = Array.zeroCreate<bool> (MapWidth * MapHeight)

/// Mark the whole map dirty — call after wholesale pixel changes that bypass
/// eraseTerrainCircle (level reset from the pristine copy, upload, switch).
/// Also drops queued water fills, which belong to the old pixels.
let markAllDirty () =
    waterFlows <- []
    Array.fill queuedWater 0 queuedWater.Length false
    markDirty 0 0 (MapWidth - 1) (MapHeight - 1)

/// Pop the accumulated dirty rect as inclusive (x0, y0, x1, y1), if any.
let takeDirtyRect () : (int * int * int * int) option =
    if dirtyX1 < dirtyX0 then None
    else
        let r = (dirtyX0, dirtyY0, dirtyX1, dirtyY1)
        dirtyX1 <- -1
        dirtyX0 <- 0
        Some r

/// Set a pixel in the terrain data. Does nothing for out-of-bounds.
let inline setPixel (pixels: byte array) (x: int) (y: int) (value: byte) =
    if x >= 0 && x < MapWidth && y >= 0 && y < MapHeight then
        pixels[y * MapWidth + x] <- value

// ─── Water Flow (holes shot next to water) ────────────────────────────
//
// A hole carved next to water fills with it, up to (just below) the top row
// of that water body. If the space the hole opens into is no bigger than the
// pond, it fills completely, bottom-up. If it is bigger — a leak — the hole
// fills, water falls straight down from its edges as a waterfall the width
// of the hole, and a small pool spreads where it lands. The pond's size caps
// the water drawn, so a tiny pond never floods the level; its surface stays
// put. All the work happens when a hole is shot; each tick then only places
// a few queued pixels, so the fill animates.

/// Most water one hole can draw from its pond (a contained cavity up to this
/// size fills completely; a leak's waterfall and pool stop around here).
[<Literal>]
let private MaxWaterFill = 4000
/// Water pixels placed per tick per fill — the flow animation speed.
[<Literal>]
let private WaterCellsPerTick = 30
/// Bound on the water-body search (the bundled ponds are under 6000 px).
[<Literal>]
let private MaxWaterBody = 30000

let inline private rowOf (i: int) = i / MapWidth

/// Pixel i's neighbours on the map: left, right, up, down.
let private neighbors (i: int) =
    let x = i % MapWidth
    let all = [| (if x > 0 then i - 1 else -1); (if x < MapWidth - 1 then i + 1 else -1); i - MapWidth; i + MapWidth |]
    // only edge pixels lose neighbours
    if x > 0 && x < MapWidth - 1 && i >= MapWidth && i < MapWidth * (MapHeight - 1) then all
    else all |> Array.filter (fun j -> j >= 0 && j < MapWidth * MapHeight)

/// Pixel i's k-th neighbour (0 left, 1 right, 2 up, 3 down), or -1 off the map.
let private neighbor (i: int) (k: int) =
    let x = i % MapWidth
    match k with
    | 0 -> if x > 0 then i - 1 else -1
    | 1 -> if x < MapWidth - 1 then i + 1 else -1
    | 2 -> if i >= MapWidth then i - MapWidth else -1
    | _ -> if i < MapWidth * (MapHeight - 1) then i + MapWidth else -1

/// The pixel below i, or -1 off the map.
let private below (i: int) = if i < MapWidth * (MapHeight - 1) then i + MapWidth else -1

/// The pixel dx (-1 or 1) beside i, or -1 off the map.
let private beside (dx: int) (i: int) =
    let x = i % MapWidth + dx
    if x >= 0 && x < MapWidth then i + dx else -1

/// Water, or queued to become water.
let private isWet (pixels: byte array) (i: int) = pixels[i] = WaterColor || queuedWater[i]

/// Empty space that no queued water is headed for.
let private isDry (pixels: byte array) (i: int) = pixels[i] = VoidColor && not queuedWater[i]

let private touchesWater (pixels: byte array) (i: int) = neighbors i |> Array.exists (isWet pixels)

// Scratch for flood: a visit stamp per pixel (a fresh stamp per search, so
// the visited set needs no clearing) and the search queue.
let private visitStamp = Array.zeroCreate<int> (MapWidth * MapHeight)
let private searchQueue = Array.zeroCreate<int> (MapWidth * MapHeight)
let mutable private lastStamp = 0

/// Breadth-first search from `seeds` through the pixels `canEnter` accepts,
/// giving up once more than `limit` are found. The pixels in visiting order.
let private flood (canEnter: int -> bool) (limit: int) (seeds: int array) =
    lastStamp <- lastStamp + 1
    let mark = lastStamp
    let enqueue n i =
        if visitStamp[i] = mark then
            n
        else
            visitStamp[i] <- mark
            searchQueue[n] <- i
            n + 1
    // the four neighbours of i, without allocating: this runs for every pixel searched
    let rec around i k n =
        if k = 4 then
            n
        else
            let j = neighbor i k
            around i (k + 1) (if j >= 0 && canEnter j then enqueue n j else n)
    let rec grow head n =
        if head >= n || n > limit then n
        else grow (head + 1) (around searchQueue[head] 0 n)
    Array.sub searchQueue 0 (grow 0 (Array.fold enqueue 0 seeds))

/// Water spreading from `starts` over the pixels `canEnter` accepts, lowest
/// first — the way a pool fills — until `budget` pixels.
let private pool (canEnter: int -> bool) (budget: int) (starts: int array) =
    let seen = System.Collections.Generic.HashSet<int>()
    let rows = Array.init MapHeight (fun _ -> System.Collections.Generic.Stack<int>())
    let push i = if canEnter i && seen.Add i then rows[rowOf i].Push i
    starts |> Array.iter push
    // only neighbours get pushed, so the lowest non-empty row is at most one below
    let rec fill row acc count =
        if count >= budget || row < 0 then acc
        elif rows[row].Count = 0 then fill (row - 1) acc count
        else
            let i = rows[row].Pop()
            neighbors i |> Array.iter push
            fill (min (MapHeight - 1) (row + 1)) (i :: acc) (count + 1)
    fill (MapHeight - 1) [] 0 |> List.rev |> Array.ofList

let private lowestFirst (cells: int array) = cells |> Array.sortBy (fun i -> -(rowOf i))

/// The water a hole just carved in the box (x0, y0)-(x1, y1) lets in: the
/// pixels to fill, in order (none when the hole touches no water).
let private planWaterFlow (pixels: byte array) (x0: int) (y0: int) (x1: int) (y1: int) : int array =
    let x0, y0 = max 0 x0, max 0 y0
    let x1, y1 = min (MapWidth - 1) x1, min (MapHeight - 1) y1
    let box = [| for y in y0 .. y1 do for x in x0 .. x1 -> y * MapWidth + x |]
    let inBox i =
        let x, y = i % MapWidth, rowOf i
        x >= x0 && x <= x1 && y >= y0 && y <= y1

    // The water touching the hole: its top row is the surface, its size the budget.
    let body =
        box
        |> Array.filter (isDry pixels)
        |> Array.collect neighbors
        |> Array.filter (isWet pixels)
        |> flood (isWet pixels) MaxWaterBody
    if body.Length = 0 then [||] else

    let top = body |> Array.fold (fun t i -> min t (rowOf i)) MapHeight
    let budget = min body.Length MaxWaterFill
    let underwater i = rowOf i > top && isDry pixels i
    // Hole pixels below the surface that touch the water seed the searches.
    let seeds = box |> Array.filter (fun i -> underwater i && touchesWater pixels i)

    // All the space below the surface that the hole opens into fills, if it fits...
    let space = flood underwater budget seeds
    if space.Length <= budget then lowestFirst space else

    // ...otherwise it leaks: the hole itself fills...
    let hole = flood (fun i -> underwater i && inBox i) MaxWaterFill seeds
    let inHole = System.Collections.Generic.HashSet<int>(hole)
    let isOpen i = i >= 0 && isDry pixels i && not (inHole.Contains i)

    // ...water drops straight down from its bottom, and from its open sides
    // after shooting out up to half the hole's width, so the waterfall is
    // about as wide as the hole...
    let fall (start: int) = start |> Array.unfold (fun c -> if isOpen c then Some(c, below c) else None)
    let spoutLength = max 1 ((x1 - x0 + 1) / 2)
    let spout (dx: int) (h: int) =
        (beside dx h, 0)
        |> Array.unfold (fun (c, n) -> if n < spoutLength && isOpen c then Some(c, (beside dx c, n + 1)) else None)
    // Many starts share a column: fall only from those not already under a
    // higher fall in the same column.
    let columns =
        hole
        |> Array.collect (fun h -> Array.concat [ [| below h |]; spout -1 h; spout 1 h ])
        |> Array.filter (fun i -> i >= 0)
        |> Array.groupBy (fun i -> i % MapWidth)
        |> Array.collect (fun (_, starts) ->
            starts
            |> Array.sortBy rowOf
            |> Array.fold
                (fun (columns, coveredTo) start ->
                    if rowOf start <= coveredTo then
                        columns, coveredTo
                    else
                        match fall start with
                        | [||] -> columns, coveredTo
                        | column -> column :: columns, rowOf (Array.last column))
                ([], -1)
            |> fst
            |> Array.ofList)
    let falls = Array.concat columns

    // ...and pools where it lands, lowest pixels first, while the pond lasts.
    let taken = System.Collections.Generic.HashSet<int>(Array.append hole falls)
    let pooled =
        columns
        |> Array.map Array.last
        |> Array.distinct
        |> Array.collect (fun l -> [| beside -1 l; beside 1 l |])
        |> Array.filter (fun i -> i >= 0)
        |> pool (fun i -> underwater i && not (taken.Contains i)) (budget - hole.Length - falls.Length)

    Array.concat [ lowestFirst hole; falls |> Array.sortBy rowOf; pooled ]

let private queueFlow (cells: int array) =
    if cells.Length > 0 then
        cells |> Array.iter (fun i -> queuedWater[i] <- true)
        waterFlows <- waterFlows @ [ { Cells = cells; Next = 0 } ]

/// Advance the queued water fills by one tick. Returns true if any pixel changed.
let stepWater (pixels: byte array) : bool =
    let advance (f: WaterFlow) =
        let stop = min f.Cells.Length (f.Next + WaterCellsPerTick)
        let batch = f.Cells[f.Next .. stop - 1]
        batch |> Array.iter (fun i -> queuedWater[i] <- false)
        // pixels something else claimed meanwhile stay as they are
        let placed = batch |> Array.filter (fun i -> pixels[i] = VoidColor)
        placed
        |> Array.iter (fun i ->
            pixels[i] <- WaterColor
            markDirty (i % MapWidth) (rowOf i) (i % MapWidth) (rowOf i))
        { f with Next = stop }, placed.Length > 0
    let stepped = waterFlows |> List.map advance
    waterFlows <- stepped |> List.map fst |> List.filter (fun f -> f.Next < f.Cells.Length)
    stepped |> List.exists snd

/// Erase terrain in a circle (paint black / VoidColor) around a point.
/// Skips the pads and the map's edge frame.
/// Returns true if any pixels were actually erased.
let eraseTerrainCircle (pixels: byte array) (cx: float) (cy: float) (radius: float) : bool =
    let ix = int (round cx)
    let iy = int (round cy)
    let r = int (ceil radius)
    let rSq = radius * radius
    let mutable erased = false
    for dy in -r .. r do
        for dx in -r .. r do
            if float (dx * dx + dy * dy) <= rSq then
                let px = ix + dx
                let py = iy + dy
                if px >= 0 && px < MapWidth && py >= 0 && py < MapHeight then
                    let existing = pixels[py * MapWidth + px]
                    if existing <> VoidColor && existing <> WaterColor && not (isBase existing)
                       && not (isFrame px py existing) then
                        pixels[py * MapWidth + px] <- VoidColor
                        erased <- true
    if erased then
        markDirty (ix - r) (iy - r) (ix + r) (iy + r)
        queueFlow (planWaterFlow pixels (ix - r) (iy - r) (ix + r) (iy + r))
    erased
