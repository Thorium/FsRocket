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
    spawnExplosionParticles rng x y count speed life (ownerIdx % 4)

/// Pool of special weapons a CPU player may draw from: everything cyclable
/// (Enabled, mirroring the human weapon-switch keys) except the plain Cannon,
/// which is the always-available main fire.
/// Every special weapon a ship can carry: the enabled weapons except the
/// Cannon (always the main gun).
let specialWeaponPool =
    [| for i in 0 .. weapons.Length - 1 do
         let wt = enum<WeaponType> i
         if weapons[i].Enabled && wt <> WeaponType.Cannon then yield wt |]

/// Random special weapon, always different from the current one.
let randomSpecialWeapon (rng: Random) (current: WeaponType) : WeaponType =
    let pool = specialWeaponPool |> Array.filter (fun wt -> wt <> current)
    if pool.Length = 0 then current
    else pool[rng.Next pool.Length]

// ─── Weapon Firing (returns updated Player * new Entity list) ──────────

/// Weapons firing cannon-type rounds, which carry the firing ship's motion
/// on top of their muzzle speed (BulletInheritVelocity)
let private inheritsShipMotion (weapon: WeaponType) =
    match weapon with
    | WeaponType.Cannon
    | WeaponType.Multicannon
    | WeaponType.Machinegun
    | WeaponType.RubberBullets
    | WeaponType.RearTurret -> true
    | _ -> false

/// Add the ship's velocity (vx, vy: before the recoil kick) to the rounds of
/// a weapon that inheritsShipMotion
let private withShipMotion (weapon: WeaponType) (vx: float) (vy: float) (ents: Entity list) =
    if inheritsShipMotion weapon then
        ents
        |> List.map (fun e ->
            { e with
                VelX = e.VelX + vx * BulletInheritVelocity
                VelY = e.VelY + vy * BulletInheritVelocity })
    else ents

let private fireWeaponRaw (rng: Random) (level: LevelData option) (p: Player) (ownerIdx: int) : Player * Entity list =
    let w = getWeapon p.WeaponType
    let p = { p with ReloadTimer = w.ReloadTicks; Ammo = p.Ammo - 1; ShotCount = p.ShotCount + 1 }

    // Recoil: main-fire shots give the ship a light backwards kick — 30% of
    // the special-weapon recoil in fireSpecial (CPU players fire through this
    // same path, so they recoil too).
    let p =
        let rad = degToRad (p.Angle + 90.0)
        let recoil = SpecialFireRecoil * CannonRecoilFraction
        { p with
            VelX = p.VelX - cos rad * recoil
            VelY = p.VelY + sin rad * recoil }

    match p.WeaponType with
    | WeaponType.Magnofilter ->
        // MAGNOFILTER — fire key toggles the field (same as DOWN)
        let flags =
            if p.Flags.HasFlag PlayerFlags.Magno then p.Flags &&& ~~~PlayerFlags.Magno
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

    | WeaponType.Dirtclod
    // DIRTCLOD — lobbed with gravity (Exploding is Dirtclod's own EntityType)
    | WeaponType.AtomWeapon ->
        // ATOM WEAPON — travels as heavy projectile, detonates on impact
        p, [ makeProjectile p ownerIdx p.WeaponType ]

    // | WeaponType.Troopers -> // TROOPERS — TODO: deploy ground units that shoot nearby opponents
    //     let ents = [ for off in [ 0.0; 90.0; 180.0; 270.0 ] -> makeProjectileAngled p ownerIdx p.WeaponType off ]
    //     p, ents

    | WeaponType.HellFire -> // HELL FIRE — flame with random spread
        let spread = float (rng.Next 7 - 3)
        p, [ makeProjectileAngled p ownerIdx p.WeaponType spread ]

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

    | WeaponType.Missile -> // MISSILE — homing (Heavy projectile; homing handled in updateEntity)
        p, [ makeProjectile p ownerIdx p.WeaponType ]

    | WeaponType.Blackhole -> // BLACKHOLE — gravity well
        let ent = { defaultEntity with
                        X = p.PosX / PositionScale; Y = p.PosY / PositionScale
                        EType = EntityType.Blackhole; Owner = ownerIdx; WeaponIdx = WeaponType.Blackhole }
        p, [ ent ]

    | _ ->  // Standard forward fire
        p, [ makeProjectile p ownerIdx p.WeaponType ]

// ─── Special Weapon Firing (DOWN key) ──────────────────────────────────
// Uses p.SpecialWeapon. Every case applies a small backwards recoil kick.

let private fireSpecialRaw (rng: Random) (level: LevelData option) (p: Player) (ownerIdx: int) : Player * Entity list =
    let sw = p.SpecialWeapon
    let w = getWeapon sw

    // Recoil: push ship backwards (opposite to facing direction). The REAR
    // TURRET fires backwards, so its recoil nudges the ship a bit FORWARD
    // instead — rapid rear fire doubles as a light afterburner.
    let rad = degToRad (p.Angle + 90.0)
    let recoil =
        if sw = WeaponType.RearTurret then -SpecialFireRecoil * 0.5
        else SpecialFireRecoil
    let p = { p with
                VelX = p.VelX - cos rad * recoil
                VelY = p.VelY + sin rad * recoil
                SpecialReloadTimer = w.ReloadTicks }

    match sw with
    | WeaponType.Magnofilter ->
        // MAGNOFILTER — toggle flag (no projectile, still gets recoil)
        let flags =
            if p.Flags.HasFlag PlayerFlags.Magno then p.Flags &&& ~~~PlayerFlags.Magno
            else p.Flags ||| PlayerFlags.Magno
        { p with Flags = flags; KeyDown = false; SpecialReloadTimer = 10 }, []

    | WeaponType.RearTurret ->
        // REAR TURRET: fire behind
        p, [ makeProjectileAngled p ownerIdx sw 180.0 ]

    | WeaponType.Multicannon ->
        // MULTICANNON: tight forward burst of 5
        let ents = [ for offset in [ -4.0; -2.0; 0.0; 2.0; 4.0 ] ->
                        makeProjectileAngled p ownerIdx sw offset ]
        p, ents

    | WeaponType.Nucleus ->
        // NUCLEUS: slow drifting freeze orb — floats ahead as area denial
        p, [ makeProjectile p ownerIdx sw ]

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
        p, [ makeProjectile p ownerIdx sw ]

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

    | WeaponType.Dirtclod
    // DIRTCLOD: lobbed with gravity
    | WeaponType.Headspinner
    // HEADSPINNER: EMP/stun shot
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

    | WeaponType.Troopers ->
        // TROOPERS: lob 3 ground units in a spread; they fall, dig in and
        // fire bullets at the sky (see the Trooper case in updateEntity)
        let ents = [ for off in [ -30.0; 0.0; 30.0 ] -> makeProjectileAngled p ownerIdx sw off ]
        p, ents

    | WeaponType.Dumbfire ->
        // DUMBFIRE: fast unguided rocket
        p, [ makeProjectile p ownerIdx sw ]

    | _ ->
        // Default: standard forward shot
        p, [ makeProjectile p ownerIdx sw ]

/// Fire the main weapon (fire key); cannon-type rounds carry the ship's motion.
let fireWeapon (rng: Random) (level: LevelData option) (p: Player) (ownerIdx: int) : Player * Entity list =
    let fired, ents = fireWeaponRaw rng level p ownerIdx
    fired, withShipMotion p.WeaponType p.VelX p.VelY ents

/// Fire the special weapon (DOWN key); cannon-type rounds carry the ship's motion.
let fireSpecial (rng: Random) (level: LevelData option) (p: Player) (ownerIdx: int) : Player * Entity list =
    let fired, ents = fireSpecialRaw rng level p ownerIdx
    fired, withShipMotion p.SpecialWeapon p.VelX p.VelY ents

// ─── Player Update ─────────────────────────────────────────────────────
// Returns (updated Player, new Entities, new Particles, terrainModified)

/// Easy CPU ships fire at most once per this many ticks (1 s at 36 fps)
[<Literal>]
let EasyCpuFireTicks = 36

let updatePlayer (gs: GameState) (idx: int) : Player * Entity list * Particle list * bool =
    let p = gs.Players[idx]
    if not p.Alive then (p, [], [], false) else

    // Shield blocks ALL input — cleared only by terrain contact or pass-through
    if p.Flags.HasFlag PlayerFlags.Shield then
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
            // Terrain contact: clear shield, doubled collision damage
            let speed = abs p.VelX + abs velY
            let speedDamage = int (speed * ShieldCollisionDamageScale)
            let flags = p.Flags &&& ~~~PlayerFlags.Shield
            let h = if speedDamage > 0 && p.InvTimer = 0 then p.Health - speedDamage else p.Health
            let inv = if speedDamage > 0 && p.InvTimer = 0 then SpawnInvincibilityTicks else p.InvTimer
            ({ p with VelX = 0.0; VelY = 0.0
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
    let canControl = not (flags.HasFlag PlayerFlags.Stunned)

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
        if flags.HasFlag PlayerFlags.Drunk then
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
                let healed =
                    if onBase && gs.GameTick % BaseHealInterval = 0
                    then min FullHealth (p.Health + BaseHealRate)
                    else p.Health
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
                let speedDamage = int (speed * CollisionDamageScale)
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
            let speedDamage = int (boundarySpeed * CollisionDamageScale)
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

    // CPU ships re-arm when they dock: on the tick a CPU settles onto a pad
    // (OnBase edge false -> true) it swaps its special weapon for a random
    // different one — the CPU counterpart of the human switch-while-based rule.
    let justDocked = onBase && not p.OnBase

    // A fresh press of the fire key this tick (edge, not held) — captured
    // before KeyFirePrev is refreshed below
    let freshFirePress = p.KeyFire && not p.KeyFirePrev

    // Build partially-updated player
    let p = { p with
                PosX = posX; PosY = posY; Angle = angle
                VelX = velX; VelY = velY; Flags = flags
                Health = health; WallHitCount = wallHitCount
                InvTimer = invTimer; WallDmgCooldown = wallDmgCooldown
                StunTimer = stunTimer
                AnimAngle = animAngle; ReloadTimer = reloadTimer
                SpecialReloadTimer = specialReloadTimer
                KeyFirePrev = p.KeyFire
                OnBase = onBase }

    let p =
        // CPU ships swap specials on docking — except when weapons are
        // fixed for the whole round
        if p.IsCpu && justDocked && gs.WeaponSwitch <> OnlyAtStart then
            // Swapping the special also drops the magnofilter field — the
            // toggle-off action belongs to the weapon being swapped away
            { p with SpecialWeapon = randomSpecialWeapon gs.Rng p.SpecialWeapon
                     SpecialReloadTimer = 0
                     Flags = p.Flags &&& ~~~PlayerFlags.Magno }
        else p

    // Cannon firing (main fire key) — always single-shot Cannon. HOLDING the
    // key auto-fires at the full reload rate; TAPPING it lets a fresh press
    // fire once the reload is half done, so a quick trigger finger can push
    // the main gun up to 2x the auto-fire rate. Main weapon only — specials
    // keep their full reload gate.
    //
    // Easy CPU ships hold their fire: after any shot, main gun or special,
    // both guns wait at least EasyCpuFireTicks and the tap shortcut is off, so
    // an Easy CPU fires about once a second. Hard CPUs and humans are untouched.
    let easyCpu = p.IsCpu && gs.CpuDifficulty = Easy
    let holdFire (p: Player) =
        if easyCpu then
            { p with ReloadTimer = max p.ReloadTimer EasyCpuFireTicks
                     SpecialReloadTimer = max p.SpecialReloadTimer EasyCpuFireTicks }
        else p

    let p, newEnts1 =
        let reloadDone = p.ReloadTimer = 0
        let tapFire = freshFirePress && not easyCpu && p.ReloadTimer * 2 <= (getWeapon p.WeaponType).ReloadTicks
        if p.KeyFire && (reloadDone || tapFire) && p.Ammo > 0 && canControl then
            let p, ents = fireWeapon gs.Rng gs.Level p idx
            holdFire p, ents
        else p, []

    // Special weapon firing (DOWN key) — uses SpecialWeapon + SpecialReloadTimer
    let p, newEnts2 =
        if p.KeyDown && p.SpecialReloadTimer = 0 && canControl then
            let p, ents = fireSpecial gs.Rng gs.Level p idx
            holdFire p, ents
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
        let vy = ent.VelY + LightRoundGravity
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

        let x = ent.X + vx
        let y = ent.Y + vy

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
            if ent.WeaponIdx = WeaponType.AtomWeapon && ent.Timer < atomArmTicks then
                // Unarmed atom round fizzles — the nuke can't be point-blank
                // detonated by firing into the wall next to your target (or
                // yourself; friendly fire would make that suicide anyway)
                eraseTerrain gs.Level x y 3.0
                let parts = spawnExplosion gs.Rng x y 4 1.0 8 (ent.Owner % 4)
                (None, [], parts @ particles, dirty)
            elif ent.WeaponIdx = WeaponType.AtomWeapon then
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
        // Missiles run out of fuel sooner — a 300-tick homing life makes your
        // own missile boomerang back at you far too often (friendly fire!)
        elif outOfBounds 20.0 x y || ent.Timer > (if ent.WeaponIdx = WeaponType.Missile then 150 else 300) then
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

    | EntityType.Trooper ->
        // TROOPER: a lobbed ground unit. Falls with gravity; with terrain
        // directly underneath it digs in and fires a bullet at the sky every
        // trooperFireInterval ticks until its lifetime runs out.
        if ent.Timer > trooperLifeTicks then
            let parts = spawnExplosion gs.Rng ent.X ent.Y 3 0.8 8 (ent.Owner % 4)
            (None, [], parts, false)
        else
            let grounded = hitsWall gs.Level ent.X (ent.Y + 2.0) 1.0
            if grounded then
                let shots =
                    if ent.Timer > 0 && ent.Timer % trooperFireInterval = 0 then
                        let angle = degToRad (90.0 + float (gs.Rng.Next 41 - 20))
                        [ { defaultEntity with
                              X = ent.X; Y = ent.Y - 2.0
                              VelX = cos angle * 3.5
                              VelY = -(sin angle) * 3.5
                              EType = EntityType.Bullet; Owner = ent.Owner
                              WeaponIdx = WeaponType.Troopers } ]
                    else []
                (Some { ent with VelX = 0.0; VelY = 0.0 }, shots, [], false)
            else
                let vy = min (ent.VelY + 0.08) 3.0
                let vx = ent.VelX * 0.98
                let x = ent.X + vx
                let y = ent.Y + vy
                if outOfBounds 10.0 x y then (None, [], [], false)
                elif hitsWall gs.Level x y 1.0 then
                    // Ran into a wall sideways or steeply — stop and dig in here
                    (Some { ent with VelX = 0.0; VelY = 0.0 }, [], [], false)
                else
                    (Some { ent with X = x; Y = y; VelX = vx; VelY = vy }, [], [], false)

    | _ ->
        // Default: linear motion (Bullet, BulletAlt, PassThrough, Shield, etc.)
        // Standard bullets get normal gravity; alt bullets get lighter gravity.
        // The Nucleus freeze orb barely sinks — it is meant to FLOAT ahead as
        // area denial, not nose-dive (its forward drift is only 1.2 px/tick).
        let gravity =
            match ent.EType with
            | EntityType.Bullet -> BulletGravity
            | EntityType.Shield when ent.WeaponIdx = WeaponType.Nucleus -> FreezeOrbGravity
            | EntityType.BulletAlt | EntityType.Shield -> LightRoundGravity
            | _ -> 0.0
        let vy = ent.VelY + gravity
        // BulletAlt has VelY clamp of 128 integer = 16.0 float
        let vy =
            if ent.EType = EntityType.BulletAlt then min vy 16.0 else vy
        let x = ent.X + ent.VelX
        let y = ent.Y + vy
        // Crater where the round meets the rock, not wherever this tick's move ends
        match firstWallHit gs.Level ent.X ent.Y x y 1.0 with
        | Some (hitX, hitY) ->
            // Freeze orbs fizzle on the wall without craters — a zero-damage
            // effect projectile shouldn't dig terrain
            if ent.EType <> EntityType.Shield then
                eraseTerrain gs.Level hitX hitY 3.0
            (None, [], [], dirty)
        | None when outOfBounds 20.0 x y || ent.Timer > 200 -> (None, [], [], dirty)
        | None -> (Some { ent with X = x; Y = y; VelY = vy }, [], [], dirty)

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
                        // Half the full field strength: at the core the pull (0.1/tick)
                        // matches full thrust — a ship that drifts deep in barely
                        // claws out, further out thrust wins comfortably
                        let force = blackholeStrength * 0.5 * (1.0 - dist / blackholeRadius)
                        vx <- vx + dx / dist * force
                        vy <- vy + dy / dist * force
                if vx <> p.VelX || vy <> p.VelY then
                    { p with VelX = vx; VelY = vy }
                else p)

    (entities, players)

// ─── Magnofilter Deflector Pass ────────────────────────────────────────
// Players with Magno flag DEFLECT enemy projectiles away from themselves —
// a defensive field (attracting bullets to yourself was worse than useless).
// The field is a QUARTER circle over the nose (±45°, like the original):
// the front is strongly protected but the tail stays open, so skilled
// flying — keeping your nose toward the threat — is what makes it work.

[<Literal>]
let magnofilterPullRadius = 60.0
[<Literal>]
let magnofilterStrength = 0.25
/// cos 45° — a projectile is inside the field when the ship-to-projectile
/// direction is within ±45° of the ship's facing
[<Literal>]
let private magnofilterArcCos = 0.7071

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
                if pi <> ent.Owner then  // Don't deflect own bullets
                    let px = mp.PosX / PositionScale
                    let py = mp.PosY / PositionScale
                    let dx = px - ent.X
                    let dy = py - ent.Y
                    let dist = sqrt (dx*dx + dy*dy) + 0.1
                    if dist < magnofilterPullRadius then
                        // Quarter-circle field: only projectiles in the ±45°
                        // arc over the nose are deflected; the tail is open
                        let rad = degToRad (mp.Angle + 90.0)
                        let towardEntX = -dx / dist
                        let towardEntY = -dy / dist
                        let facingDot = towardEntX * cos rad + towardEntY * -(sin rad)
                        if facingDot > magnofilterArcCos then
                            let force = magnofilterStrength * (1.0 - dist / magnofilterPullRadius)
                            // Push AWAY from the magno ship (dx/dy point toward it)
                            vx <- vx - dx / dist * force
                            vy <- vy - dy / dist * force
            if vx <> ent.VelX || vy <> ent.VelY then
                { ent with VelX = vx; VelY = vy }
            else ent
        | _ -> ent)

// ─── Projectiles/Blasts vs Troopers ────────────────────────────────────
// Troopers are killable:
//  - plain bullets, ricochets and shrapnel destroy one on contact and are
//    spent doing so — a dug-in nest can be cleared with the cannon (or by
//    another trooper's fire). Fresh rounds get a couple of ticks of grace
//    so a trooper doesn't shoot itself with the bullet leaving its barrel.
//  - nuke blasts and sonicboom rings kill EVERY trooper they sweep over
//    (the blast itself persists, as it does against ships).
// Mine detonations also clear troopers — handled at the detonation site in
// checkBulletPlayerCollision, since the mine is gone the same tick.

let checkTrooperHits (rng: Random) (entities: Entity list) : Entity list * Particle list =
    if not (entities |> List.exists (fun e -> e.EType = EntityType.Trooper)) then
        (entities, [])
    else
        let es = Array.ofList entities
        let mutable parts : Particle list = []
        let killTrooper ti =
            let t: Entity = es[ti]
            parts <- spawnExplosionParticles rng t.X t.Y 4 1.0 8 (t.Owner % 4) @ parts
            es[ti] <- { t with EType = EntityType.None }
        for bi in 0 .. es.Length - 1 do
            let b = es[bi]
            match b.EType with
            | EntityType.Bullet | EntityType.BulletAlt | EntityType.Ricochet | EntityType.Shrapnel when b.Timer > 2 ->
                // Kinetic round: kills the first trooper it touches, then is spent
                let mutable hit = false
                for ti in 0 .. es.Length - 1 do
                    if not hit && es[ti].EType = EntityType.Trooper
                       && collides b.X b.Y 1.0 es[ti].X es[ti].Y 2.0 then
                        hit <- true
                        killTrooper ti
                        es[bi] <- { b with EType = EntityType.None }
            | EntityType.Nuke ->
                // Expanding blast: every trooper inside the radius dies
                let blastR = max 4.0 b.Radius
                for ti in 0 .. es.Length - 1 do
                    if es[ti].EType = EntityType.Trooper
                       && collides b.X b.Y blastR es[ti].X es[ti].Y 2.0 then
                        killTrooper ti
            | EntityType.Expanding ->
                // Sonicboom: troopers caught in the ring band die
                for ti in 0 .. es.Length - 1 do
                    if es[ti].EType = EntityType.Trooper then
                        let dx = b.X - es[ti].X
                        let dy = b.Y - es[ti].Y
                        if abs (sqrt (dx*dx + dy*dy) - b.Radius) < 6.0 then
                            killTrooper ti
            | _ -> ()
        (es |> Array.filter (fun e -> e.EType <> EntityType.None) |> Array.toList, parts)

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
                // Friendly fire: after a short grace period a projectile can
                // hit its own shooter too (at reduced damage, below) — you
                // can't nuke someone point-blank without getting hurt.
                let selfHit = i = ent.Owner
                if p.Alive && p.InvTimer = 0 && (not selfHit || ent.Timer > SelfHitGraceTicks) then
                    let radius =
                        match ent.EType with
                        | EntityType.Mine -> 8.0
                        | EntityType.Heavy -> 6.0
                        // Freeze orbs (Freezer/Nucleus) deal no damage — landing
                        // one IS the payoff, so give them a forgiving hitbox
                        | EntityType.Shield -> 5.0
                        | EntityType.Nuke -> max 4.0 ent.Radius
                        | EntityType.Expanding -> ent.Radius
                        | EntityType.Blackhole -> 6.0
                        | EntityType.Flame -> max 2.0 (float ent.Timer * 0.3)
                        | _ -> 3.0
                    let px = p.PosX / PositionScale
                    let py = p.PosY / PositionScale

                    let hits =
                        if ent.EType = EntityType.Expanding then
                            // Expanding ring band vs the hull: centre or any hull
                            // vertex within the ring's band counts as a hit
                            let (a, b, c) = shipTriangle px py p.Angle
                            let inBand (vx: float, vy: float) =
                                let dx = ent.X - vx
                                let dy = ent.Y - vy
                                abs (sqrt (dx*dx + dy*dy) - ent.Radius) < 4.0
                            inBand (px, py) || inBand a || inBand b || inBand c
                        else
                            // Triangle hull hit mask — exactly the ship the
                            // player sees, not an approximating circle
                            circleHitsShip ent.X ent.Y radius px py p.Angle

                    if hits then
                        let damage =
                            match ent.EType with
                            // Per-weapon damage from the weapons table: the
                            // Cannon main gun hits hardest; special-weapon
                            // bullets (machinegun, multicannon, troopers...)
                            // are individually weaker support fire.
                            | EntityType.Bullet | EntityType.BulletAlt -> max 1 (getWeapon ent.WeaponIdx).Damage
                            | EntityType.Mine -> if ent.Timer > 0 then 30 else 0
                            | EntityType.EMP
                            | EntityType.Shield -> 0
                            | EntityType.Ricochet -> 3
                            | EntityType.PassThrough
                            | EntityType.Laser -> 1
                            // Homing missiles arrive late by nature, so the
                            // flight-time damage decay would gut them — they
                            // deal their flat table damage instead.
                            | EntityType.Heavy when ent.WeaponIdx = WeaponType.Missile -> (getWeapon ent.WeaponIdx).Damage
                            | EntityType.Heavy -> heavyDamage ent.Timer
                            | EntityType.Flame -> 1
                            | EntityType.Nuke
                            | EntityType.Railgun -> 15
                            | EntityType.Shrapnel -> 1
                            | EntityType.Expanding -> 10
                            | EntityType.Exploding -> 4
                            | EntityType.Blackhole -> 2
                            | _ -> 0

                        // Self-hits deal a fraction of the damage; zero-damage
                        // effect entities (EMP stun, freeze/shield) never
                        // affect their own shooter.
                        let damage =
                            if selfHit then
                                if damage > 0 then max 1 (int (float damage * FriendlyFireDamageScale)) else 0
                            else damage

                        if damage > 0 || (not selfHit && (ent.EType = EntityType.EMP || ent.EType = EntityType.Shield)) then
                            let mutable np = { p with Health = p.Health - damage }

                            // Knockback
                            let knockScale =
                                if np.Flags.HasFlag PlayerFlags.Shield then ShieldKnockbackScale
                                else NormalKnockbackScale
                            if ent.VelX <> 0.0 || ent.VelY <> 0.0 then
                                match ent.EType with
                                | EntityType.Bullet | EntityType.BulletAlt ->
                                    // Momentum-style push for plain bullets: the victim gets
                                    // BulletHitImpulseFraction (~80%) of the firing recoil for
                                    // this projectile class as an impulse along the bullet's
                                    // travel direction, scaled by how much of its muzzle speed
                                    // the bullet still carries at impact (slowed = softer,
                                    // gravity-accelerated = harder). Applying vel * (frac *
                                    // recoil / muzzle) gives exactly that without a sqrt.
                                    let fireRecoil =
                                        if ent.WeaponIdx = WeaponType.Cannon
                                        then SpecialFireRecoil * CannonRecoilFraction
                                        else SpecialFireRecoil
                                    let muzzle = (getWeapon ent.WeaponIdx).ProjectileSpeed
                                    let impulsePerSpeed =
                                        if muzzle > 0.0 then BulletHitImpulseFraction * fireRecoil / muzzle
                                        else 0.0
                                    // Shielded ships take half the push, matching the
                                    // Normal/Shield knockback ratio used elsewhere.
                                    let shieldScale = NormalKnockbackScale / knockScale
                                    np <- { np with
                                                VelX = np.VelX + ent.VelX * impulsePerSpeed * shieldScale
                                                VelY = np.VelY + ent.VelY * impulsePerSpeed * shieldScale }
                                    // A parked ("based") victim is unseated — the pad is
                                    // slippery for campers. The pad's rest-snap re-seats and
                                    // zeroes the velocity of any non-thrusting ship within
                                    // BaseLandReach every tick, so a velocity kick alone
                                    // would be cancelled: lift the ship clear of the scan
                                    // reach and add an upward kick so the sideways momentum
                                    // above actually slides it. Strictly gated on OnBase
                                    // (parked + refueling); flying ships near a pad keep
                                    // normal landing physics.
                                    if np.OnBase then
                                        let impulseMag =
                                            sqrt (ent.VelX * ent.VelX + ent.VelY * ent.VelY)
                                            * impulsePerSpeed * shieldScale
                                        let upKick = max BasedHitUpwardKickMin (impulseMag * BasedHitUpwardKickScale)
                                        np <- { np with
                                                    PosY = max 0.0 (np.PosY - BasedHitUnseatLift * PositionScale)
                                                    VelY = np.VelY - upKick
                                                    OnBase = false }
                                | _ ->
                                    // Divisor is in player-velocity units (world px/tick). The
                                    // heavy blasts fold in PositionScale to stay gentle; other
                                    // projectiles keep the legacy knockback divisor.
                                    let kbDiv =
                                        match ent.EType with
                                        | EntityType.Nuke | EntityType.Expanding -> 1.0 * PositionScale
                                        | EntityType.Heavy -> 2.0 * PositionScale
                                        | _ -> BulletKnockbackDiv
                                    np <- { np with
                                                VelX = np.VelX + ent.VelX / (kbDiv * knockScale)
                                                VelY = np.VelY + ent.VelY / (kbDiv * knockScale) }
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
                                            Flags = (np.Flags ||| PlayerFlags.Shield) &&& ~~~PlayerFlags.Stunned
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
                                // Prepend, not append — particle order doesn't matter and
                                // appending is quadratic across many hits in one tick
                                newParticles <- spawnExplosion gs.Rng ent.X ent.Y 10 2.0 20 (ent.Owner % 4) @ newParticles
                                // The mine blast also wipes out troopers in range
                                // (the mine is gone this same tick, so the area
                                // pass in checkTrooperHits never sees the blast)
                                for ti in 0 .. es.Length - 1 do
                                    let te = es[ti]
                                    if te.EType = EntityType.Trooper && collides ent.X ent.Y 10.0 te.X te.Y 2.0 then
                                        newParticles <- spawnExplosion gs.Rng te.X te.Y 4 1.0 8 (te.Owner % 4) @ newParticles
                                        es[ti] <- { te with EType = EntityType.None }
                                ent <- { ent with EType = EntityType.None }  // Mark for removal
                            | _ ->
                                // Consume the projectile so it cannot hit further players
                                // this tick. An atom round detonates into a Nuke *after* the
                                // player loop (below) rather than mid-loop, so overlapping
                                // players aren't struck as both a Heavy round and a Nuke.
                                // Unarmed rounds (fired a moment ago) fizzle instead.
                                if ent.WeaponIdx = WeaponType.AtomWeapon && ent.Timer >= atomArmTicks then atomDetonate <- true
                                ent <- { ent with EType = EntityType.None }  // Mark for removal

                            // Credit kill — only when THIS damaging hit is what crossed the
                            // death threshold. Prevents double-crediting when several
                            // projectiles strike a player on the tick it dies, avoids
                            // crediting zero-damage EMP/Shield hits, and gives no credit
                            // for blowing yourself up with friendly fire.
                            if not selfHit && damage > 0 && p.Health > DeathThreshold && np.Health <= DeathThreshold then
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
    let entities = es |> Array.filter (fun e -> e.EType <> EntityType.None) |> Array.toList
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
                // True triangle-vs-triangle hull overlap (separating axis) —
                // nose-first contact connects at range, sideways brushes don't.
                if shipsCollide x1 y1 p1.Angle x2 y2 p2.Angle then
                    let dx = x2 - x1
                    let dy = y2 - y1
                    let dist = sqrt (dx*dx + dy*dy) + 0.001
                    let nx = dx / dist
                    let ny = dy / dist
                    // Closing speed along the contact normal (n points p1 -> p2).
                    let approach = (p2.VelX - p1.VelX) * nx + (p2.VelY - p1.VelY) * ny
                    // Only resolve while the ships are moving together. Once they are
                    // separating the impulse is skipped, so it can't re-apply every
                    // overlapping tick and accumulate into a violent fling.
                    if approach < 0.0 then
                        // Equal-mass bounce with restitution 0.5: split the impulse.
                        let imp = -0.75 * approach
                        ps[i] <- { p1 with VelX = p1.VelX - nx * imp; VelY = p1.VelY - ny * imp }
                        ps[j] <- { p2 with VelX = p2.VelX + nx * imp; VelY = p2.VelY + ny * imp }
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
// The AI is the same at both difficulties; Easy only caps the fire rate
// (EasyCpuFireTicks, applied in updatePlayer).

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

// ── Retreat to repair ──
// A badly damaged CPU ship breaks off the fight and flies to the nearest free
// base it can see, then sits there as a turret (turning and firing, no
// thrust) until the pad has healed it to full. It's an occasional move, not a
// habit: at most one repair trip a minute (dying resets the count), never
// started with an enemy close by, and abandoned (back to the fight) the moment
// an enemy comes close. With every other ship dead, a CPU below half health
// uses the lull to repair too.

/// Health at or below which a CPU heads for a base (20%)
let cpuRepairHealth = FullHealth * 2 / 10
/// With no enemy alive, a CPU below this health (50%) heads for a base
let cpuLullRepairHealth = FullHealth / 2
/// At most one repair trip per CPU life per this many ticks (one minute at 36 fps)
[<Literal>]
let CpuRepairCooldownTicks = 36 * 60
/// An enemy closer than this (pixels) keeps a CPU fighting instead of docking.
/// About half a 2-player viewport (which shows ~128x160 map pixels), so an
/// enemy at the edge of the screen or beyond no longer counts as near.
[<Literal>]
let CpuRepairEnemyClearance = 60.0
/// A repair trip that hasn't docked within this many ticks (15 s) is given up
[<Literal>]
let CpuRepairTripTicks = 36 * 15

/// Whether a line from (x0, y0) to (x1, y1), in pixels, shifted `side` pixels
/// sideways, is clear of walls; its first `skip` pixels aren't checked
let private lineClear (level: LevelData) (x0: float) (y0: float) (x1: float) (y1: float) (side: float) (skip: float) =
    let len = sqrt ((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0))
    let steps = max 1 (int len)
    let nx, ny = if len > 0.0 then -(y1 - y0) / len, (x1 - x0) / len else 0.0, 0.0
    seq { 0..steps }
    |> Seq.forall (fun s ->
        let t = float s / float steps
        t * len < skip
        || (let x = x0 + (x1 - x0) * t + nx * side
            let y = y0 + (y1 - y0) * t + ny * side
            not (Terrain.isWall (Terrain.terrainAt level.Pixels x y))))

/// Line of sight: the bare straight line between two points is clear of walls.
/// Right for "can I see / shoot the enemy" (bullets are thin), where a
/// ship-wide corridor would clip the floor under an enemy resting on it.
let private inSight (level: LevelData) (x0: float) (y0: float) (x1: float) (y1: float) =
    lineClear level x0 y0 x1 y1 0.0 0.0

/// Whether a ship-wide corridor (the straight line between two points, in
/// pixels, plus parallel lines 3 pixels either side) is clear of walls — a bare
/// line can graze a corner the ship itself would catch on. The side lines
/// start 4 pixels out, so a ship resting on the ground can still see along it.
let private clearPath (level: LevelData) (x0: float) (y0: float) (x1: float) (y1: float) =
    inSight level x0 y0 x1 y1
    && lineClear level x0 y0 x1 y1 3.0 4.0
    && lineClear level x0 y0 x1 y1 -3.0 4.0

// Route finding: the map as a grid of RouteCell-pixel cells, open where a ship
// fits (the cell centre and points 3 pixels around it are clear of walls).
// A breadth-first search finds the shortest route to a base through open cells.

/// Route grid cell size in pixels
[<Literal>]
let private RouteCell = 4
let private routeW = MapWidth / RouteCell
let private routeH = MapHeight / RouteCell

// Search scratch space, reused by every route search (the game loop is
// single-threaded) instead of allocating ~40 KB per search, every tick
let private routeParent = Array.create (routeW * routeH) -2      // -2 unvisited, -1 start
let private routeOpen = Array.zeroCreate<byte> (routeW * routeH)  // 0 unknown, 1 open, 2 closed
let private routeQueue = System.Collections.Generic.Queue<int>()

let private cellCentre (c: int) =
    float ((c % routeW) * RouteCell + RouteCell / 2), float ((c / routeW) * RouteCell + RouteCell / 2)

let private cellAt (x: float) (y: float) =
    let cx = clampF 0.0 (float (routeW - 1)) (x / float RouteCell) |> int
    let cy = clampF 0.0 (float (routeH - 1)) (y / float RouteCell) |> int
    cy * routeW + cx

let private cellOpen (level: LevelData) (c: int) =
    let x, y = cellCentre c
    [ 0.0, 0.0; 3.0, 0.0; -3.0, 0.0; 0.0, 3.0; 0.0, -3.0 ]
    |> List.forall (fun (dx, dy) -> not (Terrain.isWall (Terrain.terrainAt level.Pixels (x + dx) (y + dy))))

/// Whether `cells` holds cell `c` — a plain loop: as fast as LINQ's Contains
/// on .NET, and it also compiles under Fable (the web build has no System.Linq)
let inline private hasCell (cells: int array) (c: int) =
    let found = cells |> Array.exists (fun x -> x = c)
    found

/// Where to fly next (pixels) from (px, py) toward the nearest of `goals` the
/// ship can reach, if any. A goal in plain sight is flown at directly;
/// otherwise the nearest goal by route wins, and the waypoint is the farthest
/// point along that route the ship has a clear corridor to — so it flies
/// straight where it can and around terrain where it must.
let private routeStep (level: LevelData) (px: float) (py: float) (goals: (float * float) array) : (float * float) option =
    let visibleGoal =
        goals
        |> Array.filter (fun (gx, gy) -> clearPath level px py gx gy)
        |> Array.sortBy (fun (gx, gy) -> (gx - px) * (gx - px) + (gy - py) * (gy - py))
        |> Array.tryHead
    match visibleGoal with
    | Some _ -> visibleGoal
    | None when goals.Length = 0 -> None
    | None ->
        // Breadth-first search from the ship's cell to the first goal cell
        let goalCells = goals |> Array.map (fun (gx, gy) -> cellAt gx gy)
        let parent = routeParent
        let isOpen = routeOpen
        let queue = routeQueue
        Array.fill parent 0 parent.Length -2
        Array.fill isOpen 0 isOpen.Length 0uy
        queue.Clear()
        let openAt c =
            if isOpen[c] = 0uy then isOpen[c] <- if cellOpen level c then 1uy else 2uy
            isOpen[c] = 1uy
        let start = cellAt px py
        parent[start] <- -1
        queue.Enqueue start
        let mutable found = -1
        while found < 0 && queue.Count > 0 do
            let c = queue.Dequeue()
            if hasCell goalCells c then found <- c
            else
                let cx, cy = c % routeW, c / routeW
                for nx, ny in [ cx - 1, cy; cx + 1, cy; cx, cy - 1; cx, cy + 1 ] do
                    if nx >= 0 && nx < routeW && ny >= 0 && ny < routeH then
                        let nc = ny * routeW + nx
                        if parent[nc] = -2 && (hasCell goalCells nc || openAt nc) then
                            parent[nc] <- c
                            queue.Enqueue nc
        if found < 0 then None
        else
            // Route from the ship to the goal; take the farthest of the next 40
            // cells in clear sight (the next cell on the route at worst). A
            // waypoint the ship is already on (e.g. at the mouth of a shaft
            // narrower than the sight check) moves on to the cell after it.
            let route =
                List.unfold (fun c -> if c < 0 then None else Some(c, parent[c])) found |> List.rev
            match List.tail route with
            | [] -> Some(cellCentre found)
            | next :: _ as ahead ->
                let best =
                    ahead
                    |> List.truncate 40
                    |> List.rev
                    |> List.tryFind (fun c -> let x, y = cellCentre c in clearPath level px py x y)
                    |> Option.defaultValue next
                let bx, by = cellCentre best
                if (bx - px) * (bx - px) + (by - py) * (by - py) < 16.0 then
                    match ahead |> List.skipWhile (fun c -> c <> best) |> List.tail with
                    | after :: _ -> Some(cellCentre after)
                    | [] -> Some(bx, by)
                else Some(bx, by)

/// Where CPU `i` should fly next on its way to the nearest free base it can
/// reach (pixels), if any. The goal is a spot 25 pixels over the pad (when the
/// drop onto it is clear) until the ship is above the pad itself, then the pad.
let private nearestRepairBase (gs: GameState) (i: int) (px: float) (py: float) : (float * float) option =
    match gs.Level with
    | Some level ->
        let occupied (sp: SpawnPoint) =
            gs.Players
            |> List.indexed
            |> List.exists (fun (j, o) ->
                j <> i && j < gs.NumPlayers && o.Alive
                && abs (o.PosX / PositionScale - (float sp.X + float sp.Width / 2.0)) < float sp.Width / 2.0 + 6.0
                && abs (o.PosY / PositionScale - float sp.Y) < 8.0)
        let goal (sp: SpawnPoint) =
            let bx, by = float sp.X + float sp.Width / 2.0, float sp.Y - 2.0
            let overPad = abs (px - bx) < float sp.Width / 2.0 - 2.0 && py <= by
            if overPad || not (clearPath level bx (by - 25.0) bx by) then bx, by else bx, by - 25.0
        level.SpawnPoints
        |> Array.filter (occupied >> not)
        |> Array.map goal
        |> routeStep level px py
    | None -> None

/// Whether flying from (px, py) along `angle` hits a wall or the arena edge
/// within 6 pixels (sampled close in, so a one-pixel bump next to a ship
/// resting on the ground counts)
let private wallAhead (level: LevelData) (px: float) (py: float) (angle: float) =
    let rad = degToRad (angle + 90.0)
    [ 1.0; 2.0; 4.0; 6.0 ]
    |> List.exists (fun d ->
        let x = px + cos rad * d
        let y = py - sin rad * d
        x < 2.0 || x > ArenaWidth - 2.0 || y < 2.0 || y > ArenaHeight - 2.0
        || Terrain.isWall (Terrain.terrainAt level.Pixels x y))

/// Open-space probe: of the 8 compass directions — north, northwest, west,
/// southwest, south, southeast, east, northeast (game angles 0, 45 .. 315; 0 is
/// nose up, angles grow anticlockwise) — those where the points 20 and 60
/// pixels out are both empty space (black: no wall, no water, inside the
/// arena) and no wall lies on the line out to them (so a thin wall right beside
/// the ship isn't skipped over): probably free to fly that way.
let private openDirections (level: LevelData) (px: float) (py: float) : float list =
    let inArena (x: float) (y: float) = x >= 2.0 && x <= ArenaWidth - 2.0 && y >= 2.0 && y <= ArenaHeight - 2.0
    let empty (x: float, y: float) = inArena x y && Terrain.isVoid (Terrain.terrainAt level.Pixels x y)
    [ for k in 0..7 do
          let angle = float k * 45.0
          let rad = degToRad (angle + 90.0)
          let at (d: float) = px + cos rad * d, py - sin rad * d
          let lineClear =
              seq { 2.0..2.0..60.0 }
              |> Seq.forall (fun d ->
                  let x, y = at d
                  inArena x y && not (Terrain.isWall (Terrain.terrainAt level.Pixels x y)))
          if lineClear && empty (at 20.0) && empty (at 60.0) then
              yield angle ]

/// Steer toward a waypoint: returns (turnLeft, turnRight, thrust). Wants a
/// velocity aimed at the waypoint that slows on approach, and only ever thrusts
/// up or sideways — gravity does the descending. If walls block the way the
/// nose wants to push, it tilts up in steps until the way is clear. A pad stops
/// a ship touching it at any speed, so the landing need not be gentle, only
/// clear of walls.
let private steerToBase (level: LevelData) (p: Player) (px: float) (py: float) (tx: float) (ty: float) =
    let dx = tx - px
    let dy = ty - py
    let wantVX = clampF -1.0 1.0 (dx * 0.08)
    let wantVY = clampF -1.0 0.8 (dy * 0.04)
    // Resting on the ground (or a slope): a push that lifts less than gravity
    // pulls scrapes the floor and the whole move is undone. So climb off it
    // first — or, when the route goes down, slide off sideways with a little lift.
    let grounded =
        p.VelY >= 0.0
        && (Terrain.isWall (Terrain.terrainAt level.Pixels px (py + 1.0))
            || Terrain.isWall (Terrain.terrainAt level.Pixels px (py + 1.5)))
    let ex =
        let e = wantVX - p.VelX
        if grounded && dy > 2.0 && abs e < 0.5 then (if dx >= 0.0 then 0.5 else -0.5) else e
    let ey =
        let lift = if not grounded then 0.0 elif dy > 2.0 then -0.25 else -0.5
        min lift (wantVY - p.VelY)
    let needThrust = sqrt (ex * ex + ey * ey) > 0.1
    let angleDiff (target: float) =
        let mutable diff = target - p.Angle
        if diff > 180.0 then diff <- diff - 360.0
        if diff < -180.0 then diff <- diff + 360.0
        diff
    // Nose along the needed push (tilted up past any wall in the way), or
    // straight up (ready to brake) when coasting
    let wantAngle =
        if needThrust then
            let a = atan2 (-ex) (-ey) * 180.0 / Math.PI
            let a = if a < 0.0 then a + 360.0 else a
            // Step toward nose-up (angle 0) 30 degrees at a time, then try
            // tilting either way off the wanted direction, and last any of the
            // 8 compass directions (nearest to the wanted one first) — wedged
            // in a corner, backing out beats waiting
            let towardUp = if a > 180.0 then 30.0 else -30.0
            let upward =
                List.init 7 (fun k ->
                    let c = a + towardUp * float k
                    if (towardUp > 0.0 && c >= 360.0) || (towardUp < 0.0 && c <= 0.0) then 0.0 else c)
            let sideways = [ a + 30.0; a - 30.0; a + 60.0; a - 60.0 ] |> List.map (fun c -> (c + 360.0) % 360.0)
            let compass =
                [ 0.0..45.0..315.0 ] |> List.sortBy (fun c -> abs ((c - a + 540.0) % 360.0 - 180.0))
            // Airborne with the way on lying below: a blocked push means
            // coast — gravity drops the ship past the obstacle, where
            // tilting up would only hold it in place
            let candidates = if dy > 2.0 && not grounded then [ a ] else upward @ sideways @ compass
            candidates |> List.tryFind (wallAhead level px py >> not)
        else Some 0.0
    match wantAngle with
    | Some target ->
        let diff = angleDiff target
        (diff > 3.0, diff < -3.0, needThrust && abs diff < 30.0)
    | None ->
        // Boxed in (or coasting down past a wall): turn nose-up, no thrust,
        // and wait for gravity or a gap
        let diff = angleDiff 0.0
        (diff > 3.0, diff < -3.0, false)

/// CPU `i`'s keys while on a repair trip, or None when it isn't on one: no
/// trip under way and `startTrip` false, back at full health, no free base in
/// sight to start for, or a trip that hasn't reached a pad within
/// CpuRepairTripTicks. Docked, it uses the `docked` keys with no thrust; en
/// route it steers for the base (firing if `fire` says so); if it loses sight
/// of the base mid-trip it climbs to find it again. A new trip uses up the
/// once-a-minute allowance (RepairTick) only if `countsTowardLimit` — lull
/// trips, with every enemy dead, don't.
let private repairTrip (gs: GameState) (i: int) (p: Player) (px: float) (py: float)
                       (startTrip: bool) (countsTowardLimit: bool) (fire: bool) (docked: Player) : Player option =
    let tick = gs.GameTick
    let newTrip = not p.Repairing
    let repairTick = if newTrip && countsTowardLimit then tick else p.RepairTick
    // The time limit is for reaching a pad: TripTick follows a docked ship, so
    // one knocked off the pad gets the full time to land again
    let tripTick = if newTrip then tick else p.TripTick
    let tripTooLong = p.Repairing && not p.OnBase && tick - p.TripTick > CpuRepairTripTicks
    if tripTooLong || p.Health >= FullHealth || not (p.Repairing || startTrip) then None
    elif p.OnBase then
        Some { docked with KeyUp = false; Repairing = true; RepairTick = repairTick; TripTick = tick }
    else
        match gs.Level, nearestRepairBase gs i px py with
        | Some level, Some (wx, wy) ->
            let left, right, thrust = steerToBase level p px py wx wy
            Some { p with
                    KeyUp = thrust
                    KeyLeft = left
                    KeyRight = right
                    KeyFire = fire
                    KeyDown = false
                    Repairing = true
                    RepairTick = repairTick
                    TripTick = tripTick }
        | Some level, None when p.Repairing ->
            // Lost sight of the base mid-trip (dropped into a pocket): climb
            let left, right, thrust = steerToBase level p px py px (py - 30.0)
            Some { p with
                    KeyUp = thrust
                    KeyLeft = left
                    KeyRight = right
                    KeyFire = fire
                    KeyDown = false }
        | _ -> None

/// Whether a cannon shell fired now at game angle `angle` would come back down
/// on the ship itself. Steps the shell (muzzle speed along `angle` plus the
/// inherited ship velocity, falling at BulletGravity) against the ship — kept
/// in place when standing or hovering, otherwise coasting on under gravity —
/// and checks whether they meet again once the self-hit grace is over. This
/// covers a steep shot from a ship standing still, climbing, or drifting
/// sideways (the shell drifts with it).
let private shellFallsBack (p: Player) (angle: float) (muzzle: float) =
    let rad = degToRad (angle + 90.0)
    let staysPut = sqrt (p.VelX * p.VelX + p.VelY * p.VelY) < 0.15
    let mutable sx, sy = 0.0, 0.0   // shell, relative to the ship's position now
    let mutable svx, svy =
        cos rad * muzzle + p.VelX * BulletInheritVelocity, -(sin rad) * muzzle + p.VelY * BulletInheritVelocity
    let mutable hx, hy = 0.0, 0.0   // the ship
    let mutable hvy = p.VelY
    let mutable hit = false
    let mutable t = 0
    while not hit && t < 150 do
        t <- t + 1
        svy <- svy + BulletGravity
        sx <- sx + svx
        sy <- sy + svy
        if not staysPut then
            hvy <- min (hvy + GravityAccel) MaxVelocity
            hx <- hx + p.VelX
            hy <- hy + hvy
        if t > SelfHitGraceTicks && (sx - hx) * (sx - hx) + (sy - hy) * (sy - hy) < 36.0 then
            hit <- true
    hit

/// Cannon aim from (px, py) at a target (tx, ty), in pixels, allowing for the
/// bullet's fall (BulletGravity): the game angle to fire at on the low, direct
/// arc, and the flight time in ticks. None when the target is beyond the
/// bullet's reach — firing anyway only drops shots short.
let private ballisticAim (speed: float) (px: float) (py: float) (tx: float) (ty: float) : (float * float) option =
    let x = tx - px
    let up = py - ty   // height of the target above the ship
    let g = BulletGravity
    let v2 = speed * speed
    let disc = v2 * v2 - g * (g * x * x + 2.0 * up * v2)
    if disc < 0.0 then None
    else
        let ax = abs x
        // Elevation above the horizontal (straight up/down for a target overhead/below)
        let theta =
            if ax < 0.5 then (if up >= 0.0 then Math.PI / 2.0 else -Math.PI / 2.0)
            else atan ((v2 - sqrt disc) / (g * ax))
        let dirX = (if x >= 0.0 then 1.0 else -1.0) * cos theta
        let dirY = -(sin theta)
        let ticks = if ax < 0.5 then abs up / speed else ax / (speed * cos theta)
        let a = atan2 (-dirX) (-dirY) * 180.0 / Math.PI
        Some((if a < 0.0 then a + 360.0 else a), ticks)

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
                    // No enemies alive — drift, or use the lull to repair on a
                    // base when below half health
                    let drift =
                        { p with KeyUp = false; KeyLeft = false; KeyRight = false; KeyFire = false; KeyDown = false
                                 Repairing = false }
                    repairTrip gs i p px py (p.Health < cpuLullRepairHealth) false false drift
                    |> Option.defaultValue drift
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
                    // Predict where the enemy will be when the shot arrives
                    // (accounting for gravity on the enemy)
                    let w = getWeapon p.WeaponType
                    let bulletSpeed = max 1.0 w.ProjectileSpeed
                    let predictAt (ticks: float) =
                        clampF 5.0 (ArenaWidth - 5.0) (ex + evx * ticks),
                        clampF 5.0 (ArenaHeight - 5.0) (ey + evy * ticks + 0.5 * GravityAccel * ticks * ticks)
                    let predX, predY = predictAt (dist / bulletSpeed)

                    // ── Target angle toward predicted position ──
                    // Cannon bullets fall (BulletGravity), so aim along their
                    // arc: solve it to the predicted spot, then refine twice
                    // with the arc's own flight time. The shell also carries
                    // part of this ship's velocity (BulletInheritVelocity) —
                    // the same as aiming from a still ship at a target drifting
                    // the opposite way, so that drift comes off the aim point.
                    // Out of the cannon's reach: no arc — face the enemy
                    // directly (pursuit thrusts that way) and hold the cannon.
                    let shellAim (ticks: float) =
                        let x, y = predictAt ticks
                        ballisticAim bulletSpeed px py
                            (x - p.VelX * BulletInheritVelocity * ticks)
                            (y - p.VelY * BulletInheritVelocity * ticks)
                    let cannonShot =
                        shellAim (dist / bulletSpeed)
                        |> Option.bind (fun (_, ticks) -> shellAim ticks)
                        |> Option.bind (fun (_, ticks) -> shellAim ticks)
                    let directAngle =
                        let a = atan2 (-(predX - px)) (-(predY - py)) * 180.0 / Math.PI
                        if a < 0.0 then a + 360.0 else a
                    let angleTo (target: float) =
                        let d = target - p.Angle
                        if d > 180.0 then d - 360.0 elif d < -180.0 then d + 360.0 else d
                    let targetAngle = match cannonShot with Some (a, _) -> a | None -> directAngle
                    let diff = angleTo targetAngle

                    let turnLeft = diff > 3.0
                    let turnRight = diff < -3.0
                    // Plain aim turn, kept apart from the dodge override below
                    let aimLeft, aimRight = turnLeft, turnRight

                    // ── Cannon aim tolerance (main fire key — always Cannon) ──
                    let aimTol = personality.AimTolerance
                    let fireRange = personality.FireRange

                    let aimed = abs diff < aimTol
                    let inRange = dist < fireRange
                    let shipSpeed = sqrt (p.VelX * p.VelX + p.VelY * p.VelY)
                    // Never fire a shell that would come back down on this ship
                    // (checked only when the CPU would otherwise fire). The
                    // ship turns before it fires within a tick, so check every
                    // angle the shell can leave at: the nose now, or one turn
                    // step either way — aiming at a target straight overhead,
                    // the nose flips across vertical every tick.
                    let ownShellReturns =
                        aimed && inRange && cannonShot.IsSome
                        && [ p.Angle; p.Angle + TurnSpeed; p.Angle - TurnSpeed ]
                           |> List.exists (fun a -> shellFallsBack p a bulletSpeed)
                    // The shot the CPU is lining up would itself come back down
                    // on it (enemy about straight overhead) — whatever the nose
                    // points at right now. Triggers the sidestep below.
                    let wantedShotReturns =
                        inRange
                        && (match cannonShot with
                            | Some (a, _) -> shellFallsBack p a bulletSpeed
                            | None -> false)
                    let shouldFire =
                        aimed && inRange && cannonShot.IsSome && not ownShellReturns
                        && phase <> 0  // skip 1/13 ticks

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
                            let a = atan2 (-threatDX) (-threatDY) * 180.0 / Math.PI
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

                    // Low enough to want a repair trip (and allowed one), with a
                    // base in sight, but the enemy is too close to dock: stop
                    // pursuing (aim, fire and dodge still) so the gap can open
                    let mayStartTrip =
                        p.Health <= cpuRepairHealth
                        && (p.RepairTick < 0 || tick - p.RepairTick >= CpuRepairCooldownTicks)
                    let holdBack =
                        not p.Repairing && mayStartTrip && dist < CpuRepairEnemyClearance
                        && (nearestRepairBase gs i px py).IsSome

                    let shouldThrust =
                        if blocked10 || boundaryBlocked then false
                        elif fallingFast then true  // always brake a fall
                        elif inWater then true  // escape water
                        elif blocked20 || blocked35 then
                            // Slow down — only thrust if speed is low
                            speed < 0.8
                        elif waterAhead then false  // don't fly into water
                        elif dodging then true  // thrust to dodge
                        elif holdBack then false  // let the enemy drift out of range
                        else
                            // Normal pursuit: thrust toward enemy, with personality aggression.
                            // A frozen enemy is a falling wrecking ball — keep clear instead
                            // of flying into the player-player collision fling.
                            let enemyFrozen = enemy.Flags.HasFlag PlayerFlags.Shield
                            let wantClose = not enemyFrozen && dist > 50.0 * (2.0 - personality.Aggression)
                            let tooClose = dist < (if enemyFrozen then 60.0 else 25.0)
                            (wantClose || phase < 8) && not tooClose

                    // ── Special weapon (DOWN key) ──
                    // Most specials fly straight or barely fall (the freezer
                    // floats), so they aim straight at the enemy; the bullet
                    // specials (machinegun, multicannon) share the cannon's
                    // arc, ship's motion included. Rubber bullets fly nearly
                    // straight but carry the ship's motion too: they lead the
                    // enemy by their own flight time, aim off the drift they
                    // pick up, and aim a little high for their light sag.
                    let arcAimed = aimed
                    let rubberAimed =
                        let t = dist / (getWeapon WeaponType.RubberBullets).ProjectileSpeed
                        let rx, ry = predictAt t
                        let ax = rx - p.VelX * BulletInheritVelocity * t - px
                        let ay = ry - p.VelY * BulletInheritVelocity * t - 0.5 * LightRoundGravity * t * t - py
                        let a = atan2 (-ax) (-ay) * 180.0 / Math.PI
                        abs (angleTo (if a < 0.0 then a + 360.0 else a)) < aimTol
                    let diff = angleTo directAngle
                    let aimed = abs diff < aimTol
                    // Mines and toxic pools stay where they are dropped and can
                    // hit their owner, so drop them only on the move, fast
                    // enough to be clear before they bite. A blackhole's pull
                    // reaches 48 px, so it needs a faster getaway.
                    let droppingClear = shipSpeed > 0.5
                    let useSpecial =
                        match p.SpecialWeapon with
                        | WeaponType.Magnofilter ->
                            // Toggle magno when enemy projectiles are nearby
                            threatCount > 0 && not (p.Flags.HasFlag PlayerFlags.Magno)
                        | WeaponType.Multicannon ->
                            // Use 360° burst when enemy is close (regular fire)
                            // Use tight burst (special) when aimed at range
                            arcAimed && dist > 60.0 && dist < 150.0 && phase % 5 = 0
                        | WeaponType.Sonicboom ->
                            // Use expanding ring when enemy very close
                            dist < 50.0 && phase % 4 = 0
                        | WeaponType.Blackhole ->
                            // Drop blackhole when enemy is close
                            shipSpeed > 1.0 && dist < 70.0 && phase % 6 = 0
                        | WeaponType.HellFire ->
                            // Wide flame spread when close
                            dist < 60.0 && aimed && phase % 3 = 0
                        | WeaponType.Machinegun ->
                            // Random offset shot for suppression
                            arcAimed && inRange && phase % 4 = 0
                        | WeaponType.Fan ->
                            // Fan arc when somewhat aimed
                            abs diff < 40.0 && dist < 120.0 && phase % 5 = 0
                        | WeaponType.Missile ->
                            // Missile recoil shot — use when aimed
                            aimed && dist > 80.0 && phase % 7 = 0
                        | WeaponType.Mine ->
                            // Drop mine when falling or enemy is very close behind
                            droppingClear
                            && ((abs diff > 120.0 && dist < 50.0) || (p.VelY > 0.5 && phase % 5 = 0))
                        | WeaponType.ToxicDump ->
                            // Drop toxic when the enemy is close (on the move)
                            droppingClear && dist < 40.0 && phase % 4 = 0
                        | WeaponType.RubberBullets ->
                            // Single bouncing shot when aimed
                            rubberAimed && inRange && phase % 5 = 0
                        | WeaponType.Troopers ->
                            // Seed the ground when the enemy is closing in
                            dist < 150.0 && phase % 6 = 0
                        | WeaponType.RearTurret ->
                            // Fires backwards — use when the enemy is behind us
                            abs diff > 120.0 && dist < 100.0 && phase % 4 = 0
                        | WeaponType.AtomWeapon ->
                            // Huge blast — sparing long-range aimed shots, well
                            // clear of the CPU's own ship
                            aimed && dist > 100.0 && phase % 9 = 0
                        | _ ->
                            // Remaining projectile specials (Dirtclod, Headspinner,
                            // Freezer, Nucleus, Dumbfire): aimed shot in range.
                            // CPUs only carry Enabled weapons, so this never
                            // fires an unimplemented one.
                            aimed && inRange && phase % 6 = 0

                    let fight =
                        { p with
                            KeyUp = shouldThrust
                            KeyLeft = turnLeft
                            KeyRight = turnRight
                            KeyFire = shouldFire
                            KeyDown = useSpecial
                            Repairing = false }

                    // ── Enemy behind terrain: fly into open space toward it ──
                    // When the line of sight to the enemy is blocked, head the
                    // open compass direction (openDirections) closest to the
                    // enemy's bearing instead of pushing into the rock. Not while
                    // dodging a crossfire, hanging back for repairs, or keeping
                    // clear of a frozen enemy; no open direction = as before.
                    // Worked out only when the fight keys are actually used.
                    //
                    // ── Shot held because it would fall back on us ──
                    // Enemy (nearly) straight overhead: slide sideways —
                    // away from the enemy's side, or the other way if that is
                    // walled — so the next shot can go up at an angle.
                    let fightWithDetour () =
                        let detour =
                            match gs.Level with
                            | Some level when wantedShotReturns && not (dodging && threatCount >= 2) ->
                                let away = if dx >= 0.0 then -1.0 else 1.0
                                let side = if clearPath level px py (px + away * 30.0) py then away else -away
                                Some(steerToBase level p px py (px + side * 30.0) py)
                            | Some level when
                                not (holdBack || dodging && threatCount >= 2)
                                && not (enemy.Flags.HasFlag PlayerFlags.Shield)
                                && not (inSight level px py ex ey) ->
                                let bearing =
                                    let a = atan2 (-dx) (-dy) * 180.0 / Math.PI
                                    if a < 0.0 then a + 360.0 else a
                                let offBearing (a: float) = abs ((a - bearing + 540.0) % 360.0 - 180.0)
                                openDirections level px py
                                |> List.sortBy offBearing
                                |> List.tryHead
                                |> Option.map (fun a ->
                                    let rad = degToRad (a + 90.0)
                                    steerToBase level p px py (px + cos rad * 60.0) (py - sin rad * 60.0))
                            | _ -> None
                        match detour with
                        | Some (left, right, thrust) -> { fight with KeyUp = thrust; KeyLeft = left; KeyRight = right }
                        | None -> fight

                    // ── Repair trip: dock on a base instead of chasing ──
                    // Docked, the CPU is a turret: it aims and fires at the
                    // enemy but never thrusts off the pad. An enemy too close
                    // to dock means fight.
                    let turret = { fight with KeyUp = false; KeyLeft = aimLeft; KeyRight = aimRight }
                    if dist < CpuRepairEnemyClearance then fightWithDetour ()
                    else
                        repairTrip gs i p px py mayStartTrip true shouldFire turret
                        |> Option.defaultWith fightWithDetour)
    { gs with Players = players }

// ─── Main Game Tick ────────────────────────────────────────────────────

let gameTick (gs: GameState) : GameState =
    let gs = { gs with GameTick = gs.GameTick + 1 }

    // CPU AI: set key states for computer-controlled players
    let gs = if gs.CpuCount > 0 then cpuAI gs else gs

    // Update all players — collect new entities and particles
    let playerResults =
        gs.Players
        |> List.mapi (fun i p -> if i < gs.NumPlayers then updatePlayer gs i else (p, [], [], false))
    let players     = playerResults |> List.map     (fun (p, _, _, _) -> p)
    let allNewEnts  = playerResults |> List.collect (fun (_, e, _, _) -> e)
    let allNewParts = playerResults |> List.collect (fun (_, _, pt, _) -> pt)

    // Update all entities — keep survivors, collect spawned entities and particles
    let entityResults = gs.Entities |> List.map (updateEntity gs)
    let survivingEnts = entityResults |> List.choose (fun (result, _, _, _) -> result)
    let spawnedEnts   = entityResults |> List.collect (fun (_, e, _, _) -> e)
    let spawnedParts  = entityResults |> List.collect (fun (_, _, pt, _) -> pt)

    // Terrain is dirty if it already was, or any player/entity update modified it this tick
    let terrainDirty =
        gs.TerrainDirty
        || List.exists (fun (_, _, _, d) -> d) playerResults
        || List.exists (fun (_, _, _, d) -> d) entityResults

    // Water flowing into freshly shot holes, a few pixels per tick
    let terrainDirty =
        match gs.Level with
        | Some level -> Terrain.stepWater level.Pixels || terrainDirty
        | None -> terrainDirty

    // Merge surviving + newly spawned entities
    let entities = survivingEnts @ allNewEnts @ spawnedEnts

    // Update particles, then append newly spawned ones
    let particles = (gs.Particles |> List.choose updateParticle) @ allNewParts @ spawnedParts

    // Blackhole pull pass
    let entities, players = applyBlackholePull entities players gs.NumPlayers

    // Magnofilter pull pass
    let entities = applyMagnoPull entities players gs.NumPlayers

    // Projectiles and blasts shoot down troopers
    let entities, trooperParts = checkTrooperHits gs.Rng entities
    let particles = particles @ trooperParts

    // Bullet-player collision
    let gs = { gs with Players = players; Entities = entities; Particles = particles; TerrainDirty = terrainDirty }
    let (players, entities, collisionParts, cDirty) = checkBulletPlayerCollision gs players entities
    let terrainDirty = if cDirty then true else terrainDirty

    // Player-player collision
    let players = checkPlayerCollision players gs.NumPlayers

    // Dead players: AnimAngle counts the ticks since death. With respawn on
    // (the default) the ship respawns after 90 ticks (~2.5 seconds); with
    // respawn off it stays dead for the rest of the round.
    let players =
        // Bases occupied by live players — a respawning ship must avoid all of them
        let occupied = players |> List.choose (fun p -> if p.Alive && p.SpawnIndex >= 0 then Some p.SpawnIndex else None)
        players |> List.mapi (fun i p ->
            if i < gs.NumPlayers && not p.Alive then
                let a = p.AnimAngle + 1.0
                if a > 90.0 && gs.RespawnOnDeath then
                    let spawned, _ = spawnPlayerExcluding gs.Rng gs.Level occupied p
                    spawned
                else
                    { p with AnimAngle = a }
            else p)

    // Last-man-standing: with respawn off in a multiplayer round, end the round
    // (back to the menu, like Escape) once at most one ship is left alive —
    // after the same ~2.5-second grace the respawn timer would have used, so
    // the final explosion plays out first.
    let roundActive =
        if gs.RespawnOnDeath || gs.NumPlayers < 2 then gs.RoundActive
        else
            let active = players |> List.truncate gs.NumPlayers
            let aliveCount = active |> List.filter (fun p -> p.Alive) |> List.length
            let graceOver = active |> List.forall (fun p -> p.Alive || p.AnimAngle > 90.0)
            if aliveCount <= 1 && graceOver then false else gs.RoundActive

    // TerrainDirty is consumed by the renderer on the next frame;
    // it must NOT be cleared here — the renderer checks it once and patches
    // the terrain bitmap (using the dirty rect accumulated in Terrain),
    // then the host clears it after rendering.
    { gs with
        Players = players
        Entities = entities
        Particles = particles @ collisionParts
        RoundActive = roundActive
        TerrainDirty = terrainDirty }

// ─── Init Round ────────────────────────────────────────────────────────

let initRound (gs: GameState) : GameState =
    // Reload level terrain from disk to reset any ammo damage. Desktop only —
    // the Fable/web host has no file system and resets terrain from a pristine
    // in-memory copy before calling initRound.
    let gs =
#if FABLE_COMPILER
        gs
#else
        if gs.LevelFilePath <> "" && System.IO.File.Exists gs.LevelFilePath then
            let freshLevel = Terrain.loadLevel gs.LevelFilePath
            // Wholesale pixel change: the renderer must repaint the whole
            // terrain bitmap, not just the accumulated crater rect.
            Terrain.markAllDirty ()
            { gs with Level = Some freshLevel; TerrainDirty = true }
        else gs
#endif

    let cpuStart = gs.NumPlayers - gs.CpuCount  // First CPU player index
    // Thread the set of occupied base indices through the players so each one spawns
    // onto a base not already taken by an earlier player this round.
    let players, _ =
        gs.Players
        |> List.indexed
        |> List.mapFold (fun occupied (i, p) ->
            if i < gs.NumPlayers then
                let isCpu = i >= cpuStart
                let spawned, idx = spawnPlayerExcluding gs.Rng gs.Level occupied p
                let spawned =
                    { spawned with
                        WeaponType = WeaponType.Cannon; Ammo = 999; KillCount = 0; DeathCount = 0
                        ReloadTimer = 0; SpecialReloadTimer = 0; IsCpu = isCpu }
                // CPU players open each round with a random special weapon
                // (humans keep their previous pick)
                let spawned =
                    if isCpu then { spawned with SpecialWeapon = randomSpecialWeapon gs.Rng spawned.SpecialWeapon }
                    else spawned
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
