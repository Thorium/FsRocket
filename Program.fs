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
///   Touch devices: P1 vs CPU, on-screen buttons feeding the same keys
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

/// Register the offline service worker (public/sw.js) for the installable app.
/// Production builds only: Vite replaces import.meta.env.PROD, and a worker
/// caching dev-server modules would serve stale code during development.
[<Emit("(import.meta.env.PROD && 'serviceWorker' in navigator) ? navigator.serviceWorker.register($0).catch(function(){}) : void 0")>]
let private registerServiceWorker (url: string) : unit = jsNative
[<Emit("window.matchMedia && window.matchMedia('(pointer: coarse)').matches")>]
let private coarsePointer () : bool = jsNative
/// Portrait orientation, when the touch layout covers the game with the
/// "turn your device sideways" message.
[<Emit("window.matchMedia('(orientation: portrait)').matches")>]
let private isPortrait () : bool = jsNative
[<Emit("new URLSearchParams(window.location.search).get($0)")>]
let private queryParam (name: string) : string = jsNative

// ─── State ───────────────────────────────────────────────────────────────
let private canvas = getEl "screen"
let private ctx = get2dCtx canvas

/// Touch layout: one human against CPU ships, a single full-window viewport,
/// a tap menu and on-screen controls. On for coarse-pointer (phone/tablet)
/// devices; `?touch=1` / `?touch=0` forces it either way.
let private touchMode =
    match queryParam "touch" with
    | "1" -> true
    | "0" -> false
    | _ -> coarsePointer ()

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
        // Phones in landscape are wider than 16:9, so the single touch
        // viewport would also show the whole map width: zoom in until the
        // logical width is no more than a 16:9 screen's.
        let zoom =
            if touchMode then max zoomBase (cw / (designH * 16.0 / 9.0))
            else zoomBase * boost
        viewW <- int (cw / zoom)
        viewH <- int (ch / zoom)
        setCanvasW canvas (floor (cw * dpr))
        setCanvasH canvas (floor (ch * dpr))
        setCtxScale ctx (dpr * zoom)
        needsRender <- true   // resizing clears the canvas backing store

let mutable private gs = createGameState 2
let mutable private humanCount = if touchMode then 1 else 2
let mutable private cpuCount = if touchMode then 1 else 0
/// A live round frozen by the touch PAUSE button or by turning the device to
/// portrait; cleared when a round starts or ends.
let mutable private paused = false
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
    // More humans leave fewer of the 4 slots for CPUs; without the clamp the
    // CPUs would take over the last human's slot (CPUs are the last N players)
    cpuCount <- min cpuCount (4 - humanCount)
    gs <- placeForMenu { gs with NumPlayers = totalPlayers (); CpuCount = cpuCount; RoundActive = false }

let private activate (i: int) =
    if i >= 0 && i < levels.Count then
        current <- i
        markAllDirty ()   // wholesale pixel change — repaint the terrain bitmap
        gs <- placeForMenu { gs with Level = Some levels[i].Data; LevelFilePath = levels[i].Data.Name; RoundActive = false; TerrainDirty = true }
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

/// The level the game opens on: wide open space, gentle for new pilots.
let private startLevel = "VIIDAKKO"

let private loadBundledLevels () =
    async {
        for name in bundledLevels do
            // Stop if the player has already switched to their own uploads.
            if not hasUserLevels then
                try
                    let! buf = fetchArrayBuffer (assetUrl (name + ".LEV")) |> Async.AwaitPromise
                    addLevel (loadLevelFromBytes name (toBytes buf)) false
                with _ -> ()
        // addLevel activates the most recently added map; start on startLevel
        // (or the first map, if it failed to load).
        if not hasUserLevels && levels.Count > 0 then
            activate (max 0 (levels.FindIndex(fun l -> l.Data.Name = startLevel)))
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

/// Whether a ship may switch its special right now (the F9 rule).
let private canSwitchWeapon (p: Player) =
    match gs.WeaponSwitch with
    | Anytime -> true
    | OnlyOnBase -> not gs.RoundActive || p.OnBase
    | OnlyAtStart -> not gs.RoundActive

/// Cycle a player's special weapon, when the F9 rule allows it.
let private cycleWeapon (playerIdx: int) (dir: int) =
    if playerIdx < gs.NumPlayers then
        let p = gs.Players[playerIdx]
        if canSwitchWeapon p then
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

// ─── Weapon choice before the round (F9 rule: Game start) ───────────────────
// START opens a dialog where each human picks a special, or Random; the round
// then begins with those weapons and nobody switches until it ends.

/// A weapon choice: Random, or the int value of a WeaponType.
[<Literal>]
let private RandomWeapon = -1

/// The dialog's options in order: Random, then every special weapon.
let private weaponChoiceOptions = Array.append [| RandomWeapon |] (specialWeaponPool |> Array.map int)

/// Each player's choice, kept between rounds.
let private weaponChoices = Array.create 4 RandomWeapon
let mutable private choosingWeapons = false
/// The desktop Keys & controls dialog is open.
let mutable private showingKeys = false

let private humanPlayers () = gs.NumPlayers - gs.CpuCount

/// Step a player's choice through the options (their weapon keys / LB, RB).
let private cycleChoice (playerIdx: int) (dir: int) =
    if playerIdx < humanPlayers () then
        let n = weaponChoiceOptions.Length
        let i = max 0 (Array.IndexOf(weaponChoiceOptions, weaponChoices[playerIdx]))
        weaponChoices[playerIdx] <- weaponChoiceOptions[(i + dir + n) % n]

/// Start the round; under the Game start rule, arm each human with their
/// choice (Random draws one now).
let private beginRound () =
    choosingWeapons <- false
    resetActiveTerrain ()   // reset ammo damage before the round
    gs <- initRound gs
    if gs.WeaponSwitch = OnlyAtStart then
        let players =
            gs.Players
            |> List.mapi (fun i p ->
                if i < gs.NumPlayers && not p.IsCpu then
                    let wt =
                        if weaponChoices[i] = RandomWeapon then randomSpecialWeapon gs.Rng p.SpecialWeapon
                        else enum<WeaponType> weaponChoices[i]
                    { p with SpecialWeapon = wt }
                else p)
        gs <- { gs with Players = players }

/// START (Space, a pad's Start, the START buttons): under the Game start rule
/// it opens the weapon dialog first, and from the dialog it begins the round.
let private startPressed () =
    if not gs.RoundActive then
        if choosingWeapons || gs.WeaponSwitch <> OnlyAtStart || humanPlayers () = 0 then beginRound ()
        else choosingWeapons <- true

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
        let step dir = if choosingWeapons then cycleChoice i dir else cycleWeapon i dir
        if isHuman && wPrev && not pw then step -1
        if isHuman && wNext && not pn then step 1
        if start && not ps && not gs.RoundActive then
            startPressed ()
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

/// Keys while the weapon dialog is open: each player's weapon keys step their
/// choice, Space starts the round, Escape goes back to the menu.
let private weaponDialogKey (code: string) =
    match code with
    | "Space" -> beginRound ()
    | "Escape" -> choosingWeapons <- false
    | "Digit1" -> cycleChoice 0 -1
    | "Digit2" -> cycleChoice 0 1
    | "Digit8" -> cycleChoice 1 -1
    | "Digit9" -> cycleChoice 1 1
    | "Digit4" -> cycleChoice 2 -1
    | "Digit5" -> cycleChoice 2 1
    | "Digit6" -> cycleChoice 3 -1
    | "Digit7" -> cycleChoice 3 1
    | _ -> ()

/// One-shot action bound to a key code in the menu or a round (menu toggles,
/// level/player changes, starting a round, weapon cycling).
let private menuKeyAction (code: string) =
    match code with
    | "Escape" ->
        paused <- false
        showingKeys <- false
        if gs.RoundActive then gs <- { gs with RoundActive = false }
    // Desktop Keys & controls dialog: its button or F1 toggles it
    | "keys" | "F1" when not gs.RoundActive -> showingKeys <- not showingKeys
    // Touch PAUSE button (no keyboard key produces this code)
    | "pause" when gs.RoundActive -> paused <- not paused
    | "Space" ->
        paused <- false
        startPressed ()
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
    | "F9" ->
        let next =
            match gs.WeaponSwitch with
            | OnlyOnBase -> Anytime
            | Anytime -> OnlyAtStart
            | OnlyAtStart -> OnlyOnBase
        gs <- { gs with WeaponSwitch = next }
    | "F10" -> gs <- { gs with GamepadsEnabled = not gs.GamepadsEnabled }
    // Toggle "respawn on death" — menu only, so the rule can't flip mid-round
    | "F4" when not gs.RoundActive -> gs <- { gs with RespawnOnDeath = not gs.RespawnOnDeath }
    | "F5" -> switchLevel -1
    | "F6" -> switchLevel 1
    | "F7" -> cpuCount <- max 0 (cpuCount - 1); applyPlayerCount ()
    | "F8" -> cpuCount <- min (4 - humanCount) (cpuCount + 1); applyPlayerCount ()
    | _ -> ()

/// A key code's one-shot action (key presses, taps and clicks alike).
let private keyAction (code: string) =
    needsRender <- true   // menu toggles/level switches change state between steps
    if choosingWeapons then weaponDialogKey code else menuKeyAction code

/// Hold a key down: the held set feeds mapInputs, then the key's action runs.
let private pressKey (code: string) =
    keys.Add code |> ignore
    keyAction code

let private onKeyDown (e: obj) =
    preventDefault e
    pressKey (evCode e)

let private onKeyUp (e: obj) =
    keys.Remove(evCode e) |> ignore

// ─── Touch controls ─────────────────────────────────────────────────────────
// The touch layout drives P1 through the same held-key set as the keyboard:
// data-key buttons (turn left/right, thrust, fire, special, weapon cycle) hold
// their key while a finger is on them, and sliding onto another button swaps
// keys; data-action buttons run keyAction once per tap.

[<Emit("document.body")>]
let private body () : obj = jsNative
[<Emit("$0.classList.toggle($1, $2)")>]
let private setClass (el: obj) (cls: string) (on: bool) : unit = jsNative
[<Emit("$0.textContent = $1")>]
let private setText (el: obj) (s: string) : unit = jsNative
[<Emit("($0 && $0.closest) ? $0.closest($1) : null")>]
let private closest (el: obj) (selector: string) : obj = jsNative
[<Emit("$0.getAttribute($1) || ''")>]
let private attr (el: obj) (name: string) : string = jsNative
[<Emit("document.elementFromPoint($0, $1)")>]
let private elementAt (x: float) (y: float) : obj = jsNative
[<Emit("$0 === $1")>]
let private sameEl (a: obj) (b: obj) : bool = jsNative
[<Emit("$0.pointerId")>]
let private pointerId (e: obj) : int = jsNative
[<Emit("$0.clientX")>]
let private clientX (e: obj) : float = jsNative
[<Emit("$0.clientY")>]
let private clientY (e: obj) : float = jsNative
[<Emit("document.activeElement && document.activeElement.blur && document.activeElement.blur()")>]
let private blurActive () : unit = jsNative
/// Fullscreen + landscape lock where the browser allows it (Android, iPad —
/// older iPads only have the webkit-prefixed call); a no-op on iPhone, whose
/// Safari has no fullscreen for pages. Must run inside a tap handler.
/// Never throws: older browsers return no promise from requestFullscreen.
[<Emit("(function(){ try { var d = document.documentElement; var req = d.requestFullscreen || d.webkitRequestFullscreen; if (!(document.fullscreenElement || document.webkitFullscreenElement) && req) { Promise.resolve(req.call(d)).then(function(){ if (screen.orientation && screen.orientation.lock) return screen.orientation.lock('landscape'); }).catch(function(){}); } } catch (e) {} })()")>]
let private enterFullscreenLandscape () : unit = jsNative
/// An iPhone running the game in a browser tab rather than from the Home
/// Screen: the browser bars stay, and only Add to Home Screen gives the game
/// the full screen.
[<Emit("/iPhone|iPod/.test(navigator.userAgent) && !navigator.standalone && !(window.matchMedia && window.matchMedia('(display-mode: standalone), (display-mode: fullscreen)').matches)")>]
let private iPhoneBrowserTab () : bool = jsNative
[<Emit("$0.prompt()")>]
let private showInstallPrompt (prompt: obj) : unit = jsNative

/// The browser's install prompt (Android / desktop Chrome, Edge), captured on
/// `beforeinstallprompt` and shown by the rotate screen's Install as app button.
let mutable private installPrompt: obj = null

/// Show the install prompt (from a tap); a prompt can be shown only once.
let private promptInstall () =
    if not (isNull installPrompt) then
        showInstallPrompt installPrompt
        installPrompt <- null
        setClass (body ()) "can-install" false

/// Finger → data-key button it is holding.
let private touchHeld = System.Collections.Generic.Dictionary<int, obj>()

/// Release `el`'s key and highlight unless another finger still holds it.
let private releaseButton (el: obj) =
    if not (touchHeld.Values |> Seq.exists (sameEl el)) then
        keys.Remove(attr el "data-key") |> ignore
        setClass el "down" false

/// Point finger `id` at button `el` (null = none), swapping held keys.
let private holdButton (id: int) (el: obj) =
    let prev = match touchHeld.TryGetValue id with | true, p -> p | _ -> null
    if not (sameEl prev el) then
        if not (isNull prev) then
            touchHeld.Remove id |> ignore
            releaseButton prev
        if not (isNull el) then
            touchHeld[id] <- el
            setClass el "down" true
            pressKey (attr el "data-key")

let private releaseAllTouches () =
    for el in Seq.toArray touchHeld.Values do
        setClass el "down" false
        keys.Remove(attr el "data-key") |> ignore
    touchHeld.Clear()

/// Release the buttons held in one player's control set (the others stay).
let private releaseTouchesIn (playerSet: obj) =
    for KeyValue(id, el) in Seq.toArray touchHeld do
        if sameEl (closest el ".tc-player") playerSet then
            touchHeld.Remove id |> ignore
            releaseButton el

[<Emit("$0.touches.length")>]
let private touchCount (e: obj) : int = jsNative
[<Emit("document.hidden")>]
let private pageHidden () : bool = jsNative
[<Emit("$0.style.setProperty($1, $2)")>]
let private setStyleVar (el: obj) (name: string) (value: string) : unit = jsNative
[<Emit("document.querySelector($0)")>]
let private queryDoc (selector: string) : obj = jsNative

let private onPointerDown (e: obj) =
    let button = closest (evTarget e) "[data-key]"
    if not (isNull button) then
        preventDefault e
        holdButton (pointerId e) button

let private onPointerMove (e: obj) =
    let id = pointerId e
    if touchHeld.ContainsKey id then
        holdButton id (closest (elementAt (clientX e) (clientY e)) "[data-key]")

let private onPointerUp (e: obj) =
    holdButton (pointerId e) null

/// Per human player (P1, P2): their control set and SPECIAL button, and
/// whether their ship was alive at the last sync (to catch the moment it dies).
let private playerSets = [| queryDoc ".tc-p1"; queryDoc ".tc-p2" |]
let private specialButtons = [| queryDoc ".tc-p1 [data-key=KeyS]"; queryDoc ".tc-p2 [data-key=ArrowDown]" |]
let private wasAlive = [| true; true |]

/// Show each human player's state on their controls: the set dims while the
/// ship is down (and lets go of its buttons the moment it dies, so a held key
/// can't carry over the respawn), and SPECIAL shows as unavailable while it
/// can't fire, filling up (--charge, 0..1) as it reloads.
let private syncPlayerControls () =
    for i in 0 .. min 1 (humanCount - 1) do
        let p = gs.Players[i]
        if wasAlive[i] && not p.Alive then releaseTouchesIn playerSets[i]
        wasAlive[i] <- p.Alive
        setClass playerSets[i] "dead" (not p.Alive)
        let stunned = p.Flags.HasFlag PlayerFlags.Stunned
        let ready = p.Alive && p.SpecialReloadTimer = 0 && not stunned
        let reload = max 1 (getWeapon p.SpecialWeapon).ReloadTicks
        let charge = if ready then 1.0 else max 0.0 (min 1.0 (1.0 - float p.SpecialReloadTimer / float reload))
        setClass specialButtons[i] "unavailable" (not ready)
        setStyleVar specialButtons[i] "--charge" (string charge)

[<Emit("Array.from(document.querySelectorAll($0))")>]
let private queryAll (selector: string) : obj[] = jsNative

/// Menu fields (tap menu and desktop menu): each `data-value` element shows one
/// setting, each `data-choice` button is selected when it matches.
let private menuValueFields = queryAll "[data-value]"
let private menuChoiceButtons = queryAll "[data-choice]"
let mutable private shownPlaying = false

let private menuValue (name: string) =
    match name with
    | "level" -> match gs.Level with Some lv -> lv.Name | None -> "No terrain"
    | "cpu" -> string cpuCount
    | "respawn" -> if gs.RespawnOnDeath then "ON" else "OFF"
    | "base" ->
        match gs.WeaponSwitch with
        | OnlyOnBase -> "On a base"
        | Anytime -> "Anytime"
        | OnlyAtStart -> "Game start"
    | "gamepads" -> if gs.GamepadsEnabled then "ON" else "OFF"
    | _ -> ""

// Weapon dialog (index.html #weapon-dialog): a <select> per player, filled once
// with Random and every special weapon; a pick there updates weaponChoices.
[<Emit("$0.innerHTML = $1")>]
let private setHtml (el: obj) (html: string) : unit = jsNative
[<Emit("$0.value")>]
let private selectValue (el: obj) : string = jsNative
[<Emit("$0.value = $1")>]
let private setSelectValue (el: obj) (value: string) : unit = jsNative

let private weaponSelects = queryAll ".wd-select"
let private weaponRows = queryAll ".wd-row"

do
    let optionsHtml =
        weaponChoiceOptions
        |> Array.map (fun v ->
            let name = if v = RandomWeapon then "Random" else weapons[v].Name
            $"<option value=\"{v}\">{name}</option>")
        |> String.concat ""
    for sel in weaponSelects do
        setHtml sel optionsHtml

let private onWeaponPicked (e: obj) =
    let sel = evTarget e
    match attr sel "data-player" with
    | "" -> ()
    | p -> weaponChoices[int p] <- int (selectValue sel)

/// Show the dialog while choosing: one row per human, each on its choice.
let private syncWeaponDialog () =
    setClass (body ()) "choosing" choosingWeapons
    if choosingWeapons then
        weaponRows |> Array.iteri (fun i row -> setClass row "unused" (i >= humanPlayers ()))
        weaponSelects
        |> Array.iteri (fun i sel ->
            let v = string weaponChoices[i]
            if selectValue sel <> v then setSelectValue sel v)

/// Touch WPN buttons dim while the F9 rule doesn't let that ship switch.
let private weaponButtons =
    [| queryAll ".tc-p1 [data-key=Digit1], .tc-p1 [data-key=Digit2]"
       queryAll ".tc-p2 [data-key=Digit8], .tc-p2 [data-key=Digit9]" |]

let private syncWeaponButtons () =
    for i in 0 .. min 1 (humanCount - 1) do
        let blocked = not (canSwitchWeapon gs.Players[i])
        for b in weaponButtons[i] do
            setClass b "unavailable" blocked

/// Mirror game state into the menus and the body.playing class (which swaps
/// menu ↔ round: the touch controls, or nothing over the desktop canvas).
/// Called after each rendered frame and tap.
let private syncMenuUi () =
    syncWeaponDialog ()
    setClass (body ()) "show-keys" showingKeys
    if gs.RoundActive && touchMode then syncWeaponButtons ()
    if gs.RoundActive <> shownPlaying then
        shownPlaying <- gs.RoundActive
        setClass (body ()) "playing" gs.RoundActive
        if not gs.RoundActive then releaseAllTouches ()
    setClass (body ()) "paused" (gs.RoundActive && paused)
    if gs.RoundActive && touchMode then syncPlayerControls ()
    if not gs.RoundActive then
        for el in menuValueFields do
            setText el (menuValue (attr el "data-value"))
        for el in menuChoiceButtons do
            setClass el "selected" (attr el "data-choice" = $"players-{humanCount}")
        // Split touch controls for 2+ humans (3-4 via a keyboard still get P1/P2 sets)
        setClass (body ()) "two-players" (humanCount >= 2)

let private onTap (e: obj) =
    let button = closest (evTarget e) "[data-action]"
    if not (isNull button) then
        let code = attr button "data-action"
        if code = "Escape" || code = "pause" then releaseAllTouches ()
        keyAction code
        if code = "Space" && touchMode then enterFullscreenLandscape ()
        if code = "install" then promptInstall ()
        syncMenuUi ()   // labels now, not on the next animation frame
        blurActive ()   // a focused button would also "click" on a keyboard Space

// ─── Game loop (fixed 36 FPS step, rendered every animation frame) ──────────
[<Literal>]
let private frameMs = 28.0
let mutable private lastTime = 0.0
let mutable private acc = 0.0

let rec private loop (ts: float) =
    let dt = if lastTime = 0.0 then frameMs else ts - lastTime
    lastTime <- ts
    acc <- acc + min dt 200.0   // clamp to avoid spiral-of-death after a stall
    // Touch layout in portrait: the rotate message hides the game, so pause a
    // live round; it stays paused after rotating back until PAUSE is tapped
    if touchMode && gs.RoundActive && not paused && isPortrait () then
        paused <- true
        releaseAllTouches ()
        needsRender <- true
    while acc >= frameMs do
        pollPadButtons ()
        mapInputs ()
        if gs.RoundActive && not paused then gs <- gameTick gs
        acc <- acc - frameMs
        needsRender <- true
    if needsRender then
        // Touch layout: viewports only for the humans, not the CPU ships
        let views = if touchMode then humanCount else gs.NumPlayers
        renderFrame ctx gs views paused viewW viewH
        syncMenuUi ()
        if gs.TerrainDirty then gs <- { gs with TerrainDirty = false }
        needsRender <- false
    requestFrame loop

// ─── Bootstrap ──────────────────────────────────────────────────────────────
onDocument "keydown" onKeyDown
onDocument "keyup" onKeyUp
onWindow "resize" (fun _ -> resizeCanvas ())
// Menu buttons (tap menu and desktop menu)
onDocument "click" onTap
// Weapon dialog drop-downs
onDocument "change" onWeaponPicked
if touchMode then
    setClass (body ()) "touch" true
    setClass (body ()) "iphone-tab" (iPhoneBrowserTab ())
    // Install as app: browsers with an install API hand over a prompt to show
    // from a tap; it goes away once the app is installed
    onWindow "beforeinstallprompt" (fun e ->
        preventDefault e
        installPrompt <- e
        setClass (body ()) "can-install" true)
    onWindow "appinstalled" (fun _ ->
        installPrompt <- null
        setClass (body ()) "can-install" false)
    onDocument "pointerdown" onPointerDown
    onDocument "pointermove" onPointerMove
    onDocument "pointerup" onPointerUp
    onDocument "pointercancel" onPointerUp
    onDocument "contextmenu" preventDefault   // long-press menus
    // A backgrounded tab never delivers pointerup: drop every held key
    onWindow "blur" (fun _ -> releaseAllTouches (); keys.Clear())
    onDocument "visibilitychange" (fun _ ->
        if pageHidden () then
            releaseAllTouches ()
            keys.Clear())
    // Safety net for a lost pointerup (iOS Safari can drop one): with no
    // finger left on the screen, nothing can be held
    onDocument "touchend" (fun e -> if touchCount e = 0 then releaseAllTouches ())
    onDocument "touchcancel" (fun e -> if touchCount e = 0 then releaseAllTouches ())
resizeCanvas ()
let private levelFileInput = getEl "level-file"
if not (isNull levelFileInput) then onElement levelFileInput "change" onLevelFile
applyPlayerCount ()
loadBundledLevels ()
registerServiceWorker (assetUrl "sw.js")
requestFrame loop
