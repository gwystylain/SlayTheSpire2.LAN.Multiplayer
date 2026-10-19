using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs
{
    //A multiplayer lobby's ascension cap is the lowest any player has unlocked. Only the host adds players, and the
    //list it builds is what every client receives, so raising each entry here lets the host pick any level for everyone.
    //Daily runs keep their fixed ascension; singleplayer caps come from character stats and are untouched.
    [HarmonyPatch(typeof(StartRunLobby), "TryAddPlayerInFirstAvailableSlot")]
    internal class StartRunLobbyTryAddPlayerInFirstAvailableSlotPatch
    {
        private static void Prefix(StartRunLobby __instance, ref int maxAscensionUnlocked)
        {
            if (__instance.NetService.Type.IsMultiplayer() && __instance.GameMode != GameMode.Daily)
            {
                maxAscensionUnlocked = AscensionManager.maxAscensionAllowed;
            }
        }
    }
}
