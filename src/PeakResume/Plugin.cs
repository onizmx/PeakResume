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
using Zorro.Core.CLI; // DebugUIHandler

namespace PeakResume
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.onizmx.peakresume";
        public const string PluginName = "PeakResume";
        public const string PluginVersion = "1.5.0";

        /// <summary>The player cap the game ships with; also the party size its item spawns are tuned for.</summary>
        public const int VanillaMaxPlayers = 4;

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> EnableResumeOnDeath;
        internal static ConfigEntry<bool> ResumeOnBoard;
        internal static ConfigEntry<bool> PersistCheckpoint;
        internal static ConfigEntry<int> MaxPlayers;
        internal static ConfigEntry<bool> ScaleItemSpawns;
        internal static ConfigEntry<float> ItemSpawnScale;
        internal static ConfigEntry<bool> CampfireFullHeal;
        internal static ConfigEntry<bool> FullHealOnRevive;

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

            CampfireFullHeal = Config.Bind(
                "Campfire",
                "FullHealOnLight",
                true,
                "Lighting a campfire fully heals the whole party, wherever they are: every affliction " +
                "(injury, hunger, cold, poison, drowsy, curse, spores, webs, thorns) is cleared and " +
                "the stamina bar is refilled. Vanilla only gives a small extra-stamina morale boost " +
                "and shaves 20% off the lighter's injury. Host-driven through the game's own RPCs, so " +
                "party members without the mod get healed too. Dead players are skipped — they still " +
                "respawn at a statue as usual.");

            FullHealOnRevive = Config.Bind(
                "Revive",
                "FullHealOnRevive",
                true,
                "Come back from a revive at full instead of pre-crippled. Vanilla re-applies Curse " +
                "0.05 + Hunger 0.3 to anyone it revives (scout statue, revive chest, skeleton, base " +
                "camp respawn — and the resume spawn too); this drops that penalty and fills the " +
                "stamina bar. Host-driven for the party, like the campfire heal, so members without " +
                "the mod get it as well.");

            // Unlock PEAK's built-in developer console (F1). Unconditional, and deliberately not a
            // config option: the console only appears if you press F1, so leaving it available costs
            // nothing during normal play, and gating it behind a setting meant a game restart just to
            // reach it. Static flag, read every frame in DebugUIHandler.Update(); nothing in the game
            // ever clears it, so setting it once here holds for the whole session. The handler itself
            // is always present (GameHandler registers debug pages on it unconditionally at startup).
            DebugUIHandler.AllowOpen = true;

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Resume-on-death={Fmt(EnableResumeOnDeath)}, " +
                        $"resume-on-board={Fmt(ResumeOnBoard)}, persist-checkpoint={Fmt(PersistCheckpoint)}, " +
                        $"max-players={MaxPlayers.Value}, scale-item-spawns={Fmt(ScaleItemSpawns)} (x{ItemSpawnScale.Value:0.##}), " +
                        $"campfire-full-heal={Fmt(CampfireFullHeal)}, revive-full-heal={Fmt(FullHealOnRevive)}, " +
                        "debug-console=ENABLED (F1).");
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
    /// Full heal when a campfire is lit. Vanilla hands out a small morale boost (extra stamina) and
    /// shaves 20% off the lighter's Injury; this clears every status and affliction for the whole
    /// party and refills the bar.
    ///
    /// Prefix, not postfix: Light_Rpc ends with Quicksave.SaveNow(), so healing first means the
    /// checkpoint records a healed party — resuming brings everyone back healed instead of broken.
    ///
    /// Light_Rpc is an RpcTarget.All call, so this runs on every client: each one heals its own
    /// character, which is the only side allowed to write it (SetStatus/AddStamina and friends are
    /// all photonView.IsMine-guarded). The host then pushes the same heal to everyone else through
    /// the game's own RPCs, so party members *without* the mod are healed as well:
    /// RPCA_Revive(false) clears statuses, afflictions and thorns (false = skip the post-revive
    /// Curse/Hunger penalty), a zeroing status delta takes care of Curse (RPCA_Revive's
    /// ClearAllStatus excludes it), and MoraleBoost fills the owner's extra-stamina bar — the main
    /// bar has no RPC, but it regenerates on its own once the statuses capping it are gone.
    ///
    /// Only a deliberate lighting counts: updateSegment is true just for Interact_CastFinished and
    /// DebugLight. The late-join sync and MapHandler's LightWithoutReveal pass false, so a resumed
    /// run doesn't hand out a free heal for the campfire it restores.
    /// </summary>
    [HarmonyPatch(typeof(Campfire), "Light_Rpc")]
    internal static class Patch_Campfire_Light_Rpc
    {
        [HarmonyPrefix]
        private static void Prefix(bool updateSegment)
        {
            if (!Plugin.CampfireFullHeal.Value || !updateSegment)
                return;

            HealSelf();
            if (PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
                HealEveryoneElse();
        }

        private static void HealSelf()
        {
            Character me = Character.localCharacter;
            if (me == null || me.data == null || me.data.dead)
                return;

            me.refs.afflictions.ClearAllStatus(excludeCurse: false);
            me.refs.afflictions.ClearAllAfflictions();
            me.refs.afflictions.RemoveAllThorns();
            me.AddStamina(1f);      // clamps to max stamina, which is a full bar now the statuses are gone
            me.SetExtraStamina(1f);
            Plugin.Log.LogInfo("Campfire lit — fully healed.");
        }

        private static void HealEveryoneElse()
        {
            var all = Character.AllCharacters;
            if (all == null)
                return;

            int healed = 0;
            foreach (var c in all)
            {
                if (c == null || c.IsLocal || c.data == null || c.data.dead)
                    continue; // dead players respawn at a statue; reviving them in place would bypass that

                c.photonView.RPC("RPCA_Revive", RpcTarget.All, false);

                // RPCA_Revive's ClearAllStatus() keeps Curse. RPC_ApplyStatusesFromFloatArray applies
                // per-status deltas and trusts the master client, so send a big negative Curse delta;
                // SubtractStatus clamps at zero, so overshooting is safe even if our copy is stale.
                var statuses = c.refs.afflictions.currentStatuses;
                if (statuses != null && statuses.Length > (int)CharacterAfflictions.STATUSTYPE.Curse)
                {
                    var deltas = new float[statuses.Length];
                    deltas[(int)CharacterAfflictions.STATUSTYPE.Curse] = -1f;
                    c.photonView.RPC("RPC_ApplyStatusesFromFloatArray", c.photonView.Owner, deltas);
                }

                // No RPC exists for the main stamina bar, so top up the extra bar instead.
                c.photonView.RPC("MoraleBoost", c.photonView.Owner, 1f, 1);
                healed++;
            }

            if (healed > 0)
                Plugin.Log.LogInfo($"Campfire lit — pushed a full heal to {healed} other player(s).");
        }
    }

    /// <summary>
    /// Drop the vanilla revive penalty. Every revive path (scout statue, revive chest, skeleton,
    /// base camp respawn, and the resume spawn in CharacterSpawner) funnels through this one method,
    /// which re-applies Curse 0.05 + Hunger 0.3 — so you come back already halfway to passing out.
    /// Skipping it is a single prefix that covers all of them.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.ApplyPostReviveStatus))]
    internal static class Patch_Character_ApplyPostReviveStatus
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !Plugin.FullHealOnRevive.Value; // false = don't run the original
        }
    }

    /// <summary>
    /// Fill the bar on revive. RPCA_Revive (reached directly or via RPCA_ReviveAtPosition) already
    /// clears statuses, afflictions and thorns, but nothing refills stamina, so a revived scout can
    /// stand up with an empty bar.
    ///
    /// It's an RpcTarget.All call, so every client runs it: each fills its own character, the only
    /// one it may write. The host then repairs anyone still running vanilla — their client applied
    /// the Curse/Hunger penalty locally (the prefix above only exists on modded clients), so undo it
    /// with a master-authoritative status delta and top up the extra-stamina bar. The correction is
    /// sent while handling the revive that the server already relayed, so it lands after it.
    /// </summary>
    [HarmonyPatch(typeof(Character), "RPCA_Revive")]
    internal static class Patch_Character_RPCA_Revive
    {
        [HarmonyPostfix]
        private static void Postfix(Character __instance)
        {
            if (!Plugin.FullHealOnRevive.Value || __instance == null || __instance.refs == null)
                return;

            if (__instance.IsLocal)
            {
                __instance.AddStamina(1f);
                __instance.SetExtraStamina(1f);
                Plugin.Log.LogInfo("Revived at full — no revive penalty applied.");
                return;
            }

            if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient)
                return;

            var statuses = __instance.refs.afflictions.currentStatuses;
            if (statuses != null && statuses.Length > (int)CharacterAfflictions.STATUSTYPE.Curse)
            {
                var deltas = new float[statuses.Length];
                deltas[(int)CharacterAfflictions.STATUSTYPE.Curse] = -1f;
                deltas[(int)CharacterAfflictions.STATUSTYPE.Hunger] = -1f;
                __instance.photonView.RPC("RPC_ApplyStatusesFromFloatArray", __instance.photonView.Owner, deltas);
            }
            __instance.photonView.RPC("MoraleBoost", __instance.photonView.Owner, 1f, 1);
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
