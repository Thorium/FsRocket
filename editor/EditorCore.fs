/// FsRocket Level Editor — pure editing logic (no WinForms / System.Drawing).
/// Everything here works on the flat 320x400 byte pixel array shared with the
/// game (FsRocket.Terrain). Kept free of UI types so it can be unit-tested with
/// `dotnet fsi` on any platform.
module FsRocketEditor.EditorCore

open FsRocket.Physics
open FsRocket.Terrain

// ─── VGA Mode 13h palette (same table the game renderer uses) ──────────────
// 0-15 CGA, 16-231 6x6x6 cube, 232-255 grayscale. Entries are 0xAARRGGBB ints.

let private buildVgaPalette () : int array =
    let pal = Array.zeroCreate<int> 256
    let cga = [|
        (0x00,0x00,0x00);(0x00,0x00,0xAA);(0x00,0xAA,0x00);(0x00,0xAA,0xAA)
        (0xAA,0x00,0x00);(0xAA,0x00,0xAA);(0xAA,0x55,0x00);(0xAA,0xAA,0xAA)
        (0x55,0x55,0x55);(0x55,0x55,0xFF);(0x55,0xFF,0x55);(0x55,0xFF,0xFF)
        (0xFF,0x55,0x55);(0xFF,0x55,0xFF);(0xFF,0xFF,0x55);(0xFF,0xFF,0xFF) |]
    for i in 0..15 do
        let r,g,b = cga[i]
        pal[i] <- (0xFF <<< 24) ||| (r <<< 16) ||| (g <<< 8) ||| b
    for r in 0..5 do
      for g in 0..5 do
        for b in 0..5 do
            let idx = 16 + r*36 + g*6 + b
            pal[idx] <- (0xFF <<< 24) ||| ((r*51) <<< 16) ||| ((g*51) <<< 8) ||| (b*51)
    for i in 0..23 do
        let v = i * 255 / 23
        pal[232 + i] <- (0xFF <<< 24) ||| (v <<< 16) ||| (v <<< 8) ||| v
    pal

let vgaPalette = buildVgaPalette ()

// ─── Terrain materials the brushes paint ───────────────────────────────────
// Only four pixel meanings matter to the game (see Terrain.fs):
//   Void  0x00       — empty sky / flyable space / caves
//   Water 0x27       — water surface (penetrable, applies friction)
//   Base  0x5C..0x5F — landing pad (indestructible; spawns derive from these)
//   Wall  everything else — solid, destructible terrain
// The wall brush paints a single representative rock colour; the game treats
// ALL non-special colours identically as wall, so the exact value is cosmetic.

let VoidMat  : byte = VoidColor        // 0x00
let WaterMat : byte = WaterColor       // 0x27
let BaseMat  : byte = BaseColorMin     // 0x5C
let WallMat  : byte = 0x82uy           // rock-brown (6x6x6 cube r=3,g=1,b=0)

/// Fixed display colour for landing pads, matching the game's renderer so pads
/// are easy to spot while editing.
let BasePadArgb = (0xFF <<< 24) ||| (0x30 <<< 16) ||| (0xC0 <<< 8) ||| 0x60

/// On-screen ARGB for a terrain pixel: bases get the recognizable green bar,
/// everything else renders through the raw VGA palette (void shows as black,
/// water as the palette's blue, walls as their own colour).
let inline displayArgb (pixel: byte) : int =
    if isBase pixel then BasePadArgb else vgaPalette[int pixel]

// ─── Material classification (for class-aware flood fill) ──────────────────

type Material = MVoid | MWater | MBase | MWall

let classify (pixel: byte) : Material =
    if pixel = VoidColor then MVoid
    elif pixel = WaterColor then MWater
    elif isBase pixel then MBase
    else MWall

let materialValue = function
    | MVoid -> VoidMat | MWater -> WaterMat | MBase -> BaseMat | MWall -> WallMat

// ─── Nearest-colour mapping for image import ───────────────────────────────

let inline private rOf argb = (argb >>> 16) &&& 0xFF
let inline private gOf argb = (argb >>> 8) &&& 0xFF
let inline private bOf argb = argb &&& 0xFF

let private isSpecialIndex (i: int) =
    i = int VoidColor || i = int WaterColor || (i >= int BaseColorMin && i <= int BaseColorMax)

/// Candidate wall palette indices for import: every palette entry EXCEPT the
/// special-meaning ones and pure black. Excluding pure black guarantees imported
/// walls never look identical to carved-out void caves, so the two are always
/// visually distinguishable.
let private wallCandidates : (int * int * int * int) array =
    [| for i in 0..255 do
         let c = vgaPalette[i]
         let r, g, b = rOf c, gOf c, bOf c
         if not (isSpecialIndex i) && (r + g + b) > 0 then
             yield (i, r, g, b) |]

/// Map an RGB colour to the nearest solid-wall palette index.
let nearestWall (r: int) (g: int) (b: int) : byte =
    let mutable best = 0xF6 // light grey fallback
    let mutable bestD = System.Int32.MaxValue
    for (i, cr, cg, cb) in wallCandidates do
        let dr = r - cr
        let dg = g - cg
        let db = b - cb
        let d = dr*dr + dg*dg + db*db
        if d < bestD then
            bestD <- d
            best <- i
    byte best

let inline private luma r g b = (r * 30 + g * 59 + b * 11) / 100

let inline private dist2 (r1, g1, b1) (r2, g2, b2) =
    let dr = r1 - r2
    let dg = g1 - g2
    let db = b1 - b2
    dr*dr + dg*dg + db*db

// "Key" RGB colours used by the colour-preserving import mode. They match what
// the editor draws on screen, so painting a level with exactly these colours in
// any image editor and importing it yields a directly playable map.
let KeyVoidRgb  = (0x00, 0x00, 0x00)   // black  #000000  → empty / caves
let KeyWaterRgb = (0x00, 0x99, 0xFF)   // blue   #0099FF  → water  (palette 0x27 on screen)
let KeyBaseRgb  = (0x30, 0xC0, 0x60)   // green  #30C060  → landing pad / base

/// Default per-channel tolerance for key-colour matching. Strict by default so an
/// imported, hand-painted level only snaps colours that are genuinely meant to be
/// void/water/base; raise it to absorb JPEG noise, lower it for exact matches only.
let DefaultKeyTolerance = 24

/// Colour-preserving classification: snap a pixel to Void/Water/Base when it is
/// within ~`perChannelTol` (per RGB channel) of a key colour, otherwise keep its
/// appearance by mapping to the nearest solid wall colour.
let classifyByColor (perChannelTol: int) (r: int) (g: int) (b: int) : byte =
    let tol = 3 * perChannelTol * perChannelTol   // squared-distance budget
    let dv = dist2 (r, g, b) KeyVoidRgb
    let dw = dist2 (r, g, b) KeyWaterRgb
    let db = dist2 (r, g, b) KeyBaseRgb
    let m = min dv (min dw db)
    if m <= tol then
        if m = dv then VoidMat
        elif m = dw then WaterMat
        else BaseMat
    else nearestWall r g b

// ─── Image import ──────────────────────────────────────────────────────────

type FitMode =
    | Stretch   // distort to exactly fill 320x400
    | Fit       // preserve aspect ratio, letterbox the remainder with void
    | Center    // native pixel size, centred; crop overflow, void-pad underflow

type ColorMode =
    /// Everything becomes solid wall (reserved colours are avoided). Best for
    /// importing a texture you will then carve by hand. `VoidThreshold` can carve
    /// dark pixels into caves.
    | SolidWalls
    /// Keep the image's colours: pixels you have already drawn as the key colours
    /// (black=void, #0099FF=water, #30C060=base) become those functional materials;
    /// everything else maps to the nearest solid wall colour. Best for importing a
    /// level you painted in an image editor. `VoidThreshold` is ignored.
    | PreserveColors

type ImportOptions =
    { Fit: FitMode
      Colors: ColorMode
      /// (SolidWalls only) Source pixels with luma <= this become Void (flyable
      /// caves). Negative disables it (the recommended default — carve caves by
      /// hand afterward with the Void brush).
      VoidThreshold: int
      /// (PreserveColors only) Per-channel tolerance for snapping a pixel to a key
      /// colour (void/water/base). See DefaultKeyTolerance.
      KeyTolerance: int }

let defaultImport =
    { Fit = Stretch; Colors = SolidWalls; VoidThreshold = -1; KeyTolerance = DefaultKeyTolerance }

/// Build a 320x400 pixel array from a source image. `sample sx sy` returns the
/// source pixel ARGB; out-of-range coordinates must never be passed (the mapping
/// below clamps to the source bounds and void-fills letterbox regions).
let importImage (srcW: int) (srcH: int) (sample: int -> int -> int) (opt: ImportOptions) : byte array =
    let px = Array.create (MapWidth * MapHeight) VoidMat
    if srcW <= 0 || srcH <= 0 then px else

    // For each destination pixel decide the source coordinate (or None = void).
    let srcCoord (dx: int) (dy: int) : (int * int) option =
        match opt.Fit with
        | Stretch ->
            Some (min (srcW - 1) (dx * srcW / MapWidth),
                  min (srcH - 1) (dy * srcH / MapHeight))
        | Fit ->
            // Scale to fit inside 320x400 preserving aspect; centre; letterbox.
            let scale = min (float MapWidth / float srcW) (float MapHeight / float srcH)
            let dw = int (float srcW * scale)
            let dh = int (float srcH * scale)
            let ox = (MapWidth - dw) / 2
            let oy = (MapHeight - dh) / 2
            if dx < ox || dx >= ox + dw || dy < oy || dy >= oy + dh then None
            else Some (min (srcW - 1) ((dx - ox) * srcW / dw),
                       min (srcH - 1) ((dy - oy) * srcH / dh))
        | Center ->
            let ox = (MapWidth - srcW) / 2
            let oy = (MapHeight - srcH) / 2
            let sx = dx - ox
            let sy = dy - oy
            if sx < 0 || sx >= srcW || sy < 0 || sy >= srcH then None
            else Some (sx, sy)

    for dy in 0 .. MapHeight - 1 do
        for dx in 0 .. MapWidth - 1 do
            match srcCoord dx dy with
            | None -> ()  // already VoidMat
            | Some (sx, sy) ->
                let c = sample sx sy
                let r, g, b = rOf c, gOf c, bOf c
                px[dy * MapWidth + dx] <-
                    match opt.Colors with
                    | PreserveColors -> classifyByColor opt.KeyTolerance r g b
                    | SolidWalls ->
                        if opt.VoidThreshold >= 0 && luma r g b <= opt.VoidThreshold then VoidMat
                        else nearestWall r g b
    px

// ─── Drawing primitives (all mutate the pixel array in place) ──────────────

let inline private inBounds x y = x >= 0 && x < MapWidth && y >= 0 && y < MapHeight

/// Stamp a filled square brush of the given radius (size = 2*radius+1) centred
/// at (cx, cy). Radius 0 paints a single pixel.
let stampBrush (px: byte array) (cx: int) (cy: int) (radius: int) (value: byte) =
    for dy in -radius .. radius do
        for dx in -radius .. radius do
            let x = cx + dx
            let y = cy + dy
            if inBounds x y then px[y * MapWidth + x] <- value

/// Bresenham line of brush stamps from (x0,y0) to (x1,y1).
let drawLine (px: byte array) (x0: int) (y0: int) (x1: int) (y1: int) (radius: int) (value: byte) =
    let dx = abs (x1 - x0)
    let dy = -abs (y1 - y0)
    let sx = if x0 < x1 then 1 else -1
    let sy = if y0 < y1 then 1 else -1
    let mutable err = dx + dy
    let mutable x = x0
    let mutable y = y0
    let mutable go = true
    while go do
        stampBrush px x y radius value
        if x = x1 && y = y1 then go <- false
        else
            let e2 = 2 * err
            if e2 >= dy then err <- err + dy; x <- x + sx
            if e2 <= dx then err <- err + dx; y <- y + sy

/// Filled rectangle spanning the two corner points (inclusive).
let drawRect (px: byte array) (x0: int) (y0: int) (x1: int) (y1: int) (value: byte) =
    let lx, hx = min x0 x1, max x0 x1
    let ly, hy = min y0 y1, max y0 y1
    for y in max 0 ly .. min (MapHeight - 1) hy do
        for x in max 0 lx .. min (MapWidth - 1) hx do
            px[y * MapWidth + x] <- value

/// Flood fill by MATERIAL CLASS (void/water/base/wall) starting at (x,y),
/// replacing the connected same-class region with `value`. Filling by class —
/// rather than exact byte — means a wall region of mixed shades fills as one.
let floodFill (px: byte array) (sx: int) (sy: int) (value: byte) =
    if inBounds sx sy then
        let targetClass = classify px[sy * MapWidth + sx]
        // No-op if the start pixel is already the requested value.
        if px[sy * MapWidth + sx] <> value then
            let stack = System.Collections.Generic.Stack<struct (int * int)>()
            stack.Push (struct (sx, sy))
            while stack.Count > 0 do
                let struct (x, y) = stack.Pop()
                if inBounds x y then
                    let i = y * MapWidth + x
                    if px[i] <> value && classify px[i] = targetClass then
                        px[i] <- value
                        stack.Push (struct (x + 1, y))
                        stack.Push (struct (x - 1, y))
                        stack.Push (struct (x, y + 1))
                        stack.Push (struct (x, y - 1))
