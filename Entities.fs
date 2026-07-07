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
      KeyRight = false; KeyFire = false; KeyFirePrev = false; KeyDown = false
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
      WeaponSwitchOnlyOnBase = true
      RespawnOnDeath = true
      GamepadsEnabled = true }

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

// ─── Ship triangle hull (matches the rendered triangle) ────────────────
// Every renderer draws the ship as a triangle: nose at 5*Scale screen px
// from the centre and two rear corners at 0.7 of that, ±135° from the nose.
// At the Scale*TerrainZoom screen-px-per-world-px used everywhere, that is
// a 4.0 world-px nose. Collision uses the same triangle, so the hit mask is
// exactly the ship the player sees — a shot can miss through the gap beside
// the nose, and a nose-first ram connects before a sideways brush.

let ShipNoseLen = 4.0
let ShipRearLen = 2.8   // 0.7 * nose
let private shipRearAngle = 0.75 * Math.PI

/// Ship hull triangle vertices (nose, rear-left, rear-right) in world px.
let shipTriangle (x: float) (y: float) (angleDeg: float) =
    let rad = degToRad (angleDeg + 90.0)
    let r1 = rad + shipRearAngle
    let r2 = rad - shipRearAngle
    ((x + cos rad * ShipNoseLen, y - sin rad * ShipNoseLen),
     (x + cos r1 * ShipRearLen, y - sin r1 * ShipRearLen),
     (x + cos r2 * ShipRearLen, y - sin r2 * ShipRearLen))

/// 2D cross product of (b - a) and (p - a) — sign tells which side of AB p is on.
let private edgeSide (ax: float, ay: float) (bx: float, by: float) (px: float, py: float) =
    (bx - ax) * (py - ay) - (by - ay) * (px - ax)

/// Is point P inside triangle ABC? (works for either winding)
let private pointInTriangle p a b c =
    let d1 = edgeSide a b p
    let d2 = edgeSide b c p
    let d3 = edgeSide c a p
    let hasNeg = d1 < 0.0 || d2 < 0.0 || d3 < 0.0
    let hasPos = d1 > 0.0 || d2 > 0.0 || d3 > 0.0
    not (hasNeg && hasPos)

/// Squared distance from point P to segment AB.
let private distSqToSeg (px: float, py: float) (ax: float, ay: float) (bx: float, by: float) =
    let abx = bx - ax
    let aby = by - ay
    let lenSq = abx * abx + aby * aby
    let t = if lenSq <= 0.0 then 0.0 else clampF 0.0 1.0 (((px - ax) * abx + (py - ay) * aby) / lenSq)
    let cx = ax + abx * t
    let cy = ay + aby * t
    (px - cx) * (px - cx) + (py - cy) * (py - cy)

/// Does a circle (projectile of radius r) intersect a ship's hull triangle?
let circleHitsShip (cx: float) (cy: float) (r: float) (shipX: float) (shipY: float) (shipAngle: float) =
    let (a, b, c) = shipTriangle shipX shipY shipAngle
    let p = (cx, cy)
    pointInTriangle p a b c
    || distSqToSeg p a b <= r * r
    || distSqToSeg p b c <= r * r
    || distSqToSeg p c a <= r * r

/// Do two ship hull triangles overlap? Separating-axis test over the edge
/// normals of both triangles (convex shapes: no separating axis = overlap).
let shipsCollide (x1: float) (y1: float) (angle1: float) (x2: float) (y2: float) (angle2: float) =
    let (a1, b1, c1) = shipTriangle x1 y1 angle1
    let (a2, b2, c2) = shipTriangle x2 y2 angle2
    let tri1 = [| a1; b1; c1 |]
    let tri2 = [| a2; b2; c2 |]
    let project (t: (float * float)[]) (nx: float) (ny: float) =
        let mutable lo = Double.MaxValue
        let mutable hi = Double.MinValue
        for (px, py) in t do
            let d = px * nx + py * ny
            if d < lo then lo <- d
            if d > hi then hi <- d
        (lo, hi)
    let separatedByEdgesOf (t: (float * float)[]) =
        let mutable separated = false
        for i in 0 .. 2 do
            let (ax, ay) = t[i]
            let (bx, by) = t[(i + 1) % 3]
            // Edge normal (perpendicular)
            let nx = ay - by
            let ny = bx - ax
            let (lo1, hi1) = project tri1 nx ny
            let (lo2, hi2) = project tri2 nx ny
            if hi1 < lo2 || hi2 < lo1 then separated <- true
        separated
    not (separatedByEdgesOf tri1 || separatedByEdgesOf tri2)

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
