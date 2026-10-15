/// FsRocket Level Editor — flyability check and automatic cleanup (no UI types).
///
/// A ship collides with terrain at its centre pixel, but it needs room to turn:
/// its sprite reaches about ShipRadius map pixels from the centre. Space the
/// ship can't fit into is dead space — pockets, slits and thin hollow frames
/// read as tunnels on screen but can't be flown. This module finds those
/// spots (the editor's Check overlay) and fixes them (Clean up), the same way
/// the bundled levels were cleaned:
///
///   * pockets, dead-end slits and small sealed caves become wall,
///   * gaps narrower than the ship that are the only way between two open areas
///     are widened,
///   * pads that no ship-sized route connects get a tunnel through the thinnest
///     wall (a player may have no digging weapon),
///   * loose specks of a few pixels go, and optionally thin wall spikes,
///   * filled areas copy the nearby terrain's texture and outline colour.
///
/// Pads, the void just above them (their spawn points) and the map's grey frame
/// are never changed.
module FsRocketEditor.LevelCheck

open System.Collections.Generic
open FsRocket.Physics
open FsRocket.Terrain

// ─── Limits ────────────────────────────────────────────────────────────────

/// Room the ship needs around its centre, in map pixels: the sprite reaches
/// 5*Scale screen px, drawn at Scale*TerrainZoom (3 * 1.25) per map pixel.
[<Literal>]
let ShipRadius = 4

/// Sealed caves (open space with no pad, cut off from the rest) smaller than
/// this are filled. In the bundled maps such caves were 82-233 px, against
/// 1300 px and up for the caves worth keeping.
[<Literal>]
let SmallCaveMax = 500

/// Wall pieces this small are noise (image-import leftovers), not terrain.
[<Literal>]
let SpeckMax = 20

/// The maps' grey frame along the edges: never carved.
[<Literal>]
let FrameDepth = 3

/// Void this many pixels above a pad stays void, so its spawn point survives.
[<Literal>]
let SpawnClearance = 2

/// How many times widening / tunnelling may repeat before giving up.
[<Literal>]
let MaxRounds = 10

// ─── Pixel masks ───────────────────────────────────────────────────────────

let private size = MapWidth * MapHeight

let inline private inBounds x y = x >= 0 && x < MapWidth && y >= 0 && y < MapHeight

let private neighbours4 i =
    let x, y = i % MapWidth, i / MapWidth
    [ if x > 0 then yield i - 1
      if x < MapWidth - 1 then yield i + 1
      if y > 0 then yield i - MapWidth
      if y < MapHeight - 1 then yield i + MapWidth ]

let private inFrame i =
    let x, y = i % MapWidth, i / MapWidth
    x < FrameDepth || y < FrameDepth || x >= MapWidth - FrameDepth || y >= MapHeight - FrameDepth

/// Offsets of a disc of radius r.
let private disc r =
    [| for dy in -r .. r do
         for dx in -r .. r do
             if dx * dx + dy * dy <= r * r then yield struct (dx, dy) |]

/// Pixels whose whole disc lies inside the mask; off-map counts as `offMap`.
let private erode (offMap: bool) (mask: bool array) r =
    let d = disc r
    Array.init size (fun i ->
        let x, y = i % MapWidth, i / MapWidth
        mask[i]
        && d |> Array.forall (fun (struct (dx, dy)) ->
            let a, b = x + dx, y + dy
            if inBounds a b then mask[b * MapWidth + a] else offMap))

/// Every pixel within a disc of a mask pixel.
let private dilate (mask: bool array) r =
    let d = disc r
    let out = Array.zeroCreate size
    for i in 0 .. size - 1 do
        if mask[i] then
            let x, y = i % MapWidth, i / MapWidth
            for struct (dx, dy) in d do
                let a, b = x + dx, y + dy
                if inBounds a b then out[b * MapWidth + a] <- true
    out

/// 4-connected components: label per pixel (-1 outside the mask) and sizes.
let private label (mask: bool array) =
    let lab = Array.create size -1
    let sizes = ResizeArray<int>()
    let stack = Stack<int>()
    for s in 0 .. size - 1 do
        if mask[s] && lab[s] < 0 then
            let id = sizes.Count
            let mutable n = 0
            lab[s] <- id
            stack.Push s
            while stack.Count > 0 do
                let i = stack.Pop()
                n <- n + 1
                for j in neighbours4 i do
                    if mask[j] && lab[j] < 0 then
                        lab[j] <- id
                        stack.Push j
            sizes.Add n
    lab, sizes.ToArray()

let private isFly (p: byte) = p = VoidColor || p = WaterColor
let private isWallPixel (p: byte) = not (isPassable p)

// ─── Analysis ──────────────────────────────────────────────────────────────

/// What the ship can use: `Opened` is the space a ship-sized disc can sweep,
/// labelled by connected area; `Narrow` is passable space it can't enter,
/// labelled, with the open areas each narrow piece touches.
type private Analysis =
    { Pass: bool array
      OLab: int array
      OSizes: int array
      PadArea: bool array
      NLab: int array
      Touches: HashSet<int> array }

let private analyse (px: byte array) =
    let pass = Array.init size (fun i -> isPassable px[i])
    let opened = Array.map2 (&&) (dilate (erode false pass ShipRadius) ShipRadius) pass
    let oLab, oSizes = label opened
    let padArea = Array.zeroCreate oSizes.Length
    for sp in findSpawnPoints px do
        for dx in 0 .. sp.Width do
            for dy in 0 .. 3 do
                let x, y = sp.X + dx, sp.Y - dy
                if inBounds x y && oLab[y * MapWidth + x] >= 0 then padArea[oLab[y * MapWidth + x]] <- true
    let narrow = Array.init size (fun i -> pass[i] && not opened[i] && not (isBase px[i]))
    let nLab, nSizes = label narrow
    let touches = Array.init nSizes.Length (fun _ -> HashSet<int>())
    for i in 0 .. size - 1 do
        if nLab[i] >= 0 then
            for j in neighbours4 i do
                if oLab[j] >= 0 then touches[nLab[i]].Add oLab[j] |> ignore
    { Pass = pass; OLab = oLab; OSizes = oSizes; PadArea = padArea; NLab = nLab; Touches = touches }

/// The open area a spawn point sits in, if the ship fits there at all.
let private areaOf (a: Analysis) (sp: SpawnPoint) =
    [ for dx in 0 .. sp.Width do
        for dy in 0 .. 3 do
            let x, y = sp.X + dx, sp.Y - dy
            if inBounds x y && a.OLab[y * MapWidth + x] >= 0 then yield a.OLab[y * MapWidth + x] ]
    |> List.tryHead

/// The open areas the spawn points sit in.
let private padAreas (px: byte array) (a: Analysis) =
    findSpawnPoints px |> Array.choose (areaOf a)

// ─── Check (the editor's overlay) ──────────────────────────────────────────

/// A spot the ship can't use as the map shows it.
type Problem =
    /// enclosed space the ship can't enter
    | Pocket
    /// a slit or corner off one open area, too narrow to fly into
    | Slit
    /// a gap narrower than the ship, the only way between two open areas
    | NarrowLink
    /// open space with no pad and no way in, too small to matter
    | SmallCave
    /// a loose wall speck of a few pixels
    | Speck
    /// a pad no ship-sized route joins to the other pads
    | CutOffPad

type Report =
    { /// The problem at each pixel, if any
      Problems: Problem voption array
      Pockets: int
      Slits: int
      NarrowLinks: int
      SmallCaves: int
      Specks: int
      /// Pads in total, and how many sit outside the main open area
      Pads: int
      CutOffPads: int }

/// Find every spot the ship can't use.
let check (px: byte array) : Report =
    let a = analyse px
    let problems = Array.create size ValueNone
    // narrow pieces by how many open areas they touch
    let kindOf (t: HashSet<int>) =
        match t.Count with
        | 0 -> Pocket
        | 1 -> Slit
        | _ -> NarrowLink
    // A slit that lies wholly in a map corner is only the corner's rounding:
    // the ship can't reach into a corner anyway, so it isn't reported.
    let corner = FrameDepth + ShipRadius
    let inCorner i =
        let x, y = i % MapWidth, i / MapWidth
        (x < corner || x >= MapWidth - corner) && (y < corner || y >= MapHeight - corner)
    let cornerOnly = Array.create a.Touches.Length true
    for i in 0 .. size - 1 do
        if a.NLab[i] >= 0 && not (inCorner i) then cornerOnly[a.NLab[i]] <- false
    let narrowKinds =
        Array.init a.Touches.Length (fun n ->
            match kindOf a.Touches[n] with
            | Slit when cornerOnly[n] -> ValueNone
            | k -> ValueSome k)
    for i in 0 .. size - 1 do
        if a.NLab[i] >= 0 then problems[i] <- narrowKinds[a.NLab[i]]
    // small sealed caves
    let smallCave = Array.init a.OSizes.Length (fun r -> not a.PadArea[r] && a.OSizes[r] < SmallCaveMax)
    for i in 0 .. size - 1 do
        if a.OLab[i] >= 0 && smallCave[a.OLab[i]] then problems[i] <- ValueSome SmallCave
    // specks
    let wall = Array.init size (fun i -> isWallPixel px[i])
    let wLab, wSizes = label wall
    let speck = wSizes |> Array.map (fun n -> n <= SpeckMax)
    for i in 0 .. size - 1 do
        if wLab[i] >= 0 && speck[wLab[i]] && not (inFrame i) then problems[i] <- ValueSome Speck
    // pads outside the open area most pads share (or with no room at all)
    let spawns = findSpawnPoints px
    let areas = spawns |> Array.map (areaOf a)
    let main = areas |> Array.choose id |> Array.countBy id |> Array.sortByDescending snd |> Array.tryHead |> Option.map fst
    let cutOff = Array.zip spawns areas |> Array.filter (fun (_, area) -> area <> main || area.IsNone)
    for sp, _ in cutOff do
        for dx in 0 .. sp.Width do
            let x, y = sp.X + dx, sp.Y + 1
            if inBounds x y then problems[y * MapWidth + x] <- ValueSome CutOffPad
    let kinds k = narrowKinds |> Array.filter ((=) (ValueSome k)) |> Array.length
    { Problems = problems
      Pockets = kinds Pocket
      Slits = kinds Slit
      NarrowLinks = kinds NarrowLink
      SmallCaves = smallCave |> Array.filter id |> Array.length
      Specks = Array.zip wSizes speck |> Array.filter (fun (_, s) -> s) |> Array.length
      Pads = spawns.Length
      CutOffPads = cutOff.Length }

/// Any problem at all?
let hasProblems (r: Report) =
    r.Pockets + r.Slits + r.NarrowLinks + r.SmallCaves + r.Specks + r.CutOffPads > 0

/// One line summing up a check.
let reportText (r: Report) =
    let parts =
        [ if r.Pockets > 0 then yield $"%d{r.Pockets} pockets"
          if r.Slits > 0 then yield $"%d{r.Slits} slits"
          if r.NarrowLinks > 0 then yield $"%d{r.NarrowLinks} narrow gaps"
          if r.SmallCaves > 0 then yield $"%d{r.SmallCaves} small caves"
          if r.Specks > 0 then yield $"%d{r.Specks} specks"
          if r.CutOffPads > 0 then yield $"%d{r.CutOffPads} of %d{r.Pads} pads cut off" ]
    if parts.IsEmpty then $"Check: flyable, %d{r.Pads} pads connected"
    else "Check: " + System.String.Join(", ", parts)

/// Overlay colour of a problem (0xAARRGGBB): red pockets, yellow slits, cyan
/// narrow gaps, magenta small caves, orange specks, white under cut-off pads.
let problemArgb (p: Problem) =
    match p with
    | Pocket -> 0xC0FF3C3C
    | Slit -> 0xC0FFDC00
    | NarrowLink -> 0xE000FFFF
    | SmallCave -> 0x90DC00DC
    | Speck -> 0xE0FF8C00
    | CutOffPad -> 0xFFFFFFFF

/// The name a problem goes by in reports and JSON.
let problemName (p: Problem) =
    match p with
    | Pocket -> "pocket"
    | Slit -> "slit"
    | NarrowLink -> "narrow-gap"
    | SmallCave -> "small-cave"
    | Speck -> "speck"
    | CutOffPad -> "cut-off-pad"

/// One problem spot: a connected group of pixels with the same problem.
type ProblemRegion =
    { Kind: Problem
      /// bounding box, inclusive, in map pixels
      X0: int
      Y0: int
      X1: int
      Y1: int
      /// pixel centre (mean position)
      CenterX: int
      CenterY: int
      Pixels: int }

/// The check's problems as spots, largest first within each kind.
let problemRegions (r: Report) : ProblemRegion list =
    let kinds = [ Pocket; Slit; NarrowLink; SmallCave; Speck; CutOffPad ]
    [ for kind in kinds do
        let mask = r.Problems |> Array.map (fun p -> p = ValueSome kind)
        let lab, sizes = label mask
        let boxes = Array.init sizes.Length (fun _ -> [| System.Int32.MaxValue; System.Int32.MaxValue; -1; -1; 0; 0 |])
        for i in 0 .. size - 1 do
            if lab[i] >= 0 then
                let b = boxes[lab[i]]
                let x, y = i % MapWidth, i / MapWidth
                b[0] <- min b[0] x
                b[1] <- min b[1] y
                b[2] <- max b[2] x
                b[3] <- max b[3] y
                b[4] <- b[4] + x
                b[5] <- b[5] + y
        yield!
            boxes
            |> Array.mapi (fun n b ->
                { Kind = kind; X0 = b[0]; Y0 = b[1]; X1 = b[2]; Y1 = b[3]
                  CenterX = b[4] / sizes[n]; CenterY = b[5] / sizes[n]; Pixels = sizes[n] })
            |> Array.sortByDescending (fun g -> g.Pixels)
            |> Array.toList ]

// ─── Cleanup ───────────────────────────────────────────────────────────────

type CleanupOptions =
    { /// pockets, slits and small sealed caves become wall
      FillDeadSpace: bool
      /// gaps narrower than the ship between open areas get ship-wide
      WidenNarrowLinks: bool
      /// tunnel to pads no ship-sized route reaches
      ConnectPads: bool
      /// loose specks go
      RemoveSpecks: bool
      /// wall spikes thinner than 2*r+1 px go; 0 = off
      SpikeRadius: int }

let defaultCleanup =
    { FillDeadSpace = true; WidenNarrowLinks = true; ConnectPads = true; RemoveSpecks = true; SpikeRadius = 0 }

type CleanupResult =
    { Pixels: byte array
      Filled: int
      Opened: int
      Tunnels: int
      /// pads still without a ship-sized route to the others
      CutOffPads: int }

/// Recolour changed pixels from the nearest unchanged pixel `isSource` accepts.
let private recolour (orig: byte array) (px: byte array) (changed: bool array) (isSource: byte -> bool) =
    let colour = Array.copy orig
    let known = Array.init size (fun i -> not changed[i] && isSource orig[i])
    let q = Queue<int>()
    for i in 0 .. size - 1 do
        if known[i] then q.Enqueue i
    while q.Count > 0 do
        let i = q.Dequeue()
        for j in neighbours4 i do
            if not known[j] then
                known[j] <- true
                colour[j] <- colour[i]
                q.Enqueue j
    for i in 0 .. size - 1 do
        if changed[i] then px[i] <- colour[i]

/// Texture each filled patch with a copy of nearby original wall: the offset
/// that covers the whole patch from unchanged inner wall and best continues
/// the pattern around it. Patches no offset covers keep their colours.
let private texture (orig: byte array) (px: byte array) (patch: bool array) =
    let inner =
        Array.init size (fun i ->
            not patch[i] && isWallPixel orig[i] && not (isBase orig[i])
            && neighbours4 i |> List.forall (fun j -> not patch[j] && isWallPixel orig[j]))
    let lab, sizes = label patch
    let members = Array.init sizes.Length (fun _ -> ResizeArray<int>())
    for i in 0 .. size - 1 do
        if lab[i] >= 0 then members[lab[i]].Add i
    let span = 48
    let at i dx dy =
        let x, y = i % MapWidth + dx, i / MapWidth + dy
        if inBounds x y then y * MapWidth + x else -1
    for ms in members do
        let ring =
            let s = HashSet<int>()
            for i in ms do
                for dy in -2 .. 2 do
                    for dx in -2 .. 2 do
                        let j = at i dx dy
                        if j >= 0 && inner[j] then s.Add j |> ignore
            Seq.toArray s
        let mutable best = ValueNone
        for dy in -span .. span do
            for dx in -span .. span do
                if (dx <> 0 || dy <> 0) && ms |> Seq.forall (fun i -> let j = at i dx dy in j >= 0 && inner[j]) then
                    let score = ring |> Array.sumBy (fun i -> let j = at i dx dy in if j >= 0 && orig[j] = orig[i] then 1 else 0)
                    let dist = dx * dx + dy * dy
                    match best with
                    | ValueSome (struct (s, d, _, _)) when s > score || (s = score && d <= dist) -> ()
                    | _ -> best <- ValueSome (struct (score, dist, dx, dy))
        match best with
        | ValueSome (struct (_, _, dx, dy)) -> for i in ms do px[i] <- orig[at i dx dy]
        | ValueNone -> ()

/// Colour what an edit changed: filled pixels take the nearby wall's texture,
/// opened ones the nearby void or water, and new edges the level's outline
/// colour. Returns the pixels that ended up filled and opened.
let private finishColours (orig: byte array) (px: byte array) (filled: bool array) (carved: bool array) =
    let filledNow = Array.init size (fun i -> filled[i] && isWallPixel px[i])
    let carvedNow = Array.init size (fun i -> carved[i] && isPassable px[i])
    recolour orig px filledNow (fun p -> isWallPixel p && not (isBase p))
    recolour orig px carvedNow isFly
    let buried =
        Array.init size (fun i ->
            filledNow[i]
            || (isWallPixel orig[i] && not (isBase orig[i]) && not carvedNow[i] && not (inFrame i)
                && neighbours4 i |> List.exists (fun j -> filledNow[j])
                && neighbours4 i |> List.forall (fun j -> isWallPixel px[j])))
    texture orig px buried
    let edgeColours =
        [| for i in 0 .. size - 1 do
             if isWallPixel orig[i] && not (isBase orig[i]) && not (inFrame i)
                && neighbours4 i |> List.exists (fun j -> isFly orig[j]) then yield orig[i] |]
    if edgeColours.Length > 0 then
        let top, topCount = edgeColours |> Array.countBy id |> Array.maxBy snd
        // an edge colour on half the edges is the level's outline
        if topCount * 2 >= edgeColours.Length then
            for i in 0 .. size - 1 do
                if isWallPixel px[i] && not (isBase px[i]) && not (inFrame i)
                   && neighbours4 i |> List.exists (fun j -> carvedNow[j] || (filledNow[i] && isFly px[j])) then
                    px[i] <- top
    filledNow, carvedNow

/// Make the level flyable: see the module comment for the steps.
let cleanup (opt: CleanupOptions) (orig: byte array) : CleanupResult =
    let px = Array.copy orig
    let filled = Array.zeroCreate<bool> size
    let carved = Array.zeroCreate<bool> size
    // the void just above each pad keeps its spawn point
    let spawnRoom =
        let room = Array.zeroCreate<bool> size
        for i in 0 .. size - 1 do
            if isBase orig[i] then
                let x, y = i % MapWidth, i / MapWidth
                for dy in 1 .. SpawnClearance do
                    if y - dy >= 0 then room[(y - dy) * MapWidth + x] <- true
        room
    /// Fill one pixel with wall; false when it is protected or already wall.
    let fill i =
        if isPassable px[i] && not (isBase px[i]) && not spawnRoom[i] && not (inFrame i) then
            px[i] <- 0x07uy   // placeholder wall, recoloured below
            filled[i] <- true
            true
        else false
    let carve i =
        if isWallPixel px[i] && not (inFrame i) then
            px[i] <- VoidColor
            carved[i] <- true
    let carveDisc i =
        let x, y = i % MapWidth, i / MapWidth
        for struct (dx, dy) in disc ShipRadius do
            let u, v = x + dx, y + dy
            if inBounds u v then carve (v * MapWidth + u)

    let removeSpecks () =
        let wall = Array.init size (fun i -> isWallPixel px[i])
        let wLab, wSizes = label wall
        for i in 0 .. size - 1 do
            if wLab[i] >= 0 && wSizes[wLab[i]] <= SpeckMax then carve i

    // specks first, so they don't count as terrain below
    if opt.RemoveSpecks then removeSpecks ()

    // small sealed caves, before widening could open them up
    if opt.FillDeadSpace then
        let a = analyse px
        let linked = HashSet<int>()
        for t in a.Touches do
            if t.Count >= 2 then linked.UnionWith t
        let small r = not a.PadArea[r] && not (linked.Contains r) && a.OSizes[r] < SmallCaveMax
        for i in 0 .. size - 1 do
            if a.OLab[i] >= 0 && small a.OLab[i] then fill i |> ignore

    // widen the only-way gaps: a ship-sized disc around each of their pixels
    if opt.WidenNarrowLinks then
        let mutable rounds = 0
        let mutable more = true
        while more && rounds < MaxRounds do
            let a = analyse px
            let links = a.Touches |> Array.map (fun t -> t.Count >= 2)
            let mutable any = false
            for i in 0 .. size - 1 do
                if a.NLab[i] >= 0 && links[a.NLab[i]] then
                    any <- true
                    carveDisc i
            more <- any
            rounds <- rounds + 1

    // every pad reachable without digging: tunnel along the route through the
    // least wall (0-1 search: open space costs 0, wall 1)
    let mutable tunnels = 0
    if opt.ConnectPads then
        let mutable areas = padAreas px (analyse px) |> Array.distinct
        while areas.Length > 1 && tunnels < MaxRounds do
            let a = analyse px
            let from = areas[0]
            let dist = Array.create size System.Int32.MaxValue
            let prev = Array.create size -1
            let dq = LinkedList<int>()
            for i in 0 .. size - 1 do
                if a.OLab[i] = from then
                    dist[i] <- 0
                    dq.AddLast i |> ignore
            let mutable hit = -1
            while hit < 0 && dq.Count > 0 do
                let i = dq.First.Value
                dq.RemoveFirst()
                if a.OLab[i] >= 0 && a.OLab[i] <> from && Array.contains a.OLab[i] areas then hit <- i
                else
                    for j in neighbours4 i do
                        if not (isBase px[j]) && not (inFrame j) then
                            let w = if a.Pass[j] then 0 else 1
                            if dist[i] + w < dist[j] then
                                dist[j] <- dist[i] + w
                                prev[j] <- i
                                if w = 0 then dq.AddFirst j |> ignore else dq.AddLast j |> ignore
            let mutable i = hit
            while i >= 0 && a.OLab[i] <> from do
                carveDisc i
                i <- prev[i]
            tunnels <- tunnels + 1
            areas <- padAreas px (analyse px) |> Array.distinct

    // spikes: wall opening, kept off the frame and pads
    if opt.SpikeRadius > 0 then
        let wall = Array.init size (fun i -> isWallPixel px[i])
        let keep = dilate (erode true wall opt.SpikeRadius) opt.SpikeRadius
        let nearPad = dilate (Array.map isBase px) (opt.SpikeRadius + 2)
        for i in 0 .. size - 1 do
            if wall[i] && not keep[i] && not nearPad[i] then carve i

    // widening, tunnels and spike cuts can leave fragments behind
    if opt.RemoveSpecks then removeSpecks ()

    // dead space, repeated until nothing changes (each fill can leave a new slit)
    if opt.FillDeadSpace then
        let mutable more = true
        while more do
            let a = analyse px
            let mutable n = 0
            for i in 0 .. size - 1 do
                let deadNarrow = a.NLab[i] >= 0 && a.Touches[a.NLab[i]].Count < 2
                let smallCave = a.OLab[i] >= 0 && not a.PadArea[a.OLab[i]] && a.OSizes[a.OLab[i]] < SmallCaveMax
                if (deadNarrow || smallCave) && fill i then n <- n + 1
            more <- n > 0

    let filledNow, carvedNow = finishColours orig px filled carved
    { Pixels = px
      Filled = filledNow |> Array.filter id |> Array.length
      Opened = carvedNow |> Array.filter id |> Array.length
      Tunnels = tunnels
      CutOffPads = (check px).CutOffPads }

// ─── Clearing areas ────────────────────────────────────────────────────────

/// An area to clear, in map pixels (inclusive corners).
type ClearArea =
    | ClearRect of x0: int * y0: int * x1: int * y1: int
    | ClearEllipse of x0: int * y0: int * x1: int * y1: int

/// Around a cleared area: thin stubs and crumbs this close go too.
[<Literal>]
let ClearTidy = 6

/// Crumbs: wall pieces up to this size touching the tidy zone are removed.
[<Literal>]
let CrumbMax = 40

/// Slivers: pieces smaller than this with over a quarter of their edge cut.
[<Literal>]
let SliverMax = 400

let private inArea (grow: float) (area: ClearArea) i =
    let x, y = float (i % MapWidth), float (i / MapWidth)
    match area with
    | ClearRect (x0, y0, x1, y1) ->
        x >= float (min x0 x1) - grow && x <= float (max x0 x1) + grow
        && y >= float (min y0 y1) - grow && y <= float (max y0 y1) + grow
    | ClearEllipse (x0, y0, x1, y1) ->
        let cx, cy = float (x0 + x1) / 2.0, float (y0 + y1) / 2.0
        let rx, ry = float (abs (x1 - x0)) / 2.0 + grow, float (abs (y1 - y0)) / 2.0 + grow
        rx > 0.0 && ry > 0.0 && ((x - cx) / rx) ** 2.0 + ((y - cy) / ry) ** 2.0 <= 1.0

type ClearResult =
    { Pixels: byte array
      Opened: int }

/// Clear the terrain inside the areas for more flight space, then tidy the
/// cut: thin stubs and small crumbs next to it go, slivers whose edge is
/// mostly the cut go, and the new edges get the level's outline colour.
/// Pads and the map frame stay.
let clearAreas (areas: ClearArea list) (orig: byte array) : ClearResult =
    let px = Array.copy orig
    let carved = Array.zeroCreate<bool> size
    let carve i =
        if isWallPixel px[i] && not (inFrame i) then
            px[i] <- VoidColor
            carved[i] <- true
    let inside grow i = areas |> List.exists (fun a -> inArea grow a i)
    for i in 0 .. size - 1 do
        if inside 0.0 i then carve i
    let near = Array.init size (inside (float ClearTidy))
    // thin stubs where the cut crossed a thin piece
    let wall = Array.init size (fun i -> isWallPixel px[i])
    let keep = dilate (erode true wall 2) 2
    for i in 0 .. size - 1 do
        if near[i] && wall[i] && not keep[i] then carve i
    // crumbs: small pieces touching the tidy zone
    let wall = Array.init size (fun i -> isWallPixel px[i])
    let wLab, wSizes = label wall
    let crumb = Array.zeroCreate wSizes.Length
    for i in 0 .. size - 1 do
        if near[i] && wLab[i] >= 0 && wSizes[wLab[i]] <= CrumbMax then crumb[wLab[i]] <- true
    for i in 0 .. size - 1 do
        if wLab[i] >= 0 && crumb[wLab[i]] then carve i
    // slivers: small pieces whose outline is mostly the new cut
    let wall = Array.init size (fun i -> isWallPixel px[i])
    let wLab, wSizes = label wall
    let edge = Array.zeroCreate wSizes.Length
    let cutEdge = Array.zeroCreate wSizes.Length
    for i in 0 .. size - 1 do
        if wLab[i] >= 0 then
            let open' = neighbours4 i |> List.filter (fun j -> not wall[j])
            if not open'.IsEmpty then
                edge[wLab[i]] <- edge[wLab[i]] + 1
                if open' |> List.exists (fun j -> carved[j]) then cutEdge[wLab[i]] <- cutEdge[wLab[i]] + 1
    for i in 0 .. size - 1 do
        let c = wLab[i]
        if c >= 0 && wSizes[c] < SliverMax && cutEdge[c] * 4 > edge[c] then carve i
    let _, carvedNow = finishColours orig px (Array.zeroCreate size) carved
    { Pixels = px; Opened = carvedNow |> Array.filter id |> Array.length }
