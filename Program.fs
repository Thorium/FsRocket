/// FsRocket — Entry Point (MonoGame)
/// MonoGame Game class with keyboard input for 1-4 players
/// Game runs at 36 FPS using MonoGame's fixed timestep
///
/// Controls:
///   Player 1 (BLUE):   Arrows/NumPad + RShift (Thrust/Left/Right/Down/Fire)
///   Player 2 (GREEN):  W/A/D/S/Tab (Thrust/Left/Right/Down/Fire)
///   Player 3 (RED):    I/J/L/K/B (Thrust/Left/Right/Down/Fire)
///
///   1-4: Set player count
///   F1-F4: Cycle weapon for P1-P4
///   F5/F6: Prev/next level
///   Space: Start round
///   Escape: Quit
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

let private discoverLevels () : string array =
    let exeDir =
        let loc = System.Reflection.Assembly.GetExecutingAssembly().Location
        let dir = IO.Path.GetDirectoryName(loc)
        if String.IsNullOrEmpty dir then "." else dir
    IO.Directory.GetFiles(exeDir, "*.LEV")
    |> Array.map (fun f -> IO.Path.GetFileNameWithoutExtension(f).ToUpperInvariant())
    |> Array.distinct
    |> Array.sort

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
        while not weapons[wt].Enabled || wt = int WeaponType.Cannon do
            wt <- (wt + 1) % weapons.Length
        let p = { p with SpecialWeapon = enum<WeaponType> wt; SpecialReloadTimer = 0 }
        let players = gs.Players |> List.mapi (fun i pl -> if i = playerIdx then p else pl)
        { gs with Players = players }
    else
        gs

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

    let totalPlayers () = min 4 (humanCount + cpuCount)

    let applyPlayerCount () =
        let total = totalPlayers ()
        gs <- { gs with NumPlayers = total; CpuCount = cpuCount; RoundActive = false }

    let switchLevel (delta: int) =
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

    do
        // Window settings
        graphics.PreferredBackBufferWidth <- 960
        graphics.PreferredBackBufferHeight <- 600
        this.Window.Title <- "FsRocket Physics"
        this.Window.AllowUserResizing <- true
        this.IsMouseVisible <- true

        // Match original 36 FPS timing
        this.IsFixedTimeStep <- true
        this.TargetElapsedTime <- TimeSpan.FromMilliseconds(28.0)

    override this.Initialize() =
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
        base.Initialize()

    override this.LoadContent() =
        renderRes <- initRenderResources this.GraphicsDevice

    /// Check if a key was just pressed this frame (not held)
    member _.JustPressed (key: Keys) (curr: KeyboardState) =
        curr.IsKeyDown(key) && prevKeyState.IsKeyUp(key)

    override this.Update(gameTime) =
        let currKeyState = Keyboard.GetState()

        // Special keys (edge-triggered)
        if this.JustPressed Keys.Escape currKeyState then
            if gs.RoundActive then
                gs <- { gs with RoundActive = false }
            else
                this.Exit()

        if this.JustPressed Keys.Space currKeyState then
            if not gs.RoundActive then
                gs <- initRound gs

        if this.JustPressed Keys.F11 currKeyState then
            graphics.IsFullScreen <- not graphics.IsFullScreen
(*          // If you want native fulscreen instead:
            if not graphics.IsFullScreen then
                // Set native resolution before switching to fullscreen
                let display = Microsoft.Xna.Framework.Graphics.GraphicsAdapter.DefaultAdapter.CurrentDisplayMode
                graphics.PreferredBackBufferWidth <- display.Width
                graphics.PreferredBackBufferHeight <- display.Height
                graphics.HardwareModeSwitch <- false
                graphics.IsFullScreen <- true
            else
                // Restore windowed resolution
                graphics.PreferredBackBufferWidth <- 960
                graphics.PreferredBackBufferHeight <- 600
                graphics.IsFullScreen <- false
*)
                graphics.ApplyChanges()

        if this.JustPressed Keys.D1 currKeyState then humanCount <- 1; applyPlayerCount ()
        if this.JustPressed Keys.D2 currKeyState then humanCount <- 2; applyPlayerCount ()
        if this.JustPressed Keys.D3 currKeyState then humanCount <- 3; applyPlayerCount ()
        if this.JustPressed Keys.D4 currKeyState then humanCount <- 4; applyPlayerCount ()
        if this.JustPressed Keys.F1 currKeyState then gs <- cycleWeapon gs 0
        if this.JustPressed Keys.F2 currKeyState then gs <- cycleWeapon gs 1
        if this.JustPressed Keys.F3 currKeyState then gs <- cycleWeapon gs 2
        if this.JustPressed Keys.F4 currKeyState then gs <- cycleWeapon gs 3
        if this.JustPressed Keys.F5 currKeyState then switchLevel -1
        if this.JustPressed Keys.F6 currKeyState then switchLevel 1
        if this.JustPressed Keys.F7 currKeyState then
            cpuCount <- max 0 (cpuCount - 1)
            applyPlayerCount ()
        if this.JustPressed Keys.F8 currKeyState then
            cpuCount <- min (4 - humanCount) (cpuCount + 1)
            applyPlayerCount ()

        // Map key states to player inputs
        let has (k: Keys) = currKeyState.IsKeyDown(k)

        let players =
            gs.Players |> List.mapi (fun i p ->
                if p.IsCpu then p
                else
                match i with
                | 0 when gs.NumPlayers >= 1 ->
                    { p with
                        KeyUp    = has Keys.Up    || has Keys.NumPad8
                        KeyLeft  = has Keys.Left  || has Keys.NumPad4
                        KeyRight = has Keys.Right || has Keys.NumPad6
                        KeyDown  = has Keys.Down  || has Keys.NumPad5
                        KeyFire  = has Keys.RightShift || has Keys.Enter }
                | 1 when gs.NumPlayers >= 2 ->
                    { p with
                        KeyUp    = has Keys.W
                        KeyLeft  = has Keys.A
                        KeyRight = has Keys.D
                        KeyDown  = has Keys.S
                        KeyFire  = has Keys.Tab }
                | 2 when gs.NumPlayers >= 3 ->
                    { p with
                        KeyUp    = has Keys.I
                        KeyLeft  = has Keys.J
                        KeyRight = has Keys.L
                        KeyDown  = has Keys.K
                        KeyFire  = has Keys.B }
                | _ -> p)

        gs <- { gs with Players = players }

        if gs.RoundActive then
            gs <- gameTick gs

        prevKeyState <- currKeyState
        base.Update(gameTime)

    override this.Draw(gameTime) =
        let windowW = this.GraphicsDevice.Viewport.Width
        let windowH = this.GraphicsDevice.Viewport.Height
        renderFrame renderRes this.GraphicsDevice gs windowW windowH

        // Clear terrain dirty flag after the frame is rendered
        if gs.TerrainDirty then
            gs <- { gs with TerrainDirty = false }

        base.Draw(gameTime)

// ─── Entry Point ───────────────────────────────────────────────────────

[<EntryPoint>]
let main _ =
    use game = new FsRocketGame()
    game.Run()
    0
