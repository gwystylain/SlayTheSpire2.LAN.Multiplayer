using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using SlayTheSpire2.LAN.Multiplayer.Models;
using SlayTheSpire2.LAN.Multiplayer.Services;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs
{
    //A LAN host's pause menu gets a Restart Encounter button above Save and Quit. It's added before the menu connects
    //its buttons' signals, since a copy made afterwards would also save and quit.
    [HarmonyPatch(typeof(NPauseMenu), "_Ready")]
    internal class NPauseMenuReadyPatch
    {
        public const string ButtonName = "RestartEncounter";

        private static void Prefix(NPauseMenu __instance)
        {
            var buttonContainer = __instance.GetNode<Control>("%ButtonContainer");
            var saveAndQuitButton = buttonContainer.GetNode<NPauseMenuButton>("SaveAndQuit");

            var restartEncounterButton = (NPauseMenuButton)saveAndQuitButton.Duplicate();
            restartEncounterButton.Name = ButtonName;
            buttonContainer.AddChildSafely(restartEncounterButton);
            buttonContainer.MoveChild(restartEncounterButton, saveAndQuitButton.GetIndex());

            restartEncounterButton.GetNode<MegaLabel>("Label").SetTextAutoSize(
                new LocString("gameplay_ui", "SlayTheSpire2.LAN.Multiplayer.RESTART_ENCOUNTER").GetFormattedText());
            restartEncounterButton.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => TaskHelper.RunSafely(ConfirmRestart())));
        }

        private static async Task ConfirmRestart()
        {
            var nGenericPopup = NGenericPopup.Create();
            if (nGenericPopup == null)
                return;

            NModalContainer.Instance?.Add(nGenericPopup);
            if (await nGenericPopup.WaitForConfirmation(
                    new LocString("gameplay_ui", "SlayTheSpire2.LAN.Multiplayer.RESTART_ENCOUNTER_CONFIRM.body"),
                    new LocString("gameplay_ui", "SlayTheSpire2.LAN.Multiplayer.RESTART_ENCOUNTER_CONFIRM.header"),
                    new LocString("main_menu_ui", "GENERIC_POPUP.cancel"),
                    new LocString("main_menu_ui", "GENERIC_POPUP.confirm")))
            {
                await LanRestartService.Instance.RestartAsHost();
            }
        }
    }

    //Shows the button to LAN hosts, usable anywhere in a run that isn't over, and re-links controller focus through the
    //visible buttons.
    [HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.Initialize))]
    internal class NPauseMenuInitializePatch
    {
        private static void Postfix(Control ____buttonContainer, IRunState runState)
        {
            var restartEncounterButton = ____buttonContainer.GetNode<NPauseMenuButton>(NPauseMenuReadyPatch.ButtonName);
            restartEncounterButton.Visible = RunManager.Instance.NetService is
                { Type: NetGameType.Host, Platform: PlatformType.None };
            if (!runState.IsGameOver)
            {
                restartEncounterButton.Enable();
            }
            else
            {
                restartEncounterButton.Disable();
            }

            var buttons = ____buttonContainer.GetChildren().OfType<NPauseMenuButton>().Where(b => b.Visible).ToList();
            for (var i = 0; i < buttons.Count; i++)
            {
                buttons[i].FocusNeighborLeft = buttons[i].GetPath();
                buttons[i].FocusNeighborRight = buttons[i].GetPath();
                buttons[i].FocusNeighborTop = buttons[(i + buttons.Count - 1) % buttons.Count].GetPath();
                buttons[i].FocusNeighborBottom = buttons[(i + 1) % buttons.Count].GetPath();
            }
        }
    }

    [HarmonyPatch(typeof(RunLobby), MethodType.Constructor, typeof(GameMode), typeof(INetGameService),
        typeof(IRunLobbyListener), typeof(IPlayerCollection), typeof(IEnumerable<RunLobbyPlayer>))]
    internal class RunLobbyConstructorRestartEncounterPatch
    {
        private static void Postfix(INetGameService netService)
        {
            if (netService is { Type: NetGameType.Client, Platform: PlatformType.None })
            {
                netService.RegisterMessageHandler<LanRestartEncounterMessage>(LanRestartService.Instance
                    .HandleLanRestartEncounterMessage);
            }
        }
    }

    [HarmonyPatch(typeof(RunLobby), "Dispose")]
    internal class RunLobbyDisposeRestartEncounterPatch
    {
        private static void Prefix(INetGameService ____netService)
        {
            if (____netService is { Type: NetGameType.Client, Platform: PlatformType.None })
            {
                ____netService.UnregisterMessageHandler<LanRestartEncounterMessage>(LanRestartService.Instance
                    .HandleLanRestartEncounterMessage);
            }
        }
    }

    [HarmonyPatch(typeof(NErrorPopup), nameof(NErrorPopup.Create), typeof(NetErrorInfo))]
    internal class NErrorPopupCreatePatch
    {
        private static bool Prefix(NetErrorInfo info, ref NErrorPopup? __result)
        {
            if (!LanRestartService.Instance.ShouldHideError(info))
                return true;

            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(NMainMenu), "_Ready")]
    internal class NMainMenuReadyRestartEncounterPatch
    {
        private static void Postfix(NMainMenu __instance)
        {
            Callable.From(() => LanRestartService.Instance.OnMainMenuReady(__instance)).CallDeferred();
        }
    }

    [HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "_Process")]
    internal class NMultiplayerLoadGameScreenProcessPatch
    {
        private static void Postfix(LoadRunLobby? ____runLobby, NConfirmButton ____confirmButton)
        {
            LanRestartService.Instance.ReadyUp(____runLobby, ____confirmButton);
        }
    }
}
