/// FsRocket — Entry Point
/// WinForms window with keyboard input for 1-4 players
/// Game runs at 36 FPS
///
/// Controls:
///   Player 1 (BLUE):   W/A/D/S/Tab (Thrust/Left/Right/Down/Fire)
///   Player 2 (GREEN):  Arrows/NumPad + Shift (Thrust/Left/Right/Down/Fire)
///   Player 3 (RED):    T/F/H/G/Y (Thrust/Left/Right/Down/Fire)
///   Player 4 (YELLOW): I/J/L/K/B (Thrust/Left/Right/Down/Fire)
///
///   1-4: Set player count (menu only)
///   Weapon switch (in-game): P1 = 1 back / 2 fwd, P2 = 8 back / 9 fwd, P3 = 4 back / 5 fwd, P4 = 6 back / 7 fwd
///     By default a weapon can only be changed while parked on a base.
///   F9: Cycle the weapon-switch rule (on a base / anytime / game start)
///   F1: Keys & controls screen (menu only)
///   F4: Toggle "respawn on death" (menu only) — off = last ship flying wins the round
///   F5/F6: Prev/next level
///   F10: Toggle gamepad input (XInput pads claim players in order; keyboard stays active)
///   Space: Start round (under the Game start rule: choose weapons first)
///   Escape: Back to the menu / quit
///   The menu is clickable with the mouse too.
module FsRocket.Program

open System
open System.Drawing
open System.Windows.Forms
open FsRocket.Physics
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types
open FsRocket.Entities
open FsRocket.Game
open FsRocket.Renderer

// ─── Level file paths ──────────────────────────────────────────────────

/// Directory containing the running executable — where the .LEV files live.
/// Falls back to the current working directory when the assembly location is
/// empty (e.g. single-file publish).
let private exeDir =
    match IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) with
    | null | "" -> "."
    | dir -> dir

let private levelPath (name: string) = IO.Path.Combine(exeDir, name + ".LEV")

/// Scan the executable's directory for all .LEV files, returning sorted
/// distinct names (without extension).
let private discoverLevels () : string array =
    IO.Directory.GetFiles(exeDir, "*.LEV")
    |> Array.map (fun f -> IO.Path.GetFileNameWithoutExtension(f).ToUpperInvariant())
    |> Array.distinct
    |> Array.sort

/// Available levels discovered at startup (file name without extension)
let levelFiles = discoverLevels ()

/// The level the game opens on: wide open space, gentle for new pilots.
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

/// The code a key's one-shot action is written in (menu buttons send the same).
let private keyCode (key: Keys) =
    match key with
    | Keys.Escape -> Some "Escape"
    | Keys.Space -> Some "Space"
    | Keys.F1 | Keys.F4 | Keys.F5 | Keys.F6 | Keys.F7 | Keys.F8 | Keys.F9 | Keys.F10 | Keys.F11 -> Some(string key)
    | _ when key >= Keys.D1 && key <= Keys.D9 -> Some $"Digit{int key - int Keys.D0}"
    | _ -> None

// ─── Gamepads (XInput) ─────────────────────────────────────────────────
// Connected gamepads claim human player slots in order: pad 1 drives P1,
// pad 2 drives P2, and so on; the remaining humans keep their per-slot
// keyboard mappings (P2 is always arrows, etc.). Pad input is OR-merged with
// the keys, so the keyboard keeps working for a pad-driven player and with no
// pads connected nothing changes. F10 disables gamepads entirely.

module private XInput =
    open System.Runtime.InteropServices

    [<StructLayout(LayoutKind.Sequential)>]
    type XINPUT_GAMEPAD =
        struct
            val mutable wButtons: uint16
            val mutable bLeftTrigger: byte
            val mutable bRightTrigger: byte
            val mutable sThumbLX: int16
            val mutable sThumbLY: int16
            val mutable sThumbRX: int16
            val mutable sThumbRY: int16
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type XINPUT_STATE =
        struct
            val mutable dwPacketNumber: uint32
            val mutable Gamepad: XINPUT_GAMEPAD
        end

    [<DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")>]
    extern uint32 XInputGetState(uint32 dwUserIndex, XINPUT_STATE& pState)

    // wButtons bit flags
    let DPAD_UP = 0x0001us
    let DPAD_LEFT = 0x0004us
    let DPAD_RIGHT = 0x0008us
    let START = 0x0010us
    let LB = 0x0100us
    let RB = 0x0200us
    let A = 0x1000us
    let B = 0x2000us
    let X = 0x4000us

/// One gamepad's controls mapped to game inputs.
type private PadInput =
    { Up: bool; Left: bool; Right: bool; Fire: bool; Special: bool
      WeaponPrev: bool; WeaponNext: bool; Start: bool }

/// Left-stick deadzone (0.35 of full deflection) and trigger threshold.
let private stickDeadzone = int16 (0.35 * 32767.0)
[<Literal>]
let private triggerThreshold = 64uy

/// Set false forever if the XInput DLL is missing (pre-Win8 systems).
let mutable private xinputAvailable = true

/// States of all connected pads, compacted in XInput user-index order so pad
/// slots line up with player slots even when e.g. only controller 2 is on.
let private connectedPads () : PadInput[] =
    if not xinputAvailable then [||]
    else
        try
            [| for i in 0u .. 3u do
                let mutable state = XInput.XINPUT_STATE()
                if XInput.XInputGetState(i, &state) = 0u then
                    let gp = state.Gamepad
                    let btn (b: uint16) = gp.wButtons &&& b <> 0us
                    { Up = btn XInput.A || btn XInput.DPAD_UP || gp.sThumbLY > 16384s
                      Left = btn XInput.DPAD_LEFT || gp.sThumbLX < -stickDeadzone
                      Right = btn XInput.DPAD_RIGHT || gp.sThumbLX > stickDeadzone
                      Fire = btn XInput.B || gp.bRightTrigger > triggerThreshold
                      Special = btn XInput.X || gp.bLeftTrigger > triggerThreshold
                      WeaponPrev = btn XInput.LB
                      WeaponNext = btn XInput.RB
                      Start = btn XInput.START } |]
        with :? DllNotFoundException | :? EntryPointNotFoundException ->
            xinputAvailable <- false
            [||]

// ─── Game Form ─────────────────────────────────────────────────────────

type GameForm() as this =
    inherit Form()

    let mutable gs = createGameState 2
    let mutable levelIdx = 0       // Index into levelFiles; -1 = no terrain
    let mutable humanCount = 2     // Number of human players (keys 1-4)
    let mutable cpuCount = 0       // Number of CPU players added on top of humans
    let mutable isFullscreen = false
    let mutable savedBorderStyle = FormBorderStyle.Sizable
    let mutable savedWindowState = FormWindowState.Normal
    let timer = new Timer()

    // Key state tracking (multiple simultaneous keys)
    let keyStates = System.Collections.Generic.HashSet<Keys>()
    // Keys held since their action ran (Windows auto-repeats KeyDown)
    let heldKeys = System.Collections.Generic.HashSet<Keys>()
    let mutable mouseLogical = Point(-1, -1)

    // Each player's weapon choice for the Game start rule, kept between rounds
    let weaponChoices = Array.create 4 RandomWeapon
    let mutable choosingWeapons = false
    let mutable showingKeys = false

    // Previous LB/RB/Start state per pad slot, for edge-triggered actions
    // (weapon cycling and starting a round fire once per press, not per tick).
    let padPrev = Array.create 4 (false, false, false)

    let totalPlayers () = min 4 (humanCount + cpuCount)

    let applyPlayerCount () =
        let total = totalPlayers ()
        gs <- placeForMenu { gs with NumPlayers = total; CpuCount = cpuCount; RoundActive = false }

    let switchLevel (delta: int) =
        // Cycle through: levelFiles[0], levelFiles[1], ..., (no terrain)
        // Total slots = levelFiles.Length + 1; last slot = no terrain
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

    let toggleFullscreen () =
        if not isFullscreen then
            savedBorderStyle <- this.FormBorderStyle
            savedWindowState <- this.WindowState
            this.FormBorderStyle <- FormBorderStyle.None
            this.WindowState <- FormWindowState.Maximized
            isFullscreen <- true
        else
            this.FormBorderStyle <- savedBorderStyle
            this.WindowState <- savedWindowState
            isFullscreen <- false

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
          WeaponChoices =
            weaponChoices |> Array.map (fun c -> if c = RandomWeapon then "Random" else weapons[c].Name)
          Mouse = mouseLogical }

    do
        // Load the start level (or the first one, if it is missing)
        if levelFiles.Length > 0 then
            levelIdx <- max 0 (Array.IndexOf(levelFiles, startLevel))
            match tryLoadLevel gs levelFiles[levelIdx] with
            | Some newGs -> gs <- newGs
            | None -> ()
        gs <- placeForMenu gs

        this.Text <- "FsRocket"
        // Window / taskbar icon: the app icon embedded in the assembly
        match Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream "Icon.ico" with
        | null -> ()
        | stream ->
            use s = stream
            this.Icon <- new Icon(s)
        this.ClientSize <- Size(960, 600)
        this.DoubleBuffered <- true
        this.SetStyle(ControlStyles.AllPaintingInWmPaint ||| ControlStyles.UserPaint
                      ||| ControlStyles.OptimizedDoubleBuffer, true)
        this.KeyPreview <- true
        this.FormBorderStyle <- FormBorderStyle.Sizable
        this.MaximizeBox <- true
        this.BackColor <- Color.Black

        // Timer at ~36 FPS
        timer.Interval <- 28  // ~36 FPS
        timer.Tick.Add(fun _ -> this.GameLoop())
        timer.Start()

    /// Keys while the weapon dialog is open: each player's weapon keys (and the
    /// dialog's < > buttons) step their choice, Space starts the round, Escape
    /// goes back to the menu.
    member private _.WeaponDialogKey(code: string) =
        match code with
        | "Space" -> beginRound ()
        | "Escape" -> choosingWeapons <- false
        | "F11" -> toggleFullscreen ()
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
    member private _.MenuKeyAction(code: string) =
        match code with
        | "Escape" ->
            if gs.RoundActive then gs <- { gs with RoundActive = false }
            else Application.Exit()
        | "Space" -> startPressed ()
        | "F11" -> toggleFullscreen ()
        | "F1" -> if not gs.RoundActive then showingKeys <- true
        // F9 cycles the weapon-switch rule
        | "F9" ->
            let next =
                match gs.WeaponSwitch with
                | OnlyOnBase -> Anytime
                | Anytime -> OnlyAtStart
                | OnlyAtStart -> OnlyOnBase
            gs <- { gs with WeaponSwitch = next }
        // Toggle gamepad input (e.g. to ignore a drifting controller)
        | "F10" -> gs <- { gs with GamepadsEnabled = not gs.GamepadsEnabled }
        // Toggle "respawn on death" — menu only, so the rule can't flip mid-round
        | "F4" -> if not gs.RoundActive then gs <- { gs with RespawnOnDeath = not gs.RespawnOnDeath }
        | "F5" -> switchLevel -1
        | "F6" -> switchLevel 1
        | "F7" ->
            // Decrease CPU count (min 0)
            cpuCount <- max 0 (cpuCount - 1)
            applyPlayerCount ()
        | "F8" ->
            // Increase CPU count — CPUs are in addition to humans, total capped at 4
            cpuCount <- min (4 - humanCount) (cpuCount + 1)
            applyPlayerCount ()
        // Player count is selected from the menu only (the number keys double as
        // weapon-switch keys during a live round).
        | "Digit1" | "Digit2" | "Digit3" | "Digit4" when not gs.RoundActive ->
            humanCount <- int (code.Substring 5)
            applyPlayerCount ()
        // Weapon switch on a number key near each player's controls:
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

    /// A key code's one-shot action (key presses and menu clicks alike): the open
    /// dialog takes it first.
    member private this.KeyAction(code: string) =
        if choosingWeapons then this.WeaponDialogKey code
        elif showingKeys then
            match code with
            | "Escape" | "F1" -> showingKeys <- false
            | "F11" -> toggleFullscreen ()
            | _ -> ()
        else this.MenuKeyAction code
        this.Invalidate()

    override this.OnKeyDown(e: KeyEventArgs) =
        keyStates.Add(e.KeyCode) |> ignore
        e.Handled <- true
        e.SuppressKeyPress <- true  // Prevent ding sound
        // Auto-repeat only holds the key; actions fire once per press
        if not (heldKeys.Contains e.KeyCode) then
            heldKeys.Add e.KeyCode |> ignore
            match keyCode e.KeyCode with
            | Some code -> this.KeyAction code
            | None -> ()
        base.OnKeyDown e

    /// Mouse position in the menu's logical pixels.
    member private this.MenuPoint(x: int, y: int) =
        let mz = menuZoomFor (float (max 1 this.ClientSize.Width)) (float (max 1 this.ClientSize.Height))
        Point(int (float x / mz), int (float y / mz))

    override this.OnMouseMove(e: MouseEventArgs) =
        mouseLogical <- this.MenuPoint(e.X, e.Y)
        base.OnMouseMove e

    // Menu clicks run the action of the button under the mouse
    override this.OnMouseDown(e: MouseEventArgs) =
        if e.Button = MouseButtons.Left && not gs.RoundActive then
            let mz = menuZoomFor (float (max 1 this.ClientSize.Width)) (float (max 1 this.ClientSize.Height))
            let w, h = int (float this.ClientSize.Width / mz), int (float this.ClientSize.Height / mz)
            match menuHit (menuLayout gs (menuView ()) w h) (this.MenuPoint(e.X, e.Y)) with
            | Some action -> this.KeyAction action
            | None -> ()
        base.OnMouseDown e

    override _.OnKeyUp(e: KeyEventArgs) =
        keyStates.Remove(e.KeyCode) |> ignore
        heldKeys.Remove(e.KeyCode) |> ignore
        e.Handled <- true
        base.OnKeyUp e

    member _.GameLoop() =
        // No Sticky Keys dialog from five presses of P2's Shift: Windows'
        // accessibility shortcuts are off while the game window is in front
        ShortcutKeys.sync ()
        let pads = if gs.GamepadsEnabled then connectedPads () else [||]

        // Edge-triggered gamepad buttons: LB/RB cycle the special weapon (or
        // the weapon dialog's choice), Start is START.
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
        this.MapInputs pads

        if gs.RoundActive then
            gs <- gameTick gs

        this.Invalidate()

    member private _.MapInputs(pads: PadInput[]) =
        let has k = keyStates.Contains k

        let players =
            gs.Players |> List.mapi (fun i p ->
                if p.IsCpu then p  // CPU players get input from AI, not keyboard
                else
                let p =
                    match i with
                    | 0 when gs.NumPlayers >= 1 ->
                        // Player 1 (BLUE): WASD + Tab
                        { p with
                            KeyUp    = has Keys.W
                            KeyLeft  = has Keys.A
                            KeyRight = has Keys.D
                            KeyDown  = has Keys.S
                            KeyFire  = has Keys.Tab }
                    | 1 when gs.NumPlayers >= 2 ->
                        // Player 2 (GREEN): Arrow keys / NumPad + Shift
                        { p with
                            KeyUp    = has Keys.Up    || has Keys.NumPad8
                            KeyLeft  = has Keys.Left  || has Keys.NumPad4
                            KeyRight = has Keys.Right || has Keys.NumPad6
                            KeyDown  = has Keys.Down  || has Keys.NumPad5
                            KeyFire  = has Keys.ShiftKey || has Keys.Enter }
                    | 2 when gs.NumPlayers >= 3 ->
                        // Player 3 (RED): TFGH + Y
                        { p with
                            KeyUp    = has Keys.T
                            KeyLeft  = has Keys.F
                            KeyRight = has Keys.H
                            KeyDown  = has Keys.G
                            KeyFire  = has Keys.Y }
                    | 3 when gs.NumPlayers >= 4 ->
                        // Player 4 (YELLOW): IJKL + B
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

    override this.OnPaint(e: PaintEventArgs) =
        renderFrame e.Graphics gs (menuView ()) this.ClientSize.Width this.ClientSize.Height
        // Clear terrain dirty flag AFTER the frame is actually rendered.
        if gs.TerrainDirty then
            gs <- { gs with TerrainDirty = false }

    override _.OnFormClosed(e: FormClosedEventArgs) =
        timer.Stop()
        timer.Dispose()
        base.OnFormClosed e

// ─── Entry Point ───────────────────────────────────────────────────────

[<STAThread; EntryPoint>]
let main _ =
    Application.EnableVisualStyles()
    Application.SetCompatibleTextRenderingDefault false
    try
        Application.Run(new GameForm())
        0
    finally
        ShortcutKeys.restore ()
