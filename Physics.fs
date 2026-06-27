// FsRocket Physics Constants
// Game influence from other rocket games, mainly:
// Jaakko Lyytinen (A-More) / The Kudos, 1995
// Tcpippeli (Ville Kujala)
// TurboRaketti
// Fuse, etc.
module FsRocket.Physics

open System

// ─── Movement Physics ───────────────────────────

/// Thrust acceleration per frame when UP key held
[<Literal>]
let ThrustAccel = 0.1

/// Gravity acceleration applied unconditionally every frame
[<Literal>]
let GravityAccel = 0.02

/// Friction deceleration per frame on water terrain - constant subtraction, NOT multiplicative
/// Applied to VelY (and mirrored for VelX)
[<Literal>]
let FrictionDecel = -0.06

/// Maximum velocity magnitude when thrusting
[<Literal>]
let MaxVelocity = 2.0

/// Maximum velocity magnitude during friction/decel mode
[<Literal>]
let FrictionMaxVel = 1.0

/// Turning speed: 8 degrees per tick
[<Literal>]
let TurnSpeed = 8.0

/// Direction range: 0..360 degrees
[<Literal>]
let MaxAngle = 360.0

// ─── Knockback / Shield ────────────────────────

/// Knockback velocity divisor when shield is active
[<Literal>]
let ShieldKnockbackScale = 4.8

/// Knockback velocity divisor without shield
[<Literal>]
let NormalKnockbackScale = 2.4

/// Impact-speed multiplier for terrain/boundary collision damage. Kept low so a
/// crash hurts less than taking direct fire — see [[bulletDamage]].
[<Literal>]
let CollisionDamageScale = 1.5

/// Collision damage multiplier while shielded (terrain contact also clears the
/// shield). Doubled relative to the normal scale, mirroring the knockback ratio.
[<Literal>]
let ShieldCollisionDamageScale = 3.0

/// Bullet knockback: velocity / 10
[<Literal>]
let BulletKnockbackDiv = 10.0

// ─── Drunk/Disoriented Effect ──────────────────────────────────────────

/// Drunk wobble force magnitude
[<Literal>]
let DrunkForce = 1.2

/// Drunk wobble range: Random(90°) with ±45° offset
[<Literal>]
let DrunkWobbleRange = 90.0

// ─── Position / Velocity Scaling ───────────────────────────────────────

/// Internal positions are multiplied by 32 for pixel conversion
[<Literal>]
let PositionScale = 32.0

// ─── Arena / Viewport ──────────────────────────────────────────────────

/// Arena pixel dimensions — single source of truth for map size
let MapWidth = 320
let MapHeight = 400

/// Arena pixel dimensions as float (for physics calculations)
let ArenaWidth = float MapWidth
let ArenaHeight = float MapHeight

/// Per-player viewport size (156x86 pixels)
let ViewportWidth = 156
let ViewportHeight = 86

// ─── Timing ────────────────────────────────────────────────────────────

/// Default FPS target
[<Literal>]
let DefaultFPS = 36.0

/// Invincibility timer on spawn
[<Literal>]
let SpawnInvincibilityTicks = 16

/// Stun duration added per stun hit
[<Literal>]
let StunDurationPerHit = 180

// ─── Health ────────────────────────────────────────────────────────────

/// Full health value
[<Literal>]
let FullHealth = 90

/// Death threshold (health <= 0 means dead)
[<Literal>]
let DeathThreshold = 0

/// Health recovered per heal tick while resting on a base/landing pad
[<Literal>]
let BaseHealRate = 1

/// Heal only every Nth game tick while parked on a base. At 36 FPS an interval of
/// 18 is ~2 HP/sec — a slow trickle that any sustained direct fire easily out-damages,
/// so a parked ship can still be killed.
[<Literal>]
let BaseHealInterval = 18

/// Distance (pixels) below the ship centre scanned for a base bar — the ship
/// rests with its centre in the void just above the pad surface.
[<Literal>]
let BaseLandReach = 3.0

/// Speed (|vx|+|vy|) below which a ship over a base counts as "parked" (so it can
/// heal and switch weapons). Faster than this = flying past, not landed.
[<Literal>]
let BaseLandSpeed = 1.0

// ─── Entity Pool Sizes ─────────────────────────────────────────────────

let MaxEntities = 152
let MaxBullets = 48
let MaxParticles = 48
let MaxExplosions = 10

// ─── Initial Spawn ─────────────────────────────────────────────────────

/// Initial direction at spawn: 0 degrees = nose up (thrust/fire/render all use Angle+90
/// in screen space, so Angle 0 gives the up vector (0,-1)).
[<Literal>]
let SpawnDirection = 0.0

// ─── Degrees to Radians helper ─────────────────────────────────────────

let inline degToRad (deg: float) = deg * Math.PI / 180.0
let inline radToDeg (rad: float) = rad * 180.0 / Math.PI

/// Clamp using idiomatic max/min (works with all comparable types)
let inline clampF lo hi v = max lo (min hi v)


