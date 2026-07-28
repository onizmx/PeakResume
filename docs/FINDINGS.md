# Findings — how PEAK's run save/resume actually works

Decompiled from `Assembly-CSharp.dll`, game **v1.64.a**. Line numbers refer to the single-file
decompile under `decompiled/` (gitignored). This is the design basis for the mod.

> **Re-verified against v1.65.a (Steam build 24347206, 2026-07-29).** Every patched method —
> `Character.RPCEndGame`, `Quicksave.DestroySaveData` / `FinalizeRunSetupAndSelfDestruct`,
> `AirportCheckInKiosk.LoadIslandMaster`, `NetworkingUtilities.MAX_PLAYERS`,
> `Spawner.GetSpawnSpots` — is byte-identical to 1.64.a. One relevant addition: 1.65.a introduces
> a **seventh `DestroySaveData()` call site** in `RunManager.StartRun`, which deletes a
> **non-host's** local quicksave when a run starts (stale client-side copy cleanup). It never runs
> on the host — whose save is the one that drives resume — and it sits outside both of our
> suppression windows, so the mod is unaffected. See the call-site tables below.

## TL;DR

PEAK **already has a complete save-and-resume system** — it's the "quit now, continue later"
feature. It auto-saves the whole run at every campfire and can rehydrate it via the devs' own
reconnect code. The *only* reason you can't resume after dying is that the game **deliberately
deletes the save the instant the party wipes**. Remove that one deletion (on loss only) and the
native "Continue" flow resumes you from the last campfire.

No seed reconstruction. No manual teleport. No Photon-ordering hacks. That's why the old mod
crashed (it reimplemented all of this) and why this approach won't.

## The native save system: `Peak.Quicksave` (line ~88380)

- Serializes to `quicksave.peak` in `Application.persistentDataPath`
  (`%USERPROFILE%\AppData\LocalLow\PEAK\<...>\quicksave.peak`), as JSON (`JsonUtility`).
- `SaveData` (version 2) contains:
  - `RunProgress run` — `runId`, `runTimer`, `timeOfDay`, `levelName`, **`biomeReached`** (the
    `Segment` enum), `ascent`, **`spawnHistory`** (every tracked spawner's items), and
    `openLuggageViewIds`.
  - `List<PlayerRunData> playerSaves` — one per player, each holding
    `ReconnectData.CreateFromCharacter(character)` (the full per-player state the game already
    uses to restore a reconnecting player: position, inventory, afflictions/status) plus
    achievement progress.
  - `runSettings` — serialized run settings.
- Key methods: `SaveNow()`, `TryLoadSave()`, `PopulateMapAndPlayerStates()`,
  `FinalizeRunSetupAndSelfDestruct()`, `DestroySaveData()`.

## When the game SAVES (line 22726 `Campfire.Light_Rpc`)

Lighting a campfire that advances the segment runs:
```
if (updateSegment) {
    GUIManager.instance.Quicksave();   // UI feedback
    Quicksave.SaveNow();               // writes quicksave.peak
}
```
So **every biome campfire = a full autosave**. This is our checkpoint. (Exception: a mini-run
ending campfire triggers `EndGame()` instead — a win.)

## When the game RESUMES (line 48847 `MainMenuMainPage`)

- `SetUpContinueButton()`: the **Continue** button is shown iff `Quicksave.Exists && TryLoadSave()`.
- `ContinueFromQuicksaveClicked()`: sets `ShouldUseSaveData = true`, then
  - online save → `StartMultiplayerLobby(destroyQuicksave: false)`
  - offline save → `Quicksave.LoadSavedGameScene()`
- On the next run start, `RunManager.Start()` (line 42395) and `CharacterSpawner`
  (`PopulateMapAndPlayerStates`, line 36707) rehydrate the map + all players.
- Friends rejoin a resumed run via run-id matching (line 19197): an invite whose lobby `RunId`
  equals the saved `runId` joins without destroying the save. The devs built this for co-op
  "continue later."

## The problem: the save is deleted on death (line 1489 → 1515 → 1555)

```
Character.CheckEndGame()   // host: if ALL AllCharacters[i].data.dead -> EndGame()
Character.EndGame()        // RPC "RPCEndGame" to all + RunManager.EndGame()
Character.RPCEndGame()     // FIRST LINE: Quicksave.DestroySaveData();  <-- the culprit
```
`RPCEndGame` fires on both **win** (reached the Peak / mini-run campfire) and **loss** (everyone
dead). Deleting the save is correct on a win, wrong on a loss. Because the delete is
unconditional, a wipe throws away the last campfire's autosave → the Continue button disappears →
you restart from the beach.

## Other `DestroySaveData()` call sites — all intentional, leave them alone

| Line | Context | Keep? |
|---|---|---|
| 1558 | `RPCEndGame` (win **and** loss) | **suppress on loss only** |
| 19212 | Joining someone else's lobby (user confirmed) | keep |
| 48903 | Main menu "Play" starts a fresh lobby (user confirmed) | keep |
| 63673 | Starting a fresh solo session (user confirmed) | keep |
| 88678 | `TryLoadSave` version mismatch | keep |
| 88737 | `FinalizeRunSetupAndSelfDestruct` after a successful load | keep |
| (1.65.a) | `RunManager.StartRun` — non-host local-save cleanup at run start | keep (never runs on the host) |

## The mod

Two Harmony patches:

1. **Prefix + Postfix on `Character.RPCEndGame`** — set a static `SuppressDestroy` flag true iff
   this end-game is a **party wipe** (`Character.AllCharacters` all `.data.dead`), reset it after.
   Win → not all dead → flag stays false.
2. **Prefix on `Peak.Quicksave.DestroySaveData`** — if `SuppressDestroy`, skip the original
   (return false). Scoped strictly to the `RPCEndGame` execution window, so every other delete
   site is unaffected.

Net effect: a loss now leaves `quicksave.peak` intact, exactly like a voluntary "quit to continue
later." The player resumes from the last campfire via the native Continue button.

## In-session co-op resume (no rejoin needed)

The obvious worry: the native "Continue" button lives on the main menu and rebuilds the lobby, so
resuming with friends looks like it needs everyone to leave and re-accept invites. Reading the
spawn code shows that's avoidable.

Per-player restore is **not** tied to physically reconnecting. In `CharacterSpawner.HostUpdate`
(line 36631) the host, for each un-spawned player, checks
`ReconnectHandler.TryGetReconnectData(player, ...)` and, if found, sends `RPC_ReconnectingPlayerSpawn`
(restoring that player's position, inventory, afflictions). The resume path
`Quicksave.PopulateMapAndPlayerStates()` (called from `SpawnHostCharacter`, line 36707)
**seeds those reconnect records for every player from the quicksave**. So the restore is keyed on
*"the host has your saved record,"* not on *"you left and came back."*

Consequence: any normal run start with `ShouldUseSaveData == true` restores the whole party — the
host respawns restored (`SpawnHostCharacter`, line 36705) and each friend respawns via
`RPC_ReconnectingPlayerSpawn`. Ordering is safe: `HostUpdate` spawns the host first (seeding the
records) before it processes the other players (line 36633–36641).

So instead of the main-menu Continue, we arm the same flag at the moment of **boarding**:

- **Prefix on `AirportCheckInKiosk.LoadIslandMaster`** (host-only; it's an `RpcTarget.MasterClient`
  RPC) — if `Quicksave.Exists && TryLoadSave()`, set `ShouldUseSaveData = true` before the scene
  loads. Boarding then continues the saved run instead of generating a fresh one; the whole party
  is restored at the last campfire without leaving the lobby. `FinalizeRunSetupAndSelfDestruct`
  clears the flag after the load, so it doesn't linger.

Rule of thumb this creates: **if a saved run exists, boarding the plane continues it.** A win
already destroyed the save, so ordinary fresh runs board normally. Config: `ResumeOnBoard`.

## Checkpoint lifecycle — when the save moves or resets

**Overwritten forward:** every campfire that advances a segment calls `Quicksave.SaveNow()`
(line 22751), replacing the save with the newer checkpoint.

**Deleted (`DestroySaveData`) at these sites:**

| Line | Trigger | Our handling |
|---|---|---|
| 1558 | `RPCEndGame` — win **or** loss | suppress on **loss** only (keep save) |
| 88737 | `FinalizeRunSetupAndSelfDestruct` — consumes save **on resume** | suppress the file delete, keep the checkpoint (see below) |
| 19212 | Joining another lobby | keep (intentional) |
| 48903 | Main-menu "Play" (fresh run) | keep (intentional) |
| 63673 | Fresh solo session | keep (intentional) |
| 88678 | `TryLoadSave` version mismatch | keep (intentional) |
| (1.65.a) | `RunManager.StartRun` — deletes a **non-host's** local save at run start | keep (host-side save is untouched; outside both suppression windows) |

### Persist-through-resume (`PersistCheckpoint`)

Vanilla `FinalizeRunSetupAndSelfDestruct` (line 88733) runs `RunManager.SetUpFromQuicksave`, sets
`timeOfDay`, then `DestroySaveData()` — which deletes the file **and** clears `ShouldUseSaveData`.
That "self-destruct" means a resumed run has no checkpoint until the next campfire: one retry only.

We prefix/postfix that method to arm `ResumeState.SuppressDestroy` around it, skipping only the file
deletion, then manually set `ShouldUseSaveData = false` in the postfix (the one side effect we still
need, so the load doesn't re-trigger). Net: the checkpoint file survives resuming, so repeated wipes
keep bouncing you back to the same campfire until you light the next one (moves it forward) or win
(clears it via the unsuppressed `RPCEndGame`).

## Bigger parties (v1.1.0): `MaxPlayers` + `ScaleItemSpawns`

Class/method references below are to the PeakStudy per-type decompile (Steam build 23871350), not
this repo's older single-file line numbers.

### The 4-player cap is one getter

`Peak.Network.NetworkingUtilities.MAX_PLAYERS => 4` is the only cap in the codebase. Both readers
run **host-side at room creation**: `HostRoomOptions()` (Photon `RoomOptions.MaxPlayers`) and
`SteamLobbyAPI.CreateLobby` (Steam lobby size). One postfix on the getter raises both; joining
clients never check it, so only the host needs the mod.

Everything that touches per-player state was audited for a hardcoded 4. All of it degrades
gracefully past 4 — guards, modulo, or list-based code — never an exception:

| Site | 4 is... | Past 4 |
|---|---|---|
| `SpawnPoint.GetSpawnPoint` | scene spawn points | index wraps via modulo — players share spots |
| `EndScreen` timeline / scout windows | scene-authored UI arrays | `< scouts.Length` guards → 4 shown |
| `PeakHandler` summit cutscene | 4 stand-in models | 4 shown |
| `PlayerHandler.AssignMixerGroup` | 4 voice mixer groups | returns `byte.MaxValue`, guarded → voice plays without per-player effects |
| `AudioLevels` pause-menu rows | 4 slider/kick rows | `sliders.Count > j` guard → no row |
| Quicksave / ReconnectHandler | — | List/Dictionary-based, no cap |

Real-world constraint: `CharacterSyncer` streams ~33–41 B per player at 30 Hz; the room's message
volume grows roughly quadratically with player count, so expect lag past ~6–8 players (Photon's
per-room guideline is what the 4-cap was protecting).

### Item quantity is "one item per scene-authored spawn spot"

`Spawner.SpawnItems` spawns exactly one item per Transform in the list returned by the **virtual**
`Spawner.GetSpawnSpots()` — and `Luggage`/`RespawnChest` (the main item sources) funnel through the
same method. So a postfix that appends offset clones of existing spots scales the whole pipeline:
pool selection (`GetObjectsToSpawn(count)`), instantiation, and spawn tracking.

Why this is resume-safe: `TrySpawnItems` calls `tracker.TrackSpawnedItems(list)` **after** spawning,
so scaled items enter the quicksave's `spawnHistory`; on resume, `SpawnAndTrackFromItemHistory()`
replays the history and never calls `GetSpawnSpots`, so nothing double-scales. Host-only by
construction (`SpawnItems` early-outs on non-masters). Cloned spots get a ~0.25 m offset because
items spawn kinematic (`SetKinematicRPC`) and would otherwise overlap.

Deliberately not scaled:

| Source | Why |
|---|---|
| Pre-placed scene items (`DestroyBasedOnPlayerCount`) | Scenes hold the 4-player max and *delete down* for smaller parties; with >4 everything already survives. Adding more means cloning networked scene objects — not worth the risk. |
| `SingleItemSpawner` | Single by design (special placements); tracking is internal to its method. |
| `BerryVine` | Overrides `GetSpawnSpots` (spots from its own colliders) and rolls its own count from `possibleBerries`. |
| `FakeItem` world pickups | Not networked objects — synced as a scene-index list. Adding instances breaks index sync and late-join. |

### Known limitations (by design, documented for honesty)

- **Checkpoint granularity, not exact death spot.** You resume from the last campfire you lit, not
  the pixel where you fell. That's the only state the game persists, and it's what "retry from
  where we died" realistically means.
- **Death before the first campfire = no resume.** If the whole party dies in the first segment
  before lighting any campfire, no autosave exists yet, so you restart. (A future enhancement
  could add an early autosave.)
- **Resume UX is the native Continue button** on the main menu — after a wipe, back out to the
  title screen and Continue will be there. (A future enhancement could offer resume directly from
  the end screen.)

---

## The developer console is shipped but gated (v1.2.0: `EnableDebugConsole`)

PEAK's retail build contains the whole console: the UI (`Zorro.Core.CLI.DebugUIHandler`, a
`UIDocument`-driven overlay with Console / Hotkeys / Settings / Network Stats pages), the command
registry (`Zorro.Core.CLI.ConsoleCommands` in `Assembly-CSharp` — ~100 `[ConsoleCommand]` methods
including `Character.GainFullStamina`, `InfiniteStamina`, `LockStatuses`,
`CharacterAfflictions.ClearAll`, `WarpToSpawn`, `TestWin`), and the open hotkey
(`InputForZorroCore.Open` = input action `OpenDebugMenu`, fallback `KeyCode.F1`).

What blocks it is one line in `DebugUIHandler.Update()`:

```csharp
if (!IsOpened) { if (AllowOpen) Show(); }
```

`public static bool AllowOpen` appears in exactly **two** places across every shipped assembly
(`Assembly-CSharp`, `-firstpass`, `pworld`, all `Zorro.*`): its declaration and this read. Nothing
assigns it — no debug build define, no launch argument, no settings entry. So F1 does nothing.

The mod sets it to `true` at plugin `Awake` when `EnableDebugConsole` is on. Notes:

- **No Harmony patch needed** — it's a plain static field, read every frame, and never cleared, so
  one assignment holds for the whole session.
- **The handler always exists**: `GameHandler` calls `Singleton<DebugUIHandler>.Instance.RegisterPage(...)`
  unconditionally at startup, so the object is in the scene; only the flag was missing.
- Requires referencing `Zorro.Core.Runtime.dll` (the type is not in `Assembly-CSharp`).
- Local-only: console commands run on `Character.localCharacter`. Nothing is broadcast, and other
  players need neither the mod nor the flag.

### Aside: why hanging on a piton may look like it doesn't restore stamina

Reading this path answered a play question, so it's worth recording. Hanging **does** enable regen —
`Character.CanRegenStamina()` returns true whenever `data.currentClimbHandle` is set — but:

- Regen is `Time.fixedDeltaTime * 0.2f`, i.e. **0.2/s — identical to resting on the ground**. A
  piton buys you a place to rest mid-wall, not a faster refill.
- `GetMaxStamina()` is `max(1 - afflictions.statusSum, 0)`. With injury/hunger/cold/drowsy stacked,
  the white bar has nowhere to grow, so it reads as "regen is broken". (`ClearAll` in the console
  distinguishes the two instantly.)
- `CharacterClimbing.HandleClimbHandle()` lerps `handleOffset` toward the movement input and calls
  `CancelHandle()` once it exceeds 0.3 — about **0.35 s of held movement input** and you let go and
  transition to wall climbing, which *drains* stamina. Rest with the stick centred.
- Only `currentStamina` regenerates; `extraStamina` (campfire morale boost) does not.
- `ShittyPiton` starts cracking after a random 1–5 s of hang time and breaks after 4 cracks.

None of this is affected by any PeakResume patch.
