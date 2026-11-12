using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using SlayTheSpire2.LAN.Multiplayer.Components;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs.Screens
{
    //Daily, Blitz and Custom & Seeds are timeline unlocks the menus read straight from the save rather than from an
    //UnlockState. Dread is left to the game: its button only shows once its epoch is revealed, which TimelinePatch does.
    //The host submenu patch also covers LanMultiplayerHostSubmenu, which inherits RefreshButtons.
    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), "RefreshButtons")]
    internal class NMultiplayerHostSubmenuRefreshButtonsPatch
    {
        private static void Postfix(NSubmenuButton ____dailyButton, NSubmenuButton ____blitzButton,
            NSubmenuButton ____customButton)
        {
            ____dailyButton.SetEnabled(true);
            ____blitzButton.SetEnabled(true);
            ____customButton.SetEnabled(true);
        }
    }

    [HarmonyPatch(typeof(NSingleplayerSubmenu), "RefreshButtons")]
    internal class NSingleplayerSubmenuRefreshButtonsPatch
    {
        private static void Postfix(NSubmenuButton ____dailyButton, NSubmenuButton ____blitzButton,
            NSubmenuButton ____customButton)
        {
            ____dailyButton.SetEnabled(true);
            ____blitzButton.SetEnabled(true);
            ____customButton.SetEnabled(true);
            ____dailyButton.RefreshLabels();
            ____blitzButton.RefreshLabels();
            ____customButton.RefreshLabels();
        }
    }

    //Every button on the host submenu ends in StartHost, which hosts on Steam. On the LAN copy, host over LAN.
    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), nameof(NMultiplayerHostSubmenu.StartHost))]
    internal class NMultiplayerHostSubmenuStartHostPatch
    {
        private static bool Prefix(NMultiplayerHostSubmenu __instance, GameMode gameMode, bool isCustom)
        {
            if (__instance is not LanMultiplayerHostSubmenu lanMultiplayerHostSubmenu)
                return true;

            lanMultiplayerHostSubmenu.StartLanHost(gameMode, isCustom);

            return false;
        }
    }
}
