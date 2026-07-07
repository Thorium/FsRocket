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
///   F9: Toggle "change weapons only on bases"
///   F4: Toggle "respawn on death" (menu only) — off = last ship flying wins the round
///   F5/F6: Prev/next level
///   F10: Toggle gamepad input (pads claim players in order; keyboard stays active)
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

let cycleWeapon (gs: GameState) (playerIdx: int) (dir: int) : GameState =
    if playerIdx >= gs.NumPlayers then gs
    else
        let p = gs.Players[playerIdx]
        // Default rule: during a live round the special weapon can only be changed
        // while the ship is parked on a base. Toggle off (F9) to allow it anywhere.
        let allowed = not gs.WeaponSwitchOnlyOnBase || not gs.RoundActive || p.OnBase
        if not allowed then gs
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
let private stickDeadzone = 0.35f
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
            //   P1 (left: WASD) = 1 back / 2 fwd, P2 (right: arrows/numpad) = 8 back / 9 fwd,
            //   P3 (left: TFGH) = 4 back / 5 fwd, P4 (right: IJKL) = 6 back / 7 fwd
            if this.JustPressed Keys.D1 currKeyState then gs <- cycleWeapon gs 0 (-1)
            if this.JustPressed Keys.D2 currKeyState then gs <- cycleWeapon gs 0 1
            if this.JustPressed Keys.D8 currKeyState then gs <- cycleWeapon gs 1 (-1)
            if this.JustPressed Keys.D9 currKeyState then gs <- cycleWeapon gs 1 1
            if this.JustPressed Keys.D4 currKeyState then gs <- cycleWeapon gs 2 (-1)
            if this.JustPressed Keys.D5 currKeyState then gs <- cycleWeapon gs 2 1
            if this.JustPressed Keys.D6 currKeyState then gs <- cycleWeapon gs 3 (-1)
            if this.JustPressed Keys.D7 currKeyState then gs <- cycleWeapon gs 3 1
        // F9 toggles the "change weapons only on bases" rule
        if this.JustPressed Keys.F9 currKeyState then
            gs <- { gs with WeaponSwitchOnlyOnBase = not gs.WeaponSwitchOnlyOnBase }
        // F10 toggles gamepad input (e.g. to ignore a drifting controller)
        if this.JustPressed Keys.F10 currKeyState then
            gs <- { gs with GamepadsEnabled = not gs.GamepadsEnabled }
        // F4 toggles "respawn on death" — menu only, so the rule can't flip mid-round
        if this.JustPressed Keys.F4 currKeyState && not gs.RoundActive then
            gs <- { gs with RespawnOnDeath = not gs.RespawnOnDeath }
        if this.JustPressed Keys.F5 currKeyState then switchLevel -1
        if this.JustPressed Keys.F6 currKeyState then switchLevel 1
        if this.JustPressed Keys.F7 currKeyState then
            cpuCount <- max 0 (cpuCount - 1)
            applyPlayerCount ()
        if this.JustPressed Keys.F8 currKeyState then
            cpuCount <- min (4 - humanCount) (cpuCount + 1)
            applyPlayerCount ()

        // Gamepads: edge-triggered buttons (LB/RB cycle the special weapon,
        // Start begins a round from the menu), then held controls merge into
        // the player inputs below.
        let pads = if gs.GamepadsEnabled then connectedPads () else [||]
        pads |> Array.iteri (fun i pad ->
            if i < 4 then
                let (pw, pn, ps) = padPrev[i]
                let isHuman = i < gs.NumPlayers && not gs.Players[i].IsCpu
                if isHuman && pad.WeaponPrev && not pw then gs <- cycleWeapon gs i -1
                if isHuman && pad.WeaponNext && not pn then gs <- cycleWeapon gs i 1
                if pad.Start && not ps && not gs.RoundActive then gs <- initRound gs
                padPrev[i] <- (pad.WeaponPrev, pad.WeaponNext, pad.Start))

        // Map key states + held pad controls to player inputs
        let has (k: Keys) = currKeyState.IsKeyDown(k)

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
