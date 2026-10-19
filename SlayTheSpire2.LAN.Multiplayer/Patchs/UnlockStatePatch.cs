using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Unlocks;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs
{
    //Every unlock (characters, cards, relics, potions, ancients, acts) is read from an UnlockState, and every UnlockState
    //made from a save comes through this constructor. Filling in all epochs and encounters here makes the save count as
    //UnlockState.all, the same as a daily run, without writing anything back to the save itself. The run count is left
    //alone: it only switches off the first-run tutorial scripting, which is not an unlock.
    [HarmonyPatch(typeof(UnlockState), MethodType.Constructor, typeof(ProgressState))]
    internal class UnlockStateConstructorPatch
    {
        private static void Postfix(HashSet<string> ____unlockedEpochIds, HashSet<ModelId> ____encountersSeen)
        {
            ____unlockedEpochIds.UnionWith(EpochModel.AllEpochIds.Where(UnlockState.IsValidEpoch));
            ____encountersSeen.UnionWith(ModelDb.AllEncounters.Select(encounter => encounter.Id));
        }
    }
}
