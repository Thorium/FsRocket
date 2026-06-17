/// FsRocket — Entry Point (MonoGame)
/// MonoGame Game class with keyboard input for 1-4 players
/// Game runs at 36 FPS using MonoGame's fixed timestep
///
/// Controls:
///   Player 1 (BLUE):   Arrows/NumPad + RShift (Thrust/Left/Right/Down/Fire)
///   Player 2 (GREEN):  W/A/D/S/Tab (Thrust/Left/Right/Down/Fire)
///   Player 3 (RED):    I/J/L/K/B (Thrust/Left/Right/Down/Fire)
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
        gs <-
            if levelIdx < levelFiles.Length then
                match tryLoadLevel gs levelFiles[levelIdx] with
                | Some newGs -> { newGs with RoundActive = false }
                | None -> { gs with RoundActive = false; LevelFilePath = "" }
            else
                { gs with Level = None; RoundActive = false; LevelFilePath = "" }

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
        if levelFiles.Length > 0 then
            match tryLoadLevel gs levelFiles[0] with
            | Some newGs -> gs <- newGs
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

        // Player count is selected from the menu only (these number keys double as
        // weapon-switch keys during a live round).
        if not gs.RoundActive then
            if this.JustPressed Keys.D1 currKeyState then humanCount <- 1; applyPlayerCount ()
            if this.JustPressed Keys.D2 currKeyState then humanCount <- 2; applyPlayerCount ()
            if this.JustPressed Keys.D3 currKeyState then humanCount <- 3; applyPlayerCount ()
            if this.JustPressed Keys.D4 currKeyState then humanCount <- 4; applyPlayerCount ()
        else
            // Weapon switch on a number key close to each player's controls:
            //   P1 (right-hand: arrows/numpad) = 9, P2 (left: WASD) = 1, P3 (mid: IJKL) = 6
            if this.JustPressed Keys.D9 currKeyState then gs <- cycleWeapon gs 0
            if this.JustPressed Keys.D1 currKeyState then gs <- cycleWeapon gs 1
            if this.JustPressed Keys.D6 currKeyState then gs <- cycleWeapon gs 2
        // F9 toggles the "change weapons only on bases" rule
        if this.JustPressed Keys.F9 currKeyState then
            gs <- { gs with WeaponSwitchOnlyOnBase = not gs.WeaponSwitchOnlyOnBase }
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
