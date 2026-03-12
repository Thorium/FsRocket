/// FsRocket Renderer
/// Split-screen WinForms rendering, scaled 320x200 VGA Mode 13h
/// Each player gets a viewport (156x86, scaled 3x here)
module FsRocket.Renderer

open System
open System.Drawing
open System.Drawing.Drawing2D
open System.Drawing.Imaging
open System.Runtime.InteropServices
open FsRocket.Physics
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types
open FsRocket.Entities

// ─── Scale factor (3x 320x200) ───────────────────────────

let Scale = 3

/// Extra zoom factor for terrain bitmaps — makes terrain features appear bigger
/// 1.0 = original size, >1.0 = zoomed in (bigger terrain features)
let TerrainZoom = 1.25

// ─── Color palette (approximate VGA Mode 13h palette) ──────────────────

let playerColors = [|
    Color.FromArgb(0x5F, 0x5F, 0xFF)   // Player 1: Blue  
    Color.FromArgb(0x3F, 0xCF, 0x3F)   // Player 2: Green 
    Color.FromArgb(0xFF, 0x3F, 0x3F)   // Player 3: Red   
    Color.FromArgb(0xFF, 0xFF, 0x3F)   // Player 4: Yellow
|]

let playerDarkColors = [|
    Color.FromArgb(0x2F, 0x2F, 0x9F)
    Color.FromArgb(0x1F, 0x7F, 0x1F)
    Color.FromArgb(0x9F, 0x1F, 0x1F)
    Color.FromArgb(0x9F, 0x9F, 0x1F)
|]

let bgColor = Color.FromArgb(0x10, 0x10, 0x18)
let wallColor = Color.FromArgb(0x40, 0x40, 0x50)
let gridColor = Color.FromArgb(0x18, 0x18, 0x24)
let bulletColor = Color.FromArgb(0xFF, 0xFF, 0x80)
let mineColor = Color.FromArgb(0xFF, 0x60, 0x20)
let nukeColor = Color.FromArgb(0xFF, 0xFF, 0xFF)
let laserColor = Color.FromArgb(0xFF, 0x20, 0x20)
let shieldColor = Color.FromArgb(0x80, 0xC0, 0xFF)
let empColor = Color.FromArgb(0xC0, 0x40, 0xFF)
let flameColor = Color.FromArgb(0xFF, 0x80, 0x20)
let hudBgColor = Color.FromArgb(0x08, 0x08, 0x10)

// ─── Pre-allocated GDI+ resources (avoid per-frame allocation churn) ───
// These are module-level cached objects reused across frames.

let private cachedBgBrush = new SolidBrush(bgColor)
let private cachedBulletBrush = new SolidBrush(bulletColor)
let private cachedGridPen = new Pen(gridColor, 1.0f)
let private cachedShieldPen = new Pen(shieldColor, float32 Scale)
let private cachedEmpStarBrush = new SolidBrush(empColor)
let private cachedHudBgBrush = new SolidBrush(hudBgColor)
let private cachedWhiteBrush = new SolidBrush(Color.White)
let private cachedClearBrush = new SolidBrush(Color.Black)
let private cachedHudFont = new Font("Consolas", 8.0f, FontStyle.Bold)
let private cachedBigFont = new Font("Consolas", 14.0f, FontStyle.Bold)
let private cachedRedBrush = new SolidBrush(Color.Red)
let private cachedHealthBarBg = new SolidBrush(Color.FromArgb(0x30, 0x00, 0x00))
let private cachedThrustBrush = new SolidBrush(Color.FromArgb(0xFF, 0xA0, 0x20))
let private cachedMmBg = new SolidBrush(Color.FromArgb(0xA0, 0x08, 0x08, 0x10))
let private cachedMmBorder = new Pen(Color.FromArgb(0x80, 0x40, 0x40, 0x60), 1.0f)
let private cachedMmWallBrush = new SolidBrush(Color.FromArgb(0x80, 0x60, 0x60, 0x80))
let private cachedDirtclodBrush = new SolidBrush(Color.FromArgb(0xFF, 0x8B, 0x60, 0x20))
let private cachedDirtclodTrailBrush = new SolidBrush(Color.FromArgb(0x80, 0x60, 0x40, 0x10))
let private cachedLaserTipBrush = new SolidBrush(Color.FromArgb(0xFF, 0xFF, 0x80, 0x80))
let private cachedExhBrush = new SolidBrush(Color.FromArgb(0x80, 0xFF, 0x60, 0x10))
let private cachedPlayerBrushes = playerColors |> Array.map (fun c -> new SolidBrush(c))
let private cachedPlayerDarkPens = playerDarkColors |> Array.map (fun c -> new Pen(c, 1.0f))

// ─── Standard VGA Mode 13h Default Palette (256 colors) ────────────────
// Entries 0-15: CGA colors, 16-255: 6x6x6 color cube + grayscale ramp

let private buildVgaPalette () : int array =
    let pal = Array.zeroCreate<int> 256
    // 0-15: Standard CGA/EGA colors (6-bit VGA values scaled to 8-bit)
    let cga = [|
        (0x00, 0x00, 0x00); (0x00, 0x00, 0xAA); (0x00, 0xAA, 0x00); (0x00, 0xAA, 0xAA)
        (0xAA, 0x00, 0x00); (0xAA, 0x00, 0xAA); (0xAA, 0x55, 0x00); (0xAA, 0xAA, 0xAA)
        (0x55, 0x55, 0x55); (0x55, 0x55, 0xFF); (0x55, 0xFF, 0x55); (0x55, 0xFF, 0xFF)
        (0xFF, 0x55, 0x55); (0xFF, 0x55, 0xFF); (0xFF, 0xFF, 0x55); (0xFF, 0xFF, 0xFF)
    |]
    for i in 0..15 do
        let r, g, b = cga[i]
        pal[i] <- (0xFF <<< 24) ||| (r <<< 16) ||| (g <<< 8) ||| b
    // 16-231: 6x6x6 color cube (standard VGA default)
    for r in 0..5 do
        for g in 0..5 do
            for b in 0..5 do
                let idx = 16 + r * 36 + g * 6 + b
                let rv = r * 51  // 0, 51, 102, 153, 204, 255
                let gv = g * 51
                let bv = b * 51
                pal[idx] <- (0xFF <<< 24) ||| (rv <<< 16) ||| (gv <<< 8) ||| bv
    // 232-255: Grayscale ramp
    for i in 0..23 do
        let v = i * 255 / 23
        pal[232 + i] <- (0xFF <<< 24) ||| (v <<< 16) ||| (v <<< 8) ||| v
    pal

let vgaPalette = buildVgaPalette ()

// ─── Terrain Bitmap Cache ──────────────────────────────────────────────

let mutable private terrainBitmap: Bitmap option = None
let mutable private terrainBitmapLevel: string = ""

/// Build a Bitmap from terrain pixel data using the VGA palette
let buildTerrainBitmap (level: LevelData) : Bitmap =
    let bmp = new Bitmap(MapWidth, MapHeight, PixelFormat.Format32bppArgb)
    let bmpData = bmp.LockBits(Rectangle(0, 0, MapWidth, MapHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb)
    let stride = bmpData.Stride
    let strideInts = stride / 4  // stride in int32 units
    let buf = Array.zeroCreate<int> (strideInts * MapHeight)
    for y in 0 .. MapHeight - 1 do
        for x in 0 .. MapWidth - 1 do
            let pixel = int level.Pixels[y * MapWidth + x]
            buf[y * strideInts + x] <- vgaPalette[pixel]
    Marshal.Copy(buf, 0, bmpData.Scan0, buf.Length)
    bmp.UnlockBits bmpData
    bmp

/// Get or create cached terrain bitmap for the current level.
/// Rebuilds when level changes or when terrain has been modified by ammo.
let getTerrainBitmap (level: LevelData) (terrainDirty: bool) : Bitmap =
    if terrainBitmapLevel <> level.Name || terrainDirty then
        terrainBitmap |> Option.iter (fun b -> b.Dispose())
        let bmp = buildTerrainBitmap level
        terrainBitmap <- Some bmp
        terrainBitmapLevel <- level.Name
        bmp
    else
        terrainBitmap.Value

// ─── Layout: viewports for 1-4 players ─────────────────────────────────

/// Returns (x, y, w, h) for each player's viewport on the window
let viewportLayout (numPlayers: int) (windowW: int) (windowH: int) =
    let hudH = 40  // HUD bar height at bottom of each viewport
    match numPlayers with
    | 1 -> [| (0, 0, windowW, windowH) |]
    | 2 -> [| (0, 0, windowW / 2, windowH)
              (windowW / 2, 0, windowW / 2, windowH) |]
    | 3 | 4 ->
        [| (0, 0, windowW / 2, windowH / 2)
           (windowW / 2, 0, windowW / 2, windowH / 2)
           (0, windowH / 2, windowW / 2, windowH / 2)
           (windowW / 2, windowH / 2, windowW / 2, windowH / 2) |]
    | _ -> [||]

// ─── Draw a single player's viewport ──────────────────────────────────

let drawPlayerView (g: Graphics) (gs: GameState) (playerIdx: int)
                   (vx: int) (vy: int) (vw: int) (vh: int) =
    let hudH = 36
    let gameH = vh - hudH
    let p = gs.Players[playerIdx]

    // Clip to viewport
    g.SetClip(Rectangle(vx, vy, vw, vh))

    // Background
    g.FillRectangle(cachedBgBrush, vx, vy, vw, gameH)

    // Calculate view offset (center on player)
    let effectiveScaleF = float Scale * TerrainZoom
    let camX = p.PosX / PositionScale - float vw / (2.0 * effectiveScaleF)
    let camY = p.PosY / PositionScale - float gameH / (2.0 * effectiveScaleF)

    let toScreenX (wx: float) = vx + int ((wx - camX) * effectiveScaleF)
    let toScreenY (wy: float) = vy + int ((wy - camY) * effectiveScaleF)

    // Grid lines (every 32 pixels in arena)
    match gs.Level with
    | Some level ->
        // Draw terrain bitmap (scaled with extra zoom)
        let tbmp = getTerrainBitmap level gs.TerrainDirty
        // Source rect: visible portion of terrain in arena coords (adjusted for zoom)
        let effectiveScale = float Scale * TerrainZoom
        let srcX = max 0 (int camX)
        let srcY = max 0 (int camY)
        let srcW = min (MapWidth - srcX) (int (float vw / effectiveScale) + 1)
        let srcH = min (MapHeight - srcY) (int (float gameH / effectiveScale) + 1)
        if srcW > 0 && srcH > 0 then
            let destX = toScreenX (float srcX)
            let destY = toScreenY (float srcY)
            let destW = int (float srcW * effectiveScale)
            let destH = int (float srcH * effectiveScale)
            g.DrawImage(tbmp, Rectangle(destX, destY, destW, destH),
                        Rectangle(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel)
    | None ->
        // No terrain: draw grid + hardcoded walls
        let startGX = int (floor (camX / 32.0)) * 32
        let startGY = int (floor (camY / 32.0)) * 32
        for gx in startGX .. 32 .. startGX + int (float vw / effectiveScaleF) + 32 do
            let sx = toScreenX (float gx)
            if sx >= vx && sx <= vx + vw then
                g.DrawLine(cachedGridPen, sx, vy, sx, vy + gameH)
        for gy in startGY .. 32 .. startGY + int (float gameH / effectiveScaleF) + 32 do
            let sy = toScreenY (float gy)
            if sy >= vy && sy <= vy + gameH then
                g.DrawLine(cachedGridPen, vx, sy, vx + vw, sy)

        // Arena boundary walls
        use wallPen = new Pen(wallColor, float32 (2 * Scale))
        let wx0 = toScreenX 0.0
        let wy0 = toScreenY 0.0
        let wx1 = toScreenX ArenaWidth
        let wy1 = toScreenY ArenaHeight
        g.DrawRectangle(wallPen, wx0, wy0, wx1 - wx0, wy1 - wy0)

        // Interior arena walls
        let wallFillColor = Color.FromArgb(0x50, 0x50, 0x68)
        let wallHighlight = Color.FromArgb(0x68, 0x68, 0x80)
        let wallShadow = Color.FromArgb(0x30, 0x30, 0x40)
        use wallFillBrush = new SolidBrush(wallFillColor)
        use wallHiPen = new Pen(wallHighlight, 1.0f)
        use wallShPen = new Pen(wallShadow, 1.0f)
        for w in arenaWalls do
            let swx = toScreenX w.X
            let swy = toScreenY w.Y
            let sww = int (w.W * effectiveScaleF)
            let swh = int (w.H * effectiveScaleF)
            g.FillRectangle(wallFillBrush, swx, swy, sww, swh)
            g.DrawLine(wallHiPen, swx, swy, swx + sww, swy)
            g.DrawLine(wallHiPen, swx, swy, swx, swy + swh)
            g.DrawLine(wallShPen, swx + sww, swy, swx + sww, swy + swh)
            g.DrawLine(wallShPen, swx, swy + swh, swx + sww, swy + swh)

    // Draw entities (bullets, mines, etc.)
    for ent in gs.Entities do
        let ex = toScreenX ent.X
        let ey = toScreenY ent.Y
        let margin = 40 * Scale
        if ex > vx - margin && ex < vx + vw + margin && ey > vy - margin && ey < vy + gameH + margin then
            match ent.EType with
            | EntityType.Expanding ->
                // Sonicboom: expanding ring
                let ringR = int (ent.Radius * float Scale)
                if ringR > 0 then
                    let alpha = max 40 (255 - int (ent.Radius * 2.0))
                    let c = playerColors[ent.Owner % 4]
                    use ringPen = new Pen(Color.FromArgb(alpha, c), float32 (max 1 (3 * Scale - int (ent.Radius / 20.0))))
                    g.DrawEllipse(ringPen, ex - ringR, ey - ringR, ringR * 2, ringR * 2)
                    // Inner glow ring
                    let innerR = ringR - Scale * 2
                    if innerR > 0 then
                        use innerPen = new Pen(Color.FromArgb(alpha / 2, Color.White), 1.0f)
                        g.DrawEllipse(innerPen, ex - innerR, ey - innerR, innerR * 2, innerR * 2)

            | EntityType.Nuke ->
                // Nuke: expanding glow + flash
                let nukeR = max 2 (int (ent.Radius * float Scale))
                // Outer glow (fading)
                let glowAlpha = max 20 (180 - int (ent.Radius * 2.0))
                use glowBrush = new SolidBrush(Color.FromArgb(glowAlpha, 0xFF, 0xFF, 0x80))
                g.FillEllipse(glowBrush, ex - nukeR, ey - nukeR, nukeR * 2, nukeR * 2)
                // Core (bright white shrinking)
                let coreR = max 1 (nukeR / 3)
                use coreBrush = new SolidBrush(Color.FromArgb(min 255 (glowAlpha + 60), Color.White))
                g.FillEllipse(coreBrush, ex - coreR, ey - coreR, coreR * 2, coreR * 2)
                // Ring outline
                use nukePen = new Pen(Color.FromArgb(glowAlpha, 0xFF, 0xA0, 0x00), float32 Scale)
                g.DrawEllipse(nukePen, ex - nukeR, ey - nukeR, nukeR * 2, nukeR * 2)

            | EntityType.Blackhole ->
                // Blackhole: swirling gravity well
                let bhR = max 3 (int (ent.Radius * float Scale))
                // Dark core
                use coreBrush = new SolidBrush(Color.FromArgb(0xE0, 0x05, 0x00, 0x10))
                g.FillEllipse(coreBrush, ex - bhR, ey - bhR, bhR * 2, bhR * 2)
                // Swirl arms (4 spiral arms rotating)
                let swirlAngle = float ent.Timer * 6.0
                for arm in 0..3 do
                    let baseAngle = degToRad (swirlAngle + float arm * 90.0)
                    let armColor = Color.FromArgb(0x80, 0x40, 0x00, 0xC0)
                    use armPen = new Pen(armColor, float32 Scale)
                    let r1 = float bhR * 0.4
                    let r2 = float bhR * 1.2
                    let r3 = float bhR * 2.0
                    let x1 = ex + int (cos baseAngle * r1)
                    let y1 = ey + int (sin baseAngle * r1)
                    let x2 = ex + int (cos (baseAngle + 0.4) * r2)
                    let y2 = ey + int (sin (baseAngle + 0.4) * r2)
                    let x3 = ex + int (cos (baseAngle + 0.8) * r3)
                    let y3 = ey + int (sin (baseAngle + 0.8) * r3)
                    g.DrawLine(armPen, x1, y1, x2, y2)
                    g.DrawLine(armPen, x2, y2, x3, y3)
                // Outer pull ring
                let pullR = int (blackholeRadius * float Scale)
                let pullAlpha = 20 + int (10.0 * sin (float ent.Timer * 0.15))
                use pullPen = new Pen(Color.FromArgb(pullAlpha, 0x80, 0x40, 0xFF), 1.0f)
                g.DrawEllipse(pullPen, ex - pullR, ey - pullR, pullR * 2, pullR * 2)

            | EntityType.Mine ->
                // Mine: pulsing circle, brighter when armed
                let armed = ent.Timer > 0
                let pulse = if armed then 0.5 + 0.5 * sin (float ent.Timer * 0.2) else 0.3
                let sz = int (float (4 * Scale) * (0.8 + 0.2 * pulse))
                let alpha = int (128.0 + 127.0 * pulse)
                let mColor = if armed then Color.FromArgb(alpha, 0xFF, 0x40, 0x10) else Color.FromArgb(0x80, 0x80, 0x40, 0x10)
                use mineBrush = new SolidBrush(mColor)
                g.FillEllipse(mineBrush, ex - sz/2, ey - sz/2, sz, sz)
                // Cross-hairs on armed mines
                if armed then
                    use crossPen = new Pen(Color.FromArgb(alpha, 0xFF, 0xFF, 0x40), 1.0f)
                    g.DrawLine(crossPen, ex - sz/2, ey, ex + sz/2, ey)
                    g.DrawLine(crossPen, ex, ey - sz/2, ex, ey + sz/2)

            | EntityType.Flame ->
                // Flame: fading orange-red glow, size grows with timer
                let fTimer = max 1 (abs ent.Timer)
                let fSize = max 2 (fTimer * Scale / 3)
                let fAlpha = max 40 (220 - fTimer * 3)
                let r = min 255 (0xFF - fTimer)
                let gv = max 0 (0x80 - fTimer * 2)
                use flameBrush = new SolidBrush(Color.FromArgb(fAlpha, r, gv, 0x10))
                g.FillEllipse(flameBrush, ex - fSize/2, ey - fSize/2, fSize, fSize)
                // Bright core
                let coreS = max 1 (fSize / 3)
                use coreB = new SolidBrush(Color.FromArgb(min 255 (fAlpha + 30), 0xFF, 0xC0, 0x40))
                g.FillEllipse(coreB, ex - coreS/2, ey - coreS/2, coreS, coreS)

            | EntityType.Heavy ->
                // Missile/dumbfire: directional shape with exhaust glow
                let sz = 4 * Scale
                let c = if ent.WeaponIdx = 19 then Color.FromArgb(0xFF, 0xC0, 0x30) else Color.Orange
                use heavyBrush = new SolidBrush(c)
                // Draw a directional diamond shape
                let speed = sqrt (ent.VelX * ent.VelX + ent.VelY * ent.VelY) + 0.01
                let dirX = ent.VelX / speed
                let dirY = ent.VelY / speed
                let noseX = ex + int (dirX * float sz)
                let noseY = ey + int (dirY * float sz)
                let tailX = ex - int (dirX * float sz * 0.6)
                let tailY = ey - int (dirY * float sz * 0.6)
                let sideX = int (-dirY * float sz * 0.4)
                let sideY = int (dirX * float sz * 0.4)
                let pts = [| Point(noseX, noseY)
                             Point(ex + sideX, ey + sideY)
                             Point(tailX, tailY)
                             Point(ex - sideX, ey - sideY) |]
                g.FillPolygon(heavyBrush, pts)
                // Exhaust glow
                use exhBrush = new SolidBrush(Color.FromArgb(0x80, 0xFF, 0x60, 0x10))
                let exhX = ex - int (dirX * float sz * 0.8)
                let exhY = ey - int (dirY * float sz * 0.8)
                let exhS = Scale * 2
                g.FillEllipse(exhBrush, exhX - exhS/2, exhY - exhS/2, exhS, exhS)

            | EntityType.EMP ->
                // EMP: oscillating purple-white sparkle
                let empR = max 2 (int (ent.Radius * float Scale))
                let empAlpha = 140 + int (80.0 * sin (float ent.Timer * 0.3))
                use empBrush = new SolidBrush(Color.FromArgb(empAlpha, 0xC0, 0x40, 0xFF))
                g.FillEllipse(empBrush, ex - empR, ey - empR, empR * 2, empR * 2)
                // Sparkle cross
                let sparkLen = empR + Scale
                use sparkPen = new Pen(Color.FromArgb(empAlpha, 0xFF, 0xFF, 0xFF), 1.0f)
                let sparkAngle = float ent.Timer * 8.0
                let sr = degToRad sparkAngle
                let sx1 = ex + int (cos sr * float sparkLen)
                let sy1 = ey + int (sin sr * float sparkLen)
                let sx2 = ex - int (cos sr * float sparkLen)
                let sy2 = ey - int (sin sr * float sparkLen)
                g.DrawLine(sparkPen, sx1, sy1, sx2, sy2)
                let sr2 = sr + Math.PI / 2.0
                let sx3 = ex + int (cos sr2 * float sparkLen)
                let sy3 = ey + int (sin sr2 * float sparkLen)
                let sx4 = ex - int (cos sr2 * float sparkLen)
                let sy4 = ey - int (sin sr2 * float sparkLen)
                g.DrawLine(sparkPen, sx3, sy3, sx4, sy4)

            | EntityType.Laser ->
                // Laser: elongated red line in direction of travel
                let speed = sqrt (ent.VelX * ent.VelX + ent.VelY * ent.VelY) + 0.01
                let dirX = ent.VelX / speed
                let dirY = ent.VelY / speed
                let laserLen = 6 * Scale
                let lx2 = ex - int (dirX * float laserLen)
                let ly2 = ey - int (dirY * float laserLen)
                use lPen = new Pen(laserColor, float32 (Scale + 1))
                g.DrawLine(lPen, ex, ey, lx2, ly2)
                // Bright tip
                g.FillEllipse(cachedLaserTipBrush, ex - Scale, ey - Scale, Scale * 2, Scale * 2)

            | EntityType.Ricochet ->
                // Ricochet: cyan with bounce count indicator
                let rSize = 2 * Scale
                let bounce = ent.SubType
                let rAlpha = max 100 (255 - bounce * 50)
                use rBrush = new SolidBrush(Color.FromArgb(rAlpha, 0x00, 0xFF, 0xFF))
                g.FillEllipse(rBrush, ex - rSize/2, ey - rSize/2, rSize, rSize)
                // Fading trail dot
                let trailX = ex - int (ent.VelX * float Scale)
                let trailY = ey - int (ent.VelY * float Scale)
                use trailBrush = new SolidBrush(Color.FromArgb(rAlpha / 3, 0x00, 0xFF, 0xFF))
                g.FillEllipse(trailBrush, trailX - Scale/2, trailY - Scale/2, Scale, Scale)

            | EntityType.Exploding ->
                // Dirtclod: brown-ish lobbed projectile
                let dSize = 3 * Scale
                g.FillEllipse(cachedDirtclodBrush, ex - dSize/2, ey - dSize/2, dSize, dSize)
                // Arc trail particles (little dots behind)
                let tx = ex - int (ent.VelX * float Scale * 0.5)
                let ty = ey - int (ent.VelY * float Scale * 0.5)
                g.FillEllipse(cachedDirtclodTrailBrush, tx - Scale/2, ty - Scale/2, Scale, Scale)

            | EntityType.Shrapnel ->
                // Shrapnel: tiny gray dots
                use sBrush = new SolidBrush(Color.FromArgb(max 60 (200 - ent.Timer * 5), Color.Gray))
                g.FillRectangle(sBrush, ex - Scale/2, ey - Scale/2, Scale, Scale)

            | EntityType.Shield ->
                // Orbiter/freezer: blue-white spinning
                let sSize = 4 * Scale
                let sAngle = float ent.Timer * 5.0
                let sAlpha = 160 + int (60.0 * sin (degToRad sAngle))
                use sBrush = new SolidBrush(Color.FromArgb(sAlpha, 0x80, 0xC0, 0xFF))
                g.FillEllipse(sBrush, ex - sSize/2, ey - sSize/2, sSize, sSize)

            | _ ->
                // Default bullet rendering
                let size = 2 * Scale
                g.FillEllipse(cachedBulletBrush, ex - size/2, ey - size/2, size, size)

    // Draw particles (death debris, explosion fragments)
    for part in gs.Particles do
        let px = toScreenX part.X
        let py = toScreenY part.Y
        if px > vx - 5 && px < vx + vw + 5 && py > vy - 5 && py < vy + gameH + 5 then
            let alpha = min 255 (part.Life * 8)
            let c = playerColors[part.Color % 4]
            use brush = new SolidBrush(Color.FromArgb(alpha, c))
            let sz = max 1 (Scale * part.Life / 10)
            g.FillRectangle(brush, px - sz/2, py - sz/2, sz, sz)

    // Draw players (all visible players in this viewport)
    for i in 0..gs.NumPlayers-1 do
        let other = gs.Players[i]
        if other.Alive then
            let ox = toScreenX (other.PosX / PositionScale)
            let oy = toScreenY (other.PosY / PositionScale)
            if ox > vx - 20 && ox < vx + vw + 20 && oy > vy - 20 && oy < vy + gameH + 20 then
                // Ship body (triangle pointing in direction of travel)
                let rad = degToRad (other.Angle + 90.0)
                let shipSize = float (5 * Scale)
                let nosX = ox + int (cos rad * shipSize)
                let nosY = oy - int (sin rad * shipSize)
                let rearRad1 = rad + Math.PI * 0.75
                let rearRad2 = rad - Math.PI * 0.75
                let r1x = ox + int (cos rearRad1 * shipSize * 0.7)
                let r1y = oy - int (sin rearRad1 * shipSize * 0.7)
                let r2x = ox + int (cos rearRad2 * shipSize * 0.7)
                let r2y = oy - int (sin rearRad2 * shipSize * 0.7)
                let pts = [| Point(nosX, nosY); Point(r1x, r1y); Point(r2x, r2y) |]
                g.FillPolygon(cachedPlayerBrushes[i % 4], pts)
                g.DrawPolygon(cachedPlayerDarkPens[i % 4], pts)

                // Thrust flame (when UP key held)
                if other.KeyUp then
                    let thrustRad = rad + Math.PI
                    let flLen = float (3 * Scale) + float (gs.GameTick % 3) * float Scale
                    let fx = ox + int (cos thrustRad * flLen)
                    let fy = oy - int (sin thrustRad * flLen)
                    g.FillEllipse(cachedThrustBrush, fx - Scale, fy - Scale, Scale * 2, Scale * 2)

                // Shield bubble
                if other.Flags.HasFlag(PlayerFlags.Shield) then
                    let sr = 7 * Scale
                    g.DrawEllipse(cachedShieldPen, ox - sr, oy - sr, sr * 2, sr * 2)

                // Stun effect (spinning stars)
                if other.Flags.HasFlag(PlayerFlags.Stunned) then
                    for s in 0..2 do
                        let sRad = degToRad (other.AnimAngle + float s * 120.0)
                        let sx = ox + int (cos sRad * float (6 * Scale))
                        let sy = oy - int (sin sRad * float (6 * Scale))
                        g.FillEllipse(cachedEmpStarBrush, sx - Scale/2, sy - Scale/2, Scale, Scale)

                // Invincibility flash
                if other.InvTimer > 0 && other.InvTimer % 4 < 2 then
                    use flashPen = new Pen(Color.White, float32 Scale)
                    g.DrawEllipse(flashPen, ox - 6*Scale, oy - 6*Scale, 12*Scale, 12*Scale)

    // ─── Minimap (top-right corner of each viewport) ──────────────
    let mmW = min 80 (vw / 5)   // minimap pixel size
    let mmH = int (float mmW * ArenaHeight / ArenaWidth)
    let mmX = vx + vw - mmW - 4
    let mmY = vy + 4
    let mmScaleX = float mmW / ArenaWidth
    let mmScaleY = float mmH / ArenaHeight

    // Background
    g.FillRectangle(cachedMmBg, mmX, mmY, mmW, mmH)
    g.DrawRectangle(cachedMmBorder, mmX, mmY, mmW, mmH)

    // Walls on minimap
    for w in arenaWalls do
        let mwx = mmX + int (w.X * mmScaleX)
        let mwy = mmY + int (w.Y * mmScaleY)
        let mww = max 1 (int (w.W * mmScaleX))
        let mwh = max 1 (int (w.H * mmScaleY))
        g.FillRectangle(cachedMmWallBrush, mwx, mwy, mww, mwh)

    // Entities on minimap (just dots for active ones)
    for ent in gs.Entities do
        let mex = mmX + int (ent.X * mmScaleX)
        let mey = mmY + int (ent.Y * mmScaleY)
        if mex >= mmX && mex <= mmX + mmW && mey >= mmY && mey <= mmY + mmH then
            let mc =
                match ent.EType with
                | EntityType.Nuke | EntityType.Expanding -> Color.White
                | EntityType.Blackhole -> Color.Purple
                | EntityType.Mine -> Color.FromArgb(0xFF, 0x80, 0x20)
                | EntityType.Flame -> Color.FromArgb(0xFF, 0x60, 0x10)
                | _ -> Color.FromArgb(0xA0, 0xA0, 0xA0)
            use meBrush = new SolidBrush(mc)
            g.FillRectangle(meBrush, mex, mey, 1, 1)

    // Players on minimap
    for i in 0..gs.NumPlayers-1 do
        let other = gs.Players[i]
        if other.Alive then
            let mpx = mmX + int (other.PosX / PositionScale * mmScaleX)
            let mpy = mmY + int (other.PosY / PositionScale * mmScaleY)
            use mpBrush = new SolidBrush(playerColors[i % 4])
            g.FillRectangle(mpBrush, mpx - 1, mpy - 1, 3, 3)

    // Camera view rect on minimap
    let cvx = mmX + int (camX * mmScaleX)
    let cvy = mmY + int (camY * mmScaleY)
    let cvw = int (float vw / effectiveScaleF * mmScaleX)
    let cvh = int (float gameH / effectiveScaleF * mmScaleY)
    use cvPen = new Pen(Color.FromArgb(0x60, playerColors[playerIdx % 4]), 1.0f)
    g.DrawRectangle(cvPen, cvx, cvy, cvw, cvh)

    // ─── HUD bar at bottom ─────────────────────────────────────────
    g.ResetClip()
    g.SetClip(Rectangle(vx, vy + gameH, vw, hudH))
    g.FillRectangle(cachedHudBgBrush, vx, vy + gameH, vw, hudH)

    let hudY = vy + gameH + 2
    let weaponName = (getWeapon p.WeaponType).Name

    // Player name and weapon
    let name = $"P{playerIdx + 1}"
    g.DrawString(name, cachedHudFont, cachedPlayerBrushes[playerIdx % 4], float32 (vx + 4), float32 hudY)

    // Health bar
    let healthPct = max 0.0 (float p.Health / float FullHealth)  // 1.0 = full, 0.0 = dead
    let barW = vw / 3
    let barH = 8
    let barX = vx + 30
    let barY = hudY + 2
    g.FillRectangle(cachedHealthBarBg, barX, barY, barW, barH)
    let healthColor =
        if healthPct > 0.6 then Color.LimeGreen
        elif healthPct > 0.3 then Color.Yellow
        else Color.Red
    use healthBrush = new SolidBrush(healthColor)
    g.FillRectangle(healthBrush, barX, barY, int (float barW * healthPct), barH)

    // Weapon + ammo
    let info = $"{weaponName} [{p.Ammo}]"
    g.DrawString(info, cachedHudFont, cachedWhiteBrush, float32 (vx + 4), float32 (hudY + 14))

    // Kill/Death count
    let kd = $"K:{p.KillCount} D:{p.DeathCount}"
    g.DrawString(kd, cachedHudFont, cachedWhiteBrush, float32 (vx + barX + barW + 8), float32 (hudY + 2))

    // Status indicators
    if not p.Alive then
        use deadBrush = new SolidBrush(Color.FromArgb(0xA0, 0, 0, 0))
        g.FillRectangle(deadBrush, vx, vy, vw, gameH)
        g.DrawString("DEAD", cachedBigFont, cachedRedBrush,
                     float32 (vx + vw/2 - 30), float32 (vy + gameH/2 - 10))

    g.ResetClip()

// ─── Draw viewport border ──────────────────────────────────────────────

let drawBorder (g: Graphics) (vx: int) (vy: int) (vw: int) (vh: int) (idx: int) =
    use pen = new Pen(playerDarkColors[idx % 4], 2.0f)
    g.DrawRectangle(pen, vx, vy, vw - 1, vh - 1)

// ─── Render full frame ─────────────────────────────────────────────────

let renderFrame (g: Graphics) (gs: GameState) (windowW: int) (windowH: int) =
    g.SmoothingMode <- SmoothingMode.None
    g.InterpolationMode <- InterpolationMode.NearestNeighbor

    // Clear
    use clearBrush = new SolidBrush(Color.Black)
    g.FillRectangle(clearBrush, 0, 0, windowW, windowH)

    let layouts = viewportLayout gs.NumPlayers windowW windowH

    for i in 0..gs.NumPlayers-1 do
        if i < layouts.Length then
            let (vx, vy, vw, vh) = layouts[i]
            drawPlayerView g gs i vx vy vw vh
            drawBorder g vx vy vw vh i

    // Title bar overlay
    if not gs.RoundActive then
        use overlayBrush = new SolidBrush(Color.FromArgb(0xC0, 0, 0, 0))
        g.FillRectangle(overlayBrush, 0, 0, windowW, windowH)
        use titleFont = new Font("Consolas", 18.0f, FontStyle.Bold)
        use subFont = new Font("Consolas", 10.0f)
        use keyFont = new Font("Consolas", 9.0f)
        use white = new SolidBrush(Color.White)
        use gray = new SolidBrush(Color.Gray)
        use yellow = new SolidBrush(Color.FromArgb(0xFF, 0xFF, 0x80))
        let cx = float32 (windowW / 2 - 200)
        let mutable y = float32 (windowH / 2 - 80)
        g.DrawString("FsRocket Physics", titleFont, white, cx, y)
        y <- y + 30.0f
        let levelName = match gs.Level with Some lv -> lv.Name | None -> "No Terrain"
        g.DrawString($"Level: {levelName}  |  Constants", subFont, gray, cx, y)
        y <- y + 24.0f
        g.DrawString("Press SPACE to start  |  F1-F4: weapon  |  1-4: players  |  ESC: quit", subFont, gray, cx, y)
        y <- y + 16.0f
        g.DrawString("F5: prev level  |  F6: next level", subFont, gray, cx, y)
        y <- y + 28.0f
        g.DrawString("Controls:", subFont, yellow, cx, y)
        y <- y + 18.0f
        g.DrawString("P1 RED:    Arrows/NumPad = Thrust/Turn    RShift = Fire    Down = Brake", keyFont, white, cx, y)
        y <- y + 15.0f
        g.DrawString("P2 GREEN:  W/A/D = Thrust/Turn            Tab = Fire       S = Brake", keyFont, white, cx, y)
        y <- y + 15.0f
        g.DrawString("P3 YELLOW: I/J/L = Thrust/Turn            B = Fire         K = Brake", keyFont, white, cx, y)
