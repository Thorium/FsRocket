/// FsRocket — Browser entry point (Fable)
/// Sets up the canvas, fetches the level, runs the 36 FPS game loop on
/// requestAnimationFrame, and maps keyboard input to the shared game logic.
///
/// Controls:
///   P1: Arrows/NumPad + RShift/Enter (Thrust/Turn/Down/Fire)   weapon switch = 9
///   P2: W/A/D/S + Tab                                          weapon switch = 1
///   P3: I/J/L/K + B                                            weapon switch = 6
///   SPACE start · ESC reset · 1-4 players (menu) · F5/F6 level · F7/F8 CPU · F9 base-rule
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
[<Emit("$0.width")>]
let private canvasW (c: obj) : int = jsNative
[<Emit("$0.height")>]
let private canvasH (c: obj) : int = jsNative
[<Emit("document.addEventListener($0, $1)")>]
let private onDocument (event: string) (handler: obj -> unit) : unit = jsNative
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
            if seg.Contains "." then p.Substring(0, i + 1) else p + "/"
    basePath + file

// ─── State ───────────────────────────────────────────────────────────────
let private canvas = getEl "screen"
let private ctx = get2dCtx canvas
let private viewW = canvasW canvas
let private viewH = canvasH canvas

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
        gs <- { gs with Level = Some levels[i].Data; LevelFilePath = levels[i].Data.Name; RoundActive = false; TerrainDirty = true }

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
        gs <- { gs with TerrainDirty = true }

let private loadDefaultLevel () =
    async {
        try
            let! buf = fetchArrayBuffer (assetUrl "CLASSIC.LEV") |> Async.AwaitPromise
            addLevel (loadLevelFromBytes "CLASSIC" (toBytes buf)) false
        with _ -> ()
    }
    |> Async.StartImmediate

/// Handle a user-picked .LEV file: decode it, add it to the rotation, make it
/// active. Same byte[] path as the bundled fetch, so it just plays.
let private onLevelFile (e: obj) =
    let input = evTarget e
    let raw = firstFileName input
    let name =
        let stem = if raw.Contains "." then raw.Substring(0, raw.LastIndexOf ".") else raw
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
let private cycleWeapon (playerIdx: int) =
    if playerIdx < gs.NumPlayers then
        let p = gs.Players[playerIdx]
        let allowed = not gs.WeaponSwitchOnlyOnBase || not gs.RoundActive || p.OnBase
        if allowed then
            let mutable wt = (int p.SpecialWeapon + 1) % weapons.Length
            let mutable guard = 0
            while (not weapons[wt].Enabled || wt = int WeaponType.Cannon) && guard < weapons.Length do
                wt <- (wt + 1) % weapons.Length
                guard <- guard + 1
            let np = { p with SpecialWeapon = enum<WeaponType> wt; SpecialReloadTimer = 0 }
            gs <- { gs with Players = gs.Players |> List.mapi (fun i pl -> if i = playerIdx then np else pl) }

// ─── Input ─────────────────────────────────────────────────────────────────
let private mapInputs () =
    let has (c: string) = keys.Contains c
    let players =
        gs.Players
        |> List.mapi (fun i p ->
            if p.IsCpu then p
            else
                match i with
                | 0 when gs.NumPlayers >= 1 ->
                    { p with
                        KeyUp = has "ArrowUp" || has "Numpad8"
                        KeyLeft = has "ArrowLeft" || has "Numpad4"
                        KeyRight = has "ArrowRight" || has "Numpad6"
                        KeyDown = has "ArrowDown" || has "Numpad5"
                        KeyFire = has "ShiftRight" || has "Enter" }
                | 1 when gs.NumPlayers >= 2 ->
                    { p with
                        KeyUp = has "KeyW"
                        KeyLeft = has "KeyA"
                        KeyRight = has "KeyD"
                        KeyDown = has "KeyS"
                        KeyFire = has "Tab" }
                | 2 when gs.NumPlayers >= 3 ->
                    { p with
                        KeyUp = has "KeyI"
                        KeyLeft = has "KeyJ"
                        KeyRight = has "KeyL"
                        KeyDown = has "KeyK"
                        KeyFire = has "KeyB" }
                | _ -> p)
    gs <- { gs with Players = players }

let private onKeyDown (e: obj) =
    let code = evCode e
    keys.Add code |> ignore
    preventDefault e
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
    // Weapon switch near each player's hand: P1=9, P2=1, P3=6
    | "Digit9" when gs.RoundActive -> cycleWeapon 0
    | "Digit1" when gs.RoundActive -> cycleWeapon 1
    | "Digit6" when gs.RoundActive -> cycleWeapon 2
    | "F9" -> gs <- { gs with WeaponSwitchOnlyOnBase = not gs.WeaponSwitchOnlyOnBase }
    | "F5" -> switchLevel -1
    | "F6" -> switchLevel 1
    | "F7" -> cpuCount <- max 0 (cpuCount - 1); applyPlayerCount ()
    | "F8" -> cpuCount <- min (4 - humanCount) (cpuCount + 1); applyPlayerCount ()
    | _ -> ()

let private onKeyUp (e: obj) =
    keys.Remove(evCode e) |> ignore

// ─── Game loop (fixed 36 FPS step, rendered every animation frame) ──────────
let private frameMs = 28.0
let mutable private lastTime = 0.0
let mutable private acc = 0.0

let rec private loop (ts: float) =
    let dt = if lastTime = 0.0 then frameMs else ts - lastTime
    lastTime <- ts
    acc <- acc + min dt 200.0   // clamp to avoid spiral-of-death after a stall
    while acc >= frameMs do
        mapInputs ()
        if gs.RoundActive then gs <- gameTick gs
        acc <- acc - frameMs
    renderFrame ctx gs viewW viewH
    if gs.TerrainDirty then gs <- { gs with TerrainDirty = false }
    requestFrame loop

// ─── Bootstrap ──────────────────────────────────────────────────────────────
onDocument "keydown" onKeyDown
onDocument "keyup" onKeyUp
let private levelFileInput = getEl "level-file"
if not (isNull levelFileInput) then onElement levelFileInput "change" onLevelFile
applyPlayerCount ()
loadDefaultLevel ()
requestFrame loop
