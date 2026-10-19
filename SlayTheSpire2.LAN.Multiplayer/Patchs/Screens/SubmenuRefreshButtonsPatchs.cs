using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs.Screens
{
    //Daily and Custom & Seeds are timeline unlocks the menus read straight from the save rather than from an UnlockState.
    //The host submenu patch also covers LanMultiplayerHostSubmenu, which inherits RefreshButtons.
    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), "RefreshButtons")]
    internal class NMultiplayerHostSubmenuRefreshButtonsPatch
    {
        private static void Postfix(NSubmenuButton ____dailyButton, NSubmenuButton ____customButton)
        {
            ____dailyButton.SetEnabled(true);
            ____customButton.SetEnabled(true);
        }
    }

    [HarmonyPatch(typeof(NSingleplayerSubmenu), "RefreshButtons")]
    internal class NSingleplayerSubmenuRefreshButtonsPatch
    {
        private static void Postfix(NSubmenuButton ____dailyButton, NSubmenuButton ____customButton)
        {
            ____dailyButton.SetEnabled(true);
            ____customButton.SetEnabled(true);
            ____dailyButton.RefreshLabels();
            ____customButton.RefreshLabels();
        }
    }
}
