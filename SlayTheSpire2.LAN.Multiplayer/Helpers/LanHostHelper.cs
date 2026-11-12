using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Blitz;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.Dread;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SlayTheSpire2.LAN.Multiplayer.Services;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedType.Global
// ReSharper disable ClassNeverInstantiated.Global

namespace SlayTheSpire2.LAN.Multiplayer.Helpers
{
    internal class LanHostHelper
    {
        private static readonly Action<NMultiplayerSubmenu> UpdateButtons;

        static LanHostHelper()
        {
            UpdateButtons = AccessTools.MethodDelegate<Action<NMultiplayerSubmenu>>(
                typeof(NMultiplayerSubmenu).GetMethod("UpdateButtons",
                    BindingFlags.Instance | BindingFlags.NonPublic)!);
        }

        //Mirrors NMultiplayerHostSubmenu.StartHostAsync, hosting over ENet on the configured port and player count
        public static void StartHost(GameMode gameMode, bool isCustom, Control loadingOverlay, NSubmenuStack stack,
            ushort port, int maxPlayers)
        {
            loadingOverlay.Visible = true;
            try
            {
                var netService = new NetHostGameService(PeerVersionInfo.LocalDefault());
                NetErrorInfo? netErrorInfo = null;
                //Add one more max client to send the full lobby message
                netService.StartENetHost(port, maxPlayers + 1);
                Log.Info($"HostGame open on port:{port}");
                if (!netErrorInfo.HasValue)
                {
                    if (isCustom)
                    {
                        var customRunScreen = stack.GetSubmenuType<NCustomRunScreen>();
                        customRunScreen.InitializeMultiplayerAsHost(netService, maxPlayers);
                        stack.Push(customRunScreen);
                        return;
                    }

                    switch (gameMode)
                    {
                        case GameMode.Standard:
                        {
                            var characterSelectScreen = stack.GetSubmenuType<NCharacterSelectScreen>();
                            characterSelectScreen.InitializeMultiplayerAsHost(netService, maxPlayers);
                            stack.Push(characterSelectScreen);
                            break;
                        }
                        case GameMode.Daily:
                        {
                            var dailyRunScreen = stack.GetSubmenuType<NDailyRunScreen>();
                            dailyRunScreen.InitializeMultiplayerAsHost(netService);
                            stack.Push(dailyRunScreen);
                            break;
                        }
                        case GameMode.Blitz:
                        {
                            var blitzScreen = stack.GetSubmenuType<NBlitzScreen>();
                            blitzScreen.InitializeMultiplayerAsHost(netService, maxPlayers);
                            stack.Push(blitzScreen);
                            break;
                        }
                        case GameMode.Dread:
                        {
                            var dreadScreen = stack.GetSubmenuType<NDreadScreen>();
                            dreadScreen.InitializeMultiplayerAsHost(netService, maxPlayers);
                            stack.Push(dreadScreen);
                            break;
                        }
                        default:
                            throw new InvalidOperationException($"Can't host game mode {gameMode}");
                    }
                }
                else
                {
                    var nErrorPopup = NErrorPopup.Create(netErrorInfo.Value);
                    if (nErrorPopup != null)
                    {
                        NModalContainer.Instance?.Add(nErrorPopup);
                    }
                }
            }
            catch
            {
                var nErrorPopup2 = NErrorPopup.Create(new NetErrorInfo(NetError.InternalError, selfInitiated: false));
                if (nErrorPopup2 != null)
                {
                    NModalContainer.Instance?.Add(nErrorPopup2);
                }

                throw;
            }
            finally
            {
                loadingOverlay.Visible = false;
            }
        }

        public static void StartHost(SerializableRun run, Control loadingOverlay, NSubmenuStack stack, ushort port,
            int maxPlayers)
        {
            loadingOverlay.Visible = true;
            try
            {
                var netService = new NetHostGameService(PeerVersionInfo.LocalDefault());
                NetErrorInfo? netErrorInfo = null;
                netService.StartENetHost(port, maxPlayers + 1);
                Log.Info($"HostGame open on port:{port}");
                if (!netErrorInfo.HasValue)
                {
                    //Same routing as NMultiplayerSubmenu.StartHostAsync: Blitz and Dread load on the standard screen
                    if (run.GameMode == GameMode.Daily)
                    {
                        var dailyRunLoadScreen = stack.GetSubmenuType<NDailyRunLoadScreen>();
                        dailyRunLoadScreen.InitializeAsHost(netService, run);
                        stack.Push(dailyRunLoadScreen);
                    }
                    else if (run.IsCustomGame)
                    {
                        var customRunLoadScreen = stack.GetSubmenuType<NCustomRunLoadScreen>();
                        customRunLoadScreen.InitializeAsHost(netService, run);
                        stack.Push(customRunLoadScreen);
                    }
                    else
                    {
                        var multiplayerLoadGameScreen = stack.GetSubmenuType<NMultiplayerLoadGameScreen>();
                        multiplayerLoadGameScreen.InitializeAsHost(netService, run);
                        stack.Push(multiplayerLoadGameScreen);
                    }
                }
                else
                {
                    var nErrorPopup = NErrorPopup.Create(netErrorInfo.Value);
                    if (nErrorPopup != null)
                    {
                        NModalContainer.Instance?.Add(nErrorPopup);
                    }
                }
            }
            finally
            {
                loadingOverlay.Visible = false;
            }
        }

        public static void StartLoad(NSubmenuButton nSubmenuButton, Control loadingOverlay, NSubmenuStack stack,
            ushort port, int maxPlayers)
        {
            var readSaveResult =
                LanRunSaveManagerService.Instance.LoadAndCanonicalizeMultiplayerRunSave(
                    PlatformUtil.GetLocalPlayerId(PlatformType.None));
            if (!readSaveResult.Success || readSaveResult.SaveData == null)
            {
                Log.Warn($"LAN run save could not be loaded (status={readSaveResult.Status}), disabling button");
                nSubmenuButton.Disable();
                var modalToCreate = NErrorPopup.CreateForRunSave(readSaveResult.Status);
                if (modalToCreate != null)
                {
                    NModalContainer.Instance?.Add(modalToCreate);
                }

                NModalContainer.Instance?.ShowBackstop();
            }
            else
            {
                StartHost(readSaveResult.SaveData, loadingOverlay, stack, port, maxPlayers);
            }
        }

        public static async Task TryAbandonMultiplayerRun(NMultiplayerSubmenu instance)
        {
            var header = new LocString("main_menu_ui", "ABANDON_RUN_CONFIRMATION.header");
            var body = new LocString("main_menu_ui", "ABANDON_RUN_CONFIRMATION.body");
            var yesButton = new LocString("main_menu_ui", "GENERIC_POPUP.confirm");
            var noButton = new LocString("main_menu_ui", "GENERIC_POPUP.cancel");
            var nGenericPopup = NGenericPopup.Create();
            if (nGenericPopup != null)
            {
                NModalContainer.Instance?.Add(nGenericPopup);
            }

            if (nGenericPopup == null || !await nGenericPopup.WaitForConfirmation(body, header, noButton, yesButton))
                return;

            var localPlayerId = PlatformUtil.GetLocalPlayerId(PlatformType.None);
            var readSaveResult = LanRunSaveManagerService.Instance.LoadAndCanonicalizeMultiplayerRunSave(localPlayerId);
            if (readSaveResult is { Success: true, SaveData: not null })
            {
                try
                {
                    var saveData = readSaveResult.SaveData;
                    SaveManager.Instance.UpdateProgressWithRunData(saveData, victory: false);
                    RunHistoryUtilities.CreateRunHistoryEntry(saveData, victory: false, isAbandoned: true,
                        saveData.PlatformType);
                    if (saveData.DailyTime.HasValue)
                    {
                        var score = ScoreUtility.CalculateDailyScore(saveData, localPlayerId, isVictory: false);
                        _ = TaskHelper.RunSafely(DailyRunUtility.UploadScore(saveData.DailyTime.Value, score,
                            saveData.Players));
                    }
                }
                catch (Exception value)
                {
                    Log.Error($"ERROR: Failed to upload run history/metrics: {value}");
                }
            }
            else
            {
                Log.Error(
                    $"ERROR: Failed to load multiplayer run save: status={readSaveResult.Status}. Deleting current run...");
            }

            LanRunSaveManagerService.Instance.DeleteCurrentMultiplayerRun();
            UpdateButtons(instance);
        }
    }
}