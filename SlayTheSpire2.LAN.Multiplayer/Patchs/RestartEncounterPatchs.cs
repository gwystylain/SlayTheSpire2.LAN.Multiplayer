using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
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
    //its buttons' signals, and copied without signal connections either way, so it never also saves and quits.
    [HarmonyPatch(typeof(NPauseMenu), "_Ready")]
    internal class NPauseMenuReadyPatch
    {
        public const string ButtonName = "RestartEncounter";

        private const int DuplicateWithoutSignals = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts |
                                                          Node.DuplicateFlags.UseInstantiation);

        private static void Prefix(NPauseMenu __instance)
        {
            AddButton(__instance.GetNode<Control>("%ButtonContainer"));
        }

        internal static NPauseMenuButton? AddButton(Control buttonContainer)
        {
            var saveAndQuitButton = buttonContainer.GetNodeOrNull<NPauseMenuButton>("SaveAndQuit");
            if (saveAndQuitButton == null)
            {
                Log.Error("[LAN] The pause menu has no SaveAndQuit button to copy, so there's no Restart Encounter");
                return null;
            }

            var restartEncounterButton = (NPauseMenuButton)saveAndQuitButton.Duplicate(DuplicateWithoutSignals);
            restartEncounterButton.Name = ButtonName;
            buttonContainer.AddChildSafely(restartEncounterButton);
            buttonContainer.MoveChild(restartEncounterButton, saveAndQuitButton.GetIndex());

            restartEncounterButton.GetNode<MegaLabel>("Label").SetTextAutoSize(
                new LocString("gameplay_ui", "SlayTheSpire2.LAN.Multiplayer.RESTART_ENCOUNTER").GetFormattedText());
            restartEncounterButton.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => TaskHelper.RunSafely(ConfirmRestart())));

            return restartEncounterButton;
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
    //visible buttons. This runs both when the menu opens (OnSubmenuOpened, which every way of opening it goes through)
    //and after Initialize (which supplies the run state), and logs each opening, so a missing button can be traced.
    internal static class RestartEncounterButton
    {
        internal static void Refresh(Control buttonContainer, IRunState? runState, bool log)
        {
            var restartEncounterButton =
                buttonContainer.GetNodeOrNull<NPauseMenuButton>(NPauseMenuReadyPatch.ButtonName);
            if (restartEncounterButton == null)
            {
                Log.Warn("[LAN] The pause menu had no Restart Encounter button, adding it now");
                restartEncounterButton = NPauseMenuReadyPatch.AddButton(buttonContainer);
                if (restartEncounterButton == null)
                    return;
            }

            var netService = RunManager.Instance.NetService;
            var isLanHost = netService is { Type: NetGameType.Host, Platform: PlatformType.None };
            restartEncounterButton.Visible = isLanHost;

            if (runState != null)
            {
                if (!runState.IsGameOver)
                {
                    restartEncounterButton.Enable();
                }
                else
                {
                    restartEncounterButton.Disable();
                }
            }

            if (log)
            {
                Log.Info($"[LAN] Pause menu opened as {netService?.Type.ToString() ?? "no net service"} on " +
                         $"{netService?.Platform.ToString() ?? "no platform"}: Restart Encounter " +
                         (isLanHost ? "shown" : "hidden, it's for LAN hosts only"));
            }

            var buttons = buttonContainer.GetChildren().OfType<NPauseMenuButton>().Where(b => b.Visible).ToList();
            for (var i = 0; i < buttons.Count; i++)
            {
                buttons[i].FocusNeighborLeft = buttons[i].GetPath();
                buttons[i].FocusNeighborRight = buttons[i].GetPath();
                buttons[i].FocusNeighborTop = buttons[(i + buttons.Count - 1) % buttons.Count].GetPath();
                buttons[i].FocusNeighborBottom = buttons[(i + 1) % buttons.Count].GetPath();
            }
        }
    }

    [HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.OnSubmenuOpened))]
    internal class NPauseMenuOnSubmenuOpenedPatch
    {
        private static void Postfix(Control ____buttonContainer, IRunState? ____runState)
        {
            RestartEncounterButton.Refresh(____buttonContainer, ____runState, log: true);
        }
    }

    [HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.Initialize))]
    internal class NPauseMenuInitializePatch
    {
        private static void Postfix(Control ____buttonContainer, IRunState runState)
        {
            RestartEncounterButton.Refresh(____buttonContainer, runState, log: false);
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
