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

Since 1.1.0 it also grows the party:

- **Up to 10 players** (configurable, default 10) instead of the hardcoded 4. Only the host needs
  the mod — the cap is baked into the lobby when it's created.
- **Item spawns scale with party size.** Luggage and ground spawners produce proportionally more
  items past 4 players (also configurable).

Since 1.3.0, campfires actually restore you:

- **Lighting a campfire fully heals the whole party** — every affliction cleared (injury, hunger,
  cold, poison, drowsy, curse, spores, webs, thorns) and the stamina bar refilled, wherever people
  are standing. Vanilla only gives a small extra-stamina morale boost and shaves 20% off the
  lighter's injury. It runs through the game's own RPCs from the host, so **party members without
  the mod get healed too**. Dead players are skipped — they still respawn at a statue.
- **Revives bring you back at full** (1.4.0). Vanilla stamps Curse 0.05 + Hunger 0.3 on anyone it
  revives — statue, revive chest, skeleton, base camp respawn, and the resume spawn — so you get up
  already halfway to passing out. That penalty is dropped and the bar is filled.

And since 1.5.0, PEAK's own **developer console is unlocked on F1** — it ships in the retail build
but the game never sets the one flag that opens it. See [Debug console](#debug-console-always-on-f1).

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

Package page (for reference): https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/

**Windows PowerShell — download, extract, and install in one go.** Set `$PEAK` to your game folder
first (the one with `PEAK.exe`; find it via Steam → right-click PEAK → Manage → Browse local files):

```powershell
$PEAK = "C:\Program Files (x86)\Steam\steamapps\common\PEAK"   # <-- change if yours differs
$zip  = "$env:TEMP\BepInExPack_PEAK.zip"
Invoke-WebRequest "https://thunderstore.io/package/download/BepInEx/BepInExPack_PEAK/5.4.75301/" -OutFile $zip
Expand-Archive $zip "$env:TEMP\BepInExPack_PEAK" -Force
# Copy the CONTENTS of the inner BepInExPack_PEAK folder into the game folder:
Copy-Item "$env:TEMP\BepInExPack_PEAK\BepInExPack_PEAK\*" $PEAK -Recurse -Force
```

Then confirm it landed: `$PEAK` should now contain `winhttp.dll` (version 4.4.1.0) and a `BepInEx\`
folder. If a newer pack version exists on Thunderstore, bump the version number in the URL.

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
PeakResume 1.5.0 loaded. Resume-on-death=ENABLED, resume-on-board=ENABLED, persist-checkpoint=ENABLED, max-players=10, scale-item-spawns=ENABLED (x1), campfire-full-heal=ENABLED, revive-full-heal=ENABLED, debug-console=ENABLED (F1).
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

Generated at `PEAK\BepInEx\config\com.onizmx.peakresume.cfg` after the first launch.

| Setting | Default | What it does |
|---|---|---|
| `EnableResumeOnDeath` | on | Keep the save on a party wipe (the core feature). |
| `ResumeOnBoard` | on | Boarding the plane resumes a saved run (in-session co-op resume). |
| `PersistCheckpoint` | on | Keep the checkpoint after resuming, for unlimited retries. |
| `MaxPlayers` | 10 | Lobby size (vanilla hardcodes 4). Host-side; set before hosting. |
| `ScaleItemSpawns` | on | More than 4 players → proportionally more items from luggage/spawners. |
| `ItemSpawnScale` | 1.0 | Scaling strength: 1.0 = fully proportional (10 players → 2.5× items). |
| `FullHealOnLight` | on | Lighting a campfire fully heals the whole party (see above). |
| `FullHealOnRevive` | on | Revives skip the vanilla Curse/Hunger penalty and refill stamina. |

The debug console (below) has no setting — it's always available on F1.

## Debug console (always on, F1)

PEAK ships with a working developer console — UI, command registry, hotkey page, the lot — but the
single flag that lets F1 open it (`DebugUIHandler.AllowOpen`) is never set anywhere in the game, so
it's unreachable in the retail build. The mod sets it at startup, every session, no config needed:
nothing appears unless you actually press F1, so there's nothing to gate.

- **F1** toggles it. Type a command and press Enter; page tabs (Console / Hotkeys / Settings /
  Network Stats / …) are the buttons along the top — the tab-cycle keys have no keyboard binding.
- Commands are local to your own character: `GainFullStamina`, `InfiniteStamina`, `ClearAll`
  (clear afflictions), `AddInjury`, `WarpToSpawn`, `Revive`, `Die`, `TestWin`, and the rest of
  `Zorro.Core.CLI.ConsoleCommands`. The Hotkeys page binds a command to a key.
- Handy for checking mechanics: e.g. if stamina won't refill, `ClearAll` shows whether afflictions
  were capping your max stamina (max = 1 − sum of afflictions) rather than something being broken.
- It's a dev tool on a live multiplayer game — expect to be able to break your own run with it.

## Limitations

- **Checkpoint = last campfire**, not the exact spot you died.
- **Dying before the first campfire** restarts from the beginning (no save exists yet).
- **No map-altering mods** (MorePeak, etc.). Resume replays the game's saved run state, which assumes
  vanilla generation and spawn tracking.
- **Past 4 players some things stay 4-wide** (by the game's design, all cosmetic): the end-screen
  timeline and the summit cutscene show 4 scouts, the pause menu has 4 volume/kick rows, and voices
  past the 4th lose per-player effects (still audible). Item scaling covers luggage and ground
  spawners; pre-placed scene items stay at their 4-player amounts, and expect some extra network lag
  past ~6 players — the game's sync traffic was budgeted for 4.
- If PEAK updates, the mod may need a rebuild (`dotnet build ...`) against the new game DLLs.

---

## Scope / environment

- **Host-authoritative.** Only the host's actions drive save/resume; clients are restored by the host.
- Confirmed against PEAK **1.65.a** (Steam build 24347206; originally built against 1.64.a — the
  update left every patched method untouched), Unity **6000.0.62f1** (Mono backend), Photon PUN
  networking, BepInExPack_PEAK **5.4.75301** (BepInEx 5.4.23.3 + doorstop `4.4.1PEAK`).

## How it works / auditing

The mod is nine small Harmony patches over the game's own methods. The full technical map — exact
classes, methods, line numbers, and the reasoning behind each patch — is in
[docs/FINDINGS.md](docs/FINDINGS.md). Plan and status: [docs/PLAN.md](docs/PLAN.md).
