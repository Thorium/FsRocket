/// FsRocket Level Editor — level commands for scripts and agents:
/// check (text or JSON with problem locations), clean, render to PNG,
/// before/after compare, clear areas. Each takes one .LEV or a folder of them.
module FsRocketEditor.EditorCli

open System
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open FsRocket.Physics
open FsRocket.Terrain
open FsRocketEditor.EditorCore
open FsRocketEditor.LevelCheck

let usage =
    String.concat "
"
        [ "Level commands (each takes a .LEV file or a folder of them):"
          "  --check <level|folder> [--json]"
          "      what the ship can't fly; exit code 1 when anything is found."
          "      --json lists every problem spot (kind, bounding box, centre, size)"
          "  --clean <in> <out> [--no-fill] [--no-widen] [--no-connect]"
          "                     [--keep-specks] [--spikes <r>]"
          "      make the level flyable; --spikes also trims wall spikes up to"
          "      2*r+1 px wide. A folder in writes a folder out."
          "  --render <level|folder> <out.png|folder> [--check] [--scale <n>]"
          "      the level as a PNG (scale 1-8, default 2); --check adds the"
          "      check overlay"
          "  --compare <before> <after> <out.png|folder> [--scale <n>]"
          "      before | after | changes (green = new wall, red = opened);"
          "      folders pair levels by file name"
          "  --clear <in.LEV> <out.LEV> <area> [<area> ...]"
          "      clear terrain for flight space and tidy the cut (pads and the"
          "      map frame stay); --clean afterwards fills any new dead space."
          "      An area is rect:x0,y0,x1,y1 or ellipse:x0,y0,x1,y1 in map"
          "      pixels (0-319, 0-399)"
          ""
          "Exit codes: 0 done, 1 the check found problems, 2 bad arguments or files." ]

// ─── Arguments ─────────────────────────────────────────────────────────────

exception private UsageError of string

let private fail msg = raise (UsageError msg)

/// The value after a flag, if the flag is there.
let private flagValue (args: string list) (name: string) =
    match List.tryFindIndex ((=) name) args with
    | Some i when i + 1 < args.Length -> Some args[i + 1]
    | Some _ -> fail $"%s{name} needs a value"
    | None -> None

let private hasFlag (args: string list) (name: string) = List.contains name args

let private intFlag (args: string list) (name: string) (fallback: int) (lo: int) (hi: int) =
    match flagValue args name with
    | None -> fallback
    | Some v ->
        match Int32.TryParse v with
        | true, n when n >= lo && n <= hi -> n
        | _ -> fail $"%s{name} must be a number %d{lo}-%d{hi}, got '%s{v}'"

/// The positional arguments: everything that isn't a flag or a flag's value.
let private positional (args: string list) (valued: string list) =
    let rec go acc rest =
        match rest with
        | [] -> List.rev acc
        | f :: _ :: tail when List.contains f valued -> go acc tail
        | (f: string) :: tail when f.StartsWith "--" -> go acc tail
        | a :: tail -> go (a :: acc) tail
    go [] args

/// The levels a path names: the file, or every .LEV in the folder.
let private levelsIn (path: string) =
    if Directory.Exists path then
        Directory.GetFiles(path, "*.LEV") |> Array.sort |> Array.toList
    elif File.Exists path then [ path ]
    else raise (FileNotFoundException $"not found: %s{path}")

/// Where a level's output goes: the output path itself for one file, or a
/// file named after the level inside the output folder.
let private outputFor (input: string) (output: string) (level: string) (ext: string) =
    if Directory.Exists input then
        Directory.CreateDirectory output |> ignore
        Path.Combine(output, Path.GetFileNameWithoutExtension level + ext)
    else
        match Path.GetDirectoryName(Path.GetFullPath output) with
        | null -> ()
        | dir -> Directory.CreateDirectory dir |> ignore
        output

let private levelName (path: string) = Path.GetFileNameWithoutExtension path

// ─── Images ────────────────────────────────────────────────────────────────

/// Blend an 0xAARRGGBB colour over an opaque one.
let private over (top: int) (under: int) =
    let a = (top >>> 24) &&& 0xFF
    let mix shift =
        let t, u = (top >>> shift) &&& 0xFF, (under >>> shift) &&& 0xFF
        (t * a + u * (255 - a)) / 255
    (0xFF <<< 24) ||| (mix 16 <<< 16) ||| (mix 8 <<< 8) ||| mix 0

let private dim (argb: int) =
    let c shift = ((argb >>> shift) &&& 0xFF) / 3
    (0xFF <<< 24) ||| (c 16 <<< 16) ||| (c 8 <<< 8) ||| c 0

/// The level as ARGB pixels, optionally with the check overlay.
let private levelArgb (px: byte array) (withCheck: bool) =
    let colours = px |> Array.map displayArgb
    if withCheck then
        let r = check px
        r.Problems
        |> Array.iteri (fun i p ->
            match p with
            | ValueSome p -> colours[i] <- over (problemArgb p) colours[i]
            | ValueNone -> ())
    colours

/// Save panels of 320x400 ARGB pixels side by side, scaled up.
let private savePanels (path: string) (scale: int) (panels: int array list) =
    let gap = 6
    let n = panels.Length
    let w = (n * MapWidth + (n - 1) * gap) * scale
    let h = MapHeight * scale
    use bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)
    let data = bmp.LockBits(Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb)
    let stride = data.Stride / 4
    let buf = Array.create (stride * h) (0xFF3C3C3C)
    panels
    |> List.iteri (fun k panel ->
        let ox = k * (MapWidth + gap) * scale
        for y in 0 .. h - 1 do
            for x in 0 .. MapWidth * scale - 1 do
                buf[y * stride + ox + x] <- panel[(y / scale) * MapWidth + x / scale])
    Marshal.Copy(buf, 0, data.Scan0, buf.Length)
    bmp.UnlockBits data
    bmp.Save(path, ImageFormat.Png)

// ─── Commands ──────────────────────────────────────────────────────────────

let private jsonOptions = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

// JSON shapes of --check --json (named records keep the field order).
type CheckCounts =
    { Pockets: int; Slits: int; NarrowGaps: int; SmallCaves: int; Specks: int }

type CheckSpot =
    { Kind: string; X0: int; Y0: int; X1: int; Y1: int; CenterX: int; CenterY: int; Pixels: int }

type CheckLevel =
    { Level: string; Flyable: bool; Pads: int; CutOffPads: int; Counts: CheckCounts; Problems: CheckSpot list }

let private checkCommand (args: string list) =
    let paths = positional args []
    let path = match paths with [ p ] -> p | _ -> fail "--check takes one level or folder"
    let results = levelsIn path |> List.map (fun f -> f, check (loadLevel f).Pixels)
    if hasFlag args "--json" then
        let levels =
            results
            |> List.map (fun (f, r) ->
                { Level = levelName f
                  Flyable = not (hasProblems r)
                  Pads = r.Pads
                  CutOffPads = r.CutOffPads
                  Counts =
                    { Pockets = r.Pockets; Slits = r.Slits; NarrowGaps = r.NarrowLinks
                      SmallCaves = r.SmallCaves; Specks = r.Specks }
                  Problems =
                    problemRegions r
                    |> List.map (fun g ->
                        { Kind = problemName g.Kind; X0 = g.X0; Y0 = g.Y0; X1 = g.X1; Y1 = g.Y1
                          CenterX = g.CenterX; CenterY = g.CenterY; Pixels = g.Pixels }) })
        Console.WriteLine(JsonSerializer.Serialize(levels, jsonOptions))
    else
        for f, r in results do
            Console.WriteLine($"%s{levelName f}: %s{reportText r}")
    if results |> List.exists (fun (_, r) -> hasProblems r) then 1 else 0

let private cleanCommand (args: string list) =
    let input, output =
        match positional args [ "--spikes" ] with
        | [ i; o ] -> i, o
        | _ -> fail "--clean takes <in> <out>"
    let opt =
        { FillDeadSpace = not (hasFlag args "--no-fill")
          WidenNarrowLinks = not (hasFlag args "--no-widen")
          ConnectPads = not (hasFlag args "--no-connect")
          RemoveSpecks = not (hasFlag args "--keep-specks")
          SpikeRadius = intFlag args "--spikes" 0 0 8 }
    for f in levelsIn input do
        let lv = loadLevel f
        let result = cleanup opt lv.Pixels
        let out = outputFor input output f ".LEV"
        saveLevel out { lv with Pixels = result.Pixels; Name = levelName out }
        Console.WriteLine(
            $"%s{levelName f} -> %s{out}: filled %d{result.Filled} px, opened %d{result.Opened} px, \
              %d{result.Tunnels} tunnels; %s{reportText (check result.Pixels)}")
    0

let private renderCommand (args: string list) =
    let input, output =
        match positional args [ "--scale" ] with
        | [ i; o ] -> i, o
        | _ -> fail "--render takes <level|folder> <out.png|folder>"
    let scale = intFlag args "--scale" 2 1 8
    let withCheck = hasFlag args "--check"
    for f in levelsIn input do
        let out = outputFor input output f ".png"
        savePanels out scale [ levelArgb (loadLevel f).Pixels withCheck ]
        Console.WriteLine($"%s{levelName f} -> %s{out}")
    0

let private compareCommand (args: string list) =
    let before, after, output =
        match positional args [ "--scale" ] with
        | [ b; a; o ] -> b, a, o
        | _ -> fail "--compare takes <before> <after> <out.png|folder>"
    let scale = intFlag args "--scale" 2 1 8
    let pairs =
        if Directory.Exists before then
            if not (Directory.Exists after) then fail "--compare: before is a folder, so after must be one too"
            levelsIn before
            |> List.choose (fun b ->
                let a = Path.Combine(after, Path.GetFileName b)
                if File.Exists a then Some (b, a) else None)
        else
            for f in [ before; after ] do
                if not (File.Exists f) then raise (FileNotFoundException $"not found: %s{f}")
            [ before, after ]
    if pairs.IsEmpty then fail "--compare: no levels with the same name in both folders"
    for b, a in pairs do
        let pb, pa = (loadLevel b).Pixels, (loadLevel a).Pixels
        let changes =
            Array.init (MapWidth * MapHeight) (fun i ->
                let was, now = isPassable pb[i], isPassable pa[i]
                if was && not now then 0xFF00DC00
                elif now && not was then 0xFFFF2828
                else dim (displayArgb pa[i]))
        let filled = Array.fold2 (fun n x y -> if isPassable x && not (isPassable y) then n + 1 else n) 0 pb pa
        let opened = Array.fold2 (fun n x y -> if not (isPassable x) && isPassable y then n + 1 else n) 0 pb pa
        let out = outputFor before output b ".png"
        savePanels out scale [ levelArgb pb false; levelArgb pa false; changes ]
        Console.WriteLine($"%s{levelName b} -> %s{out}: %d{filled} px filled, %d{opened} px opened")
    0

let private parseArea (text: string) =
    let shape, coords =
        match text.Split(':', 2) with
        | [| s; c |] -> s.ToLowerInvariant(), c
        | _ -> fail $"bad area '%s{text}': use rect:x0,y0,x1,y1 or ellipse:x0,y0,x1,y1"
    let n =
        coords.Split(',')
        |> Array.map (fun v ->
            match Int32.TryParse(v.Trim()) with
            | true, n -> n
            | _ -> fail $"bad area '%s{text}': '%s{v}' isn't a number")
    match shape, n with
    | "rect", [| x0; y0; x1; y1 |] -> ClearRect (x0, y0, x1, y1)
    | "ellipse", [| x0; y0; x1; y1 |] -> ClearEllipse (x0, y0, x1, y1)
    | _ -> fail $"bad area '%s{text}': use rect:x0,y0,x1,y1 or ellipse:x0,y0,x1,y1"

let private clearCommand (args: string list) =
    match positional args [] with
    | input :: output :: (_ :: _ as areas) ->
        if Directory.Exists input then fail "--clear works on one level: areas are per level"
        let lv = loadLevel input
        let result = clearAreas (areas |> List.map parseArea) lv.Pixels
        let out = outputFor input output input ".LEV"
        saveLevel out { lv with Pixels = result.Pixels; Name = levelName out }
        Console.WriteLine($"%s{levelName input} -> %s{out}: opened %d{result.Opened} px; %s{reportText (check result.Pixels)}")
        0
    | _ -> fail "--clear takes <in.LEV> <out.LEV> <area> [<area> ...]"

/// Run a level command; None when the arguments aren't one.
let tryRun (argv: string array) : int option =
    let args = List.ofArray argv
    let run command =
        try Some (command args)
        with
        | UsageError msg ->
            Console.Error.WriteLine($"Error: %s{msg}\n\n%s{usage}")
            Some 2
        | :? IOException | :? UnauthorizedAccessException as ex ->
            Console.Error.WriteLine($"Error: %s{ex.Message}")
            Some 2
    match args with
    | "--check" :: _ -> run checkCommand
    | "--clean" :: _ -> run cleanCommand
    | "--render" :: _ -> run renderCommand
    | "--compare" :: _ -> run compareCommand
    | "--clear" :: _ -> run clearCommand
    | _ -> None
