// FsRocket special-weapon balance simulation.
// Round-robin 1v1 CPU duels (identical AI personality) on a real level;
// each side has a pinned special weapon. Score = net kills (kills - deaths).
// Run with: dotnet fsi balance-sim.fsx
#load "Physics.fs"
#load "Terrain.fs"
#load "Weapons.fs"
#load "Types.fs"
#load "Entities.fs"
#load "Game.fs"

open System
open FsRocket.Physics
open FsRocket.Terrain
open FsRocket.Weapons
open FsRocket.Types
open FsRocket.Entities
open FsRocket.Game

// Symmetric AI: both sides get the same "Balanced" personality
let balanced = aiPersonalities[3]
for i in 0 .. 3 do aiPersonalities[i] <- balanced

let level =
    // .LEV files live in public/ on the FableWeb branch, repo root on desktop
    let candidate = if System.IO.File.Exists "HUNAJA.LEV" then "HUNAJA.LEV" else "public/HUNAJA.LEV"
    loadLevel candidate
let pristine = Array.copy level.Pixels

let duelTicks = 3600   // 100 s of game time per duel at 36 fps

/// Run one duel: P1 uses wA, P2 uses wB. Returns (killsA, deathsA, killsB, deathsB).
let duel (wA: WeaponType) (wB: WeaponType) (seed: int) =
    Array.blit pristine 0 level.Pixels 0 pristine.Length
    takeDirtyRect () |> ignore
    let gs0 =
        { createGameState 2 with
            Level = Some level
            NumPlayers = 2
            CpuCount = 2
            Rng = Random(seed) }
    let pin (g: GameState) =
        { g with
            Players = g.Players |> List.mapi (fun i p ->
                if i = 0 then { p with SpecialWeapon = wA }
                elif i = 1 then { p with SpecialWeapon = wB }
                else p) }
    let mutable gs = initRound gs0 |> pin
    for _ in 1 .. duelTicks do
        gs <- gameTick gs |> pin
        if gs.TerrainDirty then gs <- { gs with TerrainDirty = false }
    let a = gs.Players[0]
    let b = gs.Players[1]
    (a.KillCount, a.DeathCount, b.KillCount, b.DeathCount)

// All cyclable specials (enabled, not Cannon)
let specials =
    [| for i in 0 .. weapons.Length - 1 do
         let wt = enum<WeaponType> i
         if weapons[i].Enabled && wt <> WeaponType.Cannon then yield wt |]

printfn "Weapons under test (%d): %s" specials.Length
    (specials |> Array.map (fun w -> (getWeapon w).Name) |> String.concat ", ")

let sw = Diagnostics.Stopwatch.StartNew()

// score[w] accumulates net kills for weapon w across all its duels
let net = System.Collections.Generic.Dictionary<WeaponType, int>()
let kills = System.Collections.Generic.Dictionary<WeaponType, int>()
let deaths = System.Collections.Generic.Dictionary<WeaponType, int>()
let games = System.Collections.Generic.Dictionary<WeaponType, int>()
for w in specials do net[w] <- 0; kills[w] <- 0; deaths[w] <- 0; games[w] <- 0

let mutable pairIdx = 0
for ai in 0 .. specials.Length - 1 do
    for bi in ai + 1 .. specials.Length - 1 do
        let wA = specials[ai]
        let wB = specials[bi]
        // two seeds per orientation, sides swapped to cancel spawn asymmetry
        let runs =
            [ duel wA wB (1000 + pairIdx); duel wA wB (5000 + pairIdx)
              (let (k, d, k2, d2) = duel wB wA (2000 + pairIdx) in (k2, d2, k, d))
              (let (k, d, k2, d2) = duel wB wA (6000 + pairIdx) in (k2, d2, k, d)) ]
        for (ka, da, kb, db) in runs do
            net[wA] <- net[wA] + (ka - da)
            net[wB] <- net[wB] + (kb - db)
            kills[wA] <- kills[wA] + ka
            deaths[wA] <- deaths[wA] + da
            kills[wB] <- kills[wB] + kb
            deaths[wB] <- deaths[wB] + db
            games[wA] <- games[wA] + 1
            games[wB] <- games[wB] + 1
        pairIdx <- pairIdx + 1

printfn "Simulated %d pairings (%d duels) in %.1f s" pairIdx (pairIdx * 2) sw.Elapsed.TotalSeconds
printfn ""
printfn "%-14s %8s %8s %8s %10s" "WEAPON" "KILLS" "DEATHS" "NET" "NET/GAME"
for w in specials |> Array.sortByDescending (fun w -> float net[w] / float games[w]) do
    printfn "%-14s %8d %8d %8d %10.2f"
        (getWeapon w).Name kills[w] deaths[w] net[w] (float net[w] / float games[w])
