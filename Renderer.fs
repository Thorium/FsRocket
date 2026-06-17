/// FsRocket Renderer — HTML5 Canvas 2D (Fable)
/// Split-screen rendering of the 320x400 arena, ported from the GDI+ renderer.
/// Each player gets a viewport; terrain is drawn from a cached offscreen canvas.
module FsRocket.Renderer

open System
open Fable.Core
open FsRocket.Physics
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types
open FsRocket.Entities

// ─── Scale factor (3x 320x200) ──────────────────────────────────────────
let Scale = 3
/// Extra zoom factor for terrain — makes features appear bigger
let TerrainZoom = 1.25
let private scaleF = float Scale
let private effScale = scaleF * TerrainZoom

// ─── Canvas 2D interop (raw JS via Emit — front-end is Fable-only) ───────
// `ctx` and offscreen canvases are passed around as obj.

[<Emit("document.createElement('canvas')")>]
let private newCanvas () : obj = jsNative
[<Emit("$0.getContext('2d')")>]
let private get2d (canvas: obj) : obj = jsNative
[<Emit("$0.width = $1")>]
let private setCanvasW (canvas: obj) (w: int) : unit = jsNative
[<Emit("$0.height = $1")>]
let private setCanvasH (canvas: obj) (h: int) : unit = jsNative
[<Emit("$0.createImageData($1, $2)")>]
let private createImageData (ctx: obj) (w: int) (h: int) : obj = jsNative
[<Emit("$0.data")>]
let private imgBytes (img: obj) : byte[] = jsNative
[<Emit("$0.putImageData($1, 0, 0)")>]
let private putImageData (ctx: obj) (img: obj) : unit = jsNative

[<Emit("$0.fillStyle = $1")>]
let private setFill (ctx: obj) (c: string) : unit = jsNative
[<Emit("$0.strokeStyle = $1")>]
let private setStroke (ctx: obj) (c: string) : unit = jsNative
[<Emit("$0.lineWidth = $1")>]
let private setLineWidth (ctx: obj) (w: float) : unit = jsNative
[<Emit("$0.font = $1")>]
let private setFontJs (ctx: obj) (f: string) : unit = jsNative
[<Emit("$0.textBaseline = $1")>]
let private setBaseline (ctx: obj) (b: string) : unit = jsNative
[<Emit("$0.imageSmoothingEnabled = $1")>]
let private setSmoothing (ctx: obj) (b: bool) : unit = jsNative

[<Emit("$0.fillRect($1, $2, $3, $4)")>]
let private fillRectJs (ctx: obj) (x: float) (y: float) (w: float) (h: float) : unit = jsNative
[<Emit("$0.strokeRect($1, $2, $3, $4)")>]
let private strokeRectJs (ctx: obj) (x: float) (y: float) (w: float) (h: float) : unit = jsNative
[<Emit("$0.fillText($1, $2, $3)")>]
let private fillTextJs (ctx: obj) (s: string) (x: float) (y: float) : unit = jsNative
[<Emit("$0.beginPath()")>]
let private beginPath (ctx: obj) : unit = jsNative
[<Emit("$0.moveTo($1, $2)")>]
let private moveTo (ctx: obj) (x: float) (y: float) : unit = jsNative
[<Emit("$0.lineTo($1, $2)")>]
let private lineTo (ctx: obj) (x: float) (y: float) : unit = jsNative
[<Emit("$0.closePath()")>]
let private closePath (ctx: obj) : unit = jsNative
[<Emit("$0.stroke()")>]
let private strokePath (ctx: obj) : unit = jsNative
[<Emit("$0.fill()")>]
let private fillPath (ctx: obj) : unit = jsNative
[<Emit("$0.arc($1, $2, $3, 0, 6.283185307179586)")>]
let private arcFull (ctx: obj) (x: float) (y: float) (r: float) : unit = jsNative
[<Emit("$0.save()")>]
let private save (ctx: obj) : unit = jsNative
[<Emit("$0.restore()")>]
let private restore (ctx: obj) : unit = jsNative
[<Emit("$0.rect($1, $2, $3, $4)")>]
let private rectPath (ctx: obj) (x: float) (y: float) (w: float) (h: float) : unit = jsNative
[<Emit("$0.clip()")>]
let private clipJs (ctx: obj) : unit = jsNative
[<Emit("$0.drawImage($1, $2, $3, $4, $5, $6, $7, $8, $9)")>]
let private drawImage9 (ctx: obj) (img: obj) (sx: float) (sy: float) (sw: float) (sh: float) (dx: float) (dy: float) (dw: float) (dh: float) : unit = jsNative

// ─── Colour helpers ──────────────────────────────────────────────────────
let private cssRGB (r: int) (g: int) (b: int) = $"rgb({r},{g},{b})"
let private cssRGBA (r: int) (g: int) (b: int) (a: int) = $"rgba({r},{g},{b},{float a / 255.0})"

let playerRGB = [| (0x5F, 0x5F, 0xFF); (0x3F, 0xCF, 0x3F); (0xFF, 0x3F, 0x3F); (0xFF, 0xFF, 0x3F) |]
let playerDarkRGB = [| (0x2F, 0x2F, 0x9F); (0x1F, 0x7F, 0x1F); (0x9F, 0x1F, 0x1F); (0x9F, 0x9F, 0x1F) |]
let playerColors = playerRGB |> Array.map (fun (r, g, b) -> cssRGB r g b)
let playerDarkColors = playerDarkRGB |> Array.map (fun (r, g, b) -> cssRGB r g b)
let private playerRGBA (i: int) (a: int) = let (r, g, b) = playerRGB[i % 4] in cssRGBA r g b a

let private bgColor = "rgb(0,0,0)"
let private gridColor = "rgb(24,24,36)"
let private bulletColor = "rgb(255,255,128)"
let private laserColor = "rgb(255,32,32)"
let private shieldColor = "rgb(128,192,255)"
let private empColor = "rgb(192,64,255)"
let private hudBgColor = "rgb(8,8,16)"
let private healthBgColor = "rgb(48,0,0)"
let private thrustColor = "rgb(255,160,32)"
/// Landing pad / base — distinct green bar
let private basePadArgb = (0xFF <<< 24) ||| (0x30 <<< 16) ||| (0xC0 <<< 8) ||| 0x60

let private hudFont = "bold 11px Consolas, monospace"
let private bigFont = "bold 20px Consolas, monospace"
let private titleFont = "bold 26px Consolas, monospace"
let private subFont = "13px Consolas, monospace"
let private keyFont = "12px Consolas, monospace"

// ─── Mid-level draw helpers ──────────────────────────────────────────────
let private fillCircle ctx cx cy r color =
    setFill ctx color
    beginPath ctx
    arcFull ctx cx cy (max 0.0 r)
    fillPath ctx

let private strokeCircle ctx cx cy r lw color =
    setStroke ctx color
    setLineWidth ctx lw
    beginPath ctx
    arcFull ctx cx cy (max 0.0 r)
    strokePath ctx

let private lineSeg ctx x1 y1 x2 y2 lw color =
    setStroke ctx color
    setLineWidth ctx lw
    beginPath ctx
    moveTo ctx x1 y1
    lineTo ctx x2 y2
    strokePath ctx

let private fillRectC ctx x y w h color =
    setFill ctx color
    fillRectJs ctx x y w h

let private polyPath ctx (pts: (float * float) list) =
    beginPath ctx
    match pts with
    | (x0, y0) :: rest ->
        moveTo ctx x0 y0
        for (x, y) in rest do lineTo ctx x y
        closePath ctx
    | [] -> ()

let private fillPoly ctx pts color =
    setFill ctx color
    polyPath ctx pts
    fillPath ctx

let private strokePoly ctx pts lw color =
    setStroke ctx color
    setLineWidth ctx lw
    polyPath ctx pts
    strokePath ctx

let private drawText ctx (s: string) x y font color =
    setFontJs ctx font
    setFill ctx color
    fillTextJs ctx s x y

let private clipRect ctx x y w h =
    save ctx
    beginPath ctx
    rectPath ctx x y w h
    clipJs ctx

// ─── Standard VGA Mode 13h palette (256 colours) → ARGB ints ─────────────
let private buildVgaPalette () : int array =
    let pal = Array.zeroCreate<int> 256
    let cga =
        [| (0x00, 0x00, 0x00); (0x00, 0x00, 0xAA); (0x00, 0xAA, 0x00); (0x00, 0xAA, 0xAA)
           (0xAA, 0x00, 0x00); (0xAA, 0x00, 0xAA); (0xAA, 0x55, 0x00); (0xAA, 0xAA, 0xAA)
           (0x55, 0x55, 0x55); (0x55, 0x55, 0xFF); (0x55, 0xFF, 0x55); (0x55, 0xFF, 0xFF)
           (0xFF, 0x55, 0x55); (0xFF, 0x55, 0xFF); (0xFF, 0xFF, 0x55); (0xFF, 0xFF, 0xFF) |]
    for i in 0..15 do
        let r, g, b = cga[i]
        pal[i] <- (0xFF <<< 24) ||| (r <<< 16) ||| (g <<< 8) ||| b
    for r in 0..5 do
        for g in 0..5 do
            for b in 0..5 do
                let idx = 16 + r * 36 + g * 6 + b
                pal[idx] <- (0xFF <<< 24) ||| (r * 51 <<< 16) ||| (g * 51 <<< 8) ||| (b * 51)
    for i in 0..23 do
        let v = i * 255 / 23
        pal[232 + i] <- (0xFF <<< 24) ||| (v <<< 16) ||| (v <<< 8) ||| v
    pal

let vgaPalette = buildVgaPalette ()

// ─── Terrain offscreen-canvas cache ──────────────────────────────────────
let mutable private terrainCanvas: obj option = None
let mutable private terrainCanvasLevel: string = ""

let private buildTerrainCanvas (level: LevelData) : obj =
    let canvas = newCanvas ()
    setCanvasW canvas MapWidth
    setCanvasH canvas MapHeight
    let tctx = get2d canvas
    let img = createImageData tctx MapWidth MapHeight
    let data = imgBytes img
    for i in 0 .. MapWidth * MapHeight - 1 do
        let pixel = level.Pixels[i]
        // Bases (landing pads) get a fixed, recognizable colour so players can
        // spot where to land, heal and swap weapons.
        let argb = if isBase pixel then basePadArgb else vgaPalette[int pixel]
        let j = i * 4
        data[j] <- byte ((argb >>> 16) &&& 0xFF)
        data[j + 1] <- byte ((argb >>> 8) &&& 0xFF)
        data[j + 2] <- byte (argb &&& 0xFF)
        data[j + 3] <- 255uy
    putImageData tctx img
    canvas

let private getTerrainCanvas (level: LevelData) (terrainDirty: bool) : obj =
    match terrainCanvas with
    | Some c when terrainCanvasLevel = level.Name && not terrainDirty -> c
    | _ ->
        let c = buildTerrainCanvas level
        terrainCanvas <- Some c
        terrainCanvasLevel <- level.Name
        c

// ─── Layout: viewports for 1-4 players ───────────────────────────────────
let viewportLayout (numPlayers: int) (windowW: int) (windowH: int) =
    match numPlayers with
    | 1 -> [| (0, 0, windowW, windowH) |]
    | 2 -> [| (0, 0, windowW / 2, windowH); (windowW / 2, 0, windowW / 2, windowH) |]
    | 3 | 4 ->
        [| (0, 0, windowW / 2, windowH / 2)
           (windowW / 2, 0, windowW / 2, windowH / 2)
           (0, windowH / 2, windowW / 2, windowH / 2)
           (windowW / 2, windowH / 2, windowW / 2, windowH / 2) |]
    | _ -> [||]

// ─── Draw a single player's viewport ─────────────────────────────────────
let drawPlayerView (ctx: obj) (gs: GameState) (playerIdx: int) (vx: int) (vy: int) (vw: int) (vh: int) =
    let hudH = 36
    let gameH = vh - hudH
    let p = gs.Players[playerIdx]
    let fvx, fvy, fvw, fgameH = float vx, float vy, float vw, float gameH

    let camX = p.PosX / PositionScale - float vw / (2.0 * effScale)
    let camY = p.PosY / PositionScale - float gameH / (2.0 * effScale)
    let toX (wx: float) = fvx + (wx - camX) * effScale
    let toY (wy: float) = fvy + (wy - camY) * effScale

    // ── Game area (clipped) ──
    clipRect ctx fvx fvy fvw fgameH
    fillRectC ctx fvx fvy fvw fgameH bgColor

    match gs.Level with
    | Some level ->
        let tcanvas = getTerrainCanvas level gs.TerrainDirty
        let srcX = max 0 (int camX)
        let srcY = max 0 (int camY)
        let srcW = min (MapWidth - srcX) (int (float vw / effScale) + 1)
        let srcH = min (MapHeight - srcY) (int (float gameH / effScale) + 1)
        if srcW > 0 && srcH > 0 then
            let destX = toX (float srcX)
            let destY = toY (float srcY)
            drawImage9 ctx tcanvas (float srcX) (float srcY) (float srcW) (float srcH)
                destX destY (float srcW * effScale) (float srcH * effScale)
    | None ->
        // No terrain: grid + arena walls
        let startGX = int (floor (camX / 32.0)) * 32
        let startGY = int (floor (camY / 32.0)) * 32
        for gx in startGX .. 32 .. startGX + int (float vw / effScale) + 32 do
            let sx = toX (float gx)
            if sx >= fvx && sx <= fvx + fvw then lineSeg ctx sx fvy sx (fvy + fgameH) 1.0 gridColor
        for gy in startGY .. 32 .. startGY + int (float gameH / effScale) + 32 do
            let sy = toY (float gy)
            if sy >= fvy && sy <= fvy + fgameH then lineSeg ctx fvx sy (fvx + fvw) sy 1.0 gridColor
        strokePoly ctx
            [ (toX 0.0, toY 0.0); (toX ArenaWidth, toY 0.0)
              (toX ArenaWidth, toY ArenaHeight); (toX 0.0, toY ArenaHeight) ]
            (2.0 * scaleF) "rgb(64,64,80)"
        for w in arenaWalls do
            let swx, swy = toX w.X, toY w.Y
            let sww, swh = w.W * effScale, w.H * effScale
            fillRectC ctx swx swy sww swh "rgb(80,80,104)"

    let margin = 40.0 * scaleF
    let inView (ex: float) (ey: float) =
        ex > fvx - margin && ex < fvx + fvw + margin && ey > fvy - margin && ey < fvy + fgameH + margin

    // ── Entities ──
    for ent in gs.Entities do
        let ex, ey = toX ent.X, toY ent.Y
        if inView ex ey then
            match ent.EType with
            | EntityType.Expanding ->
                let ringR = ent.Radius * scaleF
                if ringR > 0.0 then
                    let alpha = max 40 (255 - int (ent.Radius * 2.0))
                    strokeCircle ctx ex ey ringR (max 1.0 (3.0 * scaleF - ent.Radius / 20.0)) (playerRGBA ent.Owner alpha)
                    let innerR = ringR - scaleF * 2.0
                    if innerR > 0.0 then strokeCircle ctx ex ey innerR 1.0 (cssRGBA 255 255 255 (alpha / 2))
            | EntityType.Nuke ->
                let nukeR = max 2.0 (ent.Radius * scaleF)
                let glowA = max 20 (180 - int (ent.Radius * 2.0))
                fillCircle ctx ex ey nukeR (cssRGBA 255 255 128 glowA)
                fillCircle ctx ex ey (max 1.0 (nukeR / 3.0)) (cssRGBA 255 255 255 (min 255 (glowA + 60)))
                strokeCircle ctx ex ey nukeR scaleF (cssRGBA 255 160 0 glowA)
            | EntityType.Blackhole ->
                let bhR = max 3.0 (ent.Radius * scaleF)
                fillCircle ctx ex ey bhR (cssRGBA 5 0 16 224)
                let swirl = float ent.Timer * 6.0
                for arm in 0..3 do
                    let a = degToRad (swirl + float arm * 90.0)
                    let x1, y1 = ex + cos a * bhR * 0.4, ey + sin a * bhR * 0.4
                    let x2, y2 = ex + cos (a + 0.4) * bhR * 1.2, ey + sin (a + 0.4) * bhR * 1.2
                    let x3, y3 = ex + cos (a + 0.8) * bhR * 2.0, ey + sin (a + 0.8) * bhR * 2.0
                    lineSeg ctx x1 y1 x2 y2 scaleF (cssRGBA 64 0 192 128)
                    lineSeg ctx x2 y2 x3 y3 scaleF (cssRGBA 64 0 192 128)
                let pullR = blackholeRadius * scaleF
                let pullA = 20 + int (10.0 * sin (float ent.Timer * 0.15))
                strokeCircle ctx ex ey pullR 1.0 (cssRGBA 128 64 255 pullA)
            | EntityType.Mine ->
                let armed = ent.Timer > 0
                let pulse = if armed then 0.5 + 0.5 * sin (float ent.Timer * 0.2) else 0.3
                let sz = float (4 * Scale) * (0.8 + 0.2 * pulse)
                let alpha = int (128.0 + 127.0 * pulse)
                let c = if armed then cssRGBA 255 64 16 alpha else cssRGBA 128 64 16 128
                fillCircle ctx ex ey (sz / 2.0) c
                if armed then
                    lineSeg ctx (ex - sz / 2.0) ey (ex + sz / 2.0) ey 1.0 (cssRGBA 255 255 64 alpha)
                    lineSeg ctx ex (ey - sz / 2.0) ex (ey + sz / 2.0) 1.0 (cssRGBA 255 255 64 alpha)
            | EntityType.Flame ->
                let fTimer = max 1 (abs ent.Timer)
                let fSize = max 2.0 (float fTimer * scaleF / 3.0)
                let fAlpha = max 40 (220 - fTimer * 3)
                let r = min 255 (0xFF - fTimer)
                let gv = max 0 (0x80 - fTimer * 2)
                fillCircle ctx ex ey (fSize / 2.0) (cssRGBA r gv 16 fAlpha)
                fillCircle ctx ex ey (max 0.5 (fSize / 6.0)) (cssRGBA 255 192 64 (min 255 (fAlpha + 30)))
            | EntityType.Heavy ->
                let sz = float (4 * Scale)
                let c =
                    if ent.WeaponIdx = WeaponType.Missile then cssRGB 255 192 48
                    elif ent.WeaponIdx = WeaponType.AtomWeapon then cssRGB 64 255 64
                    else cssRGB 255 165 0
                let speed = sqrt (ent.VelX * ent.VelX + ent.VelY * ent.VelY) + 0.01
                let dx, dy = ent.VelX / speed, ent.VelY / speed
                let noseX, noseY = ex + dx * sz, ey + dy * sz
                let tailX, tailY = ex - dx * sz * 0.6, ey - dy * sz * 0.6
                let sideX, sideY = -dy * sz * 0.4, dx * sz * 0.4
                fillPoly ctx [ (noseX, noseY); (ex + sideX, ey + sideY); (tailX, tailY); (ex - sideX, ey - sideY) ] c
                fillCircle ctx (ex - dx * sz * 0.8) (ey - dy * sz * 0.8) scaleF (cssRGBA 255 96 16 128)
            | EntityType.EMP ->
                let empR = max 2.0 (ent.Radius * scaleF)
                let empA = 140 + int (80.0 * sin (float ent.Timer * 0.3))
                fillCircle ctx ex ey empR (cssRGBA 192 64 255 empA)
                let sparkLen = empR + scaleF
                let sr = degToRad (float ent.Timer * 8.0)
                lineSeg ctx (ex + cos sr * sparkLen) (ey + sin sr * sparkLen) (ex - cos sr * sparkLen) (ey - sin sr * sparkLen) 1.0 (cssRGBA 255 255 255 empA)
                let sr2 = sr + Math.PI / 2.0
                lineSeg ctx (ex + cos sr2 * sparkLen) (ey + sin sr2 * sparkLen) (ex - cos sr2 * sparkLen) (ey - sin sr2 * sparkLen) 1.0 (cssRGBA 255 255 255 empA)
            | EntityType.Laser ->
                let speed = sqrt (ent.VelX * ent.VelX + ent.VelY * ent.VelY) + 0.01
                let dx, dy = ent.VelX / speed, ent.VelY / speed
                let laserLen = float (6 * Scale)
                lineSeg ctx ex ey (ex - dx * laserLen) (ey - dy * laserLen) (scaleF + 1.0) laserColor
                fillCircle ctx ex ey scaleF (cssRGB 255 128 128)
            | EntityType.Ricochet ->
                let bounce = ent.SubType
                let rAlpha = max 100 (255 - bounce * 50)
                fillCircle ctx ex ey scaleF (cssRGBA 0 255 255 rAlpha)
                fillCircle ctx (ex - ent.VelX * scaleF) (ey - ent.VelY * scaleF) (scaleF / 2.0) (cssRGBA 0 255 255 (rAlpha / 3))
            | EntityType.Exploding ->
                fillCircle ctx ex ey (1.5 * scaleF) (cssRGB 139 96 32)
                fillCircle ctx (ex - ent.VelX * scaleF * 0.5) (ey - ent.VelY * scaleF * 0.5) (scaleF / 2.0) (cssRGBA 96 64 16 128)
            | EntityType.Shrapnel ->
                fillRectC ctx (ex - scaleF / 2.0) (ey - scaleF / 2.0) scaleF scaleF (cssRGBA 128 128 128 (max 60 (200 - ent.Timer * 5)))
            | EntityType.Shield ->
                let sAngle = float ent.Timer * 5.0
                let sAlpha = 160 + int (60.0 * sin (degToRad sAngle))
                fillCircle ctx ex ey (2.0 * scaleF) (cssRGBA 128 192 255 sAlpha)
            | _ ->
                fillCircle ctx ex ey scaleF bulletColor

    // ── Particles ──
    for part in gs.Particles do
        let px, py = toX part.X, toY part.Y
        if px > fvx - 5.0 && px < fvx + fvw + 5.0 && py > fvy - 5.0 && py < fvy + fgameH + 5.0 then
            let alpha = min 255 (part.Life * 8)
            let (r, g, b) = playerRGB[part.Color % 4]
            let sz = max 1.0 (scaleF * float part.Life / 10.0)
            fillRectC ctx (px - sz / 2.0) (py - sz / 2.0) sz sz (cssRGBA r g b alpha)

    // ── Players (ships) ──
    for i in 0 .. gs.NumPlayers - 1 do
        let other = gs.Players[i]
        if other.Alive then
            let ox, oy = toX (other.PosX / PositionScale), toY (other.PosY / PositionScale)
            if ox > fvx - 20.0 && ox < fvx + fvw + 20.0 && oy > fvy - 20.0 && oy < fvy + fgameH + 20.0 then
                let rad = degToRad (other.Angle + 90.0)
                let shipSize = float (5 * Scale)
                let nosX, nosY = ox + cos rad * shipSize, oy - sin rad * shipSize
                let r1 = rad + Math.PI * 0.75
                let r2 = rad - Math.PI * 0.75
                let pts =
                    [ (nosX, nosY)
                      (ox + cos r1 * shipSize * 0.7, oy - sin r1 * shipSize * 0.7)
                      (ox + cos r2 * shipSize * 0.7, oy - sin r2 * shipSize * 0.7) ]
                fillPoly ctx pts playerColors[i % 4]
                strokePoly ctx pts 1.0 playerDarkColors[i % 4]

                if other.KeyUp then
                    let tRad = rad + Math.PI
                    let flLen = float (3 * Scale) + float (gs.GameTick % 3) * scaleF
                    fillCircle ctx (ox + cos tRad * flLen) (oy - sin tRad * flLen) scaleF thrustColor

                if other.Flags.HasFlag(PlayerFlags.Shield) then
                    strokeCircle ctx ox oy (float (7 * Scale)) scaleF shieldColor

                if other.Flags.HasFlag(PlayerFlags.Stunned) then
                    for s in 0..2 do
                        let sRad = degToRad (other.AnimAngle + float s * 120.0)
                        fillCircle ctx (ox + cos sRad * float (6 * Scale)) (oy - sin sRad * float (6 * Scale)) (scaleF / 2.0) empColor

                if other.InvTimer > 0 && other.InvTimer % 4 < 2 then
                    strokeCircle ctx ox oy (float (6 * Scale)) scaleF "rgb(255,255,255)"

    // ── Minimap (top-right of viewport) ──
    let mmW = float (min 80 (vw / 5))
    let mmH = mmW * ArenaHeight / ArenaWidth
    let mmX = fvx + fvw - mmW - 4.0
    let mmY = fvy + 4.0
    fillRectC ctx mmX mmY mmW mmH (cssRGBA 8 8 16 160)
    match gs.Level with
    | Some level ->
        let tcanvas = getTerrainCanvas level gs.TerrainDirty
        drawImage9 ctx tcanvas 0.0 0.0 (float MapWidth) (float MapHeight) mmX mmY mmW mmH
    | None ->
        for w in arenaWalls do
            fillRectC ctx (mmX + w.X * mmW / ArenaWidth) (mmY + w.Y * mmH / ArenaHeight)
                (max 1.0 (w.W * mmW / ArenaWidth)) (max 1.0 (w.H * mmH / ArenaHeight)) (cssRGBA 96 96 128 128)
    setStroke ctx (cssRGBA 64 64 96 128)
    setLineWidth ctx 1.0
    strokeRectJs ctx mmX mmY mmW mmH
    let mmScaleX = mmW / ArenaWidth
    let mmScaleY = mmH / ArenaHeight
    for ent in gs.Entities do
        let mex, mey = mmX + ent.X * mmScaleX, mmY + ent.Y * mmScaleY
        if mex >= mmX && mex <= mmX + mmW && mey >= mmY && mey <= mmY + mmH then
            let mc =
                match ent.EType with
                | EntityType.Nuke | EntityType.Expanding -> "rgb(255,255,255)"
                | EntityType.Blackhole -> "rgb(160,32,240)"
                | EntityType.Mine -> "rgb(255,128,32)"
                | EntityType.Flame -> "rgb(255,96,16)"
                | _ -> "rgb(160,160,160)"
            fillRectC ctx mex mey 1.0 1.0 mc
    for i in 0 .. gs.NumPlayers - 1 do
        let other = gs.Players[i]
        if other.Alive then
            fillRectC ctx (mmX + other.PosX / PositionScale * mmScaleX - 1.0) (mmY + other.PosY / PositionScale * mmScaleY - 1.0) 3.0 3.0 playerColors[i % 4]
    // Camera rect
    setStroke ctx (playerRGBA playerIdx 96)
    setLineWidth ctx 1.0
    strokeRectJs ctx (mmX + camX * mmScaleX) (mmY + camY * mmScaleY) (float vw / effScale * mmScaleX) (float gameH / effScale * mmScaleY)

    // ── DEAD overlay (within game clip so it covers the play area) ──
    if not p.Alive then
        fillRectC ctx fvx fvy fvw fgameH (cssRGBA 0 0 0 160)
        drawText ctx "DEAD" (fvx + fvw / 2.0 - 30.0) (fvy + fgameH / 2.0 - 10.0) bigFont "rgb(255,0,0)"

    restore ctx  // end game-area clip

    // ── HUD bar (separate clip) ──
    clipRect ctx fvx (fvy + fgameH) fvw (float hudH)
    fillRectC ctx fvx (fvy + fgameH) fvw (float hudH) hudBgColor
    let hudY = fvy + fgameH + 2.0
    let specialName = (getWeapon p.SpecialWeapon).Name
    let name = if p.IsCpu then $"CPU{playerIdx + 1}" else $"P{playerIdx + 1}"
    drawText ctx name (fvx + 4.0) hudY playerColors[playerIdx % 4] hudFont
    let healthPct = max 0.0 (float p.Health / float FullHealth)
    let barW = float (vw / 3)
    let barX = fvx + 30.0
    let barY = hudY + 2.0
    fillRectC ctx barX barY barW 8.0 healthBgColor
    let healthColor =
        if healthPct > 0.6 then "rgb(50,205,50)"
        elif healthPct > 0.3 then "rgb(255,255,0)"
        else "rgb(255,0,0)"
    fillRectC ctx barX barY (barW * healthPct) 8.0 healthColor
    drawText ctx $"CANNON  |  {specialName} [{p.Ammo}]" (fvx + 4.0) (hudY + 14.0) hudFont "rgb(255,255,255)"
    if p.OnBase && p.Alive then
        drawText ctx "BASE" (fvx + fvw - 36.0) (hudY + 14.0) hudFont "rgb(50,205,50)"
    drawText ctx $"K:{p.KillCount} D:{p.DeathCount}" (barX + barW + 8.0) (hudY + 2.0) hudFont "rgb(255,255,255)"
    restore ctx

// ─── Viewport border ─────────────────────────────────────────────────────
let drawBorder (ctx: obj) (vx: int) (vy: int) (vw: int) (vh: int) (idx: int) =
    setStroke ctx playerDarkColors[idx % 4]
    setLineWidth ctx 2.0
    strokeRectJs ctx (float vx + 1.0) (float vy + 1.0) (float vw - 2.0) (float vh - 2.0)

// ─── Render full frame ─────────────────────────────────────────────────
let renderFrame (ctx: obj) (gs: GameState) (windowW: int) (windowH: int) =
    setSmoothing ctx false
    setBaseline ctx "top"
    fillRectC ctx 0.0 0.0 (float windowW) (float windowH) bgColor

    let layouts = viewportLayout gs.NumPlayers windowW windowH
    for i in 0 .. gs.NumPlayers - 1 do
        if i < layouts.Length then
            let (vx, vy, vw, vh) = layouts[i]
            drawPlayerView ctx gs i vx vy vw vh
            drawBorder ctx vx vy vw vh i

    if not gs.RoundActive then
        fillRectC ctx 0.0 0.0 (float windowW) (float windowH) (cssRGBA 0 0 0 192)
        let cx = float (windowW / 2 - 230)
        let mutable y = float (windowH / 2 - 90)
        drawText ctx "FsRocket" cx y titleFont "rgb(255,255,255)"
        y <- y + 34.0
        let levelName = match gs.Level with Some lv -> lv.Name | None -> "No Terrain"
        let cpuText = if gs.CpuCount > 0 then $"  |  CPU: {gs.CpuCount}" else ""
        drawText ctx $"Level: {levelName}  |  Players: {gs.NumPlayers}{cpuText}" cx y subFont "rgb(192,192,192)"
        y <- y + 24.0
        drawText ctx "Press SPACE to start  |  1-4: players  |  ESC: reset" cx y subFont "rgb(192,192,192)"
        y <- y + 18.0
        drawText ctx "F5/F6: level   F7/F8: CPU players" cx y subFont "rgb(192,192,192)"
        y <- y + 18.0
        let weaponRule =
            if gs.WeaponSwitchOnlyOnBase
            then "Weapon switch (P1=9 P2=1 P3=6): ONLY while landed on a base   [F9 to change]"
            else "Weapon switch (P1=9 P2=1 P3=6): anytime   [F9 to require a base]"
        drawText ctx weaponRule cx y subFont "rgb(144,192,255)"
        y <- y + 28.0
        drawText ctx "Controls:" cx y subFont "rgb(255,255,128)"
        y <- y + 18.0
        drawText ctx "P1: Arrows/NumPad = Thrust/Turn   RShift = Fire   Down = Special   9 = Weapon" cx y keyFont "rgb(255,255,255)"
        y <- y + 15.0
        drawText ctx "P2: W/A/D = Thrust/Turn   Tab = Fire   S = Special   1 = Weapon" cx y keyFont "rgb(255,255,255)"
        y <- y + 15.0
        drawText ctx "P3: I/J/L = Thrust/Turn   B = Fire   K = Special   6 = Weapon" cx y keyFont "rgb(255,255,255)"
