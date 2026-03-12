/// FsRocket Game Logic
/// Main update loop: player movement, weapon firing, entity updates, collision detection.
/// Pure functional: gameTick : GameState -> GameState
module FsRocket.Game

open System
open FsRocket.Physics
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types
open FsRocket.Entities

// ─── Helpers ───────────────────────────────────────────────────────────

/// Spawn explosion particles (convenience wrapper)
let spawnExplosion (rng: Random) (x: float) (y: float) (count: int) (speed: float) (life: int) (ownerIdx: int) : Particle list =
    spawnExplosionParticles rng x y count speed life (playerColors[ownerIdx % 4])

// ─── Weapon Firing (returns updated Player * new Entity list) ──────────

let fireWeapon (rng: Random) (level: LevelData option) (p: Player) (ownerIdx: int) : Player * Entity list =
    let w = getWeapon p.WeaponType
    let p = { p with ReloadTimer = w.ReloadTicks; Ammo = p.Ammo - 1; ShotCount = p.ShotCount + 1 }

    match p.WeaponType with
    | 3 ->  // REAR TURRET — fires behind
        p, [ makeProjectileAngled p ownerIdx p.WeaponType 180.0 ]

    | 4 ->  // MULTICANNON — 3-way spread
        let ents = [ for offset in [ -2.2; 0.0; 2.2 ] -> makeProjectileAngled p ownerIdx p.WeaponType offset ]
        p, ents

    | 5 ->  // RUBBER BLTS — 24 bullets in full circle at 15° intervals
        let ents = [ for i in 0..23 ->
                        let angle = float i * 15.0 - 15.0 
                        makeProjectileAngled p ownerIdx p.WeaponType angle ]
        p, ents

    | 6 ->  // MINE — stationary at player position, arms after 25 ticks
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Mine; Owner = ownerIdx
                        Timer = -25; WeaponIdx = 6 }
        p, [ ent ]

    | 8 ->  // DIRTCLOD — lobbed with gravity
        let rad = degToRad (p.Angle + 90.0)
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Exploding; Owner = ownerIdx; WeaponIdx = 8 }
        p, [ ent ]

    | 11 -> // ATOM WEAPON — nuke
        let rad = degToRad (p.Angle + 90.0)
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Nuke; Owner = ownerIdx; WeaponIdx = 11 }
        p, [ ent ]

    // | 12 -> // TROOPERS — TODO: deploy ground units that shoot nearby opponents
    //     let ents = [ for off in [ 0.0; 90.0; 180.0; 270.0 ] -> makeProjectileAngled p ownerIdx p.WeaponType off ]
    //     p, ents

    | 13 -> // HELL FIRE — flame with random spread
        let spread = float (rng.Next 7 - 3)
        let rad = degToRad (p.Angle + 90.0 + spread)
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Flame; Owner = ownerIdx; WeaponIdx = 13 }
        p, [ ent ]

    | 15 -> // SONICBOOM — expanding ring from player position
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Expanding; Owner = ownerIdx
                        Radius = 4.0; WeaponIdx = 15 }
        p, [ ent ]

    | 17 -> // TOXIC DUMP — persisting flame pool
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Flame; Owner = ownerIdx
                        Timer = -200; Radius = 8.0; WeaponIdx = 17 }
        p, [ ent ]

    | 19 -> // MISSILE — homing
        let rad = degToRad (p.Angle + 90.0)
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Heavy; Owner = ownerIdx; WeaponIdx = 19 }
        p, [ ent ]

    | 20 -> // BLACKHOLE — gravity well
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Blackhole; Owner = ownerIdx; WeaponIdx = 20 }
        p, [ ent ]

    | _ ->  // Standard forward fire
        p, [ makeProjectile p ownerIdx p.WeaponType ]

// ─── Special Weapon Firing (DOWN key) ──────────────────────────────────

let fireSpecial (rng: Random) (level: LevelData option) (p: Player) (ownerIdx: int) : Player * Entity list =
    let w = getWeapon p.WeaponType

    match p.WeaponType with
    | 2 ->
        // MAGNOFILTER — toggle flag
        let flags =
            if p.Flags.HasFlag(PlayerFlags.Magno) then p.Flags &&& ~~~PlayerFlags.Magno
            else p.Flags ||| PlayerFlags.Magno
        { p with Flags = flags; KeyDown = false }, []

    | 3 ->
        // REAR TURRET special: set cooldown
        { p with ReloadTimer = w.ReloadTicks }, []

    | 4 ->
        // MULTICANNON special: fires behind
        { p with ReloadTimer = w.ReloadTicks }, [ makeProjectileAngled p ownerIdx p.WeaponType 180.0 ]

    | 7 ->
        // NUCLEUS special: shield at player pos
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Shield; Owner = ownerIdx; WeaponIdx = 7 }
        { p with ReloadTimer = w.ReloadTicks }, [ ent ]

    | 13 ->
        // HELL FIRE special: wider spread
        let ents = [ for _ in 1..3 ->
                        let spread = float (rng.Next 21 - 10)
                        makeProjectileAngled p ownerIdx p.WeaponType spread ]
        { p with ReloadTimer = w.ReloadTicks }, ents

    | 14 ->
        // MACHINEGUN special: random offset
        let randOffset = float (rng.Next 21 - 10)
        { p with ReloadTimer = w.ReloadTicks }, [ makeProjectileAngled p ownerIdx p.WeaponType randOffset ]

    | 16 ->
        // FAN special: fan arc
        let ents = [ for i in -4..4 -> makeProjectileAngled p ownerIdx p.WeaponType (float i * 8.0) ]
        { p with ReloadTimer = w.ReloadTicks }, ents

    | 19 ->
        // MISSILE special: fire with recoil
        let ent = makeProjectile p ownerIdx p.WeaponType
        let rad = degToRad (p.Angle + 90.0)
        { p with
            VelX = p.VelX - cos rad * 0.5
            VelY = p.VelY + sin rad * 0.5
            ReloadTimer = w.ReloadTicks }, [ ent ]

    | 20 ->
        // BLACKHOLE special: stationary gravity well + counter
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Blackhole; Owner = ownerIdx; WeaponIdx = 20 }
        { p with
            BlackholeCounter = min 5 (p.BlackholeCounter + 1)
            ReloadTimer = w.ReloadTicks }, [ ent ]

    | _ ->
        // Default: standard forward fire
        { p with ReloadTimer = w.ReloadTicks }, [ makeProjectile p ownerIdx p.WeaponType ]

// ─── Player Update ─────────────────────────────────────────────────────
// Returns (updated Player, new Entities, new Particles, terrainModified)

let updatePlayer (gs: GameState) (idx: int) : Player * Entity list * Particle list * bool =
    let p = gs.Players[idx]
    if not p.Alive then (p, [], [], false) else

    // Shield blocks ALL input — cleared only by terrain contact or pass-through
    if p.Flags.HasFlag(PlayerFlags.Shield) then
        let velY = min (p.VelY + GravityAccel) MaxVelocity
        let px = p.PosX + p.VelX * PositionScale
        let py = p.PosY + velY * PositionScale
        let maxX = ArenaWidth * PositionScale
        let maxY = ArenaHeight * PositionScale
        let pixX = px / PositionScale
        let pixY = py / PositionScale
        let hitsTerrain =
            match gs.Level with
            | Some level -> Terrain.isWall (Terrain.terrainAt level.Pixels pixX pixY)
            | None -> hitsWall gs.Level pixX pixY 4.0
        let hitsBoundary = px < 0.0 || px > maxX || py < 0.0 || py > maxY
        if hitsTerrain || hitsBoundary then
            // Terrain contact: clear shield, doubled collision damage (4.8x)
            let speed = abs p.VelX + abs velY
            let speedDamage = max 1 (int (speed * ShieldKnockbackScale))
            let flags = p.Flags &&& ~~~PlayerFlags.Shield
            let h = if p.InvTimer = 0 then p.Health - speedDamage else p.Health
            let inv = if p.InvTimer = 0 then SpawnInvincibilityTicks else p.InvTimer
            ({ p with PosX = p.PosX; PosY = p.PosY; VelX = 0.0; VelY = 0.0
                      Flags = flags; Health = h; InvTimer = inv
                      WallHitCount = p.WallHitCount + 1 }, [], [], false)
        else
            let px = clampF 0.0 maxX px
            let py = clampF 0.0 maxY py
            ({ p with PosX = px; PosY = py; VelX = p.VelX; VelY = velY }, [], [], false)
    else

    // Invincibility countdown
    let invTimer = if p.InvTimer > 0 then p.InvTimer - 1 else 0

    // Wall damage cooldown countdown
    let wallDmgCooldown = if p.WallDmgCooldown > 0 then p.WallDmgCooldown - 1 else 0

    // Stun countdown
    let stunTimer, flags =
        if p.StunTimer > 0 then
            let st = p.StunTimer - 1
            if st = 0 then (0, p.Flags &&& ~~~PlayerFlags.Stunned)
            else (st, p.Flags)
        else (p.StunTimer, p.Flags)

    // Animation timer
    let animAngle =
        let a = p.AnimAngle + 2.0
        if a >= MaxAngle then a - MaxAngle else a

    // Input processing (unless stunned)
    let canControl = not (flags.HasFlag(PlayerFlags.Stunned))

    // Turning
    let angle =
        if canControl then
            let a =
                if p.KeyLeft then
                    let a = p.Angle + TurnSpeed
                    if a >= MaxAngle then a - MaxAngle else a
                else p.Angle
            if p.KeyRight then
                let a = a - TurnSpeed
                if a < 0.0 then a + MaxAngle else a
            else a
        else p.Angle

    // Thrust
    let velX, velY =
        if canControl && p.KeyUp then
            let rad = degToRad (angle + 90.0)
            let vx = clampF -MaxVelocity MaxVelocity (p.VelX + cos rad * ThrustAccel)
            let vy = clampF -MaxVelocity MaxVelocity (p.VelY - sin rad * ThrustAccel)
            vx, vy
        else p.VelX, p.VelY

    // Gravity
    let velY = min (velY + GravityAccel) MaxVelocity

    // Drunk wobble
    let velX, velY =
        if flags.HasFlag(PlayerFlags.Drunk) then
            let wobble1 = angle - 45.0 + float (gs.Rng.Next 90)
            let wobble2 = angle + 45.0 + float (gs.Rng.Next 90)
            let rad1 = degToRad wobble1
            let rad2 = degToRad wobble2
            velX - cos rad1 * DrunkForce + cos rad2 * DrunkForce,
            velY + sin rad1 * DrunkForce - sin rad2 * DrunkForce
        else velX, velY

    // Update position
    let posX = p.PosX + velX * PositionScale
    let posY = p.PosY + velY * PositionScale

    // Terrain checks
    let px = posX / PositionScale
    let py = posY / PositionScale

    let posX, posY, velX, velY, angle, health, wallHitCount, wallDmgCooldown, terrainModified =
        match gs.Level with
        | Some level ->
            let pixel = Terrain.terrainAt level.Pixels px py
            if Terrain.isWall pixel then
                let speed = abs velX + abs velY
                let speedDamage = max 1 (int (speed * NormalKnockbackScale))
                let h, whc, wdc =
                    if wallDmgCooldown = 0 then
                        (p.Health - speedDamage, p.WallHitCount + 1, SpawnInvincibilityTicks)
                    else (p.Health, p.WallHitCount, wallDmgCooldown)
                // Undo move, keep facing direction
                (p.PosX, p.PosY, 0.0, 0.0, angle, h, whc, wdc, false)
            elif Terrain.isWater pixel then
                // Water friction
                let vy =
                    if velY > 0.0 then let v = velY + FrictionDecel in if v < 0.0 then 0.0 else v
                    elif velY < 0.0 then let v = velY - FrictionDecel in if v > 0.0 then 0.0 else v
                    else velY
                let vx =
                    if velX > 0.0 then let v = velX + FrictionDecel in if v < 0.0 then 0.0 else v
                    elif velX < 0.0 then let v = velX - FrictionDecel in if v > 0.0 then 0.0 else v
                    else velX
                let vx = clampF -FrictionMaxVel FrictionMaxVel vx
                let vy = clampF -FrictionMaxVel FrictionMaxVel vy
                (posX, posY, vx, vy, angle, p.Health, p.WallHitCount, wallDmgCooldown, false)
            else
                (posX, posY, velX, velY, angle, p.Health, p.WallHitCount, wallDmgCooldown, false)
        | None ->
            if hitsWall gs.Level px py 4.0 then
                (p.PosX, p.PosY, velX * -0.3, velY * -0.3, angle, p.Health, p.WallHitCount, wallDmgCooldown, false)
            else
                (posX, posY, velX, velY, angle, p.Health, p.WallHitCount, wallDmgCooldown, false)

    // Boundary wall collision
    let maxX = ArenaWidth * PositionScale
    let maxY = ArenaHeight * PositionScale
    let mutable hitBoundary = false
    let mutable bpx = posX
    let mutable bpy = posY
    if posX < 0.0 then bpx <- 0.0; hitBoundary <- true
    if posX > maxX then bpx <- maxX; hitBoundary <- true
    if posY < 0.0 then bpy <- 0.0; hitBoundary <- true
    if posY > maxY then bpy <- maxY; hitBoundary <- true

    let posX, posY, velX, velY, angle, health, wallHitCount, wallDmgCooldown =
        if hitBoundary then
            let boundarySpeed = abs velX + abs velY
            let speedDamage = max 1 (int (boundarySpeed * NormalKnockbackScale))
            let h, whc, wdc =
                if wallDmgCooldown = 0 then
                    (health - speedDamage, wallHitCount + 1, SpawnInvincibilityTicks)
                else (health, wallHitCount, wallDmgCooldown)
            // Keep facing direction on boundary hit
            (bpx, bpy, 0.0, 0.0, angle, h, whc, wdc)
        else
            (posX, posY, velX, velY, angle, health, wallHitCount, wallDmgCooldown)

    // Reload timer
    let reloadTimer = if p.ReloadTimer > 0 then p.ReloadTimer - 1 else 0

    // Build partially-updated player
    let p = { p with
                PosX = posX; PosY = posY; Angle = angle
                VelX = velX; VelY = velY; Flags = flags
                Health = health; WallHitCount = wallHitCount
                InvTimer = invTimer; WallDmgCooldown = wallDmgCooldown
                StunTimer = stunTimer
                AnimAngle = animAngle; ReloadTimer = reloadTimer }

    // Weapon firing
    let p, newEnts1 =
        if p.KeyFire && p.ReloadTimer = 0 && p.Ammo > 0 && canControl then
            fireWeapon gs.Rng gs.Level p idx
        else p, []

    // Special weapon firing (DOWN key)
    let p, newEnts2 =
        if p.KeyDown && canControl then
            match p.WeaponType with
            | 12 -> p, []  // TROOPERS — DOWN ignored
            | 2 | 13 | 14 ->
                fireSpecial gs.Rng gs.Level p idx
            | _ ->
                if p.ReloadTimer = 0 then fireSpecial gs.Rng gs.Level p idx
                else p, []
        else p, []

    // Death check
    if p.Health <= DeathThreshold then
        let p = { p with Alive = false; DeathCount = p.DeathCount + 1; AnimAngle = 0.0 }
        let deathParticles = spawnExplosion gs.Rng (p.PosX / PositionScale) (p.PosY / PositionScale) 12 2.5 25 idx
        (p, newEnts1 @ newEnts2, deathParticles, terrainModified)
    else
        (p, newEnts1 @ newEnts2, [], terrainModified)

// ─── Entity Update ─────────────────────────────────────────────────────
// Returns (Entity option, new Entity list, new Particle list, terrainModified)

let updateEntity (gs: GameState) (ent: Entity) : Entity option * Entity list * Particle list * bool =
    let ent = { ent with Timer = ent.Timer + 1 }
    let mutable dirty = false

    let inline eraseTerrain level cx cy radius =
        match level with
        | Some lv -> if Terrain.eraseTerrainCircle lv.Pixels cx cy radius then dirty <- true
        | None -> ()

    let inline outOfBounds margin x y =
        x < -margin || x > ArenaWidth + margin || y < -margin || y > ArenaHeight + margin

    match ent.EType with
    | EntityType.Ricochet ->
        // angle advances +16°/tick, VelX/VelY recomputed relative to owner — orbits/spirals
        let angle = float ent.Timer * 16.0  // Timer increments by 1/tick, angle = timer * 16°
        let rad = degToRad angle
        // Spiral outward: radius grows with time
        let radius = float ent.Timer * 0.5
        // Get owner position
        let ox, oy =
            if ent.Owner >= 0 && ent.Owner < gs.NumPlayers && gs.Players[ent.Owner].Alive then
                gs.Players[ent.Owner].PosX / PositionScale, gs.Players[ent.Owner].PosY / PositionScale
            else ent.X, ent.Y  // owner dead — freeze in place
        let x = ox + cos rad * radius
        let y = oy + sin rad * radius
        let alive = ent.Timer <= 200 && not (outOfBounds 10.0 x y)
        if alive then
            (Some { ent with X = x; Y = y; VelX = cos rad * 2.0; VelY = sin rad * 2.0 }, [], [], false)
        else (None, [], [], false)

    | EntityType.Flame ->
        let x = ent.X + ent.VelX
        let y = ent.Y + ent.VelY
        let vy = min (ent.VelY + 0.08) 4.0
        let vx = ent.VelX * 0.98
        // Erase terrain on wall contact
        if hitsWall gs.Level x y 1.0 then
            eraseTerrain gs.Level x y 2.0
        // Spawn flame particles every other tick
        let particles =
            if ent.Timer % 2 = 0 then
                spawnExplosion gs.Rng x y 1 0.5 6 (ent.Owner % 4)
            else []
        let alive = ent.Timer <= 60 && not (outOfBounds 10.0 x y)
        if alive then
            (Some { ent with X = x; Y = y; VelX = vx; VelY = vy }, [], particles, dirty)
        else (None, [], particles, dirty)

    | EntityType.Exploding ->
        let x = ent.X + ent.VelX
        let y = ent.Y + ent.VelY
        let vy = ent.VelY + 0.15
        let explode =
            y > ArenaHeight || y < 0.0 || x < 0.0 || x > ArenaWidth ||
            hitsWall gs.Level x y 2.0 || ent.Timer > 120
        if explode then
            eraseTerrain gs.Level x y 8.0
            let shrapnel = [
                for i in 0..5 do
                    let angle = degToRad (float i * 60.0 + float (gs.Rng.Next 30))
                    let speed = 1.5 + float (gs.Rng.Next 100) / 100.0
                    makeProjectileAt ent.Owner EntityType.Shrapnel x y (cos angle * speed) (sin angle * speed)
            ]
            let parts = spawnExplosion gs.Rng x y 8 2.0 15 (ent.Owner % 4)
            (None, shrapnel, parts, dirty)
        else
            (Some { ent with X = x; Y = y; VelY = vy }, [], [], dirty)

    | EntityType.Nuke ->
        let vy = ent.VelY + 0.125  // Gravity
        let x = ent.X + ent.VelX
        let y = ent.Y + vy
        let vx = ent.VelX * 0.96
        let vy = vy * 0.96
        let radius = ent.Radius + 1.5
        eraseTerrain gs.Level x y radius
        if radius >= nukeBlastRadius then
            let parts = spawnExplosion gs.Rng x y 20 3.0 30 (ent.Owner % 4)
            (None, [], parts, dirty)
        elif outOfBounds 20.0 x y then
            (None, [], [], dirty)
        else
            (Some { ent with X = x; Y = y; VelX = vx; VelY = vy; Radius = radius }, [], [], dirty)

    | EntityType.Blackhole ->
        // Blackhole itself doesn't move — entity/player pulling is done in a separate pass
        let alive = ent.Timer <= 180
        let radius = 6.0 + 3.0 * sin (float ent.Timer * 0.15)
        if alive then
            (Some { ent with Radius = radius }, [], [], false)
        else (None, [], [], false)

    | EntityType.Expanding ->
        let radius = ent.Radius + expandingGrowthRate
        if radius >= expandingMaxRadius then
            (None, [], [], dirty)
        else
            // Erase terrain along ring
            match gs.Level with
            | Some level ->
                let numPoints = max 8 (int (radius * 0.5))
                for i in 0..numPoints-1 do
                    let angle = degToRad (float i * 360.0 / float numPoints)
                    let rx = ent.X + cos angle * radius
                    let ry = ent.Y + sin angle * radius
                    if Terrain.eraseTerrainCircle level.Pixels rx ry 2.0 then dirty <- true
            | None -> ()
            // Spawn ring particles every 3rd tick
            let particles =
                if ent.Timer % 3 = 0 then
                    let numParts = max 4 (int (radius / 8.0))
                    [ for i in 0..numParts-1 do
                        let angle = degToRad (float i * 360.0 / float numParts)
                        let px = ent.X + cos angle * radius
                        let py = ent.Y + sin angle * radius
                        { defaultParticle with
                            X = px; Y = py
                            VelX = cos angle * 0.5; VelY = sin angle * 0.5
                            Life = 5; Color = ent.Owner % 4 } ]
                else []
            (Some { ent with Radius = radius }, [], particles, dirty)

    | EntityType.Heavy ->
        let mutable x = ent.X + ent.VelX
        let mutable y = ent.Y + ent.VelY
        let mutable vx = ent.VelX
        let mutable vy = ent.VelY

        // Missile homing (weapon 19)
        let particles =
            if ent.WeaponIdx = 19 && ent.Timer > 5 then
                // Find nearest enemy
                let mutable bestDist = Double.MaxValue
                let mutable bestIdx = -1
                for i in 0..gs.NumPlayers-1 do
                    if i <> ent.Owner && gs.Players[i].Alive then
                        let px = gs.Players[i].PosX / PositionScale
                        let py = gs.Players[i].PosY / PositionScale
                        let dx = px - x
                        let dy = py - y
                        let d = dx*dx + dy*dy
                        if d < bestDist then bestDist <- d; bestIdx <- i
                if bestIdx >= 0 then
                    let px = gs.Players[bestIdx].PosX / PositionScale
                    let py = gs.Players[bestIdx].PosY / PositionScale
                    let dx = px - x
                    let dy = py - y
                    let targetAngle = atan2 -dy dx
                    let currentAngle = atan2 -vy vx
                    let mutable diff = targetAngle - currentAngle
                    if diff > Math.PI then diff <- diff - 2.0 * Math.PI
                    if diff < -Math.PI then diff <- diff + 2.0 * Math.PI
                    let turnRate = degToRad missileHomingRate
                    let turn = clampF -turnRate turnRate diff
                    let newAngle = currentAngle + turn
                    let speed = sqrt (vx*vx + vy*vy)
                    vx <- cos newAngle * speed
                    vy <- -(sin newAngle) * speed
                // Smoke trail
                if ent.Timer % 2 = 0 then
                    [ { defaultParticle with
                          X = x; Y = y
                          VelX = float (gs.Rng.Next 3 - 1) * 0.2
                          VelY = float (gs.Rng.Next 3 - 1) * 0.2
                          Life = 8; Color = ent.Owner % 4 } ]
                else []
            else []

        // Wall collision — explode
        if hitsWall gs.Level x y 3.0 then
            eraseTerrain gs.Level x y 6.0
            let parts = spawnExplosion gs.Rng x y 6 1.5 12 (ent.Owner % 4)
            (None, [], parts @ particles, dirty)
        elif outOfBounds 20.0 x y || ent.Timer > 300 then
            (None, [], particles, dirty)
        else
            (Some { ent with X = x; Y = y; VelX = vx; VelY = vy }, [], particles, dirty)

    | EntityType.Laser ->
        let x = ent.X + ent.VelX
        let y = ent.Y + ent.VelY
        if ent.Timer > 15 || outOfBounds 10.0 x y then
            (None, [], [], false)
        else
            (Some { ent with X = x; Y = y }, [], [], false)

    | EntityType.Shrapnel ->
        let x = ent.X + ent.VelX
        let y = ent.Y + ent.VelY
        let vy = ent.VelY + 0.05
        if ent.Timer > 40 || outOfBounds 10.0 x y then
            (None, [], [], false)
        else
            (Some { ent with X = x; Y = y; VelY = vy }, [], [], false)

    | EntityType.Mine ->
        if ent.Timer > 600 then
            eraseTerrain gs.Level ent.X ent.Y 10.0
            let parts = spawnExplosion gs.Rng ent.X ent.Y 6 1.5 12 (ent.Owner % 4)
            (None, [], parts, dirty)
        else
            (Some ent, [], [], false)

    | EntityType.EMP ->
        let vy = ent.VelY + 0.125  // Gravity (VelY++ in integer units)
        let x = ent.X + ent.VelX
        let y = ent.Y + vy
        let radius = 3.0 + 2.0 * sin (float ent.Timer * 0.3)
        if ent.Timer > 120 || outOfBounds 10.0 x y then
            (None, [], [], false)
        else
            (Some { ent with X = x; Y = y; VelY = vy; Radius = radius }, [], [], false)

    | _ ->
        // Default: linear motion (Bullet, BulletAlt, PassThrough, Shield, etc.)
        // Gravity for Bullet, BulletAlt, Shield (VelY++ each tick)
        let hasGravity =
            match ent.EType with
            | EntityType.Bullet | EntityType.BulletAlt | EntityType.Shield -> true
            | _ -> false
        let vy = if hasGravity then ent.VelY + 0.125 else ent.VelY
        // BulletAlt has VelY clamp of 128 integer = 16.0 float
        let vy =
            if ent.EType = EntityType.BulletAlt then min vy 16.0 else vy
        let x = ent.X + ent.VelX
        let y = ent.Y + vy
        if hitsWall gs.Level x y 1.0 then
            eraseTerrain gs.Level x y 3.0
            (None, [], [], dirty)
        elif outOfBounds 20.0 x y || ent.Timer > 200 then
            (None, [], [], dirty)
        else
            (Some { ent with X = x; Y = y; VelY = vy }, [], [], dirty)

// ─── Blackhole Pull Pass ───────────────────────────────────────────────
// Applied after all entities update, pulls entities and players toward blackholes.
// Returns (updated Entity list, updated Player list)

let applyBlackholePull (entities: Entity list) (players: Player list) (numPlayers: int) : Entity list * Player list =
    let blackholes = entities |> List.filter (fun e -> e.EType = EntityType.Blackhole)
    if blackholes.IsEmpty then (entities, players) else

    // Pull entities
    let entities =
        entities |> List.map (fun other ->
            if other.EType = EntityType.Blackhole then other
            else
                let mutable vx = other.VelX
                let mutable vy = other.VelY
                for bh in blackholes do
                    let dx = bh.X - other.X
                    let dy = bh.Y - other.Y
                    let dist = sqrt (dx*dx + dy*dy) + 0.1
                    if dist < blackholeRadius then
                        let force = blackholeStrength * (1.0 - dist / blackholeRadius)
                        vx <- vx + dx / dist * force
                        vy <- vy + dy / dist * force
                if vx <> other.VelX || vy <> other.VelY then
                    { other with VelX = vx; VelY = vy }
                else other)

    // Pull players
    let players =
        players |> List.mapi (fun i p ->
            if i >= numPlayers || not p.Alive then p
            else
                let px = p.PosX / PositionScale
                let py = p.PosY / PositionScale
                let mutable vx = p.VelX
                let mutable vy = p.VelY
                for bh in blackholes do
                    let dx = bh.X - px
                    let dy = bh.Y - py
                    let dist = sqrt (dx*dx + dy*dy) + 0.1
                    if dist < blackholeRadius then
                        let force = blackholeStrength * 0.3 * (1.0 - dist / blackholeRadius)
                        vx <- vx + dx / dist * force
                        vy <- vy + dy / dist * force
                if vx <> p.VelX || vy <> p.VelY then
                    { p with VelX = vx; VelY = vy }
                else p)

    (entities, players)

// ─── Bullet-Player Collision ───────────────────────────────────────────
// Returns (updated Player list, updated Entity list, new Particle list, terrainModified)

let checkBulletPlayerCollision (gs: GameState) (players: Player list) (entities: Entity list) : Player list * Entity list * Particle list * bool =
    let mutable ps = Array.ofList players
    let mutable es = Array.ofList entities
    let mutable newParticles : Particle list = []
    let mutable dirty = false

    for ei in 0 .. es.Length - 1 do
        let mutable ent = es[ei]
        if ent.EType <> EntityType.None then
            for i in 0 .. gs.NumPlayers - 1 do
                let p = ps[i]
                if p.Alive && i <> ent.Owner && p.InvTimer = 0 then
                    let radius =
                        match ent.EType with
                        | EntityType.Mine -> 8.0
                        | EntityType.Heavy -> 6.0
                        | EntityType.Nuke -> max 4.0 ent.Radius
                        | EntityType.Expanding -> ent.Radius
                        | EntityType.Blackhole -> 6.0
                        | EntityType.Flame -> max 2.0 (float ent.Timer * 0.3)
                        | _ -> 4.0
                    let playerRadius = 4.0
                    let px = p.PosX / PositionScale
                    let py = p.PosY / PositionScale

                    let hits =
                        if ent.EType = EntityType.Expanding then
                            let dx = ent.X - px
                            let dy = ent.Y - py
                            let dist = sqrt (dx*dx + dy*dy)
                            abs (dist - ent.Radius) < playerRadius + 4.0
                        else
                            collides ent.X ent.Y radius px py playerRadius

                    if hits then
                        let damage =
                            match ent.EType with
                            | EntityType.Bullet | EntityType.BulletAlt -> 2
                            | EntityType.Mine -> if ent.Timer > 0 then 30 else 0
                            | EntityType.EMP -> 0
                            | EntityType.Shield -> 0
                            | EntityType.Ricochet -> 3
                            | EntityType.PassThrough -> 1
                            | EntityType.Laser -> 1
                            | EntityType.Heavy -> heavyDamage ent.Timer
                            | EntityType.Flame -> 1
                            | EntityType.Nuke -> 15
                            | EntityType.Railgun -> 15
                            | EntityType.Shrapnel -> 1
                            | EntityType.Expanding -> 10
                            | EntityType.Exploding -> 4
                            | EntityType.Blackhole -> 2
                            | _ -> 0

                        if damage > 0 || ent.EType = EntityType.EMP || ent.EType = EntityType.Shield then
                            let mutable np = { p with Health = p.Health - damage }

                            // Knockback
                            let knockScale =
                                if np.Flags.HasFlag(PlayerFlags.Shield) then ShieldKnockbackScale
                                else NormalKnockbackScale
                            if ent.VelX <> 0.0 || ent.VelY <> 0.0 then
                                let kbDiv =
                                    match ent.EType with
                                    | EntityType.Nuke | EntityType.Expanding -> 1.0
                                    | EntityType.Heavy -> 2.0
                                    | _ -> BulletKnockbackDiv
                                np <- { np with
                                            VelX = np.VelX + ent.VelX / (kbDiv * knockScale * PositionScale)
                                            VelY = np.VelY + ent.VelY / (kbDiv * knockScale * PositionScale) }
                            else
                                match ent.EType with
                                | EntityType.Mine | EntityType.Expanding | EntityType.Nuke ->
                                    let dx = px - ent.X
                                    let dy = py - ent.Y
                                    let dist = sqrt (dx*dx + dy*dy) + 0.1
                                    let kbForce =
                                        match ent.EType with
                                        | EntityType.Nuke -> 1.5
                                        | EntityType.Expanding -> 0.8
                                        | _ -> 1.0
                                    np <- { np with
                                                VelX = np.VelX + dx / dist * kbForce / knockScale
                                                VelY = np.VelY + dy / dist * kbForce / knockScale }
                                | _ -> ()

                            // Special effects
                            match ent.EType with
                            | EntityType.EMP ->
                                np <- { np with
                                            Flags = np.Flags ||| PlayerFlags.Stunned
                                            StunTimer = np.StunTimer + StunDurationPerHit }
                            | EntityType.Shield ->
                                np <- { np with
                                            Flags = np.Flags ||| PlayerFlags.Shield
                                            StunTimer = 0 }
                            | EntityType.PassThrough ->
                                np <- { np with Flags = np.Flags &&& ~~~PlayerFlags.Shield }
                            | _ -> ()

                            np <- { np with InvTimer = SpawnInvincibilityTicks }

                            // Deactivate projectile?
                            match ent.EType with
                            | EntityType.PassThrough | EntityType.Laser
                            | EntityType.Expanding | EntityType.Blackhole
                            | EntityType.Nuke | EntityType.Ricochet -> ()  // Persist through hits
                            | EntityType.Mine ->
                                match gs.Level with
                                | Some level ->
                                    if Terrain.eraseTerrainCircle level.Pixels ent.X ent.Y 10.0 then dirty <- true
                                | None -> ()
                                newParticles <- newParticles @ spawnExplosion gs.Rng ent.X ent.Y 10 2.0 20 (ent.Owner % 4)
                                ent <- { ent with EType = EntityType.None }  // Mark for removal
                            | _ ->
                                ent <- { ent with EType = EntityType.None }  // Mark for removal

                            // Credit kill
                            if np.Health <= DeathThreshold then
                                if ent.Owner >= 0 && ent.Owner < gs.NumPlayers then
                                    let killer = ps[ent.Owner]
                                    ps[ent.Owner] <- { killer with KillCount = killer.KillCount + 1 }

                            ps[i] <- np
        es[ei] <- ent

    // Filter out "removed" entities (marked with EType = None)
    let entities = es |> Array.toList |> List.filter (fun e -> e.EType <> EntityType.None)
    (Array.toList ps, entities, newParticles, dirty)

// ─── Player-Player Collision ───────────────────────────────────────────

let checkPlayerCollision (players: Player list) (numPlayers: int) : Player list =
    let ps = Array.ofList players
    for i in 0..numPlayers-2 do
        for j in i+1..numPlayers-1 do
            let p1 = ps[i]
            let p2 = ps[j]
            if p1.Alive && p2.Alive then
                let x1 = p1.PosX / PositionScale
                let y1 = p1.PosY / PositionScale
                let x2 = p2.PosX / PositionScale
                let y2 = p2.PosY / PositionScale
                let collisionDist = 8.0
                if collides x1 y1 (collisionDist/2.0) x2 y2 (collisionDist/2.0) then
                    let dx = x2 - x1
                    let dy = y2 - y1
                    let dist = sqrt (dx*dx + dy*dy) + 0.001
                    let nx = dx / dist
                    let ny = dy / dist
                    let bounce = 0.5
                    ps[i] <- { p1 with VelX = p1.VelX - nx * bounce; VelY = p1.VelY - ny * bounce }
                    ps[j] <- { p2 with VelX = p2.VelX + nx * bounce; VelY = p2.VelY + ny * bounce }
    Array.toList ps

// ─── Particle Update ───────────────────────────────────────────────────

let updateParticle (p: Particle) : Particle option =
    let x = p.X + p.VelX
    let y = p.Y + p.VelY
    let vy = p.VelY + 0.05
    let life = p.Life - 1
    if life <= 0 then None
    else Some { p with X = x; Y = y; VelY = vy; Life = life }

// ─── Main Game Tick ────────────────────────────────────────────────────

let gameTick (gs: GameState) : GameState =
    let gs = { gs with GameTick = gs.GameTick + 1 }
    let mutable terrainDirty = gs.TerrainDirty

    // Update all players — collect new entities and particles
    let mutable allNewEnts : Entity list = []
    let mutable allNewParts : Particle list = []
    let players =
        gs.Players |> List.mapi (fun i p ->
            if i < gs.NumPlayers then
                let (p, newEnts, newParts, tDirty) = updatePlayer gs i
                allNewEnts <- allNewEnts @ newEnts
                allNewParts <- allNewParts @ newParts
                if tDirty then terrainDirty <- true
                p
            else p)

    // Update all entities — collect spawned entities and particles
    let mutable spawnedEnts : Entity list = []
    let mutable spawnedParts : Particle list = []
    let entities =
        gs.Entities |> List.choose (fun ent ->
            let (result, newEnts, newParts, tDirty) = updateEntity gs ent
            spawnedEnts <- spawnedEnts @ newEnts
            spawnedParts <- spawnedParts @ newParts
            if tDirty then terrainDirty <- true
            result)

    // Merge new + spawned entities
    let entities = entities @ allNewEnts @ spawnedEnts

    // Update particles
    let particles =
        gs.Particles |> List.choose updateParticle

    // Add new particles
    let particles = particles @ allNewParts @ spawnedParts

    // Blackhole pull pass
    let entities, players = applyBlackholePull entities players gs.NumPlayers

    // Bullet-player collision
    let gs = { gs with Players = players; Entities = entities; Particles = particles; TerrainDirty = terrainDirty }
    let (players, entities, collisionParts, cDirty) = checkBulletPlayerCollision gs players entities
    let terrainDirty = if cDirty then true else terrainDirty

    // Player-player collision
    let players = checkPlayerCollision players gs.NumPlayers

    // Respawn dead players after 90 ticks (~2.5 seconds)
    let players =
        players |> List.mapi (fun i p ->
            if i < gs.NumPlayers && not p.Alive then
                let a = p.AnimAngle + 1.0
                if a > 90.0 then
                    spawnPlayer gs.Rng gs.Level p
                else
                    { p with AnimAngle = a }
            else p)

    // TerrainDirty is consumed by the renderer on the next frame;
    // it must NOT be cleared here — the renderer checks it once
    // and rebuilds the bitmap, then we clear it after rendering.
    { gs with
        Players = players
        Entities = entities
        Particles = particles @ collisionParts
        TerrainDirty = terrainDirty }

// ─── Init Round ────────────────────────────────────────────────────────

let initRound (gs: GameState) : GameState =
    let players =
        gs.Players |> List.mapi (fun i p ->
            if i < gs.NumPlayers then
                let p = spawnPlayer gs.Rng gs.Level p
                { p with WeaponType = 14; Ammo = 999; KillCount = 0; DeathCount = 0 }
            else p)
    { gs with
        Players = players
        Entities = []
        Particles = []
        RoundActive = true
        GameTick = 0 }
