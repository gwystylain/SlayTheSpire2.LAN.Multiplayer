using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Unlocks;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs
{
    //While an epoch is obtained but not yet revealed, the main menu disables singleplayer, multiplayer and the
    //compendium until it's revealed on the timeline. UnlockStatePatch already counts every epoch as unlocked, so
    //clicking through them gates nothing: mark them all revealed in the (modded profile's) progress save, the way the
    //dev console's "unlock epochs" does, right before the menu decides.
    [HarmonyPatch(typeof(NMainMenu), "UpdateTimelineButtonBehavior")]
    internal class NMainMenuUpdateTimelineButtonBehaviorPatch
    {
        private static void Prefix()
        {
            var revealed = SaveManager.Instance.Progress.Epochs
                .Where(epoch => epoch.State == EpochState.Revealed)
                .Select(epoch => epoch.Id)
                .ToHashSet();
            var unrevealed = EpochModel.AllEpochIds
                .Where(UnlockState.IsValidEpoch)
                .Where(id => !revealed.Contains(id))
                .ToList();
            if (unrevealed.Count == 0)
                return;

            foreach (var id in unrevealed)
            {
                SaveManager.Instance.ObtainEpochOverride(id, EpochState.Revealed);
            }

            SaveManager.Instance.SaveProgressFile();
            Log.Info($"Revealed {unrevealed.Count} timeline epochs");
        }
    }
}
