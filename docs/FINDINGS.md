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
