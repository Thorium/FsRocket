/// FS Rocket Entity System — Functional Helpers
/// Pure functions that create and transform immutable records.
/// Type definitions live in Types.fs.
module FsRocket.Entities

open System
open FsRocket.Physics
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types

// ─── Factory Functions ─────────────────────────────────────────────────

let playerColors = [| 0x1F; 0x28; 0x30; 0x38 |]  // Blue, Green, Red, Yellow-ish

let createPlayer (index: int) : Player =
    { PosX = 0.0; PosY = 0.0; Angle = SpawnDirection
      VelX = 0.0; VelY = 0.0
      Flags = PlayerFlags.Active; Health = FullHealth
      WeaponType = WeaponType.Cannon       // Main: always Cannon
      SpecialWeapon = WeaponType.Machinegun // Special: default Machinegun
      ReloadTimer = 0; SpecialReloadTimer = 0
      KeyUp = false; KeyLeft = false
      KeyRight = false; KeyFire = false; KeyDown = false
      Ammo = 999
      Color = playerColors[index % 4]; ShotCount = 0; WallHitCount = 0
      CloakAngle = 0.0; StunTimer = 0; Alive = true
      AnimAngle = 0.0; BlackholeCounter = 1
      InvTimer = SpawnInvincibilityTicks; WallDmgCooldown = 0
      KillCount = 0; DeathCount = 0; IsCpu = false; SpawnIndex = -1
      OnBase = false }

let defaultEntity : Entity =
    { X = 0.0; Y = 0.0; Timer = 0; SubType = 0
      VelX = 0.0; VelY = 0.0
      EType = EntityType.None; Owner = 0
      Radius = 0.0; WeaponIdx = WeaponType.NoWeapon }

let defaultParticle : Particle =
    { X = 0.0; Y = 0.0; VelX = 0.0; VelY = 0.0
      Life = 0; Color = 0 }

let createGameState (numPlayers: int) : GameState =
    { Players = List.init 4 createPlayer
      Entities = []
      Particles = []
      Rng = Random ()
      NumPlayers = min numPlayers 4
      CpuCount = 0
      GameTick = 0
      RoundActive = false
      Level = None
      LevelFilePath = ""
      TerrainDirty = false
      WeaponSwitchOnlyOnBase = true }

// ─── Spawn a player at random position ─────────────────────────────────

/// Reset a player to a freshly-spawned state at a base pixel position / index.
let private placeAtSpawn (sx: int) (sy: int) (spawnIdx: int) (p: Player) : Player * int =
    { p with
        PosX = float sx * PositionScale; PosY = float sy * PositionScale
        VelX = 0.0; VelY = 0.0
        Angle = SpawnDirection
        Health = FullHealth
        Alive = true
        InvTimer = SpawnInvincibilityTicks
        WallDmgCooldown = 0
        StunTimer = 0
        Flags = PlayerFlags.Active
        ReloadTimer = 0
        SpecialReloadTimer = 0
        AnimAngle = 0.0
        SpawnIndex = spawnIdx
        OnBase = false }, spawnIdx

/// Choose a spawn pixel, using `chooseBase` when the level has bases and falling
/// back to a random arena position otherwise.
let private pickSpawnPos (rng: Random) (level: LevelData option)
                         (chooseBase: SpawnPoint array -> int * int * int) : int * int * int =
    match level with
    | Some lv when lv.SpawnPoints.Length > 0 -> chooseBase lv.SpawnPoints
    | _ -> rng.Next(int ArenaWidth), rng.Next(int ArenaHeight), -1

/// Spawn a player onto a base, avoiding every base index already occupied by
/// another live player so two ships never share a base.
let spawnPlayerExcluding (rng: Random) (level: LevelData option) (occupied: int list) (p: Player) : Player * int =
    let sx, sy, idx = pickSpawnPos rng level (fun sps -> randomSpawnExcluding sps rng occupied)
    placeAtSpawn sx sy idx p

// ─── Spawn a bullet/projectile — returns new entity to add ─────────────

let makeProjectile (p: Player) (owner: int) (weaponIdx: WeaponType) : Entity =
    let w = getWeapon weaponIdx
    let rad = degToRad (p.Angle + 90.0)
    { defaultEntity with
        X = p.PosX / PositionScale
        Y = p.PosY / PositionScale
        VelX = cos rad * w.ProjectileSpeed
        VelY = -(sin rad) * w.ProjectileSpeed
        EType = w.EntityType
        Owner = owner
        WeaponIdx = weaponIdx }

let makeProjectileAngled (p: Player) (owner: int) (weaponIdx: WeaponType) (angleOffset: float) : Entity =
    let w = getWeapon weaponIdx
    let rad = degToRad (p.Angle + 90.0 + angleOffset)
    { defaultEntity with
        X = p.PosX / PositionScale
        Y = p.PosY / PositionScale
        VelX = cos rad * w.ProjectileSpeed
        VelY = -(sin rad) * w.ProjectileSpeed
        EType = w.EntityType
        Owner = owner
        WeaponIdx = weaponIdx }

let makeProjectileAt (owner: int) (eType: EntityType) (x: float) (y: float) (vx: float) (vy: float) : Entity =
    { defaultEntity with
        X = x; Y = y
        VelX = vx; VelY = vy
        EType = eType
        Owner = owner }

// ─── Check circle-circle collision ─────────────────────────────────────

let collides (x1: float) (y1: float) (r1: float) (x2: float) (y2: float) (r2: float) =
    let dx = x1 - x2
    let dy = y1 - y2
    let dist = dx * dx + dy * dy
    let radSum = r1 + r2
    dist < radSum * radSum

// ─── Arena Walls (obstacles inside the arena) ──────────────────────────

/// Fixed arena layout: some walls for cover
let arenaWalls = [|
    { X = 80.0;  Y = 80.0;  W = 40.0; H = 8.0 }
    { X = 200.0; Y = 80.0;  W = 40.0; H = 8.0 }
    { X = 140.0; Y = 160.0; W = 40.0; H = 8.0 }
    { X = 80.0;  Y = 300.0; W = 40.0; H = 8.0 }
    { X = 200.0; Y = 300.0; W = 40.0; H = 8.0 }
    { X = 30.0;  Y = 180.0; W = 8.0;  H = 40.0 }
    { X = 280.0; Y = 180.0; W = 8.0;  H = 40.0 }
    { X = 150.0; Y = 30.0;  W = 8.0;  H = 30.0 }
    { X = 150.0; Y = 340.0; W = 8.0;  H = 30.0 }
|]

/// Check if a point (with radius) collides with any wall.
let hitsWall (level: LevelData option) (x: float) (y: float) (r: float) =
    match level with
    | Some lv ->
        let px = int (round x)
        let py = int (round y)
        let ri = int (ceil r)
        isWall (getPixel lv.Pixels px py) ||
        isWall (getPixel lv.Pixels (px - ri) py) ||
        isWall (getPixel lv.Pixels (px + ri) py) ||
        isWall (getPixel lv.Pixels px (py - ri)) ||
        isWall (getPixel lv.Pixels px (py + ri))
    | None ->
        arenaWalls |> Array.exists (fun w ->
            x + r > w.X && x - r < w.X + w.W &&
            y + r > w.Y && y - r < w.Y + w.H)

/// Reflect velocity off a wall. Returns (newX, newY, newVelX, newVelY, bounced)
let bounceOffWalls (level: LevelData option) (x: float) (y: float) (vx: float) (vy: float) (r: float) =
    match level with
    | Some lv ->
        let mutable bx = x
        let mutable by = y
        let mutable bvx = vx
        let mutable bvy = vy
        let mutable bounced = false
        let ri = int (ceil r)
        let testX = int (round (x + vx))
        if isWall (getPixel lv.Pixels (testX + ri) (int (round y))) ||
           isWall (getPixel lv.Pixels (testX - ri) (int (round y))) then
            bvx <- -vx
            bx <- x
            bounced <- true
        let testY = int (round (y + vy))
        if isWall (getPixel lv.Pixels (int (round x)) (testY + ri)) ||
           isWall (getPixel lv.Pixels (int (round x)) (testY - ri)) then
            bvy <- -vy
            by <- y
            bounced <- true
        bx, by, bvx, bvy, bounced
    | None ->
        let mutable bx = x
        let mutable by = y
        let mutable bvx = vx
        let mutable bvy = vy
        let mutable bounced = false
        for w in arenaWalls do
            if bx + r > w.X && bx - r < w.X + w.W && by + r > w.Y && by - r < w.Y + w.H then
                let overlapLeft = (bx + r) - w.X
                let overlapRight = (w.X + w.W) - (bx - r)
                let overlapTop = (by + r) - w.Y
                let overlapBottom = (w.Y + w.H) - (by - r)
                let minOverlap = min (min overlapLeft overlapRight) (min overlapTop overlapBottom)
                if minOverlap = overlapLeft || minOverlap = overlapRight then
                    bvx <- -bvx
                    if minOverlap = overlapLeft then bx <- w.X - r - 0.1
                    else bx <- w.X + w.W + r + 0.1
                else
                    bvy <- -bvy
                    if minOverlap = overlapTop then by <- w.Y - r - 0.1
                    else by <- w.Y + w.H + r + 0.1
                bounced <- true
        bx, by, bvx, bvy, bounced

// ─── Spawn explosion particles ─────────────────────────────────────────

let spawnExplosionParticles (rng: Random) (x: float) (y: float) (count: int) (speed: float) (life: int) (color: int) : Particle list =
    [ for i in 0 .. count - 1 do
        let angle = degToRad (float i * 360.0 / float count + float (rng.Next 15))
        let spd = speed * (0.5 + float (rng.Next 100) / 100.0)
        { X = x; Y = y
          VelX = cos angle * spd
          VelY = sin angle * spd
          Life = life + rng.Next(life / 2)
          Color = color } ]
