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

// ─── Weapon cycling helper ─────────────────────────────────────────────

let cycleWeapon (gs: GameState) (playerIdx: int) : GameState =
    if playerIdx < gs.NumPlayers then
        let p = gs.Players[playerIdx]
        let mutable wt = (int p.WeaponType + 1) % weapons.Length
        // Skip weapons that are not yet implemented or are placeholders
        while not weapons[wt].Enabled do
            wt <- (wt + 1) % weapons.Length
        let p = { p with WeaponType = enum<WeaponType> wt; Ammo = 999; ReloadTimer = 0 }
        let players = gs.Players |> List.mapi (fun i pl -> if i = playerIdx then p else pl)
        { gs with Players = players }
    else
        gs

// ─── Game Form ─────────────────────────────────────────────────────────

type GameForm() as this =
    inherit Form()

    let mutable gs = createGameState 2
    let mutable levelIdx = 0  // Index into levelFiles; -1 = no terrain
    let timer = new Timer()

    // Key state tracking (multiple simultaneous keys)
    let keyStates = System.Collections.Generic.HashSet<Keys>()

    let switchLevel (delta: int) =
        // Cycle through: levelFiles[0], levelFiles[1], ..., (no terrain)
        // Total slots = levelFiles.Length + 1; last slot = no terrain
        let total = levelFiles.Length + 1
        levelIdx <- ((levelIdx + delta) % total + total) % total
        if levelIdx < levelFiles.Length then
            match tryLoadLevel gs levelFiles[levelIdx] with
            | Some newGs -> gs <- { newGs with RoundActive = false }
            | None -> gs <- { gs with RoundActive = false }
        else
            gs <- { gs with Level = None; RoundActive = false }

    do
        // Load default level
        match tryLoadLevel gs levelFiles[0] with
        | Some newGs -> gs <- newGs
        | None -> ()

        this.Text <- "FsRocket Physics"
        this.ClientSize <- Size(960, 600)
        this.DoubleBuffered <- true
        this.SetStyle(ControlStyles.AllPaintingInWmPaint ||| ControlStyles.UserPaint
                      ||| ControlStyles.OptimizedDoubleBuffer, true)
        this.KeyPreview <- true
        this.FormBorderStyle <- FormBorderStyle.FixedSingle
        this.MaximizeBox <- false
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
        | Keys.D1 -> gs <- { gs with NumPlayers = 1; CpuCount = min gs.CpuCount 1; RoundActive = false }
        | Keys.D2 -> gs <- { gs with NumPlayers = 2; CpuCount = min gs.CpuCount 2; RoundActive = false }
        | Keys.D3 -> gs <- { gs with NumPlayers = 3; CpuCount = min gs.CpuCount 3; RoundActive = false }
        | Keys.D4 -> gs <- { gs with NumPlayers = 4; CpuCount = min gs.CpuCount 4; RoundActive = false }
        | Keys.F1 -> gs <- cycleWeapon gs 0
        | Keys.F2 -> gs <- cycleWeapon gs 1
        | Keys.F3 -> gs <- cycleWeapon gs 2
        | Keys.F4 -> gs <- cycleWeapon gs 3
        | Keys.F5 -> switchLevel -1
        | Keys.F6 -> switchLevel 1
        | Keys.F7 ->
            // Decrease CPU count (min 0)
            let c = max 0 (gs.CpuCount - 1)
            gs <- { gs with CpuCount = c }
        | Keys.F8 ->
            // Increase CPU count (max = NumPlayers)
            let c = min gs.NumPlayers (gs.CpuCount + 1)
            gs <- { gs with CpuCount = c }
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
        // (Invalidate() only queues WM_PAINT; clearing before OnPaint runs
        //  would cause the renderer to never see the dirty flag.)
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
