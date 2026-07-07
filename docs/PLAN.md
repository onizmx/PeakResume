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

## Phase 3 — Play-test 🔜 (needs the user)
- [ ] **First launch**: run PEAK once so BepInEx initializes; confirm `PeakResume ... loaded` in the log
- [ ] Light at least one campfire, then die on purpose as a full-party wipe
- [ ] Back out to main menu → confirm **Continue** button is present
- [ ] Continue → confirm you resume at the last campfire with inventory/status intact
- [ ] Sanity: win a run (or `/` reach a campfire mini-run end) → confirm the save IS cleared (no stale Continue)

## Phase 4 — Harden (after test feedback)
- [ ] Optional: early autosave so a wipe before the first campfire is still resumable
- [ ] Optional: offer resume directly from the end screen instead of via main menu
- [ ] Package for Thunderstore-style manifest if wanted (currently local-only, which is the point)

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
