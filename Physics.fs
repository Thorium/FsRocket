/// FsRocket Physics Constants
/// Game influence from other rocket games, mainly:
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
/// shield). 4x the normal scale: the Shield flag is what the FREEZER inflicts,
/// so a frozen ship falls HARD — getting frozen mid-air is a real threat.
[<Literal>]
let ShieldCollisionDamageScale = 6.0

/// Bullet knockback: velocity / 10
[<Literal>]
let BulletKnockbackDiv = 10.0

// ─── Friendly Fire ─────────────────────────────

/// Fraction of a projectile's damage its own shooter takes. Area weapons
/// (nuke, sonicboom, mine, blackhole, toxic pools) are dangerous to everyone,
/// including the ship that fired them.
[<Literal>]
let FriendlyFireDamageScale = 0.4

/// A projectile cannot hit its own shooter during its first ticks — it spawns
/// at the ship's position and needs time to clear the hull, otherwise every
/// shot would be instant self-damage.
[<Literal>]
let SelfHitGraceTicks = 12

// ─── Firing Recoil / Bullet Momentum ───────────────────────────────────

/// Backwards kick applied to the firing ship on a special-weapon shot
/// (player-velocity units = world px/tick; cf. ThrustAccel = 0.1).
[<Literal>]
let SpecialFireRecoil = 0.3

/// Main-cannon shots kick the firer at this fraction of SpecialFireRecoil —
/// a light nudge (~1 tick of thrust), noticeable but not disruptive.
[<Literal>]
let CannonRecoilFraction = 0.3

/// Momentum model for plain-bullet hits: treating the firing recoil as 100%
/// of the bullet's momentum, the ship it strikes receives this fraction as a
/// push in the bullet's travel direction. Full value at muzzle speed, scaled
/// down proportionally when the bullet has slowed by impact time.
[<Literal>]
let BulletHitImpulseFraction = 0.8

/// A bullet hit unseats a ship parked on a base: lift in pixels applied to
/// the victim so it clears the pad's rest-snap, which re-seats and zeroes
/// the velocity of any non-thrusting ship within BaseLandReach (3px) of a
/// pad every tick. Must exceed BaseLandReach or the whole kick is cancelled
/// on the next tick.
[<Literal>]
let BasedHitUnseatLift = 4.0

/// Upward velocity kick for a based victim, as a fraction of the bullet's
/// hit-impulse magnitude (harder hits pop the ship higher).
[<Literal>]
let BasedHitUpwardKickScale = 0.75

/// Floor for the based-victim upward kick (px/tick) — two thrust-ticks, so
/// even a weak cannon hit keeps the ship airborne long enough to slide.
[<Literal>]
let BasedHitUpwardKickMin = 0.2

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
[<Literal>]
let MapWidth = 320
[<Literal>]
let MapHeight = 400

/// Arena pixel dimensions as float (for physics calculations)
let ArenaWidth = float MapWidth
let ArenaHeight = float MapHeight

/// Per-player viewport size (156x86 pixels)
[<Literal>]
let ViewportWidth = 156
[<Literal>]
let ViewportHeight = 86

// ─── Timing ────────────────────────────────────────────────────────────

/// Default FPS target
[<Literal>]
let DefaultFPS = 36.0

/// Invincibility timer on spawn
[<Literal>]
let SpawnInvincibilityTicks = 16

/// Stun duration added per stun hit (~1.5 s at 36 FPS). Long enough to set up
/// a punish, short enough that one EMP tag is not a guaranteed kill.
[<Literal>]
let StunDurationPerHit = 55

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

[<Literal>]
let MaxEntities = 152
[<Literal>]
let MaxBullets = 48
[<Literal>]
let MaxParticles = 48
[<Literal>]
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


