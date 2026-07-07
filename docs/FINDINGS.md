# Findings — how PEAK's run save/resume actually works

Decompiled from `Assembly-CSharp.dll`, game **v1.64.a**. Line numbers refer to the single-file
decompile under `decompiled/` (gitignored). This is the design basis for the mod.

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

### Persist-through-resume (`PersistCheckpoint`)

Vanilla `FinalizeRunSetupAndSelfDestruct` (line 88733) runs `RunManager.SetUpFromQuicksave`, sets
`timeOfDay`, then `DestroySaveData()` — which deletes the file **and** clears `ShouldUseSaveData`.
That "self-destruct" means a resumed run has no checkpoint until the next campfire: one retry only.

We prefix/postfix that method to arm `ResumeState.SuppressDestroy` around it, skipping only the file
deletion, then manually set `ShouldUseSaveData = false` in the postfix (the one side effect we still
need, so the load doesn't re-trigger). Net: the checkpoint file survives resuming, so repeated wipes
keep bouncing you back to the same campfire until you light the next one (moves it forward) or win
(clears it via the unsuppressed `RPCEndGame`).

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
