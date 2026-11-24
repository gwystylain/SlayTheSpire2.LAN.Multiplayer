using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Models;
using Steamworks;
using SteamClient = MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient;

namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // Restarts the current encounter (a fight, event, shop, rest site...) for everyone by doing what players would
    // do by hand. The host tells the clients, saves and quits, hosts the save again and readies up once everyone is
    // back; the save was written when the room was entered, so loading it starts the room over. Each client skips
    // the "host left" popup, opens the join screen, joins the host as soon as it's back, and readies up: a LAN host
    // by its address in the LAN list, a Steam host by the new lobby its Steam status shows. If a step doesn't happen
    // within a couple of minutes, it gives up and leaves the rest to the players.
    internal class LanRestartService
    {
        private static readonly Lazy<LanRestartService> Lazy = new(() => new LanRestartService());

        public static LanRestartService Instance => Lazy.Value;

        private const ulong TimeoutMsec = 120000;

        private const int SteamSocketCloseWaitMsec = 2500;

        private const int MaxSteamJoinAttempts = 4;

        private const ulong SteamJoinRetryMsec = 2000;

        private enum Stage
        {
            None,
            HostRehosting,
            HostWaitingForPlayers,
            ClientRejoining,
            ClientInLobby
        }

        private Stage _stage;

        private ulong _startedMsec;

        private PlatformType _platform;

        private string? _hostAddress;

        private ulong _hostSteamId;

        private ulong? _oldLobbyId;

        private int _steamJoinAttempts;

        private ulong _nextSteamJoinMsec;

        private LanRestartService()
        {
        }

        // LAN games and Steam invite games can restart an encounter; everyone in them runs this mod.
        public static bool CanRestart(INetGameService netService)
        {
            return netService.Platform is PlatformType.None or PlatformType.Steam;
        }

        private bool IsActive(Stage stage)
        {
            if (_stage == Stage.None || Time.GetTicksMsec() - _startedMsec <= TimeoutMsec)
                return _stage == stage;

            Log.Warn($"Gave up restarting the encounter at {_stage}");
            _stage = Stage.None;
            return false;
        }

        public async Task RestartAsHost()
        {
            _platform = RunManager.Instance.NetService.Platform;
            Log.Info($"Restarting the encounter: hosting the save again ({_platform})");
            RunManager.Instance.NetService.SendMessage(new LanRestartEncounterMessage());
            _stage = Stage.HostRehosting;
            _startedMsec = Time.GetTicksMsec();
            NRunMusicController.Instance?.StopMusic();
            await NGame.Instance!.ReturnToMainMenu();
        }

        public void HandleLanRestartEncounterMessage(LanRestartEncounterMessage message, ulong senderId)
        {
            var netService = RunManager.Instance.NetService;
            _platform = netService.Platform;
            if (_platform == PlatformType.Steam)
            {
                if (netService is not NetClientGameService { NetClient: SteamClient client })
                    return;

                _hostSteamId = client.HostNetId;
                _oldLobbyId = client.LobbyId?.m_SteamID;
                _steamJoinAttempts = 0;
                _nextSteamJoinMsec = 0;
                Log.Info($"Steam host {_hostSteamId} is restarting the encounter, joining its new lobby once it's up");
            }
            else
            {
                _hostAddress = LanClientResumeService.Instance.HostAddress(netService);
                if (_hostAddress == null)
                    return;

                Log.Info($"Host at {_hostAddress} is restarting the encounter, rejoining it once it's back");
            }

            _stage = Stage.ClientRejoining;
            _startedMsec = Time.GetTicksMsec();
        }

        // The host quitting to restart shouldn't read as an error, nor a failed join to its new Steam lobby that's
        // going to be tried again.
        public bool ShouldHideError(NetErrorInfo info)
        {
            return (info.GetReason() == NetError.Quit && IsActive(Stage.ClientRejoining)) ||
                   (_platform == PlatformType.Steam && _steamJoinAttempts < MaxSteamJoinAttempts &&
                    IsActive(Stage.ClientInLobby));
        }

        public void OnMainMenuReady(NMainMenu mainMenu)
        {
            if (IsActive(Stage.HostRehosting))
            {
                var multiplayerSubmenu = mainMenu.OpenMultiplayerSubmenu();
                _stage = Stage.HostWaitingForPlayers;
                if (_platform == PlatformType.Steam)
                {
                    TaskHelper.RunSafely(RehostSteamSaveSoon(multiplayerSubmenu));
                }
                else if (!RehostLanSave(multiplayerSubmenu))
                {
                    _stage = Stage.None;
                }
            }
            else if (IsActive(Stage.ClientRejoining))
            {
                mainMenu.OpenMultiplayerSubmenu().OnJoinFriendsPressed();
            }
        }

        private static bool RehostLanSave(NMultiplayerSubmenu multiplayerSubmenu)
        {
            var saveManager = LanRunSaveManagerService.Instance;
            if (saveManager.CurrentSlot is not { } slot || !saveManager.SlotExists(slot))
            {
                Log.Warn("No LAN save to host for the restart");
                return false;
            }

            var traverse = Traverse.Create(multiplayerSubmenu);
            var settingsModel = SettingsService.Instance.SettingsModel;
            return LanHostHelper.StartLoad(slot, traverse.Field("_loadingOverlay").GetValue<Control>(),
                traverse.Field("_stack").GetValue<NSubmenuStack>(), settingsModel.HostPort,
                settingsModel.HostMaxPlayers);
        }

        // Stopping a Steam host closes its listen socket a second later, and a new host opened before then gets no
        // working socket on the same virtual port, so players' connections to it time out. Wait for the old one.
        private async Task RehostSteamSaveSoon(NMultiplayerSubmenu multiplayerSubmenu)
        {
            await Task.Delay(SteamSocketCloseWaitMsec);
            if (!IsActive(Stage.HostWaitingForPlayers) || !GodotObject.IsInstanceValid(multiplayerSubmenu))
                return;

            if (!RehostSteamSave(multiplayerSubmenu))
            {
                _stage = Stage.None;
            }
        }

        // What the game's own Load button does for a Steam multiplayer save.
        private static bool RehostSteamSave(NMultiplayerSubmenu multiplayerSubmenu)
        {
            var readSaveResult = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(
                PlatformUtil.GetLocalPlayerId(PlatformType.Steam));
            if (!readSaveResult.Success || readSaveResult.SaveData == null)
            {
                Log.Warn($"No Steam multiplayer save to host for the restart (status={readSaveResult.Status})");
                var errorPopup = NErrorPopup.CreateForRunSave(readSaveResult.Status);
                if (errorPopup != null)
                {
                    NModalContainer.Instance?.Add(errorPopup);
                }

                return false;
            }

            multiplayerSubmenu.StartHost(readSaveResult.SaveData);
            return true;
        }

        // Called by the join screen's LAN list with the games it found.
        public void JoinRestartedGame(IReadOnlyList<LanDiscoveryService.FoundGame> games,
            Action<LanDiscoveryService.FoundGame> join)
        {
            if (_platform != PlatformType.None || !IsActive(Stage.ClientRejoining))
                return;

            var game = games.FirstOrDefault(game => game.Address == _hostAddress);
            if (game == null)
                return;

            _stage = Stage.ClientInLobby;
            join(game);
        }

        // Called every frame by the join screen: joins the Steam host's new lobby as soon as its Steam status shows
        // one. The lobby it left can still show for a moment, so that one doesn't count.
        public void JoinRestartedSteamGame(NJoinFriendScreen screen)
        {
            if (_platform != PlatformType.Steam || !IsActive(Stage.ClientRejoining) ||
                Time.GetTicksMsec() < _nextSteamJoinMsec)
                return;

            if (!SteamFriends.GetFriendGamePlayed(new CSteamID(_hostSteamId), out var gameInfo))
                return;

            var lobbyId = gameInfo.m_steamIDLobby.m_SteamID;
            if (lobbyId == 0 || lobbyId == _oldLobbyId)
                return;

            _stage = Stage.ClientInLobby;
            _steamJoinAttempts++;
            Log.Info($"Joining Steam host {_hostSteamId}'s new lobby {lobbyId} (attempt {_steamJoinAttempts})");
            TaskHelper.RunSafely(JoinSteamLobby(screen, lobbyId));
        }

        // A join that fails leaves the join screen up; try again a little later while attempts remain.
        private async Task JoinSteamLobby(NJoinFriendScreen screen, ulong lobbyId)
        {
            await screen.JoinGameAsync(SteamClientConnectionInitializer.FromLobby(lobbyId));
            if (!IsActive(Stage.ClientInLobby) || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree() ||
                _steamJoinAttempts >= MaxSteamJoinAttempts)
                return;

            Log.Info($"Couldn't join lobby {lobbyId} yet, trying again");
            _stage = Stage.ClientRejoining;
            _nextSteamJoinMsec = Time.GetTicksMsec() + SteamJoinRetryMsec;
        }

        // Called every frame by the load lobby screen; presses its confirm button once it's this player's turn.
        public void ReadyUp(LoadRunLobby? lobby, NButton confirmButton)
        {
            if (lobby == null || !confirmButton.IsEnabled)
                return;

            var ready = lobby.NetService.Type switch
            {
                NetGameType.Host => IsActive(Stage.HostWaitingForPlayers) &&
                                    lobby.PlayerCount >= lobby.Run.Players.Count,
                NetGameType.Client => IsActive(Stage.ClientInLobby),
                _ => false
            };
            if (!ready)
                return;

            _stage = Stage.None;
            confirmButton.EmitSignal(NClickableControl.SignalName.Released, confirmButton);
        }
    }
}
