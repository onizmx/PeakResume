# PeakResume

A BepInEx mod for **PEAK** (Landfall/Aggro Crab) that lets you **save a run and resume from where you died** — instead of restarting from the beach every time the party wipes.

Personal project. Host-side and single-player only. Built fresh, fully auditable — no opaque third-party DLLs in the game process.

## Why this exists

PEAK has no save system. When everyone dies, the run is over and you start again from Stage 1. Existing save mods either only teleport within a live run (don't survive a wipe) or are buggy/crash-prone and unmaintained. This is a clean reimplementation of the "serialize a run, rehydrate it later" idea, done carefully around Photon's host-authority model so it doesn't crash.

## What it does

PEAK already autosaves the whole run at every campfire and can rehydrate it (its "quit to continue
later" feature) — but it **deletes that save the instant the party wipes**, forcing a restart. This
mod is small and rides entirely on the game's own save/restore code:

- **Keeps the save on a wipe** (a win still clears it). So a death becomes resumable, exactly like a
  voluntary quit.
- **Solo:** resume from the main-menu **Continue** button — back at your last campfire, gear/status intact.
- **Co-op, in-session:** after a wipe you're all still at the airport together; the host just **boards
  the plane** and the whole party is restored at the last campfire — no one leaves the lobby, no
  re-invites. (Config: `ResumeOnBoard`.)

No world-seed reconstruction, no manual teleporting, no Photon-ordering hacks — which is why it
doesn't crash the way the old unmaintained save mod did.

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
| Unity engine | **6000.0.62f1** (Unity 6) |
| Mod loader | **BepInExPack_PEAK 5.4.75301** (BepInEx 5.4.23.3 + doorstop `4.4.1PEAK`) |
| Install path | `C:\Program Files (x86)\Steam\steamapps\common\PEAK` |

Mono backend is the good case: game code decompiles to readable C# and Harmony patches apply directly (no IL2CPP interop layer).

> **BepInEx note:** PEAK is Unity 6. The generic `BepInEx_win_x64_5.4.23.2` pack ships an older
> UnityDoorstop (`winhttp.dll` 4.3.0.0) that **crashes the game at engine startup** (crash inside
> `WINHTTP!WinHttpWriteProxySettings`, before any managed plugin loads). Use the
> **[BepInExPack_PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/)** instead — it
> carries the PEAK-specific doorstop `4.4.1PEAK` (`winhttp.dll` 4.4.1.0), which is Unity-6 safe.

## Build

Requires the .NET SDK. From the repo root:

```
dotnet build -c Release
```

The build copies the plugin DLL into the game's `BepInEx/plugins` folder (see the csproj post-build step).

## Status

See [docs/PLAN.md](docs/PLAN.md) for the phased plan and current progress.
