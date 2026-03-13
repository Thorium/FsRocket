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
let IndestructibleMin = 0x5Cuy   // Indestructible wall range low (dark gray)
let IndestructibleMax = 0x5Fuy   // Indestructible wall range high
let VoidColor = 0x00uy           // Empty/sky — the only freely flyable space
// Legacy aliases kept for reference
let WallColorMin = IndestructibleMin
let WallColorMax = IndestructibleMax

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

/// Is this pixel a solid wall? Everything that is NOT black (0x00) and NOT water
/// is treated as solid — ships and most projectiles cannot pass through.
let inline isWall (pixel: byte) =
    pixel <> VoidColor && pixel <> WaterColor

/// Is this pixel water/ground? 
let inline isWater (pixel: byte) =
    pixel = WaterColor

/// Is this pixel void/empty/flyable? (0x00)
let inline isVoid (pixel: byte) =
    pixel = VoidColor

/// Is this pixel passable (flyable)? Black or water.
let inline isPassable (pixel: byte) =
    pixel = VoidColor || pixel = WaterColor

/// Is this pixel an indestructible wall? Dark gray range
/// These cannot be erased by ammo impacts.
let inline isIndestructible (pixel: byte) =
    pixel >= IndestructibleMin && pixel <= IndestructibleMax

// ─── Spawn Point Scanning  ─────
//
// Scan rows 2..398, cols 0..319:
//   If pixel(col, row+1) == 0x27 AND pixel(col, row) == 0x00:
//     Found spawn edge. Measure width rightward while same condition holds.
//     Record {x=col, y=row, width=measured-1}. Skip col past the platform.

let findSpawnPoints (pixels: byte array) : SpawnPoint array =
    let spawns = ResizeArray<SpawnPoint>()
    let mutable row = 2

    while row <= 398 do
        let mutable col = 0

        while col < 320 do
            let below = getPixel pixels col (row + 1)

            if below = WaterColor then
                let above = getPixel pixels col row

                if above = VoidColor then
                    // Found spawn edge — measure width rightward
                    let mutable width = 0

                    let mutable measuring = true
                    while measuring do
                        width <- width + 1
                        let rightAbove = getPixel pixels (col + width) row
                        let rightBelow = getPixel pixels (col + width) (row + 1)
                        if rightAbove <> VoidColor || rightBelow <> WaterColor then
                            measuring <- false

                    spawns.Add { X = col; Y = row; Width = width - 1 }
                    col <- col + width  // Skip past this platform
                else
                    col <- col + 1
            else
                col <- col + 1

        row <- row + 1

    spawns.ToArray()

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

/// Pick a random spawn point from the level's spawn list
let randomSpawn (spawns: SpawnPoint array) (rng: Random) : int * int =
    if spawns.Length = 0 then
        // Fallback: random position in arena
        rng.Next MapWidth, rng.Next MapHeight
    else
        let sp = spawns[rng.Next spawns.Length]
        // Place at center of platform, at the row above the surface
        sp.X + sp.Width / 2, sp.Y

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
                    if existing <> VoidColor && existing <> WaterColor && not (isIndestructible existing) then
                        pixels[py * MapWidth + px] <- VoidColor
                        erased <- true
    erased
