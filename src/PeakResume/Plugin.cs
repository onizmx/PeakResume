using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Peak; // Peak.Quicksave
using Peak.Network; // NetworkingUtilities.MAX_PLAYERS
using Photon.Pun;
using UnityEngine;

namespace PeakResume
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.onizmx.peakresume";
        public const string PluginName = "PeakResume";
        public const string PluginVersion = "1.1.0";

        /// <summary>The player cap the game ships with; also the party size its item spawns are tuned for.</summary>
        public const int VanillaMaxPlayers = 4;

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> EnableResumeOnDeath;
        internal static ConfigEntry<bool> ResumeOnBoard;
        internal static ConfigEntry<bool> PersistCheckpoint;
        internal static ConfigEntry<int> MaxPlayers;
        internal static ConfigEntry<bool> ScaleItemSpawns;
        internal static ConfigEntry<float> ItemSpawnScale;

        private void Awake()
        {
            Log = Logger;

            EnableResumeOnDeath = Config.Bind(
                "General",
                "EnableResumeOnDeath",
                true,
                "When the whole party dies, keep the run's quicksave instead of deleting it, so you " +
                "can resume from the last campfire via the main-menu Continue button. A win still " +
                "clears the save normally.");

            ResumeOnBoard = Config.Bind(
                "General",
                "ResumeOnBoard",
                true,
                "In-session co-op resume: if a saved run exists, boarding the plane from the airport " +
                "continues that run (the whole party is restored at the last campfire) instead of " +
                "starting a fresh one. No one has to leave the lobby or re-accept invites. A win " +
                "destroys the save, so normal fresh runs are unaffected. Host-authoritative.");

            PersistCheckpoint = Config.Bind(
                "General",
                "PersistCheckpoint",
                true,
                "Vanilla consumes (deletes) the save the moment you resume it, so you'd only get one " +
                "retry per campfire. With this on, the checkpoint survives resuming — you can wipe " +
                "and retry the same campfire as many times as you need. Lighting the next campfire " +
                "moves the checkpoint forward; winning still clears it.");

            MaxPlayers = Config.Bind(
                "Party",
                "MaxPlayers",
                10,
                new ConfigDescription(
                    "Maximum players per lobby. The game hardcodes 4; this raises both the Photon room " +
                    "cap and the Steam lobby size (both are set by the host when the room is created, so " +
                    "only the host needs the mod). Note: past ~6 players expect some extra network lag — " +
                    "the game's sync traffic was budgeted for 4.",
                    new AcceptableValueRange<int>(1, 20)));

            ScaleItemSpawns = Config.Bind(
                "Party",
                "ScaleItemSpawns",
                true,
                "With more than 4 players, spawn proportionally more items from luggage and ground " +
                "spawners (the main item sources). Pre-placed scene items and special single-item spawns " +
                "stay at their vanilla (4-player) amounts. Host-side; scaled items are recorded in the " +
                "campfire autosave, so resume restores them too.");

            ItemSpawnScale = Config.Bind(
                "Party",
                "ItemSpawnScale",
                1.0f,
                new ConfigDescription(
                    "How strongly item spawns scale with extra players. 1.0 = fully proportional " +
                    "(10 players => 2.5x items), 0.5 = half as strong (10 players => 1.75x), 0 = off.",
                    new AcceptableValueRange<float>(0f, 2f)));

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Resume-on-death={Fmt(EnableResumeOnDeath)}, " +
                        $"resume-on-board={Fmt(ResumeOnBoard)}, persist-checkpoint={Fmt(PersistCheckpoint)}, " +
                        $"max-players={MaxPlayers.Value}, scale-item-spawns={Fmt(ScaleItemSpawns)} (x{ItemSpawnScale.Value:0.##}).");
        }

        private static string Fmt(ConfigEntry<bool> c)
        {
            return c.Value ? "ENABLED" : "disabled";
        }
    }

    /// <summary>
    /// Shared flag: while true, the game's Quicksave.DestroySaveData() is skipped. Armed briefly in
    /// two places — during a *losing* end-game (so the last campfire autosave survives a wipe) and
    /// during resume finalization (so a resumed checkpoint isn't consumed and can be retried). The
    /// two windows never overlap (a wipe and a run-start happen at different times).
    /// </summary>
    internal static class ResumeState
    {
        public static bool SuppressDestroy;
    }

    /// <summary>
    /// The game calls Character.RPCEndGame() on both win and loss, and its very first line deletes
    /// the quicksave. We only want to keep the save on a loss (a total party wipe). Detect that here
    /// (every character dead == nobody won) and arm the suppression for the duration of this method.
    /// </summary>
    [HarmonyPatch(typeof(Character), "RPCEndGame")]
    internal static class Patch_Character_RPCEndGame
    {
        private static bool IsPartyWipe()
        {
            var all = Character.AllCharacters;
            if (all == null || all.Count == 0)
                return false; // nothing to reason about — behave like vanilla

            foreach (var c in all)
            {
                if (c == null || c.data == null || !c.data.dead)
                    return false; // someone is alive (or unknown) => this is a win, not a wipe
            }
            return true;
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            bool wipe = Plugin.EnableResumeOnDeath.Value && IsPartyWipe();
            ResumeState.SuppressDestroy = wipe;
            if (wipe)
                Plugin.Log.LogInfo("Party wipe detected — preserving quicksave so the run can be resumed from the last campfire.");
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            ResumeState.SuppressDestroy = false;
        }
    }

    /// <summary>
    /// Skip Quicksave.DestroySaveData() only while a losing end-game is being processed. Every other
    /// caller (joining another lobby, starting a fresh run, load-time cleanup, winning) is untouched.
    /// </summary>
    [HarmonyPatch(typeof(Quicksave), "DestroySaveData")]
    internal static class Patch_Quicksave_DestroySaveData
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (ResumeState.SuppressDestroy)
            {
                Plugin.Log.LogInfo("Suppressed quicksave deletion on death — your run is still resumable.");
                return false; // don't run the original: keep quicksave.peak on disk
            }
            return true; // vanilla behaviour
        }
    }

    /// <summary>
    /// In-session co-op resume. LoadIslandMaster runs on the host when someone boards the plane at
    /// the airport, right before the gameplay scene loads. If a saved run exists, arm the game's own
    /// resume path (ShouldUseSaveData) so the whole party is restored at the last campfire — the
    /// host's CharacterSpawner seeds every player's reconnect record from the save and respawns them
    /// restored, without anyone leaving the lobby. A win has already destroyed the save, so fresh
    /// runs board normally. Host-only by construction (LoadIslandMaster is an RpcTarget.MasterClient
    /// call), so we never set the flag on clients (whose local quicksave file is unrelated).
    /// </summary>
    [HarmonyPatch(typeof(AirportCheckInKiosk), "LoadIslandMaster")]
    internal static class Patch_AirportCheckInKiosk_LoadIslandMaster
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (!Plugin.ResumeOnBoard.Value)
                return;
            if (Quicksave.ShouldUseSaveData)
                return; // already armed (e.g. native main-menu Continue) — don't double-arm
            if (Quicksave.Exists && Quicksave.TryLoadSave())
            {
                Quicksave.ShouldUseSaveData = true;
                Plugin.Log.LogInfo("Saved run found — boarding will resume it (whole party restored at the last campfire).");
            }
        }
    }

    /// <summary>
    /// Keep the checkpoint after resuming. Vanilla's FinalizeRunSetupAndSelfDestruct() ends with
    /// DestroySaveData(), which both deletes quicksave.peak and clears ShouldUseSaveData — so a
    /// resumed run has no checkpoint until you reach the next campfire (one retry only). We suppress
    /// just the file deletion for this call, then clear ShouldUseSaveData ourselves (the one side
    /// effect we still need). The save file stays on disk, so wiping again re-preserves it and you
    /// can retry the same campfire indefinitely; lighting the next campfire overwrites it forward,
    /// and a win still clears it via the (unsuppressed) RPCEndGame path.
    /// </summary>
    [HarmonyPatch(typeof(Quicksave), "FinalizeRunSetupAndSelfDestruct")]
    internal static class Patch_Quicksave_FinalizeRunSetupAndSelfDestruct
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (Plugin.PersistCheckpoint.Value)
                ResumeState.SuppressDestroy = true;
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (ResumeState.SuppressDestroy)
            {
                ResumeState.SuppressDestroy = false;
                // DestroySaveData() was skipped, so do its one still-needed effect: stop re-resuming.
                Quicksave.ShouldUseSaveData = false;
                Plugin.Log.LogInfo("Checkpoint preserved through resume — you can retry this campfire as many times as needed.");
            }
        }
    }

    /// <summary>
    /// Raise the game's hardcoded 4-player cap. NetworkingUtilities.MAX_PLAYERS is read in exactly two
    /// places, both host-side at room creation: the Photon RoomOptions.MaxPlayers and the Steam lobby
    /// size (SteamMatchmaking.CreateLobby). Patching the getter covers both, and since the cap is baked
    /// into the room when it's created, clients don't need the mod to join. Everything downstream that
    /// handles per-player state is list/dictionary-based or guarded (spawn points wrap via modulo, end
    /// screen and voice mixers skip players past their scene-authored 4 slots), so extra players degrade
    /// to cosmetic omissions, not errors.
    /// </summary>
    [HarmonyPatch(typeof(NetworkingUtilities), nameof(NetworkingUtilities.MAX_PLAYERS), MethodType.Getter)]
    internal static class Patch_NetworkingUtilities_MaxPlayers
    {
        [HarmonyPostfix]
        private static void Postfix(ref int __result)
        {
            __result = Plugin.MaxPlayers.Value;
        }
    }

    /// <summary>
    /// Scale item spawns with party size. The game's item quantity for luggage and ground spawners is
    /// simply "one item per scene-authored spawn spot", and every such spawner funnels through the
    /// virtual Spawner.GetSpawnSpots() to get that list — so appending offset clones of existing spots
    /// here scales the whole pipeline: pool selection, instantiation, and crucially the spawn tracking
    /// that feeds the campfire quicksave (tracking happens after spawning, so resumed runs restore the
    /// scaled items identically; the history-replay path doesn't call GetSpawnSpots, so nothing
    /// double-scales). Host-only by construction — spawning already early-outs on non-masters.
    /// Not covered (deliberately): pre-placed scene items (already at their 4-player max with a full
    /// lobby), SingleItemSpawner (single by design), BerryVine (own spot logic), FakeItem pickups
    /// (index-synced scene objects; adding any would break their sync).
    /// </summary>
    [HarmonyPatch(typeof(Spawner), "GetSpawnSpots")]
    internal static class Patch_Spawner_GetSpawnSpots
    {
        [HarmonyPostfix]
        private static void Postfix(Spawner __instance, ref List<Transform> __result)
        {
            if (!Plugin.ScaleItemSpawns.Value || __result == null || __result.Count == 0)
                return;
            if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient)
                return;
            int players = PhotonNetwork.CurrentRoom.PlayerCount;
            if (players <= Plugin.VanillaMaxPlayers)
                return;

            float factor = 1f + (players - Plugin.VanillaMaxPlayers) / (float)Plugin.VanillaMaxPlayers
                                * Plugin.ItemSpawnScale.Value;
            int extra = Mathf.RoundToInt(__result.Count * factor) - __result.Count;
            if (extra <= 0)
                return;

            // Never mutate the spawner's own serialized list — build a new one.
            var spots = new List<Transform>(__result);
            for (int i = 0; i < extra; i++)
            {
                Transform src = __result[i % __result.Count];
                // Items spawn kinematic (frozen in place), so cloned spots need a small offset or the
                // extra items would overlap the originals. Parent to the source spot so the helper
                // objects are cleaned up with the scene.
                var go = new GameObject("PeakResume_ExtraSpawnSpot");
                Vector2 ring = UnityEngine.Random.insideUnitCircle.normalized * 0.25f;
                go.transform.position = src.position + new Vector3(ring.x, 0.05f, ring.y);
                go.transform.rotation = src.rotation;
                go.transform.SetParent(src, worldPositionStays: true);
                spots.Add(go.transform);
            }
            Plugin.Log.LogInfo($"Scaled {__instance.gameObject.name} item spawns {__result.Count} -> {spots.Count} for {players} players.");
            __result = spots;
        }
    }
}
