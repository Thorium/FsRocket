/// FsRocket - Weapon Definitions
/// damage from BulletHitPlayer, collision radii, projectile speeds from entity update
module FsRocket.Weapons

// ─── Entity Types (from BulletHitPlayer switch at 0000:5425) ───────────

type EntityType =
    | None           = 0
    | Bullet         = 1    // standard bullet / multicannon
    | BulletAlt      = 6    // rear turret bullet (same damage as $01)
    | Mine           = 7    // proximity mine
    | NoOp           = 8    // placeholder / no-op
    | Exploding      = 9    // exploding projectile (dirtclod)
    | EMP            = 10   // headspinner/EMP stun
    | Shield         = 11   // shield / nucleus orbiter
    | Ricochet       = 12   // rubber bullet (bounces off walls)
    | PassThrough    = 14   // pass-through (clears shield on hit)
    | Laser          = 15   // laser beam (persists through targets)
    | Heavy          = 16   // heavy cannon (damage decreases with flight time)
    | Bouncing       = 17   // bouncing bullet
    | Flame          = 18   // hell fire / flame (gravity-affected)
    | Nuke           = 19   // atom weapon / nuke (massive radius)
    | Railgun        = 20   // railgun (instant, high damage)
    | PlayerCollide  = 21   // player-to-player collision entity
    | Blackhole      = 22   // gravity well (pulls entities + players)
    | Shrapnel       = 23   // explosion shrapnel debris
    | Decel          = 32   // decelerating projectile
    | Expanding      = 33   // expanding entity (sonicboom ring)

// ─── Weapon Types (indices into weapons[] array) ────────────────────────

type WeaponType =
    | NoWeapon      = 0
    | Cloaker       = 1
    | Magnofilter   = 2
    | RearTurret    = 3
    | Multicannon   = 4
    | RubberBullets = 5
    | Mine          = 6
    | Nucleus       = 7
    | Dirtclod      = 8
    | Headspinner   = 9
    | Freezer       = 10
    | AtomWeapon    = 11
    | Troopers      = 12
    | HellFire      = 13
    | Machinegun    = 14
    | Sonicboom     = 15
    | Fan           = 16
    | ToxicDump     = 17
    | Dumbfire      = 18
    | Missile       = 19
    | Blackhole     = 20

// ─── Weapon Info Record ────────────────────────────────────────────────

type WeaponInfo =
    { Name: string
      /// Cost = reload timer in game ticks
      ReloadTicks: int
      /// Damage dealt on hit
      Damage: int
      /// Collision radius in internal units (default 96)
      CollisionRadius: int
      /// Projectile speed multiplier (applied to trig direction vector)
      ProjectileSpeed: float
      /// Entity type spawned
      EntityType: EntityType
      /// Whether this weapon is implemented and available for cycling
      Enabled: bool }

// ─── The 21 Weapons (indices 0..20, weapon type is 1-based in code) ────
// Name and cost from DS:0008 table (15-byte entries)
// Damage from BulletHitPlayer switch
// Collision radii from entity type checks
// Projectile speeds from entity creation code

let weapons = [|
    // #0 — NONE (placeholder)
    { Name = "NONE";         ReloadTicks = 1;   Damage = 0;  CollisionRadius = 96
      ProjectileSpeed = 0.0;   EntityType = EntityType.None;   Enabled = false }

    // #1 — CLOAKER (invisibility, no projectile — not yet implemented)
    { Name = "CLOAKER";      ReloadTicks = 1;   Damage = 0;  CollisionRadius = 96
      ProjectileSpeed = 0.0;   EntityType = EntityType.None;   Enabled = false }

    // #2 — MAGNOFILTER (no projectile — utility, attracts pickups)
    { Name = "MAGNOFILTER";  ReloadTicks = 1;   Damage = 0;  CollisionRadius = 96
      ProjectileSpeed = 0.0;   EntityType = EntityType.None;   Enabled = true }

    // #3 — REAR TURRET (fires behind, same as bullet)
    { Name = "REAR TURRET";  ReloadTicks = 5;   Damage = 2;  CollisionRadius = 96
      ProjectileSpeed = 4.0;   EntityType = EntityType.BulletAlt; Enabled = true }

    // #4 — MULTICANNON (3-way spread, 2.2 degree offset)
    { Name = "MULTICANNON";  ReloadTicks = 8;   Damage = 2;  CollisionRadius = 96
      ProjectileSpeed = 4.0;   EntityType = EntityType.Bullet; Enabled = true }

    // #5 — RUBBER BLTS (24 bullets in full circle, 15° apart)
    { Name = "RUBBER BLTS";  ReloadTicks = 10;  Damage = 2;  CollisionRadius = 96
      ProjectileSpeed = 3.5;   EntityType = EntityType.Ricochet; Enabled = true }

    // #6 — MINE (proximity, larger radius, arms after 25 ticks)
    { Name = "MINE";         ReloadTicks = 30;  Damage = 30; CollisionRadius = 128
      ProjectileSpeed = 0.0;   EntityType = EntityType.Mine;   Enabled = true }

    // #7 — NUCLEUS (orbiters around player)
    { Name = "NUCLEUS";      ReloadTicks = 10;  Damage = 2;  CollisionRadius = 96
      ProjectileSpeed = 4.0;   EntityType = EntityType.Shield; Enabled = true }

    // #8 — DIRTCLOD (lobbed with gravity, explodes on impact)
    { Name = "DIRTCLOD";     ReloadTicks = 25;  Damage = 4;  CollisionRadius = 96
      ProjectileSpeed = 3.0;   EntityType = EntityType.Exploding; Enabled = true }

    // #9 — HEADSPINNER (EMP/stun, adds 180 ticks of stun)
    { Name = "HEADSPINNER";  ReloadTicks = 35;  Damage = 0;  CollisionRadius = 96
      ProjectileSpeed = 3.5;   EntityType = EntityType.EMP;    Enabled = true }

    // #10 — FREEZER (creates shield entity on target)
    { Name = "FREEZER";      ReloadTicks = 50;  Damage = 0;  CollisionRadius = 128
      ProjectileSpeed = 3.5;   EntityType = EntityType.Shield; Enabled = true }

    // #11 — ATOM WEAPON (nuke — travels as heavy projectile, detonates on impact)
    { Name = "ATOM WEAPON";  ReloadTicks = 250; Damage = 15; CollisionRadius = 256
      ProjectileSpeed = 3.5;   EntityType = EntityType.Heavy;  Enabled = true }

    // #12 — TROOPERS (ground units that fall and shoot — not yet implemented)
    { Name = "TROOPERS";     ReloadTicks = 50;  Damage = 2;  CollisionRadius = 96
      ProjectileSpeed = 3.0;   EntityType = EntityType.Bullet; Enabled = false }

    // #13 — HELL FIRE (rapid flame trail, gravity-affected)
    { Name = "HELL FIRE";    ReloadTicks = 1;   Damage = 1;  CollisionRadius = 96
      ProjectileSpeed = 3.5;   EntityType = EntityType.Flame;  Enabled = true }

    // #14 — MACHINEGUN (rapid fire standard bullet)
    { Name = "MACHINEGUN";   ReloadTicks = 4;   Damage = 2;  CollisionRadius = 96
      ProjectileSpeed = 5.0;   EntityType = EntityType.Bullet; Enabled = true }

    // #15 — SONICBOOM (expanding ring, high damage)
    { Name = "SONICBOOM";    ReloadTicks = 120; Damage = 15; CollisionRadius = 96
      ProjectileSpeed = 0.0;   EntityType = EntityType.Expanding; Enabled = true }

    // #16 — FAN (push effect, no damage — not yet implemented)
    { Name = "FAN";          ReloadTicks = 1;   Damage = 0;  CollisionRadius = 96
      ProjectileSpeed = 6.0;   EntityType = EntityType.None;   Enabled = false }

    // #17 — TOXIC DUMP (area denial, persisting flame pool)
    { Name = "TOXIC DUMP";   ReloadTicks = 90;  Damage = 1;  CollisionRadius = 96
      ProjectileSpeed = 0.0;   EntityType = EntityType.Flame;  Enabled = true }

    // #18 — DUMBFIRE (fast unguided rocket, large radius)
    { Name = "DUMBFIRE";     ReloadTicks = 60;  Damage = 6;  CollisionRadius = 192
      ProjectileSpeed = 6.0;   EntityType = EntityType.Heavy;  Enabled = true }

    // #19 — MISSILE (homing, turns toward nearest enemy)
    { Name = "MISSILE";      ReloadTicks = 90;  Damage = 8;  CollisionRadius = 192
      ProjectileSpeed = 4.0;   EntityType = EntityType.Heavy;  Enabled = true }

    // #20 — BLACKHOLE (gravity well, pulls players + entities, 1536px search)
    { Name = "BLACKHOLE";    ReloadTicks = 180; Damage = 2;  CollisionRadius = 256
      ProjectileSpeed = 0.0;   EntityType = EntityType.Blackhole; Enabled = true }
|]

/// Get weapon by WeaponType enum
let getWeapon (wt: WeaponType) =
    let idx = int wt
    if idx >= 0 && idx < weapons.Length then weapons[idx]
    else weapons[0]

/// Standard bullet damage (entity types $01/$06)
let bulletDamage = 2

/// Heavy cannon damage formula: 6 - (timer / 4), minimum 1
let heavyDamage (timer: int) = max 1 (6 - timer / 4)

/// Flame collision radius is dynamic: (timer - 3) << 5
let flameRadius (timer: int) = max 0 ((timer - 3) <<< 5)

/// Ricochet max bounces before deactivation
let ricochetMaxBounces = 3

/// Nuke blast radius in pixels (expands from 0 to this over lifetime)
let nukeBlastRadius = 80.0

/// Blackhole gravity pull radius in pixels (~1536 internal / 32)
let blackholeRadius = 48.0

/// Blackhole gravity strength
let blackholeStrength = 0.15

/// Expanding entity (sonicboom) max radius
let expandingMaxRadius = 120.0

/// Expanding entity growth rate per tick
let expandingGrowthRate = 3.0

/// Laser beam length in pixels
let laserLength = 200.0

/// Missile homing turn rate (degrees per tick)
let missileHomingRate = 4.0
