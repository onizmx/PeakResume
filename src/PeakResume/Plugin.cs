using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Peak; // Peak.Quicksave

namespace PeakResume
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.onizmx.peakresume";
        public const string PluginName = "PeakResume";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> EnableResumeOnDeath;
        internal static ConfigEntry<bool> ResumeOnBoard;

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

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Resume-on-death is " +
                        (EnableResumeOnDeath.Value ? "ENABLED" : "disabled") +
                        $"; resume-on-board is {(ResumeOnBoard.Value ? "ENABLED." : "disabled.")}");
        }
    }

    /// <summary>
    /// Shared flag: true only while we're inside a *losing* end-game, during which the game's
    /// automatic Quicksave.DestroySaveData() is suppressed so the last campfire autosave survives.
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
}
