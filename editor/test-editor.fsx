/// Tests for the editor's flyability check and cleanup (LevelCheck.fs).
/// Run with: dotnet fsi editor/test-editor.fsx [folder with raw .LEV files]
/// Without a folder the raw-level tests run on the bundled maps.

#load "../Physics.fs"
#load "../Terrain.fs"
#load "LevelCheck.fs"

open FsRocket.Physics
open FsRocket.Terrain
open FsRocketEditor.LevelCheck

let mutable passed = 0
let mutable failed = 0

let test (name: string) (ok: bool) =
    if ok then
        passed <- passed + 1
        printfn "  PASS: %s" name
    else
        failed <- failed + 1
        printfn "  FAIL: %s" name

let clean (r: Report) =
    r.Pockets = 0 && r.Slits = 0 && r.NarrowLinks = 0 && r.SmallCaves = 0 && r.Specks = 0 && r.CutOffPads = 0

let describe (r: Report) =
    sprintf "pockets %d, slits %d, narrow links %d, small caves %d, specks %d, cut-off pads %d of %d"
        r.Pockets r.Slits r.NarrowLinks r.SmallCaves r.Specks r.CutOffPads r.Pads

let repoDir = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..")
let levelFiles (dir: string) = System.IO.Directory.GetFiles(dir, "*.LEV") |> Array.sort

// ── Bundled levels are flyable ──
printfn "Bundled levels pass the check"
for f in levelFiles repoDir do
    let r = check (loadLevel f).Pixels
    test (sprintf "%s: %s" (System.IO.Path.GetFileName f) (describe r)) (clean r)

// ── Cleanup on raw levels ──
let rawDir = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue repoDir
printfn "Cleanup (%s)" rawDir
let frame (px: byte array) =
    [| for i in 0 .. px.Length - 1 do
         let x, y = i % MapWidth, i / MapWidth
         if x < FrameDepth || y < FrameDepth || x >= MapWidth - FrameDepth || y >= MapHeight - FrameDepth then yield px[i] |]
for f in levelFiles rawDir do
    let name = System.IO.Path.GetFileName f
    let before = (loadLevel f).Pixels
    let result = cleanup defaultCleanup before
    let after = check result.Pixels
    test (sprintf "%s cleaned: %s" name (describe after)) (clean after)
    test (sprintf "%s spawn points unchanged" name) (findSpawnPoints before = findSpawnPoints result.Pixels)
    test (sprintf "%s pads unchanged" name)
        (Array.forall2 (fun b a -> isBase b = isBase a && (not (isBase b) || b = a)) before result.Pixels)
    test (sprintf "%s frame unchanged" name) (frame before = frame result.Pixels)
    test (sprintf "%s cleaning twice changes nothing" name) ((cleanup defaultCleanup result.Pixels).Pixels = result.Pixels)

// ── Hand-made cases ──
printfn "Hand-made cases"
let blank () = Array.create (MapWidth * MapHeight) VoidColor
let rect (px: byte array) x0 y0 x1 y1 (v: byte) =
    for y in y0 .. y1 do
        for x in x0 .. x1 do
            px[y * MapWidth + x] <- v
let pad (px: byte array) x0 x1 y = rect px x0 y x1 y BaseColorMin

// an empty map: the rounded map corners are no problem
let empty = blank ()
pad empty 20 60 300
test "empty map with one pad reads as flyable" (clean (check empty))

// a solid block with a hidden 3x3 hole: a pocket that becomes wall
let pocket = blank ()
rect pocket 100 100 200 200 0x82uy
rect pocket 150 150 152 152 VoidColor
pad pocket 20 60 300
let rp = check pocket
test "hidden hole reads as a pocket" (rp.Pockets = 1)
let cp = cleanup defaultCleanup pocket
test "cleanup fills the pocket" (cp.Pixels[151 * MapWidth + 151] <> VoidColor && (check cp.Pixels).Pockets = 0)

// two open halves joined only by a 2 px gap: a narrow link, widened
let link = blank ()
rect link 0 190 319 209 0x82uy
rect link 159 190 160 209 VoidColor
pad link 20 60 100
pad link 20 60 300
let rl = check link
test "2 px gap between two areas reads as a narrow link" (rl.NarrowLinks = 1 && rl.CutOffPads = 1)
let cl = cleanup defaultCleanup link
test "cleanup widens it so both pads connect" ((check cl.Pixels).CutOffPads = 0 && cl.Opened > 0)

// a pad sealed inside solid wall: a tunnel connects it
let sealedPad = blank ()
rect sealedPad 0 250 319 330 0x82uy
rect sealedPad 140 280 180 300 VoidColor
pad sealedPad 150 170 300
pad sealedPad 20 60 100
let rs = check sealedPad
test "walled-in pad reads as cut off" (rs.CutOffPads = 1)
let cs = cleanup defaultCleanup sealedPad
test "cleanup tunnels to it" ((check cs.Pixels).CutOffPads = 0 && cs.Tunnels >= 1)

// loose specks go, the frame stays
let specks = blank ()
rect specks 0 0 319 2 0x07uy
rect specks 50 50 51 51 0x82uy
pad specks 20 60 300
let rk = check specks
test "a 2x2 dot reads as a speck, the frame does not" (rk.Specks = 1)
let ck = cleanup defaultCleanup specks
test "cleanup removes the speck and keeps the frame" (ck.Pixels[50 * MapWidth + 50] = VoidColor && ck.Pixels[MapWidth + 100] = 0x07uy)

// problem spots: the hidden hole is one pocket region at its place
let regions = problemRegions (check pocket)
test "the pocket is one region with its box and centre"
    (match regions |> List.filter (fun g -> g.Kind = Pocket) with
     | [ g ] -> g.X0 = 150 && g.Y0 = 150 && g.X1 = 152 && g.Y1 = 152 && g.CenterX = 151 && g.CenterY = 151 && g.Pixels = 9
     | _ -> false)
test "a flyable level has no problem regions" (problemRegions (check empty) |> List.isEmpty)
test "report text names what was found" ((reportText (check pocket)).Contains "1 pockets")

// clearing an area: wall inside goes, pads and frame stay, crumbs go too
let block = blank ()
rect block 0 0 319 2 0x07uy
rect block 100 100 200 200 0x82uy
rect block 120 210 121 211 0x82uy   // a 4 px crumb just below the block
pad block 130 170 99
let cleared = clearAreas [ ClearRect (110, 150, 190, 205) ] block
test "clear opens the area" (cleared.Pixels[180 * MapWidth + 150] = VoidColor && cleared.Opened > 0)
test "clear keeps wall outside it" (cleared.Pixels[120 * MapWidth + 150] <> VoidColor)
test "clear removes a crumb next to the cut" (cleared.Pixels[210 * MapWidth + 120] = VoidColor)
test "clear keeps the pad and the frame"
    (cleared.Pixels[99 * MapWidth + 150] = BaseColorMin && cleared.Pixels[MapWidth + 50] = 0x07uy)
let ell = clearAreas [ ClearEllipse (110, 110, 190, 190) ] block
test "an ellipse clears its middle, not the box corners"
    (ell.Pixels[150 * MapWidth + 150] = VoidColor && ell.Pixels[111 * MapWidth + 111] <> VoidColor)

printfn ""
printfn "Results: %d passed, %d failed" passed failed
if failed > 0 then exit 1
