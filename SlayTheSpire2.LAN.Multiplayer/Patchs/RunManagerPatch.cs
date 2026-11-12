using HarmonyLib;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SlayTheSpire2.LAN.Multiplayer.Services;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs
{
    [HarmonyPatch(typeof(RunManager), "CleanUp")]
    internal class RunManagerCleanUpPatch
    {
        private static void Postfix()
        {
            LanMapDrawingsService.Instance.DisableDrawingHashSet.Clear();
            LanPlayerNameService.Instance.SetDefaultPlayerNames();
        }
    }

    //When a run ends, the host deletes its multiplayer save. A LAN run's save is a separate file (see
    //LanRunSaveManagerService), so while a LAN run ends, that delete goes to the LAN save instead of the Steam
    //multiplayer slot. Everything else OnEnded does (progress, history, achievements, daily scores) is the game's own.
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
    internal class RunManagerOnEndedPatch
    {
        internal static bool EndingLanRun;

        private static void Prefix(RunManager __instance)
        {
            EndingLanRun = __instance.NetService.Platform == PlatformType.None;
        }

        private static void Finalizer()
        {
            EndingLanRun = false;
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteCurrentMultiplayerRun))]
    internal class SaveManagerDeleteCurrentMultiplayerRunPatch
    {
        private static bool Prefix()
        {
            if (!RunManagerOnEndedPatch.EndingLanRun)
                return true;

            LanRunSaveManagerService.Instance.DeleteCurrentMultiplayerRun();

            return false;
        }
    }
}
