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
    | WeaponType.Magnofilter ->
        // MAGNOFILTER — fire key toggles the field (same as DOWN)
        let flags =
            if p.Flags.HasFlag(PlayerFlags.Magno) then p.Flags &&& ~~~PlayerFlags.Magno
            else p.Flags ||| PlayerFlags.Magno
        { p with Flags = flags; ReloadTimer = 10 }, []

    | WeaponType.RearTurret ->  // REAR TURRET — fires behind
        p, [ makeProjectileAngled p ownerIdx p.WeaponType 180.0 ]

    | WeaponType.Multicannon ->  // MULTICANNON — 24 bullets in full 360° circle at 15° intervals
        let ents = [ for i in 0..23 ->
                        let angle = float i * 15.0
                        makeProjectileAngled p ownerIdx p.WeaponType angle ]
        p, ents

    | WeaponType.RubberBullets ->  // RUBBER BLTS — 3 bouncing bullets in a spread
        let ents = [ for offset in [ -8.0; 0.0; 8.0 ] ->
                        let e = makeProjectileAngled p ownerIdx p.WeaponType offset
                        { e with EType = EntityType.Ricochet; SubType = 0 } ]
        p, ents

    | WeaponType.Mine ->  // MINE — stationary at player position, arms after 25 ticks
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Mine; Owner = ownerIdx
                        Timer = -25; WeaponIdx = WeaponType.Mine }
        p, [ ent ]

    | WeaponType.Dirtclod ->  // DIRTCLOD — lobbed with gravity
        let rad = degToRad (p.Angle + 90.0)
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Exploding; Owner = ownerIdx; WeaponIdx = WeaponType.Dirtclod }
        p, [ ent ]

    | WeaponType.AtomWeapon -> // ATOM WEAPON — travels as heavy projectile, detonates on impact
        let rad = degToRad (p.Angle + 90.0)
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Heavy; Owner = ownerIdx; WeaponIdx = WeaponType.AtomWeapon }
        p, [ ent ]

    // | WeaponType.Troopers -> // TROOPERS — TODO: deploy ground units that shoot nearby opponents
    //     let ents = [ for off in [ 0.0; 90.0; 180.0; 270.0 ] -> makeProjectileAngled p ownerIdx p.WeaponType off ]
    //     p, ents

    | WeaponType.HellFire -> // HELL FIRE — flame with random spread
        let spread = float (rng.Next 7 - 3)
        let rad = degToRad (p.Angle + 90.0 + spread)
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Flame; Owner = ownerIdx; WeaponIdx = WeaponType.HellFire }
        p, [ ent ]

    | WeaponType.Sonicboom -> // SONICBOOM — expanding ring from player position
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Expanding; Owner = ownerIdx
                        Radius = 4.0; WeaponIdx = WeaponType.Sonicboom }
        p, [ ent ]

    | WeaponType.ToxicDump -> // TOXIC DUMP — persisting flame pool
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Flame; Owner = ownerIdx
                        Timer = -200; Radius = 8.0; WeaponIdx = WeaponType.ToxicDump }
        p, [ ent ]

    | WeaponType.Missile -> // MISSILE — homing
        let rad = degToRad (p.Angle + 90.0)
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Heavy; Owner = ownerIdx; WeaponIdx = WeaponType.Missile }
        p, [ ent ]

    | WeaponType.Blackhole -> // BLACKHOLE — gravity well
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Blackhole; Owner = ownerIdx; WeaponIdx = WeaponType.Blackhole }
        p, [ ent ]

    | _ ->  // Standard forward fire
        p, [ makeProjectile p ownerIdx p.WeaponType ]

// ─── Special Weapon Firing (DOWN key) ──────────────────────────────────
// Uses p.SpecialWeapon. Every case applies a small backwards recoil kick.

let fireSpecial (rng: Random) (level: LevelData option) (p: Player) (ownerIdx: int) : Player * Entity list =
    let sw = p.SpecialWeapon
    let w = getWeapon sw

    // Recoil: push ship backwards (opposite to facing direction)
    let rad = degToRad (p.Angle + 90.0)
    let recoil = 0.3
    let p = { p with
                VelX = p.VelX - cos rad * recoil
                VelY = p.VelY + sin rad * recoil
                SpecialReloadTimer = w.ReloadTicks }

    match sw with
    | WeaponType.Magnofilter ->
        // MAGNOFILTER — toggle flag (no projectile, still gets recoil)
        let flags =
            if p.Flags.HasFlag(PlayerFlags.Magno) then p.Flags &&& ~~~PlayerFlags.Magno
            else p.Flags ||| PlayerFlags.Magno
        { p with Flags = flags; KeyDown = false }, []

    | WeaponType.RearTurret ->
        // REAR TURRET: fire behind
        p, [ makeProjectileAngled p ownerIdx sw 180.0 ]

    | WeaponType.Multicannon ->
        // MULTICANNON: tight forward burst of 5
        let ents = [ for offset in [ -4.0; -2.0; 0.0; 2.0; 4.0 ] ->
                        makeProjectileAngled p ownerIdx sw offset ]
        p, ents

    | WeaponType.Nucleus ->
        // NUCLEUS: shield orbiter at player pos
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Shield; Owner = ownerIdx; WeaponIdx = WeaponType.Nucleus }
        p, [ ent ]

    | WeaponType.HellFire ->
        // HELL FIRE: wider spread burst
        let ents = [ for _ in 1..3 ->
                        let spread = float (rng.Next 21 - 10)
                        makeProjectileAngled p ownerIdx sw spread ]
        p, ents

    | WeaponType.Machinegun ->
        // MACHINEGUN: random-offset shot
        let randOffset = float (rng.Next 21 - 10)
        p, [ makeProjectileAngled p ownerIdx sw randOffset ]

    | WeaponType.Fan ->
        // FAN: wide arc
        let ents = [ for i in -4..4 -> makeProjectileAngled p ownerIdx sw (float i * 8.0) ]
        p, ents

    | WeaponType.Missile ->
        // MISSILE: homing shot
        let ent = makeProjectile p ownerIdx sw
        p, [ ent ]

    | WeaponType.Blackhole ->
        // BLACKHOLE: stationary gravity well
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Blackhole; Owner = ownerIdx; WeaponIdx = WeaponType.Blackhole }
        { p with BlackholeCounter = min 5 (p.BlackholeCounter + 1) }, [ ent ]

    | WeaponType.AtomWeapon ->
        // ATOM WEAPON: heavy projectile, detonates on impact
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Heavy; Owner = ownerIdx; WeaponIdx = WeaponType.AtomWeapon }
        p, [ ent ]

    | WeaponType.RubberBullets ->
        // RUBBER BLTS: single bouncing bullet forward
        let e = makeProjectile p ownerIdx sw
        p, [ { e with EType = EntityType.Ricochet; SubType = 0 } ]

    | WeaponType.Mine ->
        // MINE: stationary, arms after 25 ticks
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Mine; Owner = ownerIdx; Timer = -25; WeaponIdx = WeaponType.Mine }
        p, [ ent ]

    | WeaponType.Dirtclod ->
        // DIRTCLOD: lobbed with gravity
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Exploding; Owner = ownerIdx; WeaponIdx = WeaponType.Dirtclod }
        p, [ ent ]

    | WeaponType.Headspinner ->
        // HEADSPINNER: EMP/stun shot
        p, [ makeProjectile p ownerIdx sw ]

    | WeaponType.Freezer ->
        // FREEZER: shield entity on target
        p, [ makeProjectile p ownerIdx sw ]

    | WeaponType.Sonicboom ->
        // SONICBOOM: expanding ring
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Expanding; Owner = ownerIdx
                        Radius = 4.0; WeaponIdx = WeaponType.Sonicboom }
        p, [ ent ]

    | WeaponType.ToxicDump ->
        // TOXIC DUMP: persisting flame pool
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Flame; Owner = ownerIdx
                        Timer = -200; Radius = 8.0; WeaponIdx = WeaponType.ToxicDump }
        p, [ ent ]

    | WeaponType.Dumbfire ->
        // DUMBFIRE: fast unguided rocket
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        VelX = cos rad * w.ProjectileSpeed; VelY = -(sin rad) * w.ProjectileSpeed
                        EType = EntityType.Heavy; Owner = ownerIdx; WeaponIdx = WeaponType.Dumbfire }
        p, [ ent ]

    | _ ->
        // Default: standard forward shot
        p, [ makeProjectile p ownerIdx sw ]

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
            | None -> hitsWall gs.Level pixX pixY 3.0
        let hitsBoundary = px < 0.0 || px > maxX || py < 0.0 || py > maxY
        if hitsTerrain || hitsBoundary then
            // Terrain contact: clear shield, doubled collision damage (4.8x)
            let speed = abs p.VelX + abs velY
            let speedDamage = int (speed * ShieldKnockbackScale)
            let flags = p.Flags &&& ~~~PlayerFlags.Shield
            let h = if speedDamage > 0 && p.InvTimer = 0 then p.Health - speedDamage else p.Health
            let inv = if speedDamage > 0 && p.InvTimer = 0 then SpawnInvincibilityTicks else p.InvTimer
            ({ p with PosX = p.PosX; PosY = p.PosY; VelX = 0.0; VelY = 0.0
                      Flags = flags; Health = h; InvTimer = inv
                      WallHitCount = p.WallHitCount + 1; OnBase = false }, [], [], false)
        else
            let px = clampF 0.0 maxX px
            let py = clampF 0.0 maxY py
            ({ p with PosX = px; PosY = py; VelX = p.VelX; VelY = velY; OnBase = false }, [], [], false)
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

    // Base/landing pad detection. A ship rests with its centre in the void just
    // above the pad bar, so scan EVERY pixel from the centre down to BaseLandReach
    // for the topmost pad row (or -1). Scanning the whole range — not just the two
    // endpoints — matters: pad bars are beveled, so an endpoint-only check can miss
    // the pad at some columns and make a parked ship sink and snap repeatedly.
    // "Parked" (slow) ships heal and may switch weapons.
    let speed = abs velX + abs velY
    let padTopRow =
        match gs.Level with
        | Some level ->
            let ipx = int (round px)
            let irow = int (round py)
            let reach = int (ceil BaseLandReach)
            let mutable found = -1
            let mutable d = 0
            while found < 0 && d <= reach do
                if Terrain.isBase (Terrain.getPixel level.Pixels ipx (irow + d)) then found <- irow + d
                d <- d + 1
            found
        | None -> -1
    let baseNear = padTopRow >= 0
    let onBase = baseNear && speed < BaseLandSpeed

    let posX, posY, velX, velY, angle, health, wallHitCount, wallDmgCooldown, terrainModified =
        match gs.Level with
        | Some level ->
            let pixel = Terrain.terrainAt level.Pixels px py
            if baseNear then
                // Base/landing pad: a solid surface that deals NO collision damage.
                // Resting on it stops the ship and recharges energy; pressing thrust
                // lifts off again.
                let healed = if onBase then min FullHealth (p.Health + BaseHealRate) else p.Health
                if p.KeyUp then
                    // Lifting off: let thrust carry the ship freely.
                    (posX, posY, velX, velY, angle, healed, p.WallHitCount, wallDmgCooldown, false)
                else
                    // Resting: come to a full stop (the pad is not slippery) and seat
                    // the ship one pixel above the pad's top row so it doesn't hover.
                    let restY = if padTopRow > 0 then float (padTopRow - 1) * PositionScale else p.PosY
                    (p.PosX, restY, 0.0, 0.0, angle, healed, p.WallHitCount, wallDmgCooldown, false)
            elif Terrain.isWall pixel then
                let speed = abs velX + abs velY
                let speedDamage = int (speed * NormalKnockbackScale)
                let h, whc, wdc =
                    if speedDamage > 0 && wallDmgCooldown = 0 then
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
            if hitsWall gs.Level px py 3.0 then
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
            let speedDamage = int (boundarySpeed * NormalKnockbackScale)
            let h, whc, wdc =
                if speedDamage > 0 && wallDmgCooldown = 0 then
                    (health - speedDamage, wallHitCount + 1, SpawnInvincibilityTicks)
                else (health, wallHitCount, wallDmgCooldown)
            // Keep facing direction on boundary hit
            (bpx, bpy, 0.0, 0.0, angle, h, whc, wdc)
        else
            (posX, posY, velX, velY, angle, health, wallHitCount, wallDmgCooldown)

    // Reload timers
    let reloadTimer = if p.ReloadTimer > 0 then p.ReloadTimer - 1 else 0
    let specialReloadTimer = if p.SpecialReloadTimer > 0 then p.SpecialReloadTimer - 1 else 0

    // Build partially-updated player
    let p = { p with
                PosX = posX; PosY = posY; Angle = angle
                VelX = velX; VelY = velY; Flags = flags
                Health = health; WallHitCount = wallHitCount
                InvTimer = invTimer; WallDmgCooldown = wallDmgCooldown
                StunTimer = stunTimer
                AnimAngle = animAngle; ReloadTimer = reloadTimer
                SpecialReloadTimer = specialReloadTimer
                OnBase = onBase }

    // Cannon firing (main fire key) — always single-shot Cannon
    let p, newEnts1 =
        if p.KeyFire && p.ReloadTimer = 0 && p.Ammo > 0 && canControl then
            fireWeapon gs.Rng gs.Level p idx
        else p, []

    // Special weapon firing (DOWN key) — uses SpecialWeapon + SpecialReloadTimer
    let p, newEnts2 =
        if p.KeyDown && p.SpecialReloadTimer = 0 && canControl then
            match p.SpecialWeapon with
            | WeaponType.Troopers -> p, []  // TROOPERS — not implemented
            | _ -> fireSpecial gs.Rng gs.Level p idx
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
        // Rubber bullet: travels in straight line, bounces off walls/terrain
        let vy = ent.VelY + 0.04  // Light gravity
        let x = ent.X + ent.VelX
        let y = ent.Y + vy
        if outOfBounds 10.0 x y || ent.Timer > 200 then
            (None, [], [], false)
        elif hitsWall gs.Level x y 1.0 then
            // Bounce: try to reflect off wall
            let bounceCount = ent.SubType + 1
            if bounceCount > ricochetMaxBounces then
                (None, [], [], false)
            else
                let bx, by, bvx, bvy, bounced = bounceOffWalls gs.Level ent.X ent.Y ent.VelX vy 1.0
                if bounced then
                    let bvx = bvx * 0.8  // Lose some speed on bounce
                    let bvy = bvy * 0.8
                    (Some { ent with X = bx; Y = by; VelX = bvx; VelY = bvy; SubType = bounceCount }, [], [], false)
                else
                    // Fallback: simple velocity reversal
                    let hitHoriz = hitsWall gs.Level (ent.X + ent.VelX) ent.Y 1.0
                    let hitVert = hitsWall gs.Level ent.X (ent.Y + vy) 1.0
                    let nvx = if hitHoriz then -ent.VelX * 0.8 else ent.VelX
                    let nvy = if hitVert then -vy * 0.8 else vy
                    (Some { ent with X = ent.X + nvx; Y = ent.Y + nvy; VelX = nvx; VelY = nvy; SubType = bounceCount }, [], [], false)
        else
            (Some { ent with X = x; Y = y; VelY = vy }, [], [], false)

    | EntityType.Flame ->
        let x = ent.X + ent.VelX
        let y = ent.Y + ent.VelY
        // Toxic dump (stationary) has no gravity; fired flames have light gravity
        let isStationary = ent.WeaponIdx = WeaponType.ToxicDump
        let vy = if isStationary then ent.VelY else min (ent.VelY + 0.02) 2.0
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
        let vy = ent.VelY + 0.02  // Very light gravity — nuke is expanding, shouldn't fall fast
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
        let mutable vx = ent.VelX
        let mutable vy = ent.VelY

        // Non-missile Heavy projectiles get light gravity (e.g., Atom Weapon, Dumbfire)
        if ent.WeaponIdx <> WeaponType.Missile then
            vy <- vy + 0.06

        let mutable x = ent.X + vx
        let mutable y = ent.Y + vy

        // Missile homing (weapon 19)
        let particles =
            if ent.WeaponIdx = WeaponType.Missile && ent.Timer > 5 then
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
            elif ent.WeaponIdx = WeaponType.AtomWeapon && ent.Timer % 3 = 0 then
                // Atom weapon: faint trail while in flight
                [ { defaultParticle with
                      X = x; Y = y
                      VelX = float (gs.Rng.Next 3 - 1) * 0.15
                      VelY = float (gs.Rng.Next 3 - 1) * 0.15
                      Life = 6; Color = ent.Owner % 4 } ]
            else []

        // Wall collision — explode (or detonate as nuke for AtomWeapon)
        if hitsWall gs.Level x y 3.0 then
            if ent.WeaponIdx = WeaponType.AtomWeapon then
                // Atom weapon: spawn a Nuke entity at impact point
                let nuke = { defaultEntity with
                                X = x; Y = y; VelX = ent.VelX * 0.3; VelY = ent.VelY * 0.3
                                EType = EntityType.Nuke; Owner = ent.Owner
                                WeaponIdx = WeaponType.AtomWeapon; Radius = 0.0 }
                let parts = spawnExplosion gs.Rng x y 4 1.0 8 (ent.Owner % 4)
                (None, [ nuke ], parts @ particles, dirty)
            else
                eraseTerrain gs.Level x y 6.0
                let parts = spawnExplosion gs.Rng x y 6 1.5 12 (ent.Owner % 4)
                (None, [], parts @ particles, dirty)
        elif outOfBounds 20.0 x y || ent.Timer > 300 then
            if ent.WeaponIdx = WeaponType.AtomWeapon && ent.Timer > 300 then
                // Atom weapon timeout: detonate wherever it is
                let nuke = { defaultEntity with
                                X = ent.X; Y = ent.Y; VelX = 0.0; VelY = 0.0
                                EType = EntityType.Nuke; Owner = ent.Owner
                                WeaponIdx = WeaponType.AtomWeapon; Radius = 0.0 }
                (None, [ nuke ], particles, dirty)
            else
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
        let vy = ent.VelY + 0.04  // Light gravity
        let x = ent.X + ent.VelX
        let y = ent.Y + vy
        let radius = 3.0 + 2.0 * sin (float ent.Timer * 0.3)
        if ent.Timer > 120 || outOfBounds 10.0 x y then
            (None, [], [], false)
        else
            (Some { ent with X = x; Y = y; VelY = vy; Radius = radius }, [], [], false)

    | _ ->
        // Default: linear motion (Bullet, BulletAlt, PassThrough, Shield, etc.)
        // Standard bullets get normal gravity; alt bullets get lighter gravity
        let gravity =
            match ent.EType with
            | EntityType.Bullet -> 0.125
            | EntityType.BulletAlt | EntityType.Shield -> 0.04
            | _ -> 0.0
        let vy = ent.VelY + gravity
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

// ─── Magnofilter Pull Pass ─────────────────────────────────────────────
// Players with Magno flag attract enemy projectiles toward themselves.

let magnofilterPullRadius = 60.0
let magnofilterStrength = 0.25

let applyMagnoPull (entities: Entity list) (players: Player list) (numPlayers: int) : Entity list =
    let magnoPlayers =
        players |> List.indexed |> List.filter (fun (i, p) ->
            i < numPlayers && p.Alive && p.Flags.HasFlag(PlayerFlags.Magno))
    if magnoPlayers.IsEmpty then entities else

    entities |> List.map (fun ent ->
        // Only pull movable projectiles (not mines, blackholes, expanding, etc.)
        match ent.EType with
        | EntityType.Bullet | EntityType.BulletAlt | EntityType.Heavy
        | EntityType.Flame | EntityType.EMP | EntityType.Exploding
        | EntityType.Shrapnel | EntityType.Ricochet | EntityType.Laser ->
            let mutable vx = ent.VelX
            let mutable vy = ent.VelY
            for (pi, mp) in magnoPlayers do
                if pi <> ent.Owner then  // Don't attract own bullets
                    let px = mp.PosX / PositionScale
                    let py = mp.PosY / PositionScale
                    let dx = px - ent.X
                    let dy = py - ent.Y
                    let dist = sqrt (dx*dx + dy*dy) + 0.1
                    if dist < magnofilterPullRadius then
                        let force = magnofilterStrength * (1.0 - dist / magnofilterPullRadius)
                        vx <- vx + dx / dist * force
                        vy <- vy + dy / dist * force
            if vx <> ent.VelX || vy <> ent.VelY then
                { ent with VelX = vx; VelY = vy }
            else ent
        | _ -> ent)

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
            let mutable atomDetonate = false
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
                    let playerRadius = 3.0
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
                                // Consume the projectile so it cannot hit further players
                                // this tick. An atom round detonates into a Nuke *after* the
                                // player loop (below) rather than mid-loop, so overlapping
                                // players aren't struck as both a Heavy round and a Nuke.
                                if ent.WeaponIdx = WeaponType.AtomWeapon then atomDetonate <- true
                                ent <- { ent with EType = EntityType.None }  // Mark for removal

                            // Credit kill — only when THIS damaging hit is what crossed the
                            // death threshold. Prevents double-crediting when several
                            // projectiles strike a player on the tick it dies, and avoids
                            // crediting zero-damage EMP/Shield hits.
                            if damage > 0 && p.Health > DeathThreshold && np.Health <= DeathThreshold then
                                if ent.Owner >= 0 && ent.Owner < gs.NumPlayers then
                                    let killer = ps[ent.Owner]
                                    ps[ent.Owner] <- { killer with KillCount = killer.KillCount + 1 }

                            ps[i] <- np
            // Atom round detonates once, after every player was tested against the
            // original Heavy round. The resulting Nuke expands over the next ticks.
            if atomDetonate then
                ent <- { ent with EType = EntityType.Nuke; Radius = 0.0
                                  VelX = ent.VelX * 0.2; VelY = ent.VelY * 0.2 }
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
                let collisionDist = 6.0
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

// ─── CPU AI ────────────────────────────────────────────────────────────
// Full AI: lead-shot aiming, projectile dodging, weapon-aware tactics,
// gravity-compensated movement, terrain/boundary avoidance, per-CPU personality.

// AI personality traits derived from player index — gives each CPU a different style
type AiPersonality =
    { AimTolerance: float       // degrees — how well-aimed before firing
      FireRange: float          // pixels — max engagement distance
      Aggression: float         // 0..1 — preference for closing distance
      DodgeRadius: float        // pixels — threat detection range
      SpecialUseChance: float } // 0..1 — how often to use DOWN special

let aiPersonalities = [|
    { AimTolerance = 12.0; FireRange = 160.0; Aggression = 0.7; DodgeRadius = 50.0; SpecialUseChance = 0.3 }  // Aggressive
    { AimTolerance = 8.0;  FireRange = 200.0; Aggression = 0.4; DodgeRadius = 60.0; SpecialUseChance = 0.5 }  // Sniper
    { AimTolerance = 18.0; FireRange = 120.0; Aggression = 0.9; DodgeRadius = 40.0; SpecialUseChance = 0.4 }  // Brawler
    { AimTolerance = 10.0; FireRange = 180.0; Aggression = 0.6; DodgeRadius = 55.0; SpecialUseChance = 0.6 }  // Balanced
|]

let cpuAI (gs: GameState) : GameState =
    let players =
        gs.Players |> List.mapi (fun i p ->
            if i >= gs.NumPlayers || not p.IsCpu || not p.Alive then p
            else
                let personality = aiPersonalities[i % aiPersonalities.Length]
                let px = p.PosX / PositionScale
                let py = p.PosY / PositionScale
                let tick = gs.GameTick
                let phase = (tick + i * 37) % 13  // per-player phase for jitter

                // ── Find nearest alive enemy ──
                let mutable bestDist = Double.MaxValue
                let mutable bestIdx = -1
                for j in 0..gs.NumPlayers-1 do
                    if j <> i && gs.Players[j].Alive then
                        let ex = gs.Players[j].PosX / PositionScale
                        let ey = gs.Players[j].PosY / PositionScale
                        let dx = ex - px
                        let dy = ey - py
                        let d = dx*dx + dy*dy
                        if d < bestDist then bestDist <- d; bestIdx <- j

                if bestIdx < 0 then
                    // No enemies alive — drift
                    { p with KeyUp = false; KeyLeft = false; KeyRight = false; KeyFire = false; KeyDown = false }
                else
                    let enemy = gs.Players[bestIdx]
                    let ex = enemy.PosX / PositionScale
                    let ey = enemy.PosY / PositionScale
                    let evx = enemy.VelX  // enemy velocity (internal units, = px/tick)
                    let evy = enemy.VelY
                    let dx = ex - px
                    let dy = ey - py
                    let dist = sqrt (dx*dx + dy*dy) + 0.1

                    // ── Lead-shot prediction ──
                    // Estimate bullet travel time, then predict where enemy will be
                    let w = getWeapon p.WeaponType
                    let bulletSpeed = max 1.0 w.ProjectileSpeed
                    let travelTicks = dist / bulletSpeed
                    // Predict enemy position accounting for gravity on enemy
                    let predX = ex + evx * travelTicks
                    let predY = ey + evy * travelTicks + 0.5 * GravityAccel * travelTicks * travelTicks
                    // Clamp prediction to arena
                    let predX = clampF 5.0 (ArenaWidth - 5.0) predX
                    let predY = clampF 5.0 (ArenaHeight - 5.0) predY

                    // ── Target angle toward predicted position ──
                    let tdx = predX - px
                    let tdy = predY - py
                    let targetAngle =
                        let a = atan2 tdx (-tdy) * 180.0 / Math.PI
                        if a < 0.0 then a + 360.0 else a
                    let mutable diff = targetAngle - p.Angle
                    if diff > 180.0 then diff <- diff - 360.0
                    if diff < -180.0 then diff <- diff + 360.0

                    let turnLeft = diff > 3.0
                    let turnRight = diff < -3.0

                    // ── Cannon aim tolerance (main fire key — always Cannon) ──
                    let aimTol = personality.AimTolerance
                    let fireRange = personality.FireRange

                    let aimed = abs diff < aimTol
                    let inRange = dist < fireRange
                    let shouldFire =
                        aimed && inRange && phase <> 0  // skip 1/13 ticks

                    // ── Dodge incoming projectiles ──
                    let mutable threatDX = 0.0
                    let mutable threatDY = 0.0
                    let mutable threatCount = 0
                    for ent in gs.Entities do
                        if ent.Owner <> i && ent.EType <> EntityType.None then
                            let edx = ent.X - px
                            let edy = ent.Y - py
                            let eDist = sqrt (edx*edx + edy*edy)
                            if eDist < personality.DodgeRadius then
                                // Check if projectile is heading toward us
                                let dot = edx * ent.VelX + edy * ent.VelY
                                if dot < 0.0 then  // moving toward us
                                    // Accumulate threat direction (away from projectile)
                                    let norm = eDist + 0.1
                                    threatDX <- threatDX - edx / norm
                                    threatDY <- threatDY - edy / norm
                                    threatCount <- threatCount + 1

                    let dodging = threatCount > 0
                    let dodgeAngle =
                        if dodging then
                            // Perpendicular to threat — flee sideways
                            let a = atan2 threatDX (-threatDY) * 180.0 / Math.PI
                            if a < 0.0 then a + 360.0 else a
                        else 0.0

                    // When dodging, override turn direction toward escape angle
                    let turnLeft, turnRight =
                        if dodging && threatCount >= 2 then
                            let mutable dodgeDiff = dodgeAngle - p.Angle
                            if dodgeDiff > 180.0 then dodgeDiff <- dodgeDiff - 360.0
                            if dodgeDiff < -180.0 then dodgeDiff <- dodgeDiff + 360.0
                            (dodgeDiff > 3.0, dodgeDiff < -3.0)
                        else (turnLeft, turnRight)

                    // ── Movement: gravity compensation + terrain avoidance ──
                    let rad = degToRad (p.Angle + 90.0)
                    let cosR = cos rad
                    let sinR = sin rad

                    // Multi-distance terrain probing (10px, 20px, 35px ahead)
                    let blocked10 =
                        let px2 = px + cosR * 10.0
                        let py2 = py - sinR * 10.0
                        match gs.Level with
                        | Some level -> Terrain.isWall (Terrain.terrainAt level.Pixels px2 py2)
                        | None -> hitsWall gs.Level px2 py2 3.0
                    let blocked20 =
                        let px2 = px + cosR * 20.0
                        let py2 = py - sinR * 20.0
                        match gs.Level with
                        | Some level -> Terrain.isWall (Terrain.terrainAt level.Pixels px2 py2)
                        | None -> hitsWall gs.Level px2 py2 3.0
                    let blocked35 =
                        let px2 = px + cosR * 35.0
                        let py2 = py - sinR * 35.0
                        match gs.Level with
                        | Some level -> Terrain.isWall (Terrain.terrainAt level.Pixels px2 py2)
                        | None -> hitsWall gs.Level px2 py2 3.0

                    // Boundary avoidance
                    let probeX = px + cosR * 15.0
                    let probeY = py - sinR * 15.0
                    let boundaryBlocked =
                        probeX < 8.0 || probeX > ArenaWidth - 8.0 ||
                        probeY < 8.0 || probeY > ArenaHeight - 8.0

                    // Water detection (avoid flying into water)
                    let inWater =
                        match gs.Level with
                        | Some level -> Terrain.isOnWater level.Pixels px py
                        | None -> false
                    let waterAhead =
                        match gs.Level with
                        | Some level ->
                            let wx = px + cosR * 15.0
                            let wy = py - sinR * 15.0
                            Terrain.isOnWater level.Pixels wx wy
                        | None -> false

                    // Falling fast — should thrust to brake regardless
                    let fallingFast = p.VelY > 1.0
                    // Moving fast in general
                    let speed = sqrt (p.VelX * p.VelX + p.VelY * p.VelY)

                    let shouldThrust =
                        if blocked10 || boundaryBlocked then false
                        elif fallingFast then true  // always brake a fall
                        elif inWater then true  // escape water
                        elif blocked20 || blocked35 then
                            // Slow down — only thrust if speed is low
                            speed < 0.8
                        elif waterAhead then false  // don't fly into water
                        elif dodging then true  // thrust to dodge
                        else
                            // Normal pursuit: thrust toward enemy, with personality aggression
                            let wantClose = dist > 50.0 * (2.0 - personality.Aggression)
                            let tooClose = dist < 25.0
                            (wantClose || phase < 8) && not tooClose

                    // ── Special weapon (DOWN key) ──
                    let useSpecial =
                        match p.WeaponType with
                        | WeaponType.Magnofilter ->
                            // Toggle magno when enemy projectiles are nearby
                            threatCount > 0 && not (p.Flags.HasFlag(PlayerFlags.Magno))
                        | WeaponType.Multicannon ->
                            // Use 360° burst when enemy is close (regular fire)
                            // Use tight burst (special) when aimed at range
                            aimed && dist > 60.0 && dist < 150.0 && phase % 5 = 0
                        | WeaponType.Sonicboom ->
                            // Use expanding ring when enemy very close
                            dist < 50.0 && phase % 4 = 0
                        | WeaponType.Blackhole ->
                            // Drop blackhole when enemy is close
                            dist < 70.0 && phase % 6 = 0
                        | WeaponType.HellFire ->
                            // Wide flame spread when close
                            dist < 60.0 && aimed && phase % 3 = 0
                        | WeaponType.Machinegun ->
                            // Random offset shot for suppression
                            aimed && inRange && phase % 4 = 0
                        | WeaponType.Fan ->
                            // Fan arc when somewhat aimed
                            abs diff < 40.0 && dist < 120.0 && phase % 5 = 0
                        | WeaponType.Missile ->
                            // Missile recoil shot — use when aimed
                            aimed && dist > 80.0 && phase % 7 = 0
                        | WeaponType.Mine ->
                            // Drop mine when falling or enemy is very close behind
                            (abs diff > 120.0 && dist < 50.0) || (p.VelY > 0.5 && phase % 5 = 0)
                        | WeaponType.ToxicDump ->
                            // Drop toxic when standing still or enemy close
                            dist < 40.0 && phase % 4 = 0
                        | WeaponType.RubberBullets ->
                            // Single bouncing shot when aimed
                            aimed && inRange && phase % 5 = 0
                        | _ -> false

                    { p with
                        KeyUp = shouldThrust
                        KeyLeft = turnLeft
                        KeyRight = turnRight
                        KeyFire = shouldFire
                        KeyDown = useSpecial })
    { gs with Players = players }

// ─── Main Game Tick ────────────────────────────────────────────────────

let gameTick (gs: GameState) : GameState =
    let gs = { gs with GameTick = gs.GameTick + 1 }

    // CPU AI: set key states for computer-controlled players
    let gs = if gs.CpuCount > 0 then cpuAI gs else gs

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

    // Magnofilter pull pass
    let entities = applyMagnoPull entities players gs.NumPlayers

    // Bullet-player collision
    let gs = { gs with Players = players; Entities = entities; Particles = particles; TerrainDirty = terrainDirty }
    let (players, entities, collisionParts, cDirty) = checkBulletPlayerCollision gs players entities
    let terrainDirty = if cDirty then true else terrainDirty

    // Player-player collision
    let players = checkPlayerCollision players gs.NumPlayers

    // Respawn dead players after 90 ticks (~2.5 seconds)
    let players =
        // Bases occupied by live players — a respawning ship must avoid all of them
        let occupied = players |> List.choose (fun p -> if p.Alive && p.SpawnIndex >= 0 then Some p.SpawnIndex else None)
        players |> List.mapi (fun i p ->
            if i < gs.NumPlayers && not p.Alive then
                let a = p.AnimAngle + 1.0
                if a > 90.0 then
                    let spawned, _ = spawnPlayerExcluding gs.Rng gs.Level occupied p
                    spawned
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
    // Reload level terrain from disk to reset any ammo damage
    let gs =
        if gs.LevelFilePath <> "" && System.IO.File.Exists gs.LevelFilePath then
            let freshLevel = Terrain.loadLevel gs.LevelFilePath
            { gs with Level = Some freshLevel; TerrainDirty = true }
        else gs

    let cpuStart = gs.NumPlayers - gs.CpuCount  // First CPU player index
    // Thread the set of occupied base indices through the players so each one spawns
    // onto a base not already taken by an earlier player this round.
    let players, _ =
        gs.Players
        |> List.indexed
        |> List.mapFold (fun occupied (i, p) ->
            if i < gs.NumPlayers then
                let spawned, idx = spawnPlayerExcluding gs.Rng gs.Level occupied p
                let spawned =
                    { spawned with
                        WeaponType = WeaponType.Cannon; Ammo = 999; KillCount = 0; DeathCount = 0
                        ReloadTimer = 0; SpecialReloadTimer = 0; IsCpu = i >= cpuStart }
                spawned, (if idx >= 0 then idx :: occupied else occupied)
            else
                p, occupied
        ) []
    { gs with
        Players = players
        Entities = []
        Particles = []
        RoundActive = true
        GameTick = 0 }
