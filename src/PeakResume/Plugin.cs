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

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Resume-on-death is " +
                        (EnableResumeOnDeath.Value ? "ENABLED." : "disabled."));
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
}
