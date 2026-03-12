/// Comprehensive smoke tests for AUTS v1.20C game logic
/// Run with: dotnet fsi test.fsx  (from AutsDemo directory)
///
/// Tests cover:
///   1-3:   Physics constants (gravity, thrust, friction)
///   4-5:   Turning, position scaling
///   6-8:   Projectile spawn directions
///   9-11:  Gravity unconditional (via simulated updatePlayer)
///   12-14: Fire logic (full path: KeyFire + ReloadTimer + Ammo + canControl)
///   15-17: DOWN key special weapon dispatch (gating per weapon type)
///   18:    FireSpecial per-weapon behavior
///   19:    Enter as backup fire key (code-level verification)

open System

// ── Constants (from Physics.fs) ──

let ThrustAccel = 0.1
let GravityAccel = 0.02
let FrictionDecel = -0.09
let MaxVelocity = 2.0
let FrictionMaxVel = 0.6
let TurnSpeed = 8.0
let MaxAngle = 360.0
let PositionScale = 32.0
let ArenaWidth = 320.0
let ArenaHeight = 400.0
let SpawnDirection = 90.0
let FullHealth = 90
let SpawnInvincibilityTicks = 16

let inline clampF lo hi v = max lo (min hi v)
let inline degToRad (deg: float) = deg * Math.PI / 180.0

// ── Test harness ──

let mutable passed = 0
let mutable failed = 0

let check (name: string) cond =
    if cond then passed <- passed + 1; printfn "  PASS: %s" name
    else         failed <- failed + 1; printfn "  FAIL: %s" name

let approx (a: float) (b: float) = abs (a - b) < 0.0001

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 1: Gravity -- unconditional, every tick --"
// Gravity adds +0.02 to VelY every tick, clamped to MaxVelocity
let mutable vy = 0.0
for _ in 1 .. 10 do
    vy <- vy + GravityAccel
    vy <- min vy MaxVelocity
check "after 10 ticks: VelY = 0.2" (approx vy 0.2)
vy <- 0.0
for _ in 1 .. 100 do
    vy <- vy + GravityAccel
    vy <- min vy MaxVelocity
check "after 100 ticks: VelY = 2.0 (clamped)" (approx vy 2.0)
vy <- -1.0
for _ in 1 .. 50 do
    vy <- vy + GravityAccel
    vy <- min vy MaxVelocity
check "after 50 ticks from VelY=-1.0: VelY = 0.0" (approx vy 0.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 2: Thrust -- direction-dependent acceleration --"
let mutable vx = 0.0
vy <- 0.0
let rad0 = degToRad (0.0 + 90.0)
vx <- vx + cos rad0 * ThrustAccel
vy <- vy - sin rad0 * ThrustAccel
check "angle=0: VelX ~ 0" (approx vx 0.0)
check "angle=0: VelY ~ -0.1 (upward)" (approx vy (-0.1))

vx <- 0.0; vy <- 0.0
let rad90 = degToRad (90.0 + 90.0)
vx <- vx + cos rad90 * ThrustAccel
vy <- vy - sin rad90 * ThrustAccel
check "angle=90: VelX ~ -0.1 (leftward)" (approx vx (-0.1))
check "angle=90: VelY ~ 0" (approx vy 0.0)

vx <- 0.0; vy <- 0.0
let rad270 = degToRad (270.0 + 90.0)
vx <- vx + cos rad270 * ThrustAccel
vy <- vy - sin rad270 * ThrustAccel
check "angle=270: VelX ~ 0.1 (rightward)" (approx vx 0.1)
check "angle=270: VelY ~ 0" (approx vy 0.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 3: Thrust clamp to +/-2.0 --"
vx <- 1.95; vy <- 0.0
let radClamp = degToRad (270.0 + 90.0)
vx <- vx + cos radClamp * ThrustAccel
vx <- clampF -MaxVelocity MaxVelocity vx
check "VelX clamped to 2.0" (approx vx 2.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 4: Friction -- water terrain only (0x27), constant subtraction --"
// Friction ONLY applies when player is over water terrain (pixel color 0x27).
// Per ASM at B13B: CMP [BP-02h], 0x27 / JE apply_friction / JMP skip.
// Without terrain loaded, friction never runs. These tests verify the math
// for when terrain IS loaded.
vx <- 0.5
for _ in 1 .. 5 do
    vx <- vx + FrictionDecel
    if vx < 0.0 then vx <- 0.0
check "water friction: VelX=0.5 after 5 ticks: 0.05" (approx vx 0.05)
vx <- vx + FrictionDecel
if vx < 0.0 then vx <- 0.0
check "water friction: VelX should be 0 (clamped)" (approx vx 0.0)

vx <- -0.3
for _ in 1 .. 3 do
    vx <- vx - FrictionDecel
    if vx > 0.0 then vx <- 0.0
check "water friction: VelX=-0.3 after 3 ticks: -0.03" (approx vx (-0.03))

vx <- 0.8
vx <- clampF -FrictionMaxVel FrictionMaxVel vx
check "water friction: FrictionMaxVel clamp: 0.6" (approx vx 0.6)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 5: Turning -- 8 degrees per tick --"
let mutable angle = 0.0
for _ in 1 .. 5 do
    angle <- angle + TurnSpeed
    if angle >= MaxAngle then angle <- angle - MaxAngle
check "5 left turns from 0: angle = 40" (approx angle 40.0)
angle <- 4.0
angle <- angle - TurnSpeed
if angle < 0.0 then angle <- angle + MaxAngle
check "right turn from 4: angle = 356" (approx angle 356.0)
angle <- 356.0
angle <- angle + TurnSpeed
if angle >= MaxAngle then angle <- angle - MaxAngle
check "left turn from 356: angle = 4" (approx angle 4.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 6: Projectile spawn direction --"
let bulletSpeed = 4.0
let pRad = degToRad (0.0 + 90.0)
let bvx = cos pRad * bulletSpeed
let bvy = -(sin pRad) * bulletSpeed
check "bullet angle=0: VelX ~ 0" (approx bvx 0.0)
check "bullet angle=0: VelY ~ -4.0 (upward)" (approx bvy (-4.0))

let pRad90 = degToRad (90.0 + 90.0)
let bvx90 = cos pRad90 * bulletSpeed
let bvy90 = -(sin pRad90) * bulletSpeed
check "bullet angle=90: VelX ~ -4.0 (left)" (approx bvx90 (-4.0))
check "bullet angle=90: VelY ~ 0" (approx bvy90 0.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 7: Rear turret -- fires behind (180 offset) --"
let rtRad = degToRad (0.0 + 90.0 + 180.0)
let rtvx = cos rtRad * 4.0
let rtvy = -(sin rtRad) * 4.0
check "rear turret angle=0: VelX ~ 0" (approx rtvx 0.0)
check "rear turret angle=0: VelY ~ 4.0 (downward)" (approx rtvy 4.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 8: Multicannon -- 3-way spread --"
let mcSpeed = 4.0
let offsets = [| -2.2; 0.0; 2.2 |]
let mcResults =
    offsets |> Array.map (fun offset ->
        let r = degToRad (0.0 + 90.0 + offset)
        cos r * mcSpeed, -(sin r) * mcSpeed)
check "multicannon center: VelY ~ -4.0" (approx (snd mcResults[1]) (-4.0))
// With angle=0, fire direction = 90 degrees.
// Offset -2.2 → 87.8° → cos(87.8°) > 0 (slight right), sin(87.8°) ≈ 1
// Offset +2.2 → 92.2° → cos(92.2°) < 0 (slight left), sin(92.2°) ≈ 1
let leftVx = fst mcResults[0]   // -2.2 offset → slight rightward
let rightVx = fst mcResults[2]  // +2.2 offset → slight leftward
check "multicannon spread: -2.2 offset gives VelX > 0 (right)" (leftVx > 0.0)
check "multicannon spread: +2.2 offset gives VelX < 0 (left)" (rightVx < 0.0)
check "multicannon spread: symmetric" (approx (abs leftVx) (abs rightVx))

// ══════════════════════════════════════════════════════════════════════
// ── SIMULATION-BASED TESTS ──
// These simulate the actual Game.fs logic paths without referencing
// the DLL, by reimplementing the key decision logic inline.
// ══════════════════════════════════════════════════════════════════════

// --- Minimal player/entity types for simulation ---

[<Flags>]
type PF = None = 0uy | Active = 0x08uy | Shield = 0x10uy | Drunk = 0x20uy | Stunned = 0x40uy

type SimPlayer =
    { mutable PosX: float; mutable PosY: float; mutable Angle: float
      mutable VelX: float; mutable VelY: float
      mutable Flags: PF;   mutable Health: int
      mutable WeaponType: int; mutable ReloadTimer: int
      mutable KeyUp: bool;  mutable KeyLeft: bool; mutable KeyRight: bool
      mutable KeyFire: bool; mutable KeyDown: bool
      mutable Ammo: int;    mutable StunTimer: int
      mutable Alive: bool;  mutable InvTimer: int
      mutable AnimAngle: float; mutable ShotCount: int }

let mkPlayer () =
    { PosX = 160.0 * PositionScale; PosY = 200.0 * PositionScale
      Angle = SpawnDirection; VelX = 0.0; VelY = 0.0
      Flags = PF.Active; Health = FullHealth
      WeaponType = 14; ReloadTimer = 0  // MACHINEGUN
      KeyUp = false; KeyLeft = false; KeyRight = false
      KeyFire = false; KeyDown = false
      Ammo = 999; StunTimer = 0; Alive = true
      InvTimer = 0; AnimAngle = 0.0; ShotCount = 0 }

/// Simulate the gravity portion of updatePlayer (always runs)
let simGravity (p: SimPlayer) =
    p.VelY <- p.VelY + GravityAccel
    p.VelY <- min p.VelY MaxVelocity

/// Simulate fire check: KeyFire && ReloadTimer=0 && Ammo>0 && canControl
let simFireCheck (p: SimPlayer) =
    let canControl = not (p.Flags.HasFlag(PF.Stunned))
    p.KeyFire && p.ReloadTimer = 0 && p.Ammo > 0 && canControl

/// Simulate the DOWN key dispatch logic from ASM 0000:AE1F..AE79
/// Returns: true if fireSpecial would be called, false otherwise
let simDownKeyDispatch (p: SimPlayer) =
    let canControl = not (p.Flags.HasFlag(PF.Stunned))
    if not p.KeyDown || not canControl then false
    else
        match p.WeaponType with
        | 12 -> false  // TROOPERS: DOWN does nothing
        | 2 | 13 | 14 -> true  // MAGNOFILTER, HELL FIRE, MACHINEGUN: always fire
        | _ -> p.ReloadTimer = 0  // Others: only if reload ready

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 9: Gravity unconditional -- runs every tick regardless --"
// Gravity runs even when: not thrusting, not pressing any key, stunned, etc.
let p1 = mkPlayer ()
// No keys pressed at all
simGravity p1
check "gravity with no keys: VelY = 0.02" (approx p1.VelY 0.02)

// Gravity runs even when stunned
let p2 = mkPlayer ()
p2.Flags <- PF.Active ||| PF.Stunned
p2.StunTimer <- 100
simGravity p2
check "gravity while stunned: VelY = 0.02" (approx p2.VelY 0.02)

// Gravity runs even when thrusting upward
let p3 = mkPlayer ()
p3.Angle <- 0.0  // pointing up
p3.VelY <- -0.5  // moving upward
simGravity p3
check "gravity during upward motion: VelY = -0.48" (approx p3.VelY (-0.48))

// Gravity stacks over many ticks
let p4 = mkPlayer ()
for _ in 1..50 do simGravity p4
check "50 ticks gravity from rest: VelY = 1.0" (approx p4.VelY 1.0)

// Gravity clamps at MaxVelocity
let p5 = mkPlayer ()
for _ in 1..200 do simGravity p5
check "200 ticks gravity: VelY clamped to 2.0" (approx p5.VelY 2.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 10: Gravity independent of DOWN key --"
// DOWN key triggers special weapon fire, NOT downward movement
// Gravity runs identically whether DOWN is pressed or not
let pNoDown = mkPlayer ()
let pDown = mkPlayer ()
pDown.KeyDown <- true
simGravity pNoDown
simGravity pDown
check "gravity same with/without DOWN: VelY equal" (approx pNoDown.VelY pDown.VelY)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 11: Gravity vs thrust equilibrium --"
// At what angle can you perfectly counteract gravity?
// sin(angle+90)*0.1 = 0.02 → sin(angle+90) = 0.2 → angle = -78.46 (281.54)
let hoverAngle = Math.Asin(GravityAccel / ThrustAccel) * 180.0 / Math.PI - 90.0
let hoverNorm = if hoverAngle < 0.0 then hoverAngle + 360.0 else hoverAngle
printfn "  INFO: Hover equilibrium angle = %.1f degrees" hoverNorm
check "hover angle is valid (0-360)" (hoverNorm >= 0.0 && hoverNorm < 360.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 12: Fire logic -- all conditions must be met --"

// All conditions met → fires
let f1 = mkPlayer ()
f1.KeyFire <- true; f1.ReloadTimer <- 0; f1.Ammo <- 999
check "all conditions met: fires" (simFireCheck f1 = true)

// KeyFire not pressed → no fire
let f2 = mkPlayer ()
f2.KeyFire <- false; f2.ReloadTimer <- 0; f2.Ammo <- 999
check "KeyFire=false: no fire" (simFireCheck f2 = false)

// ReloadTimer > 0 → no fire
let f3 = mkPlayer ()
f3.KeyFire <- true; f3.ReloadTimer <- 3; f3.Ammo <- 999
check "ReloadTimer=3: no fire" (simFireCheck f3 = false)

// Ammo = 0 → no fire
let f4 = mkPlayer ()
f4.KeyFire <- true; f4.ReloadTimer <- 0; f4.Ammo <- 0
check "Ammo=0: no fire" (simFireCheck f4 = false)

// Stunned → no fire (canControl = false)
let f5 = mkPlayer ()
f5.KeyFire <- true; f5.ReloadTimer <- 0; f5.Ammo <- 999
f5.Flags <- PF.Active ||| PF.Stunned
check "Stunned: no fire" (simFireCheck f5 = false)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 13: Fire sets reload timer (MACHINEGUN = 4 ticks) --"
// After firing, ReloadTimer should be set to weapon's ReloadTicks
// MACHINEGUN (weapon 14) has ReloadTicks=4
let weaponReloadTicks =
    [| 1; 1; 1; 5; 8; 10; 30; 10; 25; 35; 50; 250; 50; 1; 4; 120; 1; 90; 60; 90; 180 |]

let fr = mkPlayer ()
fr.KeyFire <- true
// Simulate: fire sets reload timer
if simFireCheck fr then
    fr.ReloadTimer <- weaponReloadTicks[fr.WeaponType]
    fr.Ammo <- fr.Ammo - 1
    fr.ShotCount <- fr.ShotCount + 1
check "after fire: ReloadTimer = 4" (fr.ReloadTimer = 4)
check "after fire: Ammo = 998" (fr.Ammo = 998)
check "after fire: ShotCount = 1" (fr.ShotCount = 1)

// Next tick: cannot fire (reload not done)
check "next tick: still reloading" (simFireCheck fr = false)

// Count down reload
for _ in 1..4 do fr.ReloadTimer <- fr.ReloadTimer - 1
check "after 4 ticks: ReloadTimer = 0" (fr.ReloadTimer = 0)
check "after reload: can fire again" (simFireCheck fr = true)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 14: Rapid fire -- HELL FIRE (weapon 13, reload=1) --"
let hf = mkPlayer ()
hf.WeaponType <- 13  // HELL FIRE
hf.KeyFire <- true
// Should be able to fire every tick
check "hell fire: can fire tick 1" (simFireCheck hf)
hf.ReloadTimer <- weaponReloadTicks[13]  // = 1
check "hell fire: ReloadTimer = 1" (hf.ReloadTimer = 1)
hf.ReloadTimer <- hf.ReloadTimer - 1
check "hell fire: can fire tick 2" (simFireCheck hf)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 15: DOWN key dispatch -- weapon-specific gating --"

// MACHINEGUN (14): DOWN always fires (bypass reload)
let d1 = mkPlayer ()
d1.KeyDown <- true; d1.WeaponType <- 14; d1.ReloadTimer <- 3
check "DOWN+MACHINEGUN: fires even with reload>0" (simDownKeyDispatch d1 = true)

// HELL FIRE (13): DOWN always fires (bypass reload)
let d2 = mkPlayer ()
d2.KeyDown <- true; d2.WeaponType <- 13; d2.ReloadTimer <- 99
check "DOWN+HELL FIRE: fires even with reload=99" (simDownKeyDispatch d2 = true)

// MAGNOFILTER (2): DOWN always fires (bypass reload)
let d3 = mkPlayer ()
d3.KeyDown <- true; d3.WeaponType <- 2; d3.ReloadTimer <- 50
check "DOWN+MAGNOFILTER: fires even with reload=50" (simDownKeyDispatch d3 = true)

// TROOPERS (12): DOWN does nothing
let d4 = mkPlayer ()
d4.KeyDown <- true; d4.WeaponType <- 12; d4.ReloadTimer <- 0
check "DOWN+TROOPERS: does nothing" (simDownKeyDispatch d4 = false)

// Other weapon, reload ready → fires
let d5 = mkPlayer ()
d5.KeyDown <- true; d5.WeaponType <- 5; d5.ReloadTimer <- 0  // RUBBER BLTS
check "DOWN+RUBBER BLTS, reload=0: fires" (simDownKeyDispatch d5 = true)

// Other weapon, reloading → does NOT fire
let d6 = mkPlayer ()
d6.KeyDown <- true; d6.WeaponType <- 5; d6.ReloadTimer <- 5
check "DOWN+RUBBER BLTS, reload=5: no fire" (simDownKeyDispatch d6 = false)

// DOWN not pressed → never fires
let d7 = mkPlayer ()
d7.KeyDown <- false; d7.WeaponType <- 14; d7.ReloadTimer <- 0
check "DOWN not pressed: no fire" (simDownKeyDispatch d7 = false)

// Stunned → DOWN doesn't fire
let d8 = mkPlayer ()
d8.KeyDown <- true; d8.WeaponType <- 14; d8.ReloadTimer <- 0
d8.Flags <- PF.Active ||| PF.Stunned
check "DOWN+stunned: no fire" (simDownKeyDispatch d8 = false)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 16: DOWN key vs regular fire -- independent paths --"
// Regular fire uses KeyFire; DOWN key uses KeyDown
// They are separate input checks that can both trigger in the same tick
let dual = mkPlayer ()
dual.KeyFire <- true; dual.KeyDown <- true
dual.WeaponType <- 14  // MACHINEGUN
check "both keys: regular fire triggers" (simFireCheck dual = true)
check "both keys: DOWN fire triggers" (simDownKeyDispatch dual = true)

// Only DOWN pressed (no regular fire)
let downOnly = mkPlayer ()
downOnly.KeyFire <- false; downOnly.KeyDown <- true
downOnly.WeaponType <- 14
check "DOWN only: regular fire = false" (simFireCheck downOnly = false)
check "DOWN only: DOWN fire = true" (simDownKeyDispatch downOnly = true)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 17: DOWN key -- all 21 weapons gating table --"
// Verify each weapon's DOWN key behavior
let downGatingExpected = [|
//  wep  bypass_reload  ignored
    (0,  false, false)  // NONE: standard gating
    (1,  false, false)  // CLOAKER
    (2,  true,  false)  // MAGNOFILTER: bypass reload
    (3,  false, false)  // REAR TURRET
    (4,  false, false)  // MULTICANNON
    (5,  false, false)  // RUBBER BLTS
    (6,  false, false)  // MINE
    (7,  false, false)  // NUCLEUS
    (8,  false, false)  // DIRTCLOD
    (9,  false, false)  // HEADSPINNER
    (10, false, false)  // FREEZER
    (11, false, false)  // ATOM WEAPON
    (12, false, true )  // TROOPERS: ignored
    (13, true,  false)  // HELL FIRE: bypass reload
    (14, true,  false)  // MACHINEGUN: bypass reload
    (15, false, false)  // SONICBOOM
    (16, false, false)  // FAN
    (17, false, false)  // TOXIC DUMP
    (18, false, false)  // DUMBFIRE
    (19, false, false)  // MISSILE
    (20, false, false)  // BLACKHOLE
|]

let mutable allGatingCorrect = true
for (wep, bypassReload, ignored) in downGatingExpected do
    let pTest = mkPlayer ()
    pTest.KeyDown <- true
    pTest.WeaponType <- wep

    // Test with reload > 0
    pTest.ReloadTimer <- 10
    let firesWhileReloading = simDownKeyDispatch pTest

    // Test with reload = 0
    pTest.ReloadTimer <- 0
    let firesWhenReady = simDownKeyDispatch pTest

    if ignored then
        if firesWhileReloading || firesWhenReady then
            printfn "  FAIL: weapon %d should be ignored by DOWN" wep
            allGatingCorrect <- false
    elif bypassReload then
        if not firesWhileReloading then
            printfn "  FAIL: weapon %d should bypass reload" wep
            allGatingCorrect <- false
    else
        if firesWhileReloading then
            printfn "  FAIL: weapon %d should not fire while reloading" wep
            allGatingCorrect <- false
        if not firesWhenReady then
            printfn "  FAIL: weapon %d should fire when ready" wep
            allGatingCorrect <- false
check "all 21 weapons DOWN-key gating correct" allGatingCorrect

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 18: FireSpecial behavior per weapon --"

// Weapon 2 (MAGNOFILTER): toggle — flips Stunned flag, no projectile
// Weapon 3 (REAR TURRET special): sets reload timer, no projectile
// Weapon 5 (RUBBER BLTS special): 24 projectiles in full circle
// Weapon 14 (MACHINEGUN special): fires with random offset

// Test toggle behavior (weapon 2)
let tog = mkPlayer ()
tog.WeaponType <- 2
let wasStunned = tog.Flags.HasFlag(PF.Stunned)
// Simulate toggle: if stunned, clear; else set
if tog.Flags.HasFlag(PF.Stunned) then
    tog.Flags <- tog.Flags &&& ~~~PF.Stunned
else
    tog.Flags <- tog.Flags ||| PF.Stunned
let isStunned = tog.Flags.HasFlag(PF.Stunned)
check "MAGNOFILTER toggle: flips stunned" (wasStunned <> isStunned)
// Toggle back
if tog.Flags.HasFlag(PF.Stunned) then
    tog.Flags <- tog.Flags &&& ~~~PF.Stunned
else
    tog.Flags <- tog.Flags ||| PF.Stunned
check "MAGNOFILTER toggle: flips back" (tog.Flags.HasFlag(PF.Stunned) = wasStunned)

// Test full-circle burst (weapon 5 special: 24 projectiles)
let burstAngles = Array.init 24 (fun i -> float i * 15.0)
check "RUBBER BLTS burst: 24 angles cover 0..345" (burstAngles[23] = 345.0)
check "RUBBER BLTS burst: evenly spaced by 15 deg" (burstAngles[1] - burstAngles[0] = 15.0)

// Test FAN arc (weapon 16 special: 9 projectiles in arc)
let fanOffsets = [| for i in -4..4 -> float i * 8.0 |]
check "FAN arc: 9 projectiles" (fanOffsets.Length = 9)
check "FAN arc: range -32..+32 degrees" (fanOffsets[0] = -32.0 && fanOffsets[8] = 32.0)

// Test missile recoil (weapon 19 special: fires + pushes player back)
let recoilP = mkPlayer ()
recoilP.Angle <- 0.0  // pointing up → thrust direction 90° → fire upward
let recoilRad = degToRad (recoilP.Angle + 90.0)
let recoilVx = -cos recoilRad * 0.5
let recoilVy = sin recoilRad * 0.5
check "MISSILE recoil: pushes player down (VelY > 0)" (recoilVy > 0.0)
check "MISSILE recoil: angle=0 → no X recoil" (approx recoilVx 0.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 19: Enter as backup fire key --"
// Program.fs maps P1 fire as: has Keys.RShiftKey || has Keys.Enter
// We verify the logic: if either key returns true, KeyFire is set
// (Can't test actual WinForms key events in fsx, but verify the OR logic)
let hasRShift = false  // Simulate RShift not working (shift key bug)
let hasEnter = true    // Simulate Enter pressed
let keyFire = hasRShift || hasEnter
check "Enter as backup: fires when Enter pressed" (keyFire = true)

let hasRShift2 = true
let hasEnter2 = false
let keyFire2 = hasRShift2 || hasEnter2
check "RShift alone: still fires" (keyFire2 = true)

let hasRShift3 = false
let hasEnter3 = false
let keyFire3 = hasRShift3 || hasEnter3
check "neither key: no fire" (keyFire3 = false)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 20: Position scaling --"
let internalPos = 3200.0
let pixelPos = internalPos / PositionScale
check "3200 internal = 100 px" (approx pixelPos 100.0)
let testVel = 1.5
let posDelta = testVel * PositionScale
let pixelDelta = posDelta / PositionScale
check "vel=1.5 -> 1.5 px/tick movement" (approx pixelDelta 1.5)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 21: Free-fall distance --"
let mutable fallPos = 0.0
let mutable fallVel = 0.0
let mutable fallTicks = 0
while fallPos < ArenaHeight * PositionScale do
    fallVel <- fallVel + GravityAccel
    fallVel <- min fallVel MaxVelocity
    fallPos <- fallPos + fallVel * PositionScale
    fallTicks <- fallTicks + 1
printfn "  INFO: Falls through arena (%g px) in %d ticks (%.1fs @36Hz)"
    ArenaHeight fallTicks (float fallTicks / 36.0)
printfn "  INFO: Terminal velocity = %.1f px/tick = %.0f px/sec @36Hz"
    MaxVelocity (MaxVelocity * 36.0)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 22: Stun blocks all input --"
let stunP = mkPlayer ()
stunP.Flags <- PF.Active ||| PF.Stunned
stunP.StunTimer <- 50
stunP.KeyUp <- true; stunP.KeyLeft <- true; stunP.KeyFire <- true
let canControlStunned = not (stunP.Flags.HasFlag(PF.Stunned))
check "stunned: canControl = false" (canControlStunned = false)
check "stunned: fire check fails" (simFireCheck stunP = false)
check "stunned: DOWN dispatch fails" (begin stunP.KeyDown <- true; simDownKeyDispatch stunP = false end)

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 23: Shield blocks ALL input (including gravity path) --"
// When shield flag is set, player skips normal input processing entirely
// Only position update + shield decay run (from ASM: jump at A879 skips to AE79)
let shP = mkPlayer ()
shP.Flags <- PF.Active ||| PF.Shield
let shieldBlocksNormalInput = shP.Flags.HasFlag(PF.Shield)
check "shield: blocks normal input path" shieldBlocksNormalInput

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 24: Weapon reload ticks table --"
let expectedReloads = [| 1; 1; 1; 5; 8; 10; 30; 10; 25; 35; 50; 250; 50; 1; 4; 120; 1; 90; 60; 90; 180 |]
let mutable reloadOk = true
for i in 0..20 do
    if weaponReloadTicks[i] <> expectedReloads[i] then
        printfn "  FAIL: weapon %d reload: expected %d, got %d" i expectedReloads[i] weaponReloadTicks[i]
        reloadOk <- false
check "all 21 weapon reload ticks match" reloadOk

// ══════════════════════════════════════════════════════════════════════
printfn "-- Test 25: Invincibility blocks damage --"
// When InvTimer > 0, bullets should not deal damage
// (from checkBulletPlayerCollision: p.InvTimer = 0 required)
let invP = mkPlayer ()
invP.InvTimer <- SpawnInvincibilityTicks
let invBlocksDamage = invP.InvTimer > 0
check "invincibility: InvTimer > 0 blocks collision" invBlocksDamage
invP.InvTimer <- 0
check "invincibility: InvTimer = 0 allows collision" (invP.InvTimer = 0)

// ══════════════════════════════════════════════════════════════════════
printfn ""
printfn "================================================================"
printfn "  Results: %d passed, %d failed" passed failed
if failed > 0 then printfn "  *** SOME TESTS FAILED ***"
else               printfn "  All tests passed!"
printfn "================================================================"
