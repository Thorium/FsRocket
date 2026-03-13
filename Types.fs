/// FsRocket — Immutable Game Types
/// All record types are pure data with no mutable fields.
/// Game logic produces new records via { old with Field = value } copy expressions.
module FsRocket.Types

open System
open FsRocket.Weapons

// ─── Player Flags ─────────────────────────

[<Flags>]
type PlayerFlags =
    | None       = 0uy
    | Magno      = 0x01uy   // Magnofilter active
    | Active     = 0x08uy   // Player is in the game
    | Shield     = 0x10uy   // Shield active (blocks ALL input)
    | Drunk      = 0x20uy   // Disoriented / drunk effect
    | Stunned    = 0x40uy   // Stunned / cloaked

// ─── Player Record ────────────────────────────────────────────────────

type Player =
    { PosX: float               // Internal position (÷32 for pixels)
      PosY: float
      Angle: float              // Direction 0..360
      VelX: float               // Velocity in internal units
      VelY: float
      Flags: PlayerFlags
      Health: int
      WeaponType: WeaponType      // Main weapon — always Cannon (fire key)
      SpecialWeapon: WeaponType   // Special weapon — selected via F1-F4 (DOWN key)
      ReloadTimer: int          // Countdown to next cannon shot
      SpecialReloadTimer: int   // Countdown to next special weapon shot
      KeyUp: bool               // Key states set by input
      KeyLeft: bool
      KeyRight: bool
      KeyFire: bool
      KeyDown: bool
      Ammo: int
      Color: int                // Player color index
      ShotCount: int            // Total shots fired
      WallHitCount: int         // Wall collision damage count
      CloakAngle: float         // Cloaker distortion
      StunTimer: int            // Stun countdown
      Alive: bool
      AnimAngle: float          // Animation timer (+=2, wraps 360)
      BlackholeCounter: int     // Blackhole weapon counter (1..5)
      InvTimer: int             // Invincibility timer on spawn (protects from bullets)
      WallDmgCooldown: int      // Cooldown after wall damage (does NOT protect from bullets)
      KillCount: int            // Kills (for scoreboard)
      DeathCount: int           // Deaths (for scoreboard)
      IsCpu: bool }             // Is this a CPU-controlled player

// ─── Entity Record ────────────────────────────────────────────────────

type Entity =
    { X: float                  // Position
      Y: float
      Timer: int                // Angle/timer
      SubType: int              // Bounce count for ricochet, etc.
      VelX: float               // Velocity
      VelY: float
      EType: EntityType         // Entity type
      Owner: int                // Owner player index
      Radius: float             // Expanding entities: current blast/effect radius
      WeaponIdx: WeaponType }   // Which weapon spawned this

// ─── Particle (for death debris / explosions) ─────────────────────────
[<Struct>]
type Particle =
    { X: float
      Y: float
      VelX: float
      VelY: float
      Life: int
      Color: int }

// ─── Arena Walls (obstacles inside the arena) ─────────────────────────
[<Struct>]
type Wall =
    { X: float; Y: float; W: float; H: float }

// ─── Game State ───────────────────────────────────────────────────────

type GameState =
    { Players: Player list
      Entities: Entity list
      Particles: Particle list
      Rng: Random
      NumPlayers: int
      CpuCount: int               // Number of CPU-controlled players (last N players)
      GameTick: int
      RoundActive: bool
      Level: Terrain.LevelData option
      LevelFilePath: string       // Full path to current .LEV file (empty = no terrain)
      TerrainDirty: bool }
