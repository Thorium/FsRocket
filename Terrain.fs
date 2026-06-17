/// FsRocket - Terrain System
/// RLE decompression and pixel-based terrain from .LEV files
/// Decompression matches LoadAndDecompressBackground at 0000:11AF
/// Spawn scanning matches LoadMapAndFindSpawns at 0000:141F
module FsRocket.Terrain

open System
open System.IO
open FsRocket.Physics

// ─── Constants ─────────────────────────────────────────────────────────

/// Arena is two stacked 320x200 VGA pages = 320x400
let PageSize = MapWidth * 200  // 64000 bytes per VGA page

/// Terrain color constants
/// New model: only totally black is penetrable (flyable).
/// Water is also penetrable but applies friction.
/// Everything else is solid wall.
/// Dark gray pixels are indestructible walls that cannot be erased by ammo.
let WaterColor = 0x27uy          // Water surface — penetrable, friction applies
// Base / landing pad colour range — shaded grey bars (0x5C..0x5F), drawn as small
// horizontal rectangles in the AUTS maps. Ships land here without taking collision
// damage, recharge energy, and may switch weapons. Bases are also indestructible:
// ammo cannot erase them. (Earlier code mislabelled this range as "indestructible
// walls" and used 0x28 for bases — but real AUTS maps colour bases 0x5C..0x5F.)
let BaseColorMin = 0x5Cuy        // Base/landing pad range low
let BaseColorMax = 0x5Fuy        // Base/landing pad range high
let VoidColor = 0x00uy           // Empty/sky — the only freely flyable space

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

/// Decompress a .LEV file into a 320x400 pixel array + 4 viewport words
let decompressLevel (data: byte array) : byte array * (int * int * int * int) =
    // Last 8 bytes are 4 LE uint16 viewport words
    let compressedSize = data.Length - 8

    // Read viewport words from last 8 bytes
    let vp0 = int (BitConverter.ToUInt16(data, compressedSize))
    let vp1 = int (BitConverter.ToUInt16(data, compressedSize + 2))
    let vp2 = int (BitConverter.ToUInt16(data, compressedSize + 4))
    let vp3 = int (BitConverter.ToUInt16(data, compressedSize + 6))

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

let loadLevel (filePath: string) : LevelData =
    let data = File.ReadAllBytes filePath
    let pixels, viewports = decompressLevel data
    let spawns = findSpawnPoints pixels
    let name = Path.GetFileNameWithoutExtension filePath

    { Pixels = pixels
      Viewports = viewports
      SpawnPoints = spawns
      Name = name }

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

/// Set a pixel in the terrain data. Does nothing for out-of-bounds.
let inline setPixel (pixels: byte array) (x: int) (y: int) (value: byte) =
    if x >= 0 && x < MapWidth && y >= 0 && y < MapHeight then
        pixels[y * MapWidth + x] <- value

/// Erase terrain in a circle (paint black / VoidColor) around a point.
/// Skips indestructible pixels (dark gray).
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
                    if existing <> VoidColor && existing <> WaterColor && not (isBase existing) then
                        pixels[py * MapWidth + px] <- VoidColor
                        erased <- true
    erased
