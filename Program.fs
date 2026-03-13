/// FsRocket Physics — Entry Point
/// WinForms window with keyboard input for 1-4 players
/// Game runs at 36 FPS
///
/// Controls:
///   Player 1 (RED):    Arrows/NumPad + RShift (Thrust/Left/Right/Down/Fire)
///   Player 2 (GREEN):  W/A/D/S/Tab (Thrust/Left/Right/Down/Fire)
///   Player 3 (YELLOW): I/J/L/K/B (Thrust/Left/Right/Down/Fire)
///
///   1-4: Set player count
///   F1-F4: Cycle weapon for P1-P4
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

/// Scan the executable's directory for all .LEV files, returning sorted
/// distinct names (without extension).  Falls back to the current
/// working directory when the assembly location is empty (e.g. single-file publish).
let private discoverLevels () : string array =
    let exeDir =
        let loc = System.Reflection.Assembly.GetExecutingAssembly().Location
        let dir = IO.Path.GetDirectoryName(loc)
        if String.IsNullOrEmpty dir then "." else dir
    IO.Directory.GetFiles(exeDir, "*.LEV")
    |> Array.map (fun f -> IO.Path.GetFileNameWithoutExtension(f).ToUpperInvariant())
    |> Array.distinct
    |> Array.sort

/// Available levels discovered at startup (file name without extension)
let levelFiles = discoverLevels ()

let tryLoadLevel (gs: GameState) (name: string) : GameState option =
    let exeDir =
        let loc = System.Reflection.Assembly.GetExecutingAssembly().Location
        let dir = IO.Path.GetDirectoryName(loc)
        if String.IsNullOrEmpty dir then "." else dir
    let path = IO.Path.Combine(exeDir, name + ".LEV")
    if IO.File.Exists path then
        let level = loadLevel path
        Some { gs with Level = Some level }
    else
        None

// ─── Special weapon cycling helper ────────────────────────────────────

let cycleWeapon (gs: GameState) (playerIdx: int) : GameState =
    if playerIdx < gs.NumPlayers then
        let p = gs.Players[playerIdx]
        let mutable wt = (int p.SpecialWeapon + 1) % weapons.Length
        // Skip Cannon (main weapon), disabled weapons, and NoWeapon
        while not weapons[wt].Enabled || wt = int WeaponType.Cannon do
            wt <- (wt + 1) % weapons.Length
        let p = { p with SpecialWeapon = enum<WeaponType> wt; SpecialReloadTimer = 0 }
        let players = gs.Players |> List.mapi (fun i pl -> if i = playerIdx then p else pl)
        { gs with Players = players }
    else
        gs

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
        if levelIdx < levelFiles.Length then
            let exeDir =
                let loc = System.Reflection.Assembly.GetExecutingAssembly().Location
                let dir = System.IO.Path.GetDirectoryName(loc)
                if String.IsNullOrEmpty dir then "." else dir
            let path = System.IO.Path.Combine(exeDir, levelFiles[levelIdx] + ".LEV")
            match tryLoadLevel gs levelFiles[levelIdx] with
            | Some newGs -> gs <- { newGs with RoundActive = false; LevelFilePath = path }
            | None -> gs <- { gs with RoundActive = false; LevelFilePath = "" }
        else
            gs <- { gs with Level = None; RoundActive = false; LevelFilePath = "" }

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
        let exeDir =
            let loc = System.Reflection.Assembly.GetExecutingAssembly().Location
            let dir = System.IO.Path.GetDirectoryName(loc)
            if String.IsNullOrEmpty dir then "." else dir
        if levelFiles.Length > 0 then
            let path = System.IO.Path.Combine(exeDir, levelFiles[0] + ".LEV")
            match tryLoadLevel gs levelFiles[0] with
            | Some newGs -> gs <- { newGs with LevelFilePath = path }
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
        | Keys.D1 -> humanCount <- 1; applyPlayerCount ()
        | Keys.D2 -> humanCount <- 2; applyPlayerCount ()
        | Keys.D3 -> humanCount <- 3; applyPlayerCount ()
        | Keys.D4 -> humanCount <- 4; applyPlayerCount ()
        | Keys.F1 -> gs <- cycleWeapon gs 0
        | Keys.F2 -> gs <- cycleWeapon gs 1
        | Keys.F3 -> gs <- cycleWeapon gs 2
        | Keys.F4 -> gs <- cycleWeapon gs 3
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
