# PeakResume

A BepInEx mod for **PEAK** (Landfall/Aggro Crab) that lets you **resume a run after the party wipes**
— instead of restarting from the beach every time everyone dies.

Personal project, host-side. Fully auditable: the mod adds no save/restore logic of its own — it
rides PEAK's built-in campfire autosave and only changes *when that save is kept vs. deleted*. Every
line it runs is in this repo.

## What it does

PEAK already autosaves the whole run at every campfire (its "quit to continue later" feature) — but
it **deletes that save the instant the party wipes**, forcing a restart. This mod stops that:

- **Die → the save is kept** (a win still clears it). A death becomes resumable, like a voluntary quit.
- **Board the plane → resume.** If a saved run exists, boarding from the airport continues it: the
  whole party loads back at the last campfire, restored. No one leaves the lobby, no re-invites.
- **Retry unlimited.** The checkpoint survives resuming, so you can wipe and retry the same campfire
  as many times as you need. Lighting the next campfire moves the checkpoint up; reaching the Peak
  clears it.

Solo works too — resume from the main-menu **Continue** button, or just board again.

No world-seed math, no manual teleporting, no network-ordering hacks — that's why it doesn't crash
like the old, unmaintained save mods.

---

# Setup

> **Following this with Claude Code?** Point it at this repo and ask it to "install the PeakResume
> mod." The steps below are ordered so an agent can execute them top to bottom. The one thing that
> must not be improvised: **Step 2 — use `BepInExPack_PEAK`, not generic BepInEx** (see the warning).

## Prerequisites

- **PEAK** installed via Steam.
- **.NET SDK 8+** — needed to build the plugin. (`winget install Microsoft.DotNet.SDK.8` on Windows,
  or grab it from https://dotnet.microsoft.com/download.)
- **git** to clone this repo.

## Step 1 — Clone

```
git clone https://github.com/onizmx/PeakResume.git
cd PeakResume
```

## Step 2 — Install the mod loader (BepInExPack_PEAK)

> ⚠️ **Use the PEAK-specific pack, not generic BepInEx.** PEAK runs on **Unity 6**. The generic
> `BepInEx_win_x64_5.4.23.x` pack ships an older UnityDoorstop (`winhttp.dll` 4.3.0.0) that
> **crashes the game at startup** — before any plugin loads — with a crash inside
> `WINHTTP!WinHttpWriteProxySettings`. The PEAK pack carries a Unity-6-safe doorstop
> (`winhttp.dll` 4.4.1.0, `.doorstop_version` = `4.4.1PEAK`). This bit us; don't repeat it.

1. Download **BepInExPack_PEAK**: https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/
2. Open the zip, go into the inner `BepInExPack_PEAK/` folder, and copy its **contents**
   (`BepInEx/`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`) into your PEAK game
   folder — the one containing `PEAK.exe`.
3. To find that folder: Steam → right-click **PEAK** → Manage → Browse local files. (Common default:
   `C:\Program Files (x86)\Steam\steamapps\common\PEAK`.)

## Step 3 — Build the plugin

From the repo root:

```
dotnet build src/PeakResume/PeakResume.csproj -c Release
```

The build **auto-detects** your PEAK folder (checks the common Steam library locations) and copies
`PeakResume.dll` into `PEAK\BepInEx\plugins\PeakResume\`.

If your PEAK is somewhere unusual and the build can't find it, it stops with a message telling you to
pass the path explicitly:

```
dotnet build src/PeakResume/PeakResume.csproj -c Release -p:GameDir="D:\Games\SteamLibrary\steamapps\common\PEAK"
```

## Step 4 — Verify

Launch PEAK once. Open `PEAK\BepInEx\LogOutput.log` and look for:

```
PeakResume 1.0.0 loaded. Resume-on-death=ENABLED, resume-on-board=ENABLED, persist-checkpoint=ENABLED.
```

If the game **crashes at startup instead of reaching the menu**, you almost certainly used generic
BepInEx — redo Step 2 with `BepInExPack_PEAK`.

---

# Using it

1. Play a run and **light a campfire** (that's your checkpoint — the game autosaves there).
2. Wipe. You'll land back at the airport with the save intact (vanilla would delete it here).
3. **Host boards the plane** → everyone resumes at that campfire, restored.
4. Wipe again → back to the same campfire. Repeat as needed.
5. Light the next campfire to move the checkpoint up; reach the Peak to finish (save clears).

Log breadcrumbs that confirm it's working: `Party wipe detected`, `Saved run found — boarding will
resume it`, `Checkpoint preserved through resume`.

## Config

Generated at `PEAK\BepInEx\config\com.onizmx.peakresume.cfg` after the first launch. All default on:

| Setting | What it does |
|---|---|
| `EnableResumeOnDeath` | Keep the save on a party wipe (the core feature). |
| `ResumeOnBoard` | Boarding the plane resumes a saved run (in-session co-op resume). |
| `PersistCheckpoint` | Keep the checkpoint after resuming, for unlimited retries. |

## Limitations

- **Checkpoint = last campfire**, not the exact spot you died.
- **Dying before the first campfire** restarts from the beginning (no save exists yet).
- **No map-altering mods** (MorePeak, etc.). Resume replays the game's saved run state, which assumes
  vanilla generation and spawn tracking.
- If PEAK updates, the mod may need a rebuild (`dotnet build ...`) against the new game DLLs.

---

## Scope / environment

- **Host-authoritative.** Only the host's actions drive save/resume; clients are restored by the host.
- Confirmed against PEAK **1.64.a**, Unity **6000.0.62f1** (Mono backend), Photon PUN networking,
  BepInExPack_PEAK **5.4.75301** (BepInEx 5.4.23.3 + doorstop `4.4.1PEAK`).

## How it works / auditing

The mod is four small Harmony patches over the game's own methods. The full technical map — exact
classes, methods, line numbers, and the reasoning behind each patch — is in
[docs/FINDINGS.md](docs/FINDINGS.md). Plan and status: [docs/PLAN.md](docs/PLAN.md).
