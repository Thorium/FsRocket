/// FsRocket — Entry Point (MonoGame)
/// MonoGame Game class with keyboard input for 1-4 players
/// Game runs at 36 FPS using MonoGame's fixed timestep
///
/// Controls:
///   Player 1 (BLUE):   W/A/D/S/Tab (Thrust/Left/Right/Down/Fire)
///   Player 2 (GREEN):  Arrows/NumPad + RShift (Thrust/Left/Right/Down/Fire)
///   Player 3 (RED):    T/F/H/G/Y (Thrust/Left/Right/Down/Fire)
///   Player 4 (YELLOW): I/J/L/K/B (Thrust/Left/Right/Down/Fire)
///
///   1-4: Set player count (menu only)
///   Weapon switch (in-game): P1 = 1 back / 2 fwd, P2 = 8 back / 9 fwd, P3 = 4 back / 5 fwd, P4 = 6 back / 7 fwd
///     By default a weapon can only be changed while parked on a base.
///   F9: Cycle the weapon-switch rule (on a base / anytime / game start)
///   F1: Keys & controls screen (menu only)
///   F2: Toggle the CPU level (menu only) — Hard (default) / Easy = CPU ships fire about once a second
///   F4: Toggle "respawn on death" (menu only) — off = last ship flying wins the round
///   F5/F6: Prev/next level
///   F10: Toggle gamepad input (pads claim players in order; keyboard stays active)
///   Space: Start round (under the Game start rule: choose weapons first)
///   Escape: Back to the menu / quit
///   The menu is clickable with the mouse too.
module FsRocket.Program

open System
open Microsoft.Xna.Framework
open Microsoft.Xna.Framework.Input
open FsRocket.Physics
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types
open FsRocket.Entities
open FsRocket.Game
open FsRocket.Renderer

// ─── Level file paths ──────────────────────────────────────────────────

/// Directory containing the running executable — where the .LEV files live.
let private exeDir =
    match IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) with
    | null | "" -> "."
    | dir -> dir

let private levelPath (name: string) = IO.Path.Combine(exeDir, name + ".LEV")

let private discoverLevels () : string array =
    IO.Directory.GetFiles(exeDir, "*.LEV")
    |> Array.map (fun f -> IO.Path.GetFileNameWithoutExtension(f).ToUpperInvariant())
    |> Array.distinct
    |> Array.sort

let levelFiles = discoverLevels ()

/// The level the game opens on: wide open space, gentle for new pilots.
[<Literal>]
let private startLevel = "VIIDAKKO"

let tryLoadLevel (gs: GameState) (name: string) : GameState option =
    let path = levelPath name
    if IO.File.Exists path then
        Some { gs with Level = Some (loadLevel path); LevelFilePath = path }
    else
        None

// ─── Special weapon cycling helper ────────────────────────────────────

/// Whether a ship may switch its special right now (the F9 rule).
let canSwitchWeapon (gs: GameState) (p: Player) =
    match gs.WeaponSwitch with
    | Anytime -> true
    | OnlyOnBase -> not gs.RoundActive || p.OnBase
    | OnlyAtStart -> not gs.RoundActive

let cycleWeapon (gs: GameState) (playerIdx: int) (dir: int) : GameState =
    if playerIdx >= gs.NumPlayers then gs
    else
        let p = gs.Players[playerIdx]
        if not (canSwitchWeapon gs p) then gs
        else
            let len = weapons.Length
            let mutable wt = (int p.SpecialWeapon + dir + len) % len
            // Skip disabled weapons and Cannon (the always-on main gun). Guard the
            // scan so a weapon table with no enabled alternative can't hang the loop.
            let mutable guard = 0
            while (not weapons[wt].Enabled || wt = int WeaponType.Cannon) && guard < len do
                wt <- (wt + dir + len) % len
                guard <- guard + 1
            // Switching the special also drops the magnofilter field — otherwise
            // it stays on forever with its toggle-off action swapped away
            let p = { p with SpecialWeapon = enum<WeaponType> wt; SpecialReloadTimer = 0
                             Flags = p.Flags &&& ~~~PlayerFlags.Magno }
            let players = gs.Players |> List.mapi (fun i pl -> if i = playerIdx then p else pl)
            { gs with Players = players }

// ─── Weapon choice before the round (F9 rule: Game start) ─────────────
// START opens a dialog where each human picks a special, or Random; the round
// then begins with those weapons and nobody switches until it ends.

/// A weapon choice: Random, or the int value of a WeaponType.
[<Literal>]
let private RandomWeapon = -1

/// The dialog's options in order: Random, then every special weapon.
let private weaponChoiceOptions = Array.append [| RandomWeapon |] (specialWeaponPool |> Array.map int)

// ─── Gamepads ──────────────────────────────────────────────────────────
// Connected gamepads claim human player slots in order: pad 1 drives P1,
// pad 2 drives P2, and so on; the remaining humans keep their per-slot
// keyboard mappings (P2 is always arrows, etc.). Pad input is OR-merged with
// the keys, so the keyboard keeps working for a pad-driven player and with no
// pads connected nothing changes. F10 disables gamepads entirely.

/// One gamepad's controls mapped to game inputs.
type private PadInput =
    { Up: bool; Left: bool; Right: bool; Fire: bool; Special: bool
      WeaponPrev: bool; WeaponNext: bool; Start: bool }

/// Left-stick deadzone (fraction of full deflection) and trigger threshold.
[<Literal>]
let private stickDeadzone = 0.35f
[<Literal>]
let private triggerThreshold = 0.25f

/// States of all connected pads, compacted in player-index order so pad slots
/// line up with player slots even when e.g. only controller 2 is on.
let private connectedPads () : PadInput[] =
    [| for i in 0 .. 3 do
        let st = GamePad.GetState(enum<PlayerIndex> i)
        if st.IsConnected then
            let pressed (b: ButtonState) = b = ButtonState.Pressed
            { Up = pressed st.Buttons.A || pressed st.DPad.Up || st.ThumbSticks.Left.Y > 0.5f
              Left = pressed st.DPad.Left || st.ThumbSticks.Left.X < -stickDeadzone
              Right = pressed st.DPad.Right || st.ThumbSticks.Left.X > stickDeadzone
              Fire = pressed st.Buttons.B || st.Triggers.Right > triggerThreshold
              Special = pressed st.Buttons.X || st.Triggers.Left > triggerThreshold
              WeaponPrev = pressed st.Buttons.LeftShoulder
              WeaponNext = pressed st.Buttons.RightShoulder
              Start = pressed st.Buttons.Start } |]

/// Keys with one-shot actions, by the code the actions are written in (menu
/// buttons send the same codes).
let private keyCodes =
    [ Keys.Escape, "Escape"; Keys.Space, "Space"; Keys.F1, "F1"; Keys.F2, "F2"; Keys.F4, "F4"
      Keys.F5, "F5"; Keys.F6, "F6"; Keys.F7, "F7"; Keys.F8, "F8"; Keys.F9, "F9"
      Keys.F10, "F10"; Keys.F11, "F11"
      Keys.D1, "Digit1"; Keys.D2, "Digit2"; Keys.D3, "Digit3"; Keys.D4, "Digit4"; Keys.D5, "Digit5"
      Keys.D6, "Digit6"; Keys.D7, "Digit7"; Keys.D8, "Digit8"; Keys.D9, "Digit9" ]

// ─── Game Class ────────────────────────────────────────────────────────

type FsRocketGame() as this =
    inherit Microsoft.Xna.Framework.Game()

    let graphics = new GraphicsDeviceManager(this)
    let mutable renderRes = Unchecked.defaultof<RenderResources>

    let mutable gs = createGameState 2
    let mutable levelIdx = 0
    let mutable humanCount = 2
    let mutable cpuCount = 0
    let mutable prevKeyState = Keyboard.GetState()

    // Previous LB/RB/Start state per pad slot, for edge-triggered actions
    // (weapon cycling and starting a round fire once per press, not per tick).
    let padPrev = Array.create 4 (false, false, false)
    let mutable prevMouse = Mouse.GetState()
    let mutable mouseLogical = Point(-1, -1)

    // Each player's weapon choice for the Game start rule, kept between rounds
    let weaponChoices = Array.create 4 RandomWeapon
    let mutable choosingWeapons = false
    let mutable showingKeys = false
    // Escape during a round asks first; the round waits while the prompt is up
    let mutable confirmingQuit = false

    let totalPlayers () = min 4 (humanCount + cpuCount)

    let applyPlayerCount () =
        let total = totalPlayers ()
        gs <- placeForMenu { gs with NumPlayers = total; CpuCount = cpuCount; RoundActive = false }

    let switchLevel (delta: int) =
        let total = levelFiles.Length + 1
        levelIdx <- ((levelIdx + delta) % total + total) % total
        let next =
            if levelIdx < levelFiles.Length then
                match tryLoadLevel gs levelFiles[levelIdx] with
                | Some newGs -> { newGs with RoundActive = false }
                | None -> { gs with RoundActive = false; LevelFilePath = "" }
            else
                { gs with Level = None; RoundActive = false; LevelFilePath = "" }
        gs <- placeForMenu next

    do
        // Window settings
        graphics.PreferredBackBufferWidth <- 960
        graphics.PreferredBackBufferHeight <- 600
        this.Window.Title <- "FsRocket"
        this.Window.AllowUserResizing <- true
        this.IsMouseVisible <- true

        // Match original 36 FPS timing
        this.IsFixedTimeStep <- true
        this.TargetElapsedTime <- TimeSpan.FromMilliseconds(28.0)

    let humanPlayers () = gs.NumPlayers - gs.CpuCount

    /// Step a player's choice through the options (their weapon keys / LB, RB).
    let cycleChoice (playerIdx: int) (dir: int) =
        if playerIdx < humanPlayers () then
            let n = weaponChoiceOptions.Length
            let i = max 0 (Array.IndexOf(weaponChoiceOptions, weaponChoices[playerIdx]))
            weaponChoices[playerIdx] <- weaponChoiceOptions[(i + dir + n) % n]

    /// Start the round; under the Game start rule, arm each human with their
    /// choice (Random draws one now).
    let beginRound () =
        choosingWeapons <- false
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
    let startPressed () =
        if not gs.RoundActive then
            if choosingWeapons || gs.WeaponSwitch <> OnlyAtStart || humanPlayers () = 0 then beginRound ()
            else choosingWeapons <- true

    let menuView () =
        { HumanCount = humanCount
          ChoosingWeapons = choosingWeapons
          ShowingKeys = showingKeys
          ConfirmingQuit = confirmingQuit
          WeaponChoices =
            weaponChoices |> Array.map (fun c -> if c = RandomWeapon then "Random" else weapons[c].Name)
          Mouse = mouseLogical }

    override this.Initialize() =
        // Load the start level (or the first one, if it is missing)
        if levelFiles.Length > 0 then
            levelIdx <- max 0 (Array.IndexOf(levelFiles, startLevel))
            match tryLoadLevel gs levelFiles[levelIdx] with
            | Some newGs -> gs <- newGs
            | None -> ()
        gs <- placeForMenu gs
        base.Initialize()

    override this.LoadContent() =
        renderRes <- initRenderResources this.GraphicsDevice

    /// Check if a key was just pressed this frame (not held)
    member _.JustPressed (key: Keys) (curr: KeyboardState) =
        curr.IsKeyDown(key) && prevKeyState.IsKeyUp(key)

    /// F11: borderless fullscreen at the desktop resolution, so the layout sees
    /// the screen's real shape (an ultrawide screen gets the side-by-side
    /// viewports); a hardware mode switch would pick a 4:3 mode instead.
    member private this.ToggleFullScreen() =
        if graphics.IsFullScreen then
            graphics.IsFullScreen <- false
            graphics.PreferredBackBufferWidth <- 960
            graphics.PreferredBackBufferHeight <- 600
        else
            let display = Microsoft.Xna.Framework.Graphics.GraphicsAdapter.DefaultAdapter.CurrentDisplayMode
            graphics.HardwareModeSwitch <- false
            graphics.PreferredBackBufferWidth <- display.Width
            graphics.PreferredBackBufferHeight <- display.Height
            graphics.IsFullScreen <- true
        graphics.ApplyChanges()

    /// Keys while the weapon dialog is open: each player's weapon keys (and the
    /// dialog's < > buttons) step their choice, Space starts the round, Escape
    /// goes back to the menu.
    member private this.WeaponDialogKey(code: string) =
        match code with
        | "Space" -> beginRound ()
        | "Escape" -> choosingWeapons <- false
        | "F11" -> this.ToggleFullScreen()
        | "Digit1" -> cycleChoice 0 -1
        | "Digit2" -> cycleChoice 0 1
        | "Digit8" -> cycleChoice 1 -1
        | "Digit9" -> cycleChoice 1 1
        | "Digit4" -> cycleChoice 2 -1
        | "Digit5" -> cycleChoice 2 1
        | "Digit6" -> cycleChoice 3 -1
        | "Digit7" -> cycleChoice 3 1
        | _ when code.StartsWith "wpn:" ->
            match code.Split ':' with
            | [| _; player; dir |] -> cycleChoice (int player) (int dir)
            | _ -> ()
        | _ -> ()

    /// One-shot action bound to a key code in the menu or a round (menu toggles,
    /// level/player changes, starting a round, weapon cycling).
    member private this.MenuKeyAction(code: string) =
        match code with
        | "Escape" ->
            if gs.RoundActive then confirmingQuit <- true
            else this.Exit()
        | "Space" -> startPressed ()
        | "F11" -> this.ToggleFullScreen()
        | "F1" -> if not gs.RoundActive then showingKeys <- true
        // F9 cycles the weapon-switch rule
        | "F9" ->
            let next =
                match gs.WeaponSwitch with
                | OnlyOnBase -> Anytime
                | Anytime -> OnlyAtStart
                | OnlyAtStart -> OnlyOnBase
            gs <- { gs with WeaponSwitch = next }
        // F10 toggles gamepad input (e.g. to ignore a drifting controller)
        | "F10" -> gs <- { gs with GamepadsEnabled = not gs.GamepadsEnabled }
        // F4 toggles "respawn on death" — menu only, so the rule can't flip mid-round
        | "F4" -> if not gs.RoundActive then gs <- { gs with RespawnOnDeath = not gs.RespawnOnDeath }
        // F2 toggles the CPU difficulty (Hard / Easy) — menu only, like F4
        | "F2" ->
            if not gs.RoundActive then
                let next =
                    match gs.CpuDifficulty with
                    | Hard -> Easy
                    | Easy -> Hard
                gs <- { gs with CpuDifficulty = next }
        | "F5" -> switchLevel -1
        | "F6" -> switchLevel 1
        | "F7" ->
            cpuCount <- max 0 (cpuCount - 1)
            applyPlayerCount ()
        | "F8" ->
            cpuCount <- min (4 - humanCount) (cpuCount + 1)
            applyPlayerCount ()
        // Player count is selected from the menu only (these number keys double as
        // weapon-switch keys during a live round).
        | "Digit1" | "Digit2" | "Digit3" | "Digit4" when not gs.RoundActive ->
            humanCount <- int (code.Substring 5)
            applyPlayerCount ()
        // Weapon switch on a number key close to each player's controls:
        //   P1 (left: WASD) = 1 back / 2 fwd, P2 (right: arrows/numpad) = 8 back / 9 fwd,
        //   P3 (left: TFGH) = 4 back / 5 fwd, P4 (right: IJKL) = 6 back / 7 fwd
        | "Digit1" -> gs <- cycleWeapon gs 0 (-1)
        | "Digit2" -> gs <- cycleWeapon gs 0 1
        | "Digit8" -> gs <- cycleWeapon gs 1 (-1)
        | "Digit9" -> gs <- cycleWeapon gs 1 1
        | "Digit4" -> gs <- cycleWeapon gs 2 (-1)
        | "Digit5" -> gs <- cycleWeapon gs 2 1
        | "Digit6" -> gs <- cycleWeapon gs 3 (-1)
        | "Digit7" -> gs <- cycleWeapon gs 3 1
        | _ -> ()

    /// Keys while the "end the round?" prompt is up: Space ends it (the menu
    /// opens), Escape goes back to the round. Space, not a player's fire key:
    /// nothing else answers to Space during a round.
    member private this.QuitPromptKey(code: string) =
        match code with
        | "Space" ->
            confirmingQuit <- false
            gs <- { gs with RoundActive = false }
        | "Escape" -> confirmingQuit <- false
        | "F11" -> this.ToggleFullScreen()
        | _ -> ()

    /// A key code's one-shot action (key presses and menu clicks alike): the open
    /// dialog takes it first.
    member private this.KeyAction(code: string) =
        if confirmingQuit then this.QuitPromptKey code
        elif choosingWeapons then this.WeaponDialogKey code
        elif showingKeys then
            match code with
            | "Escape" | "F1" -> showingKeys <- false
            | "F11" -> this.ToggleFullScreen()
            | _ -> ()
        else this.MenuKeyAction code

    override this.Update(gameTime) =
        // No Sticky Keys dialog from five presses of P2's RShift: Windows'
        // accessibility shortcuts are off while the game window is in front
        ShortcutKeys.sync ()
        let currKeyState = Keyboard.GetState()

        // One-shot keys (edge-triggered)
        for key, code in keyCodes do
            if this.JustPressed key currKeyState then this.KeyAction code

        // Menu clicks: the mouse position in the renderer's logical pixels
        let ms = Mouse.GetState()
        let vp = this.GraphicsDevice.Viewport
        let client = this.Window.ClientBounds
        let zoom = menuZoomFor (float (max 1 vp.Width)) (float (max 1 vp.Height))
        let toLogical (v: int) (clientSize: int) (physSize: int) =
            int (float v * float physSize / float (max 1 clientSize) / zoom)
        mouseLogical <- Point(toLogical ms.X client.Width vp.Width, toLogical ms.Y client.Height vp.Height)
        let clicked = ms.LeftButton = ButtonState.Pressed && prevMouse.LeftButton = ButtonState.Released
        if clicked && this.IsActive && (not gs.RoundActive || confirmingQuit) then
            let w, h = int (float (max 1 vp.Width) / zoom), int (float (max 1 vp.Height) / zoom)
            match menuHit (menuLayout gs (menuView ()) w h) mouseLogical with
            | Some action -> this.KeyAction action
            | None -> ()
        prevMouse <- ms

        // Gamepads: edge-triggered buttons (LB/RB cycle the special weapon, or
        // the weapon dialog's choice; Start is START), then held controls merge
        // into the player inputs below.
        let pads = if gs.GamepadsEnabled then connectedPads () else [||]
        pads |> Array.iteri (fun i pad ->
            if i < 4 then
                let (pw, pn, ps) = padPrev[i]
                let isHuman = i < gs.NumPlayers && not gs.Players[i].IsCpu
                let step dir = if choosingWeapons then cycleChoice i dir else gs <- cycleWeapon gs i dir
                if isHuman && pad.WeaponPrev && not pw then step -1
                if isHuman && pad.WeaponNext && not pn then step 1
                if pad.Start && not ps && not gs.RoundActive then
                    showingKeys <- false
                    startPressed ()
                padPrev[i] <- (pad.WeaponPrev, pad.WeaponNext, pad.Start))

        // Map key states + held pad controls to player inputs
        let has (k: Keys) = currKeyState.IsKeyDown k

        let players =
            gs.Players |> List.mapi (fun i p ->
                if p.IsCpu then p
                else
                let p =
                    match i with
                    | 0 when gs.NumPlayers >= 1 ->
                        { p with
                            KeyUp    = has Keys.W
                            KeyLeft  = has Keys.A
                            KeyRight = has Keys.D
                            KeyDown  = has Keys.S
                            KeyFire  = has Keys.Tab }
                    | 1 when gs.NumPlayers >= 2 ->
                        { p with
                            KeyUp    = has Keys.Up    || has Keys.NumPad8
                            KeyLeft  = has Keys.Left  || has Keys.NumPad4
                            KeyRight = has Keys.Right || has Keys.NumPad6
                            KeyDown  = has Keys.Down  || has Keys.NumPad5
                            KeyFire  = has Keys.RightShift || has Keys.Enter }
                    | 2 when gs.NumPlayers >= 3 ->
                        { p with
                            KeyUp    = has Keys.T
                            KeyLeft  = has Keys.F
                            KeyRight = has Keys.H
                            KeyDown  = has Keys.G
                            KeyFire  = has Keys.Y }
                    | 3 when gs.NumPlayers >= 4 ->
                        { p with
                            KeyUp    = has Keys.I
                            KeyLeft  = has Keys.J
                            KeyRight = has Keys.L
                            KeyDown  = has Keys.K
                            KeyFire  = has Keys.B }
                    | _ -> p
                // Gamepad i drives player i, merged on top of the keys
                if i < pads.Length then
                    let pad = pads[i]
                    { p with
                        KeyUp    = p.KeyUp || pad.Up
                        KeyLeft  = p.KeyLeft || pad.Left
                        KeyRight = p.KeyRight || pad.Right
                        KeyDown  = p.KeyDown || pad.Special
                        KeyFire  = p.KeyFire || pad.Fire }
                else p)

        gs <- { gs with Players = players }

        if gs.RoundActive && not confirmingQuit then
            gs <- gameTick gs

        prevKeyState <- currKeyState
        base.Update gameTime

    override this.Draw(gameTime) =
        let windowW = this.GraphicsDevice.Viewport.Width
        let windowH = this.GraphicsDevice.Viewport.Height
        renderFrame renderRes this.GraphicsDevice gs (menuView ()) windowW windowH

        // Clear terrain dirty flag after the frame is rendered
        if gs.TerrainDirty then
            gs <- { gs with TerrainDirty = false }

        base.Draw gameTime

// ─── Entry Point ───────────────────────────────────────────────────────

[<EntryPoint>]
let main _ =
    try
        use game = new FsRocketGame()
        game.Run()
        0
    finally
        ShortcutKeys.restore ()
