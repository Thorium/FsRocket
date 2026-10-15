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
    | Trooper        = 24   // trooper ground unit (falls, digs in, fires at the sky)
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
    | Cannon        = 21

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
// Name and cost 
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
    { Name = "REAR TURRET";  ReloadTicks = 5;   Damage = 4;  CollisionRadius = 96
      ProjectileSpeed = 4.0;   EntityType = EntityType.BulletAlt; Enabled = true }

    // #4 — MULTICANNON (burst spread; per-bullet damage kept low and reload long
    // so a full-burst hit stays a surprise punish, not a main gun)
    { Name = "MULTICANNON";  ReloadTicks = 10;  Damage = 3;  CollisionRadius = 96
      ProjectileSpeed = 4.0;   EntityType = EntityType.Bullet; Enabled = true }

    // #5 — RUBBER BLTS (24 bullets in full circle, 15° apart)
    { Name = "RUBBER BLTS";  ReloadTicks = 10;  Damage = 2;  CollisionRadius = 96
      ProjectileSpeed = 3.5;   EntityType = EntityType.Ricochet; Enabled = true }

    // #6 — MINE (proximity, larger radius, arms after 25 ticks)
    { Name = "MINE";         ReloadTicks = 30;  Damage = 30; CollisionRadius = 128
      ProjectileSpeed = 0.0;   EntityType = EntityType.Mine;   Enabled = true }

    // #7 — NUCLEUS (slow drifting freeze orb — area denial in front of the ship)
    { Name = "NUCLEUS";      ReloadTicks = 30;  Damage = 2;  CollisionRadius = 96
      ProjectileSpeed = 1.2;   EntityType = EntityType.Shield; Enabled = true }

    // #8 — DIRTCLOD (lobbed with gravity, explodes on impact)
    { Name = "DIRTCLOD";     ReloadTicks = 25;  Damage = 4;  CollisionRadius = 96
      ProjectileSpeed = 3.0;   EntityType = EntityType.Exploding; Enabled = true }

    // #9 — HEADSPINNER (EMP/stun, adds StunDurationPerHit ticks of stun; slow
    // reload — a stun is a strong setup, not a spammable lockdown)
    { Name = "HEADSPINNER";  ReloadTicks = 70;  Damage = 0;  CollisionRadius = 96
      ProjectileSpeed = 3.5;   EntityType = EntityType.EMP;    Enabled = true }

    // #10 — FREEZER (creates shield entity on target)
    { Name = "FREEZER";      ReloadTicks = 30;  Damage = 0;  CollisionRadius = 128
      ProjectileSpeed = 3.5;   EntityType = EntityType.Shield; Enabled = true }

    // #11 — ATOM WEAPON (nuke — travels as heavy projectile, detonates on impact)
    { Name = "ATOM WEAPON";  ReloadTicks = 250; Damage = 15; CollisionRadius = 256
      ProjectileSpeed = 3.5;   EntityType = EntityType.Heavy;  Enabled = true }

    // #12 — TROOPERS (lobbed ground units that fall, dig in and fire at the sky;
    // Damage is the damage of the bullets each trooper fires)
    { Name = "TROOPERS";     ReloadTicks = 120; Damage = 3;  CollisionRadius = 96
      ProjectileSpeed = 2.5;   EntityType = EntityType.Trooper; Enabled = true }

    // #13 — HELL FIRE (rapid flame trail, gravity-affected)
    { Name = "HELL FIRE";    ReloadTicks = 1;   Damage = 1;  CollisionRadius = 96
      ProjectileSpeed = 3.5;   EntityType = EntityType.Flame;  Enabled = true }

    // #14 — MACHINEGUN (rapid fire, weak per-bullet — suppression, not a main gun)
    { Name = "MACHINEGUN";   ReloadTicks = 6;   Damage = 4;  CollisionRadius = 96
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
    { Name = "MISSILE";      ReloadTicks = 90;  Damage = 12;  CollisionRadius = 192
      ProjectileSpeed = 4.0;   EntityType = EntityType.Heavy;  Enabled = true }

    // #20 — BLACKHOLE (gravity well, pulls players + entities, 1536px search)
    { Name = "BLACKHOLE";    ReloadTicks = 180; Damage = 2;  CollisionRadius = 256
      ProjectileSpeed = 0.0;   EntityType = EntityType.Blackhole; Enabled = true }

    // #21 — CANNON (the always-available main gun, ~0.3 second reload; hits
    // hardest per bullet so the specials stay support weapons)
    { Name = "CANNON";       ReloadTicks = 12;  Damage = 8;  CollisionRadius = 96
      ProjectileSpeed = 4.0;   EntityType = EntityType.Bullet; Enabled = true }
|]

/// Get weapon by WeaponType enum
let getWeapon (wt: WeaponType) =
    let idx = int wt
    if idx >= 0 && idx < weapons.Length then weapons[idx]
    else weapons[0]

/// Standard bullet damage (entity types $01/$06) — legacy flat value; bullet
/// collisions now use the per-weapon Damage from the table above.
[<Literal>]
let bulletDamage = 5

/// Atom round arming time: the heavy projectile must fly this many ticks
/// before a wall or ship impact detonates the nuke. Earlier hits fizzle, so
/// the nuke can't be point-blank detonated in someone's (or your own) face.
[<Literal>]
let atomArmTicks = 25

/// Trooper: ticks between the upward shots of a dug-in trooper
[<Literal>]
let trooperFireInterval = 36

/// Trooper: total lifetime in ticks before the unit expires
[<Literal>]
let trooperLifeTicks = 450

/// Heavy cannon damage formula: 6 - (timer / 4), minimum 1
let heavyDamage (timer: int) = max 1 (6 - timer / 4)

/// Flame collision radius is dynamic: (timer - 3) << 5
let flameRadius (timer: int) = max 0 ((timer - 3) <<< 5)

/// Ricochet max bounces before deactivation
[<Literal>]
let ricochetMaxBounces = 3

/// Nuke blast radius in pixels (expands from 0 to this over lifetime)
[<Literal>]
let nukeBlastRadius = 64.0

/// Blackhole gravity pull radius in pixels (~1536 internal / 32)
[<Literal>]
let blackholeRadius = 48.0

/// Blackhole gravity strength
[<Literal>]
let blackholeStrength = 0.2

/// Expanding entity (sonicboom) max radius
[<Literal>]
let expandingMaxRadius = 120.0

/// Expanding entity growth rate per tick
[<Literal>]
let expandingGrowthRate = 3.0

/// Laser beam length in pixels
[<Literal>]
let laserLength = 200.0

/// Missile homing turn rate (degrees per tick)
[<Literal>]
let missileHomingRate = 4.0
