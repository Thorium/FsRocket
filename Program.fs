/// FsRocket — Browser entry point (Fable)
/// Sets up the canvas, fetches the level, runs the 36 FPS game loop on
/// requestAnimationFrame, and maps keyboard input to the shared game logic.
///
/// Controls:
///   P1: W/A/D/S + Tab (Thrust/Turn/Down/Fire)                  weapon switch = 1 back / 2 fwd
///   P2: Arrows/NumPad + RShift/Enter                           weapon switch = 8 back / 9 fwd
///   P3: T/F/H/G + Y                                            weapon switch = 4 back / 5 fwd
///   P4: I/J/L/K + B                                            weapon switch = 6 back / 7 fwd
///   SPACE start · ESC reset · 1-4 players (menu) · F4 respawn-rule (menu) · F5/F6 level · F7/F8 CPU · F9 base-rule
module FsRocket.Program

open System
open Fable.Core
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types
open FsRocket.Entities
open FsRocket.Game
open FsRocket.Renderer

// ─── DOM / JS interop (raw, to stay independent of binding versions) ─────
[<Emit("document.getElementById($0)")>]
let private getEl (id: string) : obj = jsNative
[<Emit("$0.getContext('2d')")>]
let private get2dCtx (canvas: obj) : obj = jsNative
[<Emit("$0.getBoundingClientRect().width")>]
let private clientW (c: obj) : float = jsNative
[<Emit("$0.getBoundingClientRect().height")>]
let private clientH (c: obj) : float = jsNative
[<Emit("$0.width = $1")>]
let private setCanvasW (c: obj) (w: float) : unit = jsNative
[<Emit("$0.height = $1")>]
let private setCanvasH (c: obj) (h: float) : unit = jsNative
[<Emit("window.devicePixelRatio || 1")>]
let private getDpr () : float = jsNative
[<Emit("$0.setTransform($1, 0, 0, $1, 0, 0)")>]
let private setCtxScale (ctx: obj) (s: float) : unit = jsNative
[<Emit("document.addEventListener($0, $1)")>]
let private onDocument (event: string) (handler: obj -> unit) : unit = jsNative
[<Emit("window.addEventListener($0, $1)")>]
let private onWindow (event: string) (handler: obj -> unit) : unit = jsNative
[<Emit("window.requestAnimationFrame($0)")>]
let private requestFrame (cb: float -> unit) : unit = jsNative
[<Emit("$0.code")>]
let private evCode (e: obj) : string = jsNative
[<Emit("$0.preventDefault()")>]
let private preventDefault (e: obj) : unit = jsNative
[<Emit("window.location.pathname")>]
let private locationPath () : string = jsNative
[<Emit("fetch($0).then(r => r.arrayBuffer())")>]
let private fetchArrayBuffer (url: string) : JS.Promise<JS.ArrayBuffer> = jsNative
[<Emit("$0.addEventListener($1, $2)")>]
let private onElement (el: obj) (event: string) (handler: obj -> unit) : unit = jsNative
[<Emit("$0.target")>]
let private evTarget (e: obj) : obj = jsNative
/// Name of the first selected file on a file <input>, or "" if none.
[<Emit("($0.files && $0.files.length) ? $0.files[0].name : ''")>]
let private firstFileName (input: obj) : string = jsNative
/// Read the first selected file of a file <input> as bytes (FileReader →
/// ArrayBuffer → Uint8Array), then invoke the callback. Mirrors fame-boy's ROM
/// upload; a Uint8Array is exactly Fable's byte[].
[<Emit("(function(input, cb){ if (input.files && input.files.length > 0) { var r = new FileReader(); r.onload = function(){ cb(new Uint8Array(r.result)); }; r.readAsArrayBuffer(input.files[0]); } })($0, $1)")>]
let private readFileBytes (input: obj) (cb: byte[] -> unit) : unit = jsNative

let private toBytes (buf: JS.ArrayBuffer) : byte[] =
    let arr = JS.Constructors.Uint8Array.Create(buf)
    Array.init (int arr.length) (fun i -> arr[i])

/// Resolve an asset path relative to the document, so it works both at the site
/// root and under a GitHub Pages project sub-path (/<repo>/).
let private assetUrl (file: string) =
    let p = locationPath ()
    let basePath =
        if p.EndsWith "/" then p
        else
            let i = p.LastIndexOf "/"
            let seg = p.Substring(i + 1)
            if seg.Contains '.' then p.Substring(0, i + 1) else p + "/"
    basePath + file

// ─── State ───────────────────────────────────────────────────────────────
let private canvas = getEl "screen"
let private ctx = get2dCtx canvas

/// Design (logical) resolution: the game is laid out as if on the original
/// 960x600 canvas and zoomed up uniformly to fill the window, so a bigger
/// window means bigger pixels — not more visible map (limited per-viewport
/// visibility is part of the game design).
[<Literal>]
let private designW = 960.0
[<Literal>]
let private designH = 600.0

/// Canvas size in logical pixels: the fitted axis is exactly 960 (or 600),
/// the other one at least that, so there are no letterbox bars. All drawing
/// code works in this logical space; the context transform bakes in
/// devicePixelRatio * zoom for crisp HiDPI rendering.
let mutable private viewW = 0
let mutable private viewH = 0

/// Render only when something could have changed (a game step ran, a key was
/// pressed, the window resized, a level loaded) — the simulation is a fixed
/// 36 Hz step, so re-rendering identical state at 120+ Hz monitor refresh
/// just burns CPU/GPU.
let mutable private needsRender = true

/// Size the canvas backing store from its displayed (CSS) size and derive the
/// logical resolution. Called at startup and whenever the window is resized
/// (including F11 fullscreen toggles, which fire a resize event).
let private resizeCanvas () =
    let cw = clientW canvas
    let ch = clientH canvas

    if cw > 0.0 && ch > 0.0 then
        let dpr = getDpr ()
        let zoomBase = min (cw / designW) (ch / designH)
        // On very wide screens the logical width would grow enough for a
        // single-player viewport to reveal the whole 320px-wide map. Above
        // 2500 CSS px, zoom in a bit more (smoothly, no jump at the
        // threshold) so the level is never shown fully.
        let boost = 1.0 + max 0.0 (cw - 2500.0) / 2500.0
        let zoom = zoomBase * boost
        viewW <- int (cw / zoom)
        viewH <- int (ch / zoom)
        setCanvasW canvas (floor (cw * dpr))
        setCanvasH canvas (floor (ch * dpr))
        setCtxScale ctx (dpr * zoom)
        needsRender <- true   // resizing clears the canvas backing store

let mutable private gs = createGameState 2
let mutable private humanCount = 2
let mutable private cpuCount = 0
let private keys = System.Collections.Generic.HashSet<string>()

/// A level that has been loaded into the rotation, with a pristine copy of its
/// pixels so ammo damage can be reset at the start of each round (the desktop
/// builds re-read the .LEV file for this).
type private LoadedLevel = { Data: LevelData; Pristine: byte[] }
let private levels = System.Collections.Generic.List<LoadedLevel>()
let mutable private current = 0
/// Once the player uploads their own map, the bundled demo level is dropped and
/// the rotation is made up of uploaded maps (F5/F6 cycles them).
let mutable private hasUserLevels = false

let private totalPlayers () = min 4 (humanCount + cpuCount)
let private applyPlayerCount () =
    gs <- { gs with NumPlayers = totalPlayers (); CpuCount = cpuCount; RoundActive = false }

let private activate (i: int) =
    if i >= 0 && i < levels.Count then
        current <- i
        markAllDirty ()   // wholesale pixel change — repaint the terrain bitmap
        gs <- { gs with Level = Some levels[i].Data; LevelFilePath = levels[i].Data.Name; RoundActive = false; TerrainDirty = true }
        needsRender <- true

/// Add a level to the rotation and make it active. The first uploaded level
/// replaces the bundled demo so the rotation becomes the player's own maps.
let private addLevel (lvl: LevelData) (fromUpload: bool) =
    if fromUpload && not hasUserLevels then
        levels.Clear()
        hasUserLevels <- true
    levels.Add { Data = lvl; Pristine = Array.copy lvl.Pixels }
    activate (levels.Count - 1)

/// Reset the active level's terrain to its pristine state (called before a round).
let private resetActiveTerrain () =
    if current >= 0 && current < levels.Count then
        let ll = levels[current]
        Array.blit ll.Pristine 0 ll.Data.Pixels 0 ll.Pristine.Length
        markAllDirty ()   // wholesale pixel change — repaint the terrain bitmap
        gs <- { gs with TerrainDirty = true }

/// The .LEV maps shipped in public/ and fetched at startup (F5/F6 cycles them).
let private bundledLevels =
    [ "HUNAJA"; "JAATIKKO"; "KARKKI"; "KIERTO"; "KORALLI"
      "NEBULA"; "SIENI"; "TEHDAS"; "TULIVUOR"; "VIIDAKKO" ]

let private loadBundledLevels () =
    async {
        for name in bundledLevels do
            // Stop if the player has already switched to their own uploads.
            if not hasUserLevels then
                try
                    let! buf = fetchArrayBuffer (assetUrl (name + ".LEV")) |> Async.AwaitPromise
                    addLevel (loadLevelFromBytes name (toBytes buf)) false
                with _ -> ()
        // addLevel activates the most recently added map; start on the first.
        if not hasUserLevels && levels.Count > 0 then activate 0
    }
    |> Async.StartImmediate

/// Handle a user-picked .LEV file: decode it, add it to the rotation, make it
/// active. Same byte[] path as the bundled fetch, so it just plays.
let private onLevelFile (e: obj) =
    let input = evTarget e
    let raw = firstFileName input
    let name =
        let stem = if raw.Contains '.' then raw.Substring(0, raw.LastIndexOf ".") else raw
        if stem = "" then "UPLOAD" else stem.ToUpperInvariant()
    readFileBytes input (fun bytes ->
        try addLevel (loadLevelFromBytes name bytes) true
        with _ -> ())

/// F5/F6 — cycle through the loaded levels.
let private switchLevel (delta: int) =
    if levels.Count > 0 then
        activate (((current + delta) % levels.Count + levels.Count) % levels.Count)

/// Cycle a player's special weapon. During a live round this is only allowed
/// while the ship is parked on a base (unless the F9 rule is toggled off).
let private cycleWeapon (playerIdx: int) (dir: int) =
    if playerIdx < gs.NumPlayers then
        let p = gs.Players[playerIdx]
        let allowed = (not (gs.WeaponSwitchOnlyOnBase && gs.RoundActive)) || p.OnBase
        if allowed then
            let len = weapons.Length
            let mutable wt = (int p.SpecialWeapon + dir + len) % len
            let mutable guard = 0
            while (not weapons[wt].Enabled || wt = int WeaponType.Cannon) && guard < len do
                wt <- (wt + dir + len) % len
                guard <- guard + 1
            // Switching the special also drops the magnofilter field — otherwise
            // it stays on forever with its toggle-off action swapped away
            let np = { p with SpecialWeapon = enum<WeaponType> wt; SpecialReloadTimer = 0
                              Flags = p.Flags &&& ~~~PlayerFlags.Magno }
            gs <- { gs with Players = gs.Players |> List.mapi (fun i pl -> if i = playerIdx then np else pl) }

// ─── Gamepads ───────────────────────────────────────────────────────────────
// Connected gamepads claim human player slots in order: pad 1 drives P1,
// pad 2 drives P2, and so on; the remaining humans keep their per-slot
// keyboard mappings (P2 is always arrows, etc.). Pad input is OR-merged with
// the keys, so the keyboard keeps working for a pad-driven player and with no
// pads connected nothing changes. Buttons follow the W3C "standard" mapping:
// 0=A 1=B 2=X 4=LB 5=RB 6=LT 7=RT 9=Start 12-15=DPad, axes 0/1=left stick.

/// Connected gamepads in slot order (holes from unplugged pads filtered out).
[<Emit("navigator.getGamepads ? Array.prototype.filter.call(navigator.getGamepads(), g => g && g.connected) : []")>]
let private connectedGamepads () : obj[] = jsNative
[<Emit("$0.buttons[$1] ? $0.buttons[$1].pressed : false")>]
let private padButton (pad: obj) (i: int) : bool = jsNative
[<Emit("$0.axes[$1] || 0")>]
let private padAxis (pad: obj) (i: int) : float = jsNative

[<Literal>]
let private stickDeadzone = 0.35

/// Previous LB/RB/Start state per pad slot, for edge-triggered actions
/// (weapon cycling and starting a round fire once per press, not per tick).
let mutable private padPrev = Array.create 4 (false, false, false)

/// Edge-triggered gamepad buttons: LB/RB cycle the special weapon, Start
/// begins a round from the menu. Runs once per fixed step, before mapInputs.
let private pollPadButtons () =
    let pads = if gs.GamepadsEnabled then connectedGamepads () else [||]
    for i in 0 .. min (pads.Length - 1) 3 do
        let pad = pads[i]
        let wPrev = padButton pad 4
        let wNext = padButton pad 5
        let start = padButton pad 9
        let (pw, pn, ps) = padPrev[i]
        let isHuman = i < gs.NumPlayers && not gs.Players[i].IsCpu
        if isHuman && wPrev && not pw then cycleWeapon i -1
        if isHuman && wNext && not pn then cycleWeapon i 1
        if start && not ps && not gs.RoundActive then
            resetActiveTerrain ()   // reset ammo damage before the round
            gs <- initRound gs
            needsRender <- true
        padPrev[i] <- (wPrev, wNext, start)

// ─── Input ─────────────────────────────────────────────────────────────────
let private mapInputs () =
    let has (c: string) = keys.Contains c
    let pads = if gs.GamepadsEnabled then connectedGamepads () else [||]
    let players =
        gs.Players
        |> List.mapi (fun i p ->
            if p.IsCpu then p
            else
                let p =
                    match i with
                    | 0 when gs.NumPlayers >= 1 ->
                        { p with
                            KeyUp = has "KeyW"
                            KeyLeft = has "KeyA"
                            KeyRight = has "KeyD"
                            KeyDown = has "KeyS"
                            KeyFire = has "Tab" }
                    | 1 when gs.NumPlayers >= 2 ->
                        { p with
                            KeyUp = has "ArrowUp" || has "Numpad8"
                            KeyLeft = has "ArrowLeft" || has "Numpad4"
                            KeyRight = has "ArrowRight" || has "Numpad6"
                            KeyDown = has "ArrowDown" || has "Numpad5"
                            KeyFire = has "ShiftRight" || has "Enter" }
                    | 2 when gs.NumPlayers >= 3 ->
                        { p with
                            KeyUp = has "KeyT"
                            KeyLeft = has "KeyF"
                            KeyRight = has "KeyH"
                            KeyDown = has "KeyG"
                            KeyFire = has "KeyY" }
                    | 3 when gs.NumPlayers >= 4 ->
                        { p with
                            KeyUp = has "KeyI"
                            KeyLeft = has "KeyJ"
                            KeyRight = has "KeyL"
                            KeyDown = has "KeyK"
                            KeyFire = has "KeyB" }
                    | _ -> p
                // Gamepad i drives player i, merged on top of the keys
                if i < pads.Length && i < 4 then
                    let pad = pads[i]
                    let axX = padAxis pad 0
                    let axY = padAxis pad 1
                    { p with
                        KeyUp = p.KeyUp || padButton pad 0 || padButton pad 12 || axY < -0.5
                        KeyLeft = p.KeyLeft || padButton pad 14 || axX < -stickDeadzone
                        KeyRight = p.KeyRight || padButton pad 15 || axX > stickDeadzone
                        KeyDown = p.KeyDown || padButton pad 2 || padButton pad 6
                        KeyFire = p.KeyFire || padButton pad 1 || padButton pad 7 }
                else p)
    gs <- { gs with Players = players }

let private onKeyDown (e: obj) =
    let code = evCode e
    keys.Add code |> ignore
    preventDefault e
    needsRender <- true   // menu toggles/level switches change state between steps
    match code with
    | "Escape" -> if gs.RoundActive then gs <- { gs with RoundActive = false }
    | "Space" ->
        if not gs.RoundActive then
            resetActiveTerrain ()   // reset ammo damage before the round
            gs <- initRound gs
    // Player count — menu only (number keys double as weapon-switch in a live round)
    | "Digit1" when not gs.RoundActive -> humanCount <- 1; applyPlayerCount ()
    | "Digit2" when not gs.RoundActive -> humanCount <- 2; applyPlayerCount ()
    | "Digit3" when not gs.RoundActive -> humanCount <- 3; applyPlayerCount ()
    | "Digit4" when not gs.RoundActive -> humanCount <- 4; applyPlayerCount ()
    // Weapon switch near each player's hand: P1=1/2, P2=8/9, P3=4/5, P4=6/7
    | "Digit1" when gs.RoundActive -> cycleWeapon 0 (-1)
    | "Digit2" when gs.RoundActive -> cycleWeapon 0 1
    | "Digit8" when gs.RoundActive -> cycleWeapon 1 (-1)
    | "Digit9" when gs.RoundActive -> cycleWeapon 1 1
    | "Digit4" when gs.RoundActive -> cycleWeapon 2 (-1)
    | "Digit5" when gs.RoundActive -> cycleWeapon 2 1
    | "Digit6" when gs.RoundActive -> cycleWeapon 3 (-1)
    | "Digit7" when gs.RoundActive -> cycleWeapon 3 1
    | "F9" -> gs <- { gs with WeaponSwitchOnlyOnBase = not gs.WeaponSwitchOnlyOnBase }
    | "F10" -> gs <- { gs with GamepadsEnabled = not gs.GamepadsEnabled }
    // Toggle "respawn on death" — menu only, so the rule can't flip mid-round
    | "F4" when not gs.RoundActive -> gs <- { gs with RespawnOnDeath = not gs.RespawnOnDeath }
    | "F5" -> switchLevel -1
    | "F6" -> switchLevel 1
    | "F7" -> cpuCount <- max 0 (cpuCount - 1); applyPlayerCount ()
    | "F8" -> cpuCount <- min (4 - humanCount) (cpuCount + 1); applyPlayerCount ()
    | _ -> ()

let private onKeyUp (e: obj) =
    keys.Remove(evCode e) |> ignore

// ─── Game loop (fixed 36 FPS step, rendered every animation frame) ──────────
[<Literal>]
let private frameMs = 28.0
let mutable private lastTime = 0.0
let mutable private acc = 0.0

let rec private loop (ts: float) =
    let dt = if lastTime = 0.0 then frameMs else ts - lastTime
    lastTime <- ts
    acc <- acc + min dt 200.0   // clamp to avoid spiral-of-death after a stall
    while acc >= frameMs do
        pollPadButtons ()
        mapInputs ()
        if gs.RoundActive then gs <- gameTick gs
        acc <- acc - frameMs
        needsRender <- true
    if needsRender then
        renderFrame ctx gs viewW viewH
        if gs.TerrainDirty then gs <- { gs with TerrainDirty = false }
        needsRender <- false
    requestFrame loop

// ─── Bootstrap ──────────────────────────────────────────────────────────────
onDocument "keydown" onKeyDown
onDocument "keyup" onKeyUp
onWindow "resize" (fun _ -> resizeCanvas ())
resizeCanvas ()
let private levelFileInput = getEl "level-file"
if not (isNull levelFileInput) then onElement levelFileInput "change" onLevelFile
applyPlayerCount ()
loadBundledLevels ()
requestFrame loop
