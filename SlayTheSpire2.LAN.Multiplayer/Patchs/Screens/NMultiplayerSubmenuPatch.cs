using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SlayTheSpire2.LAN.Multiplayer.Components;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Services;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs.Screens
{
    //The multiplayer menu gets LAN Host, which starts a new LAN run, and LAN Load, which opens the list of saved LAN
    //runs to continue or delete. Several LAN runs can be saved at once, so hosting a new one is always offered.
    [HarmonyPatch(typeof(NMultiplayerSubmenu), "_Ready")]
    internal class NMultiplayerSubmenuReadyPatch
    {
        private static void Prefix(NMultiplayerSubmenu __instance)
        {
            var buttonContainerNode = __instance.GetNode("ButtonContainer");

            var lanHostButton = (NSubmenuButton)buttonContainerNode.GetNode("HostButton").Duplicate();

            NSubmenuButtonDuplicateMaterial(lanHostButton);

            buttonContainerNode.AddChildSafely(lanHostButton);
            buttonContainerNode.MoveChild(lanHostButton, 1);

            lanHostButton.SetLocalization("HOST");
            lanHostButton.SetIcon("host");

            var lanHostTitle = Traverse.Create(lanHostButton).Field("_title").GetValue<MegaLabel>();
            lanHostTitle.SetTextAutoSize($"LAN {lanHostTitle.Text}");

            lanHostButton.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ =>
                {
                    var traverse = Traverse.Create(__instance);

                    var settingsModel = SettingsService.Instance.SettingsModel;

                    var stack = traverse.Field("_stack").GetValue<NSubmenuStack>();

                    if (SaveManager.Instance.Progress.NumberOfRuns > 0)
                    {
                        stack.PushSubmenuType<LanMultiplayerHostSubmenu>();
                    }
                    else
                    {
                        LanHostHelper.StartHost(GameMode.Standard, isCustom: false,
                            traverse.Field("_loadingOverlay").GetValue<Control>(), stack, settingsModel.HostPort,
                            settingsModel.HostMaxPlayers);
                    }
                }));

            LanMultiplayerSubmenuButtonService.Instance.LanHostButton = lanHostButton;

            var lanLoadButton = (NSubmenuButton)buttonContainerNode.GetNode("LoadButton").Duplicate();

            NSubmenuButtonDuplicateMaterial(lanLoadButton);

            buttonContainerNode.AddChildSafely(lanLoadButton);
            buttonContainerNode.MoveChild(lanLoadButton, 2);

            lanLoadButton.SetLocalization("MP_LOAD");
            lanLoadButton.SetIcon("load");

            var lanLoadButtonTitle = Traverse.Create(lanLoadButton).Field("_title").GetValue<MegaLabel>();
            lanLoadButtonTitle.SetTextAutoSize($"LAN {lanLoadButtonTitle.Text}");
            NMultiplayerSubmenuUpdateButtonsPatch.DescribeLoadButton(lanLoadButton);

            lanLoadButton.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ =>
                {
                    Traverse.Create(__instance).Field("_stack").GetValue<NSubmenuStack>()
                        .PushSubmenuType<LanSaveListSubmenu>();
                }));

            LanMultiplayerSubmenuButtonService.Instance.LanLoadButton = lanLoadButton;
        }

        private static void NSubmenuButtonDuplicateMaterial(NSubmenuButton nSubmenuButton)
        {
            var bgPanel = nSubmenuButton.GetNode<Control>("BgPanel");
            bgPanel.Material = (Material)bgPanel.Material.Duplicate();
        }
    }

    [HarmonyPatch(typeof(NMultiplayerSubmenu), "UpdateButtons")]
    internal class NMultiplayerSubmenuUpdateButtonsPatch
    {
        private static void Postfix()
        {
            var lanMultiplayerSubmenuButtonService = LanMultiplayerSubmenuButtonService.Instance;

            if (lanMultiplayerSubmenuButtonService.LanHostButton != null)
            {
                lanMultiplayerSubmenuButtonService.LanHostButton.Visible = true;
            }

            if (lanMultiplayerSubmenuButtonService.LanLoadButton != null)
            {
                lanMultiplayerSubmenuButtonService.LanLoadButton.Visible =
                    LanRunSaveManagerService.Instance.HasMultiplayerRunSave;
                DescribeLoadButton(lanMultiplayerSubmenuButtonService.LanLoadButton);
            }
        }

        //The game's description says it continues your last run; the LAN one opens a list of them.
        internal static void DescribeLoadButton(NSubmenuButton lanLoadButton)
        {
            Traverse.Create(lanLoadButton).Field("_description").GetValue<MegaRichTextLabel>().Text =
                new LocString("main_menu_ui", "SlayTheSpire2.LAN.Multiplayer.LAN_LOAD_DESCRIPTION")
                    .GetFormattedText();
        }
    }

    //Coming back from the save list (where the last save may have been deleted) or from a finished run.
    [HarmonyPatch(typeof(NMultiplayerSubmenu), "OnSubmenuShown")]
    internal class NMultiplayerSubmenuOnSubmenuShownPatch
    {
        private static void Postfix(NMultiplayerSubmenu __instance)
        {
            LanHostHelper.RefreshButtons(__instance);
        }
    }
}
