/// FsRocket Renderer — MonoGame / DesktopGL
/// Split-screen rendering using SpriteBatch, replacing GDI+.
/// Each player gets a viewport scaled from the 320×400 arena.
module FsRocket.Renderer

open System
open Microsoft.Xna.Framework
open Microsoft.Xna.Framework.Graphics
open FsRocket.Physics
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types
open FsRocket.Entities

// ─── Color helper (all-int constructor including alpha) ───
/// Create a Color from int r, g, b, a values (0-255)
let inline rgba r g b a = Color(int r, int g, int b, int a)

// ─── Scale factor (3× 320×200) ───────────────────────────
let Scale = 3

/// Extra zoom factor for terrain bitmaps
let TerrainZoom = 1.25

// ─── Color palette (MonoGame Color equivalents) ──────────────────
let playerColors = [|
    Color(0x5F, 0x5F, 0xFF)   // Player 1: Blue
    Color(0x3F, 0xCF, 0x3F)   // Player 2: Green
    Color(0xFF, 0x3F, 0x3F)   // Player 3: Red
    Color(0xFF, 0xFF, 0x3F)   // Player 4: Yellow
|]

let playerDarkColors = [|
    Color(0x2F, 0x2F, 0x9F)
    Color(0x1F, 0x7F, 0x1F)
    Color(0x9F, 0x1F, 0x1F)
    Color(0x9F, 0x9F, 0x1F)
|]

let bgColor        = Color(0x00, 0x00, 0x00)
let wallColor      = Color(0x40, 0x40, 0x50)
let gridColor      = Color(0x18, 0x18, 0x24)
let bulletColor    = Color(0xFF, 0xFF, 0x80)
let mineColorVal   = Color(0xFF, 0x60, 0x20)
let nukeColor      = Color(0xFF, 0xFF, 0xFF)
let laserColor     = Color(0xFF, 0x20, 0x20)
let shieldColor    = Color(0x80, 0xC0, 0xFF)
let empColor       = Color(0xC0, 0x40, 0xFF)
let flameColor     = Color(0xFF, 0x80, 0x20)
let hudBgColor     = Color(0x08, 0x08, 0x10)

// ─── Standard VGA Mode 13h Default Palette (256 colors) ────────────────
let private buildVgaPalette () : int array =
    let pal = Array.zeroCreate<int> 256
    let cga = [|
        (0x00, 0x00, 0x00); (0x00, 0x00, 0xAA); (0x00, 0xAA, 0x00); (0x00, 0xAA, 0xAA)
        (0xAA, 0x00, 0x00); (0xAA, 0x00, 0xAA); (0xAA, 0x55, 0x00); (0xAA, 0xAA, 0xAA)
        (0x55, 0x55, 0x55); (0x55, 0x55, 0xFF); (0x55, 0xFF, 0x55); (0x55, 0xFF, 0xFF)
        (0xFF, 0x55, 0x55); (0xFF, 0x55, 0xFF); (0xFF, 0xFF, 0x55); (0xFF, 0xFF, 0xFF)
    |]
    for i in 0..15 do
        let r, g, b = cga[i]
        pal[i] <- (0xFF <<< 24) ||| (r <<< 16) ||| (g <<< 8) ||| b
    for r in 0..5 do
        for g in 0..5 do
            for b in 0..5 do
                let idx = 16 + r * 36 + g * 6 + b
                let rv = r * 51
                let gv = g * 51
                let bv = b * 51
                pal[idx] <- (0xFF <<< 24) ||| (rv <<< 16) ||| (gv <<< 8) ||| bv
    for i in 0..23 do
        let v = i * 255 / 23
        pal[232 + i] <- (0xFF <<< 24) ||| (v <<< 16) ||| (v <<< 8) ||| v
    pal

let vgaPalette = buildVgaPalette ()

// ─── Embedded Bitmap Font (5×7 pixels per character, ASCII 32-127) ─────
// Standard 5×7 LED/LCD font, column-major encoding.
// Each character is 5 bytes; each byte is one column (LSB = top row).
let private fontColumns : byte[] = [|
    // ASCII 32 (space) through ASCII 127 (DEL)
    0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy  // 32 ' '
    0x00uy; 0x00uy; 0x5Fuy; 0x00uy; 0x00uy  // 33 '!'
    0x00uy; 0x07uy; 0x00uy; 0x07uy; 0x00uy  // 34 '"'
    0x14uy; 0x7Fuy; 0x14uy; 0x7Fuy; 0x14uy  // 35 '#'
    0x24uy; 0x2Auy; 0x7Fuy; 0x2Auy; 0x12uy  // 36 '$'
    0x23uy; 0x13uy; 0x08uy; 0x64uy; 0x62uy  // 37 '%'
    0x36uy; 0x49uy; 0x55uy; 0x22uy; 0x50uy  // 38 '&'
    0x00uy; 0x05uy; 0x03uy; 0x00uy; 0x00uy  // 39 '''
    0x00uy; 0x1Cuy; 0x22uy; 0x41uy; 0x00uy  // 40 '('
    0x00uy; 0x41uy; 0x22uy; 0x1Cuy; 0x00uy  // 41 ')'
    0x14uy; 0x08uy; 0x3Euy; 0x08uy; 0x14uy  // 42 '*'
    0x08uy; 0x08uy; 0x3Euy; 0x08uy; 0x08uy  // 43 '+'
    0x00uy; 0x50uy; 0x30uy; 0x00uy; 0x00uy  // 44 ','
    0x08uy; 0x08uy; 0x08uy; 0x08uy; 0x08uy  // 45 '-'
    0x00uy; 0x60uy; 0x60uy; 0x00uy; 0x00uy  // 46 '.'
    0x20uy; 0x10uy; 0x08uy; 0x04uy; 0x02uy  // 47 '/'
    0x3Euy; 0x51uy; 0x49uy; 0x45uy; 0x3Euy  // 48 '0'
    0x00uy; 0x42uy; 0x7Fuy; 0x40uy; 0x00uy  // 49 '1'
    0x42uy; 0x61uy; 0x51uy; 0x49uy; 0x46uy  // 50 '2'
    0x21uy; 0x41uy; 0x45uy; 0x4Buy; 0x31uy  // 51 '3'
    0x18uy; 0x14uy; 0x12uy; 0x7Fuy; 0x10uy  // 52 '4'
    0x27uy; 0x45uy; 0x45uy; 0x45uy; 0x39uy  // 53 '5'
    0x3Cuy; 0x4Auy; 0x49uy; 0x49uy; 0x30uy  // 54 '6'
    0x01uy; 0x71uy; 0x09uy; 0x05uy; 0x03uy  // 55 '7'
    0x36uy; 0x49uy; 0x49uy; 0x49uy; 0x36uy  // 56 '8'
    0x06uy; 0x49uy; 0x49uy; 0x29uy; 0x1Euy  // 57 '9'
    0x00uy; 0x36uy; 0x36uy; 0x00uy; 0x00uy  // 58 ':'
    0x00uy; 0x56uy; 0x36uy; 0x00uy; 0x00uy  // 59 ';'
    0x08uy; 0x14uy; 0x22uy; 0x41uy; 0x00uy  // 60 '<'
    0x14uy; 0x14uy; 0x14uy; 0x14uy; 0x14uy  // 61 '='
    0x00uy; 0x41uy; 0x22uy; 0x14uy; 0x08uy  // 62 '>'
    0x02uy; 0x01uy; 0x51uy; 0x09uy; 0x06uy  // 63 '?'
    0x32uy; 0x49uy; 0x79uy; 0x41uy; 0x3Euy  // 64 '@'
    0x7Euy; 0x11uy; 0x11uy; 0x11uy; 0x7Euy  // 65 'A'
    0x7Fuy; 0x49uy; 0x49uy; 0x49uy; 0x36uy  // 66 'B'
    0x3Euy; 0x41uy; 0x41uy; 0x41uy; 0x22uy  // 67 'C'
    0x7Fuy; 0x41uy; 0x41uy; 0x22uy; 0x1Cuy  // 68 'D'
    0x7Fuy; 0x49uy; 0x49uy; 0x49uy; 0x41uy  // 69 'E'
    0x7Fuy; 0x09uy; 0x09uy; 0x09uy; 0x01uy  // 70 'F'
    0x3Euy; 0x41uy; 0x49uy; 0x49uy; 0x7Auy  // 71 'G'
    0x7Fuy; 0x08uy; 0x08uy; 0x08uy; 0x7Fuy  // 72 'H'
    0x00uy; 0x41uy; 0x7Fuy; 0x41uy; 0x00uy  // 73 'I'
    0x20uy; 0x40uy; 0x41uy; 0x3Fuy; 0x01uy  // 74 'J'
    0x7Fuy; 0x08uy; 0x14uy; 0x22uy; 0x41uy  // 75 'K'
    0x7Fuy; 0x40uy; 0x40uy; 0x40uy; 0x40uy  // 76 'L'
    0x7Fuy; 0x02uy; 0x0Cuy; 0x02uy; 0x7Fuy  // 77 'M'
    0x7Fuy; 0x04uy; 0x08uy; 0x10uy; 0x7Fuy  // 78 'N'
    0x3Euy; 0x41uy; 0x41uy; 0x41uy; 0x3Euy  // 79 'O'
    0x7Fuy; 0x09uy; 0x09uy; 0x09uy; 0x06uy  // 80 'P'
    0x3Euy; 0x41uy; 0x51uy; 0x21uy; 0x5Euy  // 81 'Q'
    0x7Fuy; 0x09uy; 0x19uy; 0x29uy; 0x46uy  // 82 'R'
    0x46uy; 0x49uy; 0x49uy; 0x49uy; 0x31uy  // 83 'S'
    0x01uy; 0x01uy; 0x7Fuy; 0x01uy; 0x01uy  // 84 'T'
    0x3Fuy; 0x40uy; 0x40uy; 0x40uy; 0x3Fuy  // 85 'U'
    0x1Fuy; 0x20uy; 0x40uy; 0x20uy; 0x1Fuy  // 86 'V'
    0x3Fuy; 0x40uy; 0x38uy; 0x40uy; 0x3Fuy  // 87 'W'
    0x63uy; 0x14uy; 0x08uy; 0x14uy; 0x63uy  // 88 'X'
    0x07uy; 0x08uy; 0x70uy; 0x08uy; 0x07uy  // 89 'Y'
    0x61uy; 0x51uy; 0x49uy; 0x45uy; 0x43uy  // 90 'Z'
    0x00uy; 0x7Fuy; 0x41uy; 0x41uy; 0x00uy  // 91 '['
    0x02uy; 0x04uy; 0x08uy; 0x10uy; 0x20uy  // 92 '\'
    0x00uy; 0x41uy; 0x41uy; 0x7Fuy; 0x00uy  // 93 ']'
    0x04uy; 0x02uy; 0x01uy; 0x02uy; 0x04uy  // 94 '^'
    0x40uy; 0x40uy; 0x40uy; 0x40uy; 0x40uy  // 95 '_'
    0x00uy; 0x01uy; 0x02uy; 0x04uy; 0x00uy  // 96 '`'
    0x20uy; 0x54uy; 0x54uy; 0x54uy; 0x78uy  // 97 'a'
    0x7Fuy; 0x48uy; 0x44uy; 0x44uy; 0x38uy  // 98 'b'
    0x38uy; 0x44uy; 0x44uy; 0x44uy; 0x20uy  // 99 'c'
    0x38uy; 0x44uy; 0x44uy; 0x48uy; 0x7Fuy  // 100 'd'
    0x38uy; 0x54uy; 0x54uy; 0x54uy; 0x18uy  // 101 'e'
    0x08uy; 0x7Euy; 0x09uy; 0x01uy; 0x02uy  // 102 'f'
    0x0Cuy; 0x52uy; 0x52uy; 0x52uy; 0x3Euy  // 103 'g'
    0x7Fuy; 0x08uy; 0x04uy; 0x04uy; 0x78uy  // 104 'h'
    0x00uy; 0x44uy; 0x7Duy; 0x40uy; 0x00uy  // 105 'i'
    0x20uy; 0x40uy; 0x44uy; 0x3Duy; 0x00uy  // 106 'j'
    0x7Fuy; 0x10uy; 0x28uy; 0x44uy; 0x00uy  // 107 'k'
    0x00uy; 0x41uy; 0x7Fuy; 0x40uy; 0x00uy  // 108 'l'
    0x7Cuy; 0x04uy; 0x18uy; 0x04uy; 0x78uy  // 109 'm'
    0x7Cuy; 0x08uy; 0x04uy; 0x04uy; 0x78uy  // 110 'n'
    0x38uy; 0x44uy; 0x44uy; 0x44uy; 0x38uy  // 111 'o'
    0x7Cuy; 0x14uy; 0x14uy; 0x14uy; 0x08uy  // 112 'p'
    0x08uy; 0x14uy; 0x14uy; 0x18uy; 0x7Cuy  // 113 'q'
    0x7Cuy; 0x08uy; 0x04uy; 0x04uy; 0x08uy  // 114 'r'
    0x48uy; 0x54uy; 0x54uy; 0x54uy; 0x20uy  // 115 's'
    0x04uy; 0x3Fuy; 0x44uy; 0x40uy; 0x20uy  // 116 't'
    0x3Cuy; 0x40uy; 0x40uy; 0x20uy; 0x7Cuy  // 117 'u'
    0x1Cuy; 0x20uy; 0x40uy; 0x20uy; 0x1Cuy  // 118 'v'
    0x3Cuy; 0x40uy; 0x30uy; 0x40uy; 0x3Cuy  // 119 'w'
    0x44uy; 0x28uy; 0x10uy; 0x28uy; 0x44uy  // 120 'x'
    0x0Cuy; 0x50uy; 0x50uy; 0x50uy; 0x3Cuy  // 121 'y'
    0x44uy; 0x64uy; 0x54uy; 0x4Cuy; 0x44uy  // 122 'z'
    0x00uy; 0x08uy; 0x36uy; 0x41uy; 0x00uy  // 123 '{'
    0x00uy; 0x00uy; 0x7Fuy; 0x00uy; 0x00uy  // 124 '|'
    0x00uy; 0x41uy; 0x36uy; 0x08uy; 0x00uy  // 125 '}'
    0x10uy; 0x08uy; 0x08uy; 0x10uy; 0x08uy  // 126 '~'
    0x00uy; 0x7Fuy; 0x41uy; 0x7Fuy; 0x00uy  // 127 DEL (rendered as box)
|]

// ─── Render State (created once at initialization) ─────────────────────

type RenderResources =
    { SpriteBatch: SpriteBatch
      Pixel: Texture2D              // 1×1 white pixel for rects/lines
      CircleFilled: Texture2D       // 64×64 filled white circle
      CircleOutline: Texture2D      // 64×64 circle outline
      FontTexture: Texture2D        // Bitmap font atlas
      BasicEffect: BasicEffect      // For polygon rendering
      ScissorRasterizer: RasterizerState
      mutable TerrainTexture: Texture2D option
      mutable TerrainLevelName: string }

// ─── Texture creation helpers ─────────────────────────────────────────

/// Create a 1×1 white pixel texture
let createPixelTexture (device: GraphicsDevice) =
    let tex = new Texture2D(device, 1, 1)
    tex.SetData([| Color.White |])
    tex

/// Create a filled circle texture (diameter × diameter)
let createCircleFilledTexture (device: GraphicsDevice) (diameter: int) =
    let tex = new Texture2D(device, diameter, diameter)
    let r = float diameter / 2.0
    let data = Array.init (diameter * diameter) (fun i ->
        let x = float (i % diameter) - r + 0.5
        let y = float (i / diameter) - r + 0.5
        if x * x + y * y <= r * r then Color.White else Color.Transparent
    )
    tex.SetData(data)
    tex

/// Create a circle outline texture (diameter × diameter)
let createCircleOutlineTexture (device: GraphicsDevice) (diameter: int) (thickness: float) =
    let tex = new Texture2D(device, diameter, diameter)
    let r = float diameter / 2.0
    let data = Array.init (diameter * diameter) (fun i ->
        let x = float (i % diameter) - r + 0.5
        let y = float (i / diameter) - r + 0.5
        let dist = sqrt (x * x + y * y)
        if dist <= r && dist >= r - thickness then Color.White else Color.Transparent
    )
    tex.SetData(data)
    tex

/// Create the bitmap font atlas texture
let createFontTexture (device: GraphicsDevice) =
    let charW = 6   // 5 pixels + 1 spacing
    let charH = 8   // 7 pixels + 1 spacing
    let numChars = 96  // ASCII 32-127
    let atlasW = charW * numChars
    let atlasH = charH
    let tex = new Texture2D(device, atlasW, atlasH)
    let data = Array.create (atlasW * atlasH) Color.Transparent
    for ci in 0..numChars-1 do
        let baseIdx = ci * 5
        for col in 0..4 do
            let colByte = fontColumns[baseIdx + col]
            for row in 0..6 do
                if colByte &&& (1uy <<< row) <> 0uy then
                    let px = ci * charW + col
                    let py = row
                    data[py * atlasW + px] <- Color.White
    tex.SetData(data)
    tex

// ─── Initialize render resources ──────────────────────────────────────

let initRenderResources (device: GraphicsDevice) : RenderResources =
    let basicEffect = new BasicEffect(device)
    basicEffect.VertexColorEnabled <- true
    basicEffect.TextureEnabled <- false
    { SpriteBatch = new SpriteBatch(device)
      Pixel = createPixelTexture device
      CircleFilled = createCircleFilledTexture device 64
      CircleOutline = createCircleOutlineTexture device 64 3.0
      FontTexture = createFontTexture device
      BasicEffect = basicEffect
      ScissorRasterizer = new RasterizerState(ScissorTestEnable = true, CullMode = CullMode.None)
      TerrainTexture = None
      TerrainLevelName = "" }

// ─── Drawing Helpers ──────────────────────────────────────────────────

let inline drawRect (sb: SpriteBatch) (pixel: Texture2D) (x: int) (y: int) (w: int) (h: int) (color: Color) =
    sb.Draw(pixel, Rectangle(x, y, w, h), color)

let drawFilledCircle (sb: SpriteBatch) (circle: Texture2D) (cx: int) (cy: int) (radius: int) (color: Color) =
    let d = radius * 2
    sb.Draw(circle, Rectangle(cx - radius, cy - radius, d, d), color)

let drawCircleOutline (sb: SpriteBatch) (ring: Texture2D) (cx: int) (cy: int) (radius: int) (color: Color) =
    let d = radius * 2
    sb.Draw(ring, Rectangle(cx - radius, cy - radius, d, d), color)

let drawLine (sb: SpriteBatch) (pixel: Texture2D) (x1: int) (y1: int) (x2: int) (y2: int) (color: Color) (thickness: float32) =
    let dx = float32 (x2 - x1)
    let dy = float32 (y2 - y1)
    let length = sqrt (dx * dx + dy * dy)
    let angle = atan2 dy dx
    sb.Draw(pixel, Vector2(float32 x1, float32 y1), System.Nullable(), color, angle,
            Vector2.Zero, Vector2(length, thickness), SpriteEffects.None, 0.0f)

let drawFilledTriangle (device: GraphicsDevice) (effect: BasicEffect)
                       (x1: int) (y1: int) (x2: int) (y2: int) (x3: int) (y3: int) (color: Color) =
    let vertices = [|
        VertexPositionColor(Vector3(float32 x1, float32 y1, 0.0f), color)
        VertexPositionColor(Vector3(float32 x2, float32 y2, 0.0f), color)
        VertexPositionColor(Vector3(float32 x3, float32 y3, 0.0f), color)
    |]
    for pass in effect.CurrentTechnique.Passes do
        pass.Apply()
        device.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, 1)

let drawFilledQuad (device: GraphicsDevice) (effect: BasicEffect)
                   (x1: int) (y1: int) (x2: int) (y2: int) (x3: int) (y3: int) (x4: int) (y4: int) (color: Color) =
    let vertices = [|
        VertexPositionColor(Vector3(float32 x1, float32 y1, 0.0f), color)
        VertexPositionColor(Vector3(float32 x2, float32 y2, 0.0f), color)
        VertexPositionColor(Vector3(float32 x3, float32 y3, 0.0f), color)
        VertexPositionColor(Vector3(float32 x1, float32 y1, 0.0f), color)
        VertexPositionColor(Vector3(float32 x3, float32 y3, 0.0f), color)
        VertexPositionColor(Vector3(float32 x4, float32 y4, 0.0f), color)
    |]
    for pass in effect.CurrentTechnique.Passes do
        pass.Apply()
        device.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, 2)

// ─── Text rendering (bitmap font) ────────────────────────────────────

let private charW = 6
let private charH = 8

let drawText (sb: SpriteBatch) (fontTex: Texture2D) (text: string) (x: int) (y: int) (color: Color) (scale: int) =
    let mutable cx = x
    for ch in text do
        let ci = int ch - 32
        if ci >= 0 && ci < 96 then
            let srcRect = Rectangle(ci * charW, 0, charW, charH)
            sb.Draw(fontTex, Rectangle(cx, y, charW * scale, charH * scale), System.Nullable srcRect, color)
        cx <- cx + charW * scale

let measureText (text: string) (scale: int) =
    text.Length * charW * scale, charH * scale

// ─── Terrain Texture ──────────────────────────────────────────────────

/// Build a Texture2D from terrain pixel data using the VGA palette
let buildTerrainTexture (device: GraphicsDevice) (level: LevelData) : Texture2D =
    let tex = new Texture2D(device, MapWidth, MapHeight)
    let data = Array.init (MapWidth * MapHeight) (fun i ->
        let pixel = int level.Pixels[i]
        let argb = vgaPalette[pixel]
        let a = (argb >>> 24) &&& 0xFF
        let r = (argb >>> 16) &&& 0xFF
        let g = (argb >>> 8) &&& 0xFF
        let b = argb &&& 0xFF
        Color(r, g, b, a)
    )
    tex.SetData(data)
    tex

/// Get or create cached terrain texture
let getTerrainTexture (res: RenderResources) (device: GraphicsDevice) (level: LevelData) (terrainDirty: bool) : Texture2D =
    if res.TerrainLevelName <> level.Name || terrainDirty then
        res.TerrainTexture |> Option.iter (fun t -> t.Dispose())
        let tex = buildTerrainTexture device level
        res.TerrainTexture <- Some tex
        res.TerrainLevelName <- level.Name
        tex
    else
        res.TerrainTexture.Value

// ─── Layout: viewports for 1-4 players ─────────────────────────────────

let viewportLayout (numPlayers: int) (windowW: int) (windowH: int) =
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

let drawPlayerView (res: RenderResources) (device: GraphicsDevice) (gs: GameState)
                   (playerIdx: int) (vx: int) (vy: int) (vw: int) (vh: int) =
    let hudH = 36
    let gameH = vh - hudH
    let p = gs.Players[playerIdx]
    let sb = res.SpriteBatch

    // Set scissor rectangle for viewport clipping
    device.ScissorRectangle <- Rectangle(vx, vy, vw, vh)

    // Background
    drawRect sb res.Pixel vx vy vw gameH bgColor

    // Calculate view offset (center on player)
    let effectiveScaleF = float Scale * TerrainZoom
    let camX = p.PosX / PositionScale - float vw / (2.0 * effectiveScaleF)
    let camY = p.PosY / PositionScale - float gameH / (2.0 * effectiveScaleF)

    let toScreenX (wx: float) = vx + int ((wx - camX) * effectiveScaleF)
    let toScreenY (wy: float) = vy + int ((wy - camY) * effectiveScaleF)

    match gs.Level with
    | Some level ->
        // Draw terrain texture (scaled with extra zoom)
        let tbmp = getTerrainTexture res device level gs.TerrainDirty
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
            sb.Draw(tbmp, Rectangle(destX, destY, destW, destH),
                    System.Nullable(Rectangle(srcX, srcY, srcW, srcH)), Color.White)
    | None ->
        // No terrain: draw grid + hardcoded walls
        let startGX = int (floor (camX / 32.0)) * 32
        let startGY = int (floor (camY / 32.0)) * 32
        for gx in startGX .. 32 .. startGX + int (float vw / effectiveScaleF) + 32 do
            let sx = toScreenX (float gx)
            if sx >= vx && sx <= vx + vw then
                drawLine sb res.Pixel sx vy sx (vy + gameH) gridColor 1.0f
        for gy in startGY .. 32 .. startGY + int (float gameH / effectiveScaleF) + 32 do
            let sy = toScreenY (float gy)
            if sy >= vy && sy <= vy + gameH then
                drawLine sb res.Pixel vx sy (vx + vw) sy gridColor 1.0f

        // Arena boundary walls
        let wallThick = float32 (2 * Scale)
        let wx0 = toScreenX 0.0
        let wy0 = toScreenY 0.0
        let wx1 = toScreenX ArenaWidth
        let wy1 = toScreenY ArenaHeight
        drawLine sb res.Pixel wx0 wy0 wx1 wy0 wallColor wallThick
        drawLine sb res.Pixel wx1 wy0 wx1 wy1 wallColor wallThick
        drawLine sb res.Pixel wx1 wy1 wx0 wy1 wallColor wallThick
        drawLine sb res.Pixel wx0 wy1 wx0 wy0 wallColor wallThick

        // Interior arena walls
        let wallFillColor = Color(0x50, 0x50, 0x68)
        let wallHighlight = Color(0x68, 0x68, 0x80)
        let wallShadow = Color(0x30, 0x30, 0x40)
        for w in arenaWalls do
            let swx = toScreenX w.X
            let swy = toScreenY w.Y
            let sww = int (w.W * effectiveScaleF)
            let swh = int (w.H * effectiveScaleF)
            drawRect sb res.Pixel swx swy sww swh wallFillColor
            drawLine sb res.Pixel swx swy (swx + sww) swy wallHighlight 1.0f
            drawLine sb res.Pixel swx swy swx (swy + swh) wallHighlight 1.0f
            drawLine sb res.Pixel (swx + sww) swy (swx + sww) (swy + swh) wallShadow 1.0f
            drawLine sb res.Pixel swx (swy + swh) (swx + sww) (swy + swh) wallShadow 1.0f

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
                    let ringColor = rgba (int c.R) (int c.G) (int c.B) alpha
                    drawCircleOutline sb res.CircleOutline ex ey ringR ringColor
                    // Inner glow ring
                    let innerR = ringR - Scale * 2
                    if innerR > 0 then
                        let innerColor = rgba 255 255 255 (alpha / 2)
                        drawCircleOutline sb res.CircleOutline ex ey innerR innerColor

            | EntityType.Nuke ->
                // Nuke: expanding glow + flash
                let nukeR = max 2 (int (ent.Radius * float Scale))
                let glowAlpha = max 20 (180 - int (ent.Radius * 2.0))
                let glowColor = rgba 0xFF 0xFF 0x80 glowAlpha
                drawFilledCircle sb res.CircleFilled ex ey nukeR glowColor
                // Core
                let coreR = max 1 (nukeR / 3)
                let coreColor = rgba 255 255 255 (min 255 (glowAlpha + 60))
                drawFilledCircle sb res.CircleFilled ex ey coreR coreColor
                // Ring outline
                let nukeOutlineColor = rgba 0xFF 0xA0 0x00 glowAlpha
                drawCircleOutline sb res.CircleOutline ex ey nukeR nukeOutlineColor

            | EntityType.Blackhole ->
                // Blackhole: swirling gravity well
                let bhR = max 3 (int (ent.Radius * float Scale))
                // Dark core
                let coreColor = Color(0x05, 0x00, 0x10, 0xE0)
                drawFilledCircle sb res.CircleFilled ex ey bhR coreColor
                // Swirl arms (4 spiral arms rotating)
                let swirlAngle = float ent.Timer * 6.0
                for arm in 0..3 do
                    let baseAngle = degToRad (swirlAngle + float arm * 90.0)
                    let armColor = Color(0x40, 0x00, 0xC0, 0x80)
                    let r1 = float bhR * 0.4
                    let r2 = float bhR * 1.2
                    let r3 = float bhR * 2.0
                    let x1 = ex + int (cos baseAngle * r1)
                    let y1 = ey + int (sin baseAngle * r1)
                    let x2 = ex + int (cos (baseAngle + 0.4) * r2)
                    let y2 = ey + int (sin (baseAngle + 0.4) * r2)
                    let x3 = ex + int (cos (baseAngle + 0.8) * r3)
                    let y3 = ey + int (sin (baseAngle + 0.8) * r3)
                    drawLine sb res.Pixel x1 y1 x2 y2 armColor (float32 Scale)
                    drawLine sb res.Pixel x2 y2 x3 y3 armColor (float32 Scale)
                // Outer pull ring
                let pullR = int (blackholeRadius * float Scale)
                let pullAlpha = 20 + int (10.0 * sin (float ent.Timer * 0.15))
                let pullColor = rgba 0x80 0x40 0xFF pullAlpha
                drawCircleOutline sb res.CircleOutline ex ey pullR pullColor

            | EntityType.Mine ->
                // Mine: pulsing circle, brighter when armed
                let armed = ent.Timer > 0
                let pulse = if armed then 0.5 + 0.5 * sin (float ent.Timer * 0.2) else 0.3
                let sz = int (float (4 * Scale) * (0.8 + 0.2 * pulse))
                let alpha = int (128.0 + 127.0 * pulse)
                let mColor = if armed then rgba 0xFF 0x40 0x10 alpha else Color(0x80, 0x40, 0x10, 0x80)
                drawFilledCircle sb res.CircleFilled ex ey (sz / 2) mColor
                // Cross-hairs on armed mines
                if armed then
                    let crossColor = rgba 0xFF 0xFF 0x40 alpha
                    drawLine sb res.Pixel (ex - sz/2) ey (ex + sz/2) ey crossColor 1.0f
                    drawLine sb res.Pixel ex (ey - sz/2) ex (ey + sz/2) crossColor 1.0f

            | EntityType.Flame ->
                // Flame: fading orange-red glow
                let fTimer = max 1 (abs ent.Timer)
                let fSize = max 2 (fTimer * Scale / 3)
                let fAlpha = max 40 (220 - fTimer * 3)
                let r = min 255 (0xFF - fTimer)
                let gv = max 0 (0x80 - fTimer * 2)
                let fColor = rgba r gv 0x10 fAlpha
                drawFilledCircle sb res.CircleFilled ex ey (fSize / 2) fColor
                // Bright core
                let coreS = max 1 (fSize / 3)
                let coreColor = rgba 0xFF 0xC0 0x40 (min 255 (fAlpha + 30))
                drawFilledCircle sb res.CircleFilled ex ey (coreS / 2) coreColor

            | EntityType.Heavy ->
                // Missile/dumbfire: directional shape with exhaust glow
                // End SpriteBatch to draw polygon
                sb.End()

                let sz = 4 * Scale
                let c = if ent.WeaponIdx = WeaponType.Missile then Color(0xFF, 0xC0, 0x30)
                        elif ent.WeaponIdx = WeaponType.AtomWeapon then Color(0x40, 0xFF, 0x40)
                        else Color.Orange
                let speed = sqrt (ent.VelX * ent.VelX + ent.VelY * ent.VelY) + 0.01
                let dirX = ent.VelX / speed
                let dirY = ent.VelY / speed
                let noseX = ex + int (dirX * float sz)
                let noseY = ey + int (dirY * float sz)
                let tailX = ex - int (dirX * float sz * 0.6)
                let tailY = ey - int (dirY * float sz * 0.6)
                let sideX = int (-dirY * float sz * 0.4)
                let sideY = int (dirX * float sz * 0.4)

                drawFilledQuad device res.BasicEffect
                    noseX noseY
                    (ex + sideX) (ey + sideY)
                    tailX tailY
                    (ex - sideX) (ey - sideY)
                    c

                // Resume SpriteBatch
                sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
                         SamplerState.PointClamp, null, res.ScissorRasterizer)

                // Exhaust glow
                let exhColor = Color(0xFF, 0x60, 0x10, 0x80)
                let exhX = ex - int (dirX * float sz * 0.8)
                let exhY = ey - int (dirY * float sz * 0.8)
                let exhS = Scale
                drawFilledCircle sb res.CircleFilled exhX exhY exhS exhColor

            | EntityType.EMP ->
                // EMP: oscillating purple-white sparkle
                let empR = max 2 (int (ent.Radius * float Scale))
                let empAlpha = 140 + int (80.0 * sin (float ent.Timer * 0.3))
                let empBrColor = rgba 0xC0 0x40 0xFF empAlpha
                drawFilledCircle sb res.CircleFilled ex ey empR empBrColor
                // Sparkle cross
                let sparkLen = empR + Scale
                let sparkColor = rgba 0xFF 0xFF 0xFF empAlpha
                let sparkAngle = float ent.Timer * 8.0
                let sr = degToRad sparkAngle
                let sx1 = ex + int (cos sr * float sparkLen)
                let sy1 = ey + int (sin sr * float sparkLen)
                let sx2 = ex - int (cos sr * float sparkLen)
                let sy2 = ey - int (sin sr * float sparkLen)
                drawLine sb res.Pixel sx1 sy1 sx2 sy2 sparkColor 1.0f
                let sr2 = sr + Math.PI / 2.0
                let sx3 = ex + int (cos sr2 * float sparkLen)
                let sy3 = ey + int (sin sr2 * float sparkLen)
                let sx4 = ex - int (cos sr2 * float sparkLen)
                let sy4 = ey - int (sin sr2 * float sparkLen)
                drawLine sb res.Pixel sx3 sy3 sx4 sy4 sparkColor 1.0f

            | EntityType.Laser ->
                // Laser: elongated red line
                let speed = sqrt (ent.VelX * ent.VelX + ent.VelY * ent.VelY) + 0.01
                let dirX = ent.VelX / speed
                let dirY = ent.VelY / speed
                let laserLen = 6 * Scale
                let lx2 = ex - int (dirX * float laserLen)
                let ly2 = ey - int (dirY * float laserLen)
                drawLine sb res.Pixel ex ey lx2 ly2 laserColor (float32 (Scale + 1))
                // Bright tip
                let tipColor = Color(0xFF, 0x80, 0x80)
                drawFilledCircle sb res.CircleFilled ex ey Scale tipColor

            | EntityType.Ricochet ->
                // Ricochet: cyan with bounce count indicator
                let rSize = 2 * Scale
                let bounce = ent.SubType
                let rAlpha = max 100 (255 - bounce * 50)
                let rColor = rgba 0x00 0xFF 0xFF rAlpha
                drawFilledCircle sb res.CircleFilled ex ey (rSize / 2) rColor
                // Fading trail dot
                let trailX = ex - int (ent.VelX * float Scale)
                let trailY = ey - int (ent.VelY * float Scale)
                let trailColor = rgba 0x00 0xFF 0xFF (rAlpha / 3)
                drawFilledCircle sb res.CircleFilled trailX trailY (Scale / 2) trailColor

            | EntityType.Exploding ->
                // Dirtclod: brown-ish lobbed projectile
                let dSize = 3 * Scale
                let dirtColor = Color(0x8B, 0x60, 0x20)
                drawFilledCircle sb res.CircleFilled ex ey (dSize / 2) dirtColor
                // Trail
                let tx = ex - int (ent.VelX * float Scale * 0.5)
                let ty = ey - int (ent.VelY * float Scale * 0.5)
                let trailColor = Color(0x60, 0x40, 0x10, 0x80)
                drawFilledCircle sb res.CircleFilled tx ty (Scale / 2) trailColor

            | EntityType.Shrapnel ->
                // Shrapnel: tiny gray dots
                let sAlpha = max 60 (200 - ent.Timer * 5)
                let sColor = Color(128, 128, 128, sAlpha)
                drawRect sb res.Pixel (ex - Scale/2) (ey - Scale/2) Scale Scale sColor

            | EntityType.Shield ->
                // Orbiter/freezer: blue-white spinning
                let sSize = 4 * Scale
                let sAngle = float ent.Timer * 5.0
                let sAlpha = 160 + int (60.0 * sin (degToRad sAngle))
                let sColor = rgba 0x80 0xC0 0xFF sAlpha
                drawFilledCircle sb res.CircleFilled ex ey (sSize / 2) sColor

            | _ ->
                // Default bullet rendering
                let size = 2 * Scale
                drawFilledCircle sb res.CircleFilled ex ey (size / 2) bulletColor

    // Draw particles (death debris, explosion fragments)
    for part in gs.Particles do
        let px = toScreenX part.X
        let py = toScreenY part.Y
        if px > vx - 5 && px < vx + vw + 5 && py > vy - 5 && py < vy + gameH + 5 then
            let alpha = min 255 (part.Life * 8)
            let c = playerColors[part.Color % 4]
            let partColor = rgba (int c.R) (int c.G) (int c.B) alpha
            let sz = max 1 (Scale * part.Life / 10)
            drawRect sb res.Pixel (px - sz/2) (py - sz/2) sz sz partColor

    // Draw players — end SpriteBatch for polygon rendering
    sb.End()

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

                // Fill triangle
                let pColor = playerColors[i % 4]
                drawFilledTriangle device res.BasicEffect nosX nosY r1x r1y r2x r2y pColor

                // Outline
                let dColor = playerDarkColors[i % 4]
                // Use a temporary SpriteBatch for lines
                res.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
                                      SamplerState.PointClamp, null, res.ScissorRasterizer)
                drawLine res.SpriteBatch res.Pixel nosX nosY r1x r1y dColor 1.0f
                drawLine res.SpriteBatch res.Pixel r1x r1y r2x r2y dColor 1.0f
                drawLine res.SpriteBatch res.Pixel r2x r2y nosX nosY dColor 1.0f

                // Thrust flame
                if other.KeyUp then
                    let thrustRad = rad + Math.PI
                    let flLen = float (3 * Scale) + float (gs.GameTick % 3) * float Scale
                    let fx = ox + int (cos thrustRad * flLen)
                    let fy = oy - int (sin thrustRad * flLen)
                    let thrustColor = Color(0xFF, 0xA0, 0x20)
                    drawFilledCircle res.SpriteBatch res.CircleFilled fx fy Scale thrustColor

                // Shield bubble
                if other.Flags.HasFlag(PlayerFlags.Shield) then
                    let sr = 7 * Scale
                    drawCircleOutline res.SpriteBatch res.CircleOutline ox oy sr shieldColor

                // Stun effect
                if other.Flags.HasFlag(PlayerFlags.Stunned) then
                    for s in 0..2 do
                        let sRad = degToRad (other.AnimAngle + float s * 120.0)
                        let sx = ox + int (cos sRad * float (6 * Scale))
                        let sy = oy - int (sin sRad * float (6 * Scale))
                        drawFilledCircle res.SpriteBatch res.CircleFilled sx sy (Scale / 2) empColor

                // Invincibility flash
                if other.InvTimer > 0 && other.InvTimer % 4 < 2 then
                    drawCircleOutline res.SpriteBatch res.CircleOutline ox oy (6 * Scale) Color.White

                res.SpriteBatch.End()

    // Resume SpriteBatch for minimap and HUD
    sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
             SamplerState.PointClamp, null, res.ScissorRasterizer)

    // ─── Minimap (top-right corner of each viewport) ──────────────
    let mmW = min 80 (vw / 5)
    let mmH = int (float mmW * ArenaHeight / ArenaWidth)
    let mmX = vx + vw - mmW - 4
    let mmY = vy + 4
    let mmScaleX = float mmW / ArenaWidth
    let mmScaleY = float mmH / ArenaHeight

    // Background
    let mmBgColor = Color(0x08, 0x08, 0x10, 0xA0)
    drawRect sb res.Pixel mmX mmY mmW mmH mmBgColor

    // Terrain preview on minimap
    match gs.Level with
    | Some level ->
        let tbmp = getTerrainTexture res device level gs.TerrainDirty
        sb.Draw(tbmp, Rectangle(mmX, mmY, mmW, mmH),
                System.Nullable(Rectangle(0, 0, MapWidth, MapHeight)), Color.White)
    | None ->
        // Walls on minimap
        let mmWallColor = Color(0x60, 0x60, 0x80, 0x80)
        for w in arenaWalls do
            let mwx = mmX + int (w.X * mmScaleX)
            let mwy = mmY + int (w.Y * mmScaleY)
            let mww = max 1 (int (w.W * mmScaleX))
            let mwh = max 1 (int (w.H * mmScaleY))
            drawRect sb res.Pixel mwx mwy mww mwh mmWallColor

    // Minimap border
    let mmBorderColor = Color(0x40, 0x40, 0x60, 0x80)
    drawLine sb res.Pixel mmX mmY (mmX + mmW) mmY mmBorderColor 1.0f
    drawLine sb res.Pixel (mmX + mmW) mmY (mmX + mmW) (mmY + mmH) mmBorderColor 1.0f
    drawLine sb res.Pixel (mmX + mmW) (mmY + mmH) mmX (mmY + mmH) mmBorderColor 1.0f
    drawLine sb res.Pixel mmX (mmY + mmH) mmX mmY mmBorderColor 1.0f

    // Entities on minimap
    for ent in gs.Entities do
        let mex = mmX + int (ent.X * mmScaleX)
        let mey = mmY + int (ent.Y * mmScaleY)
        if mex >= mmX && mex <= mmX + mmW && mey >= mmY && mey <= mmY + mmH then
            let mc =
                match ent.EType with
                | EntityType.Nuke | EntityType.Expanding -> Color.White
                | EntityType.Blackhole -> Color.Purple
                | EntityType.Mine -> Color(0xFF, 0x80, 0x20)
                | EntityType.Flame -> Color(0xFF, 0x60, 0x10)
                | _ -> Color(0xA0, 0xA0, 0xA0)
            drawRect sb res.Pixel mex mey 1 1 mc

    // Players on minimap
    for i in 0..gs.NumPlayers-1 do
        let other = gs.Players[i]
        if other.Alive then
            let mpx = mmX + int (other.PosX / PositionScale * mmScaleX)
            let mpy = mmY + int (other.PosY / PositionScale * mmScaleY)
            drawRect sb res.Pixel (mpx - 1) (mpy - 1) 3 3 playerColors[i % 4]

    // Camera view rect on minimap
    let cvx = mmX + int (camX * mmScaleX)
    let cvy = mmY + int (camY * mmScaleY)
    let cvw = int (float vw / effectiveScaleF * mmScaleX)
    let cvh = int (float gameH / effectiveScaleF * mmScaleY)
    let cvColor = rgba (int playerColors[playerIdx % 4].R) (int playerColors[playerIdx % 4].G)
                      (int playerColors[playerIdx % 4].B) 0x60
    drawLine sb res.Pixel cvx cvy (cvx + cvw) cvy cvColor 1.0f
    drawLine sb res.Pixel (cvx + cvw) cvy (cvx + cvw) (cvy + cvh) cvColor 1.0f
    drawLine sb res.Pixel (cvx + cvw) (cvy + cvh) cvx (cvy + cvh) cvColor 1.0f
    drawLine sb res.Pixel cvx (cvy + cvh) cvx cvy cvColor 1.0f

    // ─── HUD bar at bottom ─────────────────────────────────────────
    device.ScissorRectangle <- Rectangle(vx, vy + gameH, vw, hudH)
    drawRect sb res.Pixel vx (vy + gameH) vw hudH hudBgColor

    let hudY = vy + gameH + 2
    let specialName = (getWeapon p.SpecialWeapon).Name

    // Player name and weapon
    let name = if p.IsCpu then $"CPU{playerIdx + 1}" else $"P{playerIdx + 1}"
    drawText sb res.FontTexture name (vx + 4) hudY playerColors[playerIdx % 4] 1

    // Health bar
    let healthPct = max 0.0 (float p.Health / float FullHealth)
    let barW = vw / 3
    let barH = 8
    let barX = vx + 30
    let barY = hudY + 2
    let healthBgColor = Color(0x30, 0x00, 0x00)
    drawRect sb res.Pixel barX barY barW barH healthBgColor
    let healthColor =
        if healthPct > 0.6 then Color.LimeGreen
        elif healthPct > 0.3 then Color.Yellow
        else Color.Red
    drawRect sb res.Pixel barX barY (int (float barW * healthPct)) barH healthColor

    // Cannon (main) + special weapon
    let info = $"CANNON | {specialName} [{p.Ammo}]"
    drawText sb res.FontTexture info (vx + 4) (hudY + 14) Color.White 1

    // Kill/Death count
    let kd = $"K:{p.KillCount} D:{p.DeathCount}"
    drawText sb res.FontTexture kd (barX + barW + 8) (hudY + 2) Color.White 1

    // Status indicators
    if not p.Alive then
        let deadOverlay = Color(0, 0, 0, 0xA0)
        drawRect sb res.Pixel vx vy vw gameH deadOverlay
        drawText sb res.FontTexture "DEAD" (vx + vw/2 - 16) (vy + gameH/2 - 5) Color.Red 2

    // Reset scissor
    device.ScissorRectangle <- Rectangle(0, 0, device.Viewport.Width, device.Viewport.Height)

// ─── Draw viewport border ──────────────────────────────────────────────

let drawBorder (sb: SpriteBatch) (pixel: Texture2D) (vx: int) (vy: int) (vw: int) (vh: int) (idx: int) =
    let c = playerDarkColors[idx % 4]
    drawLine sb pixel vx vy (vx + vw - 1) vy c 2.0f
    drawLine sb pixel (vx + vw - 1) vy (vx + vw - 1) (vy + vh - 1) c 2.0f
    drawLine sb pixel (vx + vw - 1) (vy + vh - 1) vx (vy + vh - 1) c 2.0f
    drawLine sb pixel vx (vy + vh - 1) vx vy c 2.0f

// ─── Render full frame ─────────────────────────────────────────────────

let renderFrame (res: RenderResources) (device: GraphicsDevice) (gs: GameState) (windowW: int) (windowH: int) =
    device.Clear(Color.Black)

    // Update BasicEffect projection for current window size
    res.BasicEffect.Projection <-
        Matrix.CreateOrthographicOffCenter(0.0f, float32 windowW, float32 windowH, 0.0f, 0.0f, 1.0f)
    res.BasicEffect.View <- Matrix.Identity
    res.BasicEffect.World <- Matrix.Identity

    let layouts = viewportLayout gs.NumPlayers windowW windowH

    for i in 0..gs.NumPlayers-1 do
        if i < layouts.Length then
            let (vx, vy, vw, vh) = layouts[i]
            // Begin SpriteBatch with scissor test enabled
            res.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
                                  SamplerState.PointClamp, null, res.ScissorRasterizer)
            drawPlayerView res device gs i vx vy vw vh
            res.SpriteBatch.End()

            // Draw border
            res.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
                                  SamplerState.PointClamp, null, null)
            drawBorder res.SpriteBatch res.Pixel vx vy vw vh i
            res.SpriteBatch.End()

    // Title bar overlay
    if not gs.RoundActive then
        res.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
                              SamplerState.PointClamp, null, null)
        let overlayColor = Color(0, 0, 0, 0xC0)
        drawRect res.SpriteBatch res.Pixel 0 0 windowW windowH overlayColor

        let cx = windowW / 2 - 200
        let mutable y = windowH / 2 - 80

        drawText res.SpriteBatch res.FontTexture "FsRocket Physics" cx y Color.White 3
        y <- y + 30

        let levelName = match gs.Level with Some lv -> lv.Name | None -> "No Terrain"
        let cpuText = if gs.CpuCount > 0 then $" | CPU: {gs.CpuCount}" else ""
        drawText res.SpriteBatch res.FontTexture $"Level: {levelName} | Players: {gs.NumPlayers}{cpuText}" cx y (Color(0xC0, 0xC0, 0xC0)) 2
        y <- y + 24

        drawText res.SpriteBatch res.FontTexture "Press SPACE to start | F1-F4: weapon | 1-4: players | ESC: quit" cx y (Color(0xC0, 0xC0, 0xC0)) 1
        y <- y + 12
        drawText res.SpriteBatch res.FontTexture "F5: prev level | F6: next level | F7/F8: CPU players | F11: Full-screen" cx y (Color(0xC0, 0xC0, 0xC0)) 1
        y <- y + 20

        drawText res.SpriteBatch res.FontTexture "Controls:" cx y (Color(0xFF, 0xFF, 0x80)) 1
        y <- y + 12
        drawText res.SpriteBatch res.FontTexture "P1 BLUE:   Arrows/NumPad = Thrust/Turn  RShift = Fire  Down = Special" cx y Color.White 1
        y <- y + 10
        drawText res.SpriteBatch res.FontTexture "P2 GREEN:  W/A/D = Thrust/Turn           Tab = Fire     S = Special" cx y Color.White 1
        y <- y + 10
        drawText res.SpriteBatch res.FontTexture "P3 RED:    I/J/L = Thrust/Turn             B = Fire     K = Special" cx y Color.White 1

        res.SpriteBatch.End()
