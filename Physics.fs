/// FsRocket Physics Constants
/// Game influence from other rocket games, mainly:
// Jaakko Lyytinen (A-More) / The Kudos, 1995
// Tcpippeli (Ville Kujala)
// TurboRaketti
// Fuse, etc.
module FsRocket.Physics

open System

// ─── Movement Physics (from CS:A674h..A6BCh) ───────────────────────────

/// Thrust acceleration per frame when UP key held (CS:A674h, Real80 = 0.1)
[<Literal>]
let ThrustAccel = 0.1

/// Gravity acceleration applied unconditionally every frame (CS:A68Ah, Real80 = 0.02)
[<Literal>]
let GravityAccel = 0.02

/// Friction deceleration per frame on water terrain - constant subtraction, NOT multiplicative
/// (CS:A6A8h, Real80 = -0.09)  Applied to VelY (and mirrored for VelX)
[<Literal>]
let FrictionDecel = -0.06

/// Maximum velocity magnitude when thrusting (CS:A67Eh/A682h, Float32 = ±2.0)
[<Literal>]
let MaxVelocity = 2.0

/// Maximum velocity magnitude during friction/decel mode (CS:A6B2h/A6BCh, Real80 = ±0.6)
[<Literal>]
let FrictionMaxVel = 1.0

/// Turning speed: 8 degrees per tick (from ADD AX, 8 / SUB AX, 8 at 0000:ABC0/ABE8)
[<Literal>]
let TurnSpeed = 8.0

/// Direction range: 0..360 degrees
[<Literal>]
let MaxAngle = 360.0

// ─── Knockback / Shield (from CS:A6C6h..A6D0h) ────────────────────────

/// Knockback velocity divisor when shield is active (CS:A6C6h, Real80 = 4.8)
[<Literal>]
let ShieldKnockbackScale = 4.8

/// Knockback velocity divisor without shield (CS:A6D0h, Real80 = 2.4)
[<Literal>]
let NormalKnockbackScale = 2.4

/// Bullet knockback: velocity / 10 (from BulletHitPlayer, entity type $01/$06)
[<Literal>]
let BulletKnockbackDiv = 10.0

// ─── Drunk/Disoriented Effect ──────────────────────────────────────────

/// Drunk wobble force magnitude (CS:A694h/A69Eh, Real80 = ±1.2)
[<Literal>]
let DrunkForce = 1.2

/// Drunk wobble range: Random(90°) with ±45° offset
[<Literal>]
let DrunkWobbleRange = 90.0

// ─── Position / Velocity Scaling ───────────────────────────────────────

/// Internal positions are multiplied by 32 for pixel conversion (CS:8744h, Float32 = 32)
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

/// Invincibility timer on spawn: 16 ticks
[<Literal>]
let SpawnInvincibilityTicks = 16

/// Stun duration added per stun hit: 180 ticks ($B4)
[<Literal>]
let StunDurationPerHit = 180

// ─── Health ────────────────────────────────────────────────────────────

/// Full health value ($5A = 90)
[<Literal>]
let FullHealth = 90

/// Death threshold (health <= 0 means dead)
[<Literal>]
let DeathThreshold = 0

// ─── Entity Pool Sizes ─────────────────────────────────────────────────

let MaxEntities = 152
let MaxBullets = 48
let MaxParticles = 48
let MaxExplosions = 10

// ─── Initial Spawn ─────────────────────────────────────────────────────

/// Initial direction at spawn: 90 degrees
[<Literal>]
let SpawnDirection = 90.0

// ─── Degrees to Radians helper ─────────────────────────────────────────

let inline degToRad (deg: float) = deg * Math.PI / 180.0
let inline radToDeg (rad: float) = rad * 180.0 / Math.PI

/// Clamp using idiomatic max/min (works with all comparable types)
let inline clampF lo hi v = max lo (min hi v)


