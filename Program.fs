/// FsRocket Physics — Entry Point
/// WinForms window with keyboard input for 1-4 players
/// Game runs at 36 FPS
///
/// Controls:
///   Player 1 (RED):    Arrows/NumPad + RShift (Thrust/Left/Right/Down/Fire)
///   Player 2 (GREEN):  W/A/D/S/Tab (Thrust/Left/Right/Down/Fire)
///   Player 3 (YELLOW): I/J/L/K/B (Thrust/Left/Right/Down/Fire)
///
///   1-4: Set player count (menu only)
///   Weapon switch (in-game): P1 = 9, P2 = 1, P3 = 6
///     By default a weapon can only be changed while parked on a base.
///   F9: Toggle "change weapons only on bases"
///   F5/F6: Prev/next level
///   Space: Start round
///   Escape: Quit
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

let tryLoadLevel (gs: GameState) (name: string) : GameState option =
    let path = levelPath name
    if IO.File.Exists path then
        Some { gs with Level = Some (loadLevel path); LevelFilePath = path }
    else
        None

// ─── Special weapon cycling helper ────────────────────────────────────

let cycleWeapon (gs: GameState) (playerIdx: int) : GameState =
    if playerIdx >= gs.NumPlayers then gs
    else
        let p = gs.Players[playerIdx]
        // Default rule: during a live round the special weapon can only be changed
        // while the ship is parked on a base. Toggle off (F9) to allow it anywhere.
        let allowed = not gs.WeaponSwitchOnlyOnBase || not gs.RoundActive || p.OnBase
        if not allowed then gs
        else
            let mutable wt = (int p.SpecialWeapon + 1) % weapons.Length
            // Skip disabled weapons and Cannon (the always-on main gun). Guard the
            // scan so a weapon table with no enabled alternative can't hang the loop.
            let mutable guard = 0
            while (not weapons[wt].Enabled || wt = int WeaponType.Cannon) && guard < weapons.Length do
                wt <- (wt + 1) % weapons.Length
                guard <- guard + 1
            let p = { p with SpecialWeapon = enum<WeaponType> wt; SpecialReloadTimer = 0 }
            let players = gs.Players |> List.mapi (fun i pl -> if i = playerIdx then p else pl)
            { gs with Players = players }

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

    let totalPlayers () = min 4 (humanCount + cpuCount)

    let applyPlayerCount () =
        let total = totalPlayers ()
        gs <- { gs with NumPlayers = total; CpuCount = cpuCount; RoundActive = false }

    let switchLevel (delta: int) =
        // Cycle through: levelFiles[0], levelFiles[1], ..., (no terrain)
        // Total slots = levelFiles.Length + 1; last slot = no terrain
        let total = levelFiles.Length + 1
        levelIdx <- ((levelIdx + delta) % total + total) % total
        gs <-
            if levelIdx < levelFiles.Length then
                match tryLoadLevel gs levelFiles[levelIdx] with
                | Some newGs -> { newGs with RoundActive = false }
                | None -> { gs with RoundActive = false; LevelFilePath = "" }
            else
                { gs with Level = None; RoundActive = false; LevelFilePath = "" }

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

    do
        // Load default level
        if levelFiles.Length > 0 then
            match tryLoadLevel gs levelFiles[0] with
            | Some newGs -> gs <- newGs
            | None -> ()

        this.Text <- "FsRocket Physics"
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

    override _.OnKeyDown(e: KeyEventArgs) =
        keyStates.Add(e.KeyCode) |> ignore
        e.Handled <- true
        e.SuppressKeyPress <- true  // Prevent ding sound

        // Special keys
        match e.KeyCode with
        | Keys.Escape ->
            if gs.RoundActive then
                gs <- { gs with RoundActive = false }
            else
                Application.Exit()
        | Keys.Space ->
            if not gs.RoundActive then
                gs <- initRound gs
        | Keys.F11 -> toggleFullscreen ()
        // Player count is selected from the menu only (the number keys double as
        // weapon-switch keys during a live round).
        | Keys.D1 when not gs.RoundActive -> humanCount <- 1; applyPlayerCount ()
        | Keys.D2 when not gs.RoundActive -> humanCount <- 2; applyPlayerCount ()
        | Keys.D3 when not gs.RoundActive -> humanCount <- 3; applyPlayerCount ()
        | Keys.D4 when not gs.RoundActive -> humanCount <- 4; applyPlayerCount ()
        // Weapon switch on a number key near each player's controls:
        //   P1 (right: arrows/numpad) = 9, P2 (left: WASD) = 1, P3 (mid: IJKL) = 6
        | Keys.D9 when gs.RoundActive -> gs <- cycleWeapon gs 0
        | Keys.D1 when gs.RoundActive -> gs <- cycleWeapon gs 1
        | Keys.D6 when gs.RoundActive -> gs <- cycleWeapon gs 2
        // Toggle the "change weapons only on bases" rule
        | Keys.F9 -> gs <- { gs with WeaponSwitchOnlyOnBase = not gs.WeaponSwitchOnlyOnBase }
        | Keys.F5 -> switchLevel -1
        | Keys.F6 -> switchLevel 1
        | Keys.F7 ->
            // Decrease CPU count (min 0)
            cpuCount <- max 0 (cpuCount - 1)
            applyPlayerCount ()
        | Keys.F8 ->
            // Increase CPU count — CPUs are in addition to humans, total capped at 4
            cpuCount <- min (4 - humanCount) (cpuCount + 1)
            applyPlayerCount ()
        | _ -> ()

        base.OnKeyDown(e)

    override _.OnKeyUp(e: KeyEventArgs) =
        keyStates.Remove(e.KeyCode) |> ignore
        e.Handled <- true
        base.OnKeyUp(e)

    member _.GameLoop() =
        // Map key states to player inputs
        this.MapInputs()

        if gs.RoundActive then
            gs <- gameTick gs

        this.Invalidate()

    member _.MapInputs() =
        let has k = keyStates.Contains(k)

        let players =
            gs.Players |> List.mapi (fun i p ->
                if p.IsCpu then p  // CPU players get input from AI, not keyboard
                else
                match i with
                | 0 when gs.NumPlayers >= 1 ->
                    // Player 1 (RED): Arrow keys / NumPad + RShift
                    { p with
                        KeyUp    = has Keys.Up    || has Keys.NumPad8
                        KeyLeft  = has Keys.Left  || has Keys.NumPad4
                        KeyRight = has Keys.Right || has Keys.NumPad6
                        KeyDown  = has Keys.Down  || has Keys.NumPad5
                        KeyFire  = has Keys.RShiftKey || has Keys.Enter }
                | 1 when gs.NumPlayers >= 2 ->
                    // Player 2 (GREEN): WASD + Tab
                    { p with
                        KeyUp    = has Keys.W
                        KeyLeft  = has Keys.A
                        KeyRight = has Keys.D
                        KeyDown  = has Keys.S
                        KeyFire  = has Keys.Tab }
                | 2 when gs.NumPlayers >= 3 ->
                    // Player 3 (YELLOW): IJKL + B
                    { p with
                        KeyUp    = has Keys.I
                        KeyLeft  = has Keys.J
                        KeyRight = has Keys.L
                        KeyDown  = has Keys.K
                        KeyFire  = has Keys.B }
                | _ -> p)

        gs <- { gs with Players = players }

    override this.OnPaint(e: PaintEventArgs) =
        renderFrame e.Graphics gs this.ClientSize.Width this.ClientSize.Height
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
    Application.Run(new GameForm())
    0
