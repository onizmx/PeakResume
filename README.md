# PeakResume

A BepInEx mod for **PEAK** (Landfall/Aggro Crab) that lets you **save a run and resume from where you died** — instead of restarting from the beach every time the party wipes.

Personal project. Host-side and single-player only. Built fresh, fully auditable — no opaque third-party DLLs in the game process.

## Why this exists

PEAK has no save system. When everyone dies, the run is over and you start again from Stage 1. Existing save mods either only teleport within a live run (don't survive a wipe) or are buggy/crash-prone and unmaintained. This is a clean reimplementation of the "serialize a run, rehydrate it later" idea, done carefully around Photon's host-authority model so it doesn't crash.

## What it does

- **Save** (hotkey): snapshot the current run — world seed, every player's position, inventory, health, and status effects — to a local JSON file on your machine.
- **Load** (hotkey): pin the saved seed, rebuild the run, and restore positions/inventory/health in the correct network order so it doesn't desync or crash.

## Scope / constraints

- **Host + single-player only.** Saves live locally; no cross-machine portability. If a friend hosts, this does nothing on your end.
- **Vanilla generation only.** No map-altering mods (MorePeak etc.) — the resume relies on the seed reproducing the same world.
- Auditable source: everything the mod does is in this repo. Nothing loads code you can't read.

## Environment (confirmed)

| Thing | Value |
|---|---|
| Game version | **1.64.a** (build `20b1c898c`) |
| Unity scripting backend | **Mono** (`MonoBleedingEdge`, readable `Assembly-CSharp.dll`) |
| Networking | **Photon PUN** (`PhotonUnityNetworking.dll`) + Steamworks lobby |
| Mod loader | **BepInEx 5.4.23.2** (Mono x64) |
| Install path | `C:\Program Files (x86)\Steam\steamapps\common\PEAK` |

Mono backend is the good case: game code decompiles to readable C# and Harmony patches apply directly (no IL2CPP interop layer).

## Build

Requires the .NET SDK. From the repo root:

```
dotnet build -c Release
```

The build copies the plugin DLL into the game's `BepInEx/plugins` folder (see the csproj post-build step).

## Status

See [docs/PLAN.md](docs/PLAN.md) for the phased plan and current progress.
