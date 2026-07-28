# Plan

Phased. Testing is a loop: I build, you launch PEAK and report, I adjust.

## Pivot (what the decompile changed)

The original plan assumed we'd have to serialize a run and rebuild it around a pinned seed —
the hard, crash-prone path the old mod took. Reading the code (see [FINDINGS.md](FINDINGS.md))
revealed PEAK **already has a full save/resume system** ("quit to continue later"): it autosaves
at every campfire and rehydrates through the devs' own reconnect code. The only thing stopping
post-death resume is that the game **deletes the save on a party wipe**.

So the mod shrank from "reimplement save/load" to "**stop deleting the save when you lose**."
Native code does all the serialization and restoration — that's why this won't crash like the old one.

## Phase 0 — Toolchain & recon ✅
- [x] Locate PEAK install, confirm Mono backend (not IL2CPP)
- [x] Install BepInEx 5.4.23.2 (Mono x64) into game folder
- [x] Install .NET SDK 8 + ilspycmd
- [x] Decompile `Assembly-CSharp.dll`, map the run lifecycle

## Phase 1 — Understand the run lifecycle ✅
- [x] Save system: `Peak.Quicksave` (autosaves at each campfire via `Campfire.Light_Rpc`)
- [x] Resume path: main-menu Continue button → native reconnect rehydration
- [x] Death path: `Character.RPCEndGame` deletes the save as its first line
- [x] All other `DestroySaveData` call sites catalogued (intentional; left alone)
- Output: [FINDINGS.md](FINDINGS.md)

## Phase 2 — Implement ✅ (built, not yet play-tested)
- [x] BepInEx plugin scaffolded (`src/PeakResume`), builds and auto-deploys to `BepInEx/plugins`
- [x] Patch: suppress `Quicksave.DestroySaveData()` only during a losing `RPCEndGame`
- [x] Win still clears the save (wipe detection = every character dead)
- [x] Config toggle `EnableResumeOnDeath` (default true)

## Phase 2b — In-session co-op resume ✅ (built, not yet play-tested)
- [x] Patch: `AirportCheckInKiosk.LoadIslandMaster` prefix arms `ShouldUseSaveData` when a save exists
- [x] Restores the whole party at the last campfire on boarding — no leaving the lobby / re-invites
- [x] Config toggle `ResumeOnBoard` (default true)
- Rationale + code map in [FINDINGS.md](FINDINGS.md) ("In-session co-op resume")

## Phase 3 — Play-test 🔜 (needs the user)
Single-player (✅ confirmed working):
- [x] First launch: BepInEx loads, `PeakResume ... loaded` in the log
- [x] Campfire → wipe → main-menu **Continue** → resume at campfire
Co-op / in-session (needs a friend):
- [ ] Host + friend, light a campfire, wipe the party
- [ ] Back at the airport together (no one leaves), host boards the plane
- [ ] Confirm both players land at the last campfire with their own gear/status restored
- [ ] Watch log for `Saved run found — boarding will resume it`
Sanity:
- [ ] Win a run → save cleared → next boarding is a normal fresh run (no accidental resume)

## Phase 4 — Harden (after test feedback)
- [ ] Optional: early autosave so a wipe before the first campfire is still resumable
- [ ] Optional: offer resume directly from the end screen instead of via main menu
- [ ] Package for Thunderstore-style manifest if wanted (currently local-only, which is the point)

## Phase 5 — Bigger parties (v1.1.0) ✅ (built, not yet play-tested)
- [x] `MaxPlayers` config (default 10): postfix on `NetworkingUtilities.MAX_PLAYERS` getter — covers
  both the Photon room cap and the Steam lobby size; host-only, clients can be vanilla
- [x] Audited every 4-player assumption in the decompile (spawn points, end screen, cutscene, voice
  mixers, pause menu) — all guarded/modulo, cosmetic-only past 4 (see FINDINGS.md)
- [x] `ScaleItemSpawns` + `ItemSpawnScale` configs: postfix on `Spawner.GetSpawnSpots` appends offset
  spot clones past 4 players — covers Luggage/RespawnChest/ground spawners, flows through native
  spawn tracking so quicksave resume restores scaled items
- [ ] Play-test: host a 5+ lobby, verify item counts, wipe → board → resume with scaled items intact

## How to build

```
dotnet build src/PeakResume/PeakResume.csproj -c Release
```
Requires the .NET SDK. The build copies `PeakResume.dll` into
`<PEAK>\BepInEx\plugins\PeakResume\`. If PEAK is installed elsewhere, pass
`-p:GameDir="D:\path\to\PEAK"`.

## Where to watch it work

- BepInEx log: `<PEAK>\BepInEx\LogOutput.log` — look for `PeakResume 1.0.0 loaded`,
  `Party wipe detected`, `Suppressed quicksave deletion on death`.
- Save file: `%USERPROFILE%\AppData\LocalLow\PEAK\...\quicksave.peak` — should still exist
  after a wipe (vanilla deletes it).
