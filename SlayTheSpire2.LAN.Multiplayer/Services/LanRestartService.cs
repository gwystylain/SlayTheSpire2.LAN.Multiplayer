using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using SlayTheSpire2.LAN.Multiplayer.Models;

namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // Restarts the current fight for everyone by doing what players would do by hand. The host tells the
    // clients, saves and quits, hosts the save again and readies up once everyone is back; the save was written
    // when the fight's room was entered, so loading it starts the fight over. Each client skips the "host left"
    // popup, opens the join screen, joins the host as soon as its lobby shows up there, and readies up. If a step
    // doesn't happen within a couple of minutes, it gives up and leaves the rest to the players.
    internal class LanRestartService
    {
        private static readonly Lazy<LanRestartService> Lazy = new(() => new LanRestartService());

        public static LanRestartService Instance => Lazy.Value;

        private const ulong TimeoutMsec = 120000;

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

        private string? _hostAddress;

        private LanRestartService()
        {
        }

        private bool IsActive(Stage stage)
        {
            if (_stage == Stage.None || Time.GetTicksMsec() - _startedMsec <= TimeoutMsec)
                return _stage == stage;

            Log.Warn($"Gave up restarting the fight at {_stage}");
            _stage = Stage.None;
            return false;
        }

        public async Task RestartAsHost()
        {
            Log.Info("Restarting the fight: hosting the save again");
            RunManager.Instance.NetService.SendMessage(new LanRestartFightMessage());
            _stage = Stage.HostRehosting;
            _startedMsec = Time.GetTicksMsec();
            NRunMusicController.Instance?.StopMusic();
            await NGame.Instance!.ReturnToMainMenu();
        }

        public void HandleLanRestartFightMessage(LanRestartFightMessage message, ulong senderId)
        {
            _hostAddress = LanClientResumeService.Instance.HostAddress(RunManager.Instance.NetService);
            if (_hostAddress == null)
                return;

            Log.Info($"Host at {_hostAddress} is restarting the fight, rejoining it once it's back");
            _stage = Stage.ClientRejoining;
            _startedMsec = Time.GetTicksMsec();
        }

        // The host quitting to restart shouldn't read as an error.
        public bool ShouldHideError(NetErrorInfo info)
        {
            return info.GetReason() == NetError.Quit && IsActive(Stage.ClientRejoining);
        }

        public void OnMainMenuReady(NMainMenu mainMenu)
        {
            if (IsActive(Stage.HostRehosting))
            {
                mainMenu.OpenMultiplayerSubmenu();
                var loadButton = LanMultiplayerSubmenuButtonService.Instance.LanLoadButton;
                if (!GodotObject.IsInstanceValid(loadButton) || !loadButton!.Visible)
                {
                    Log.Warn("No LAN save to host for the restart");
                    _stage = Stage.None;
                    return;
                }

                _stage = Stage.HostWaitingForPlayers;
                loadButton.EmitSignal(NClickableControl.SignalName.Released, loadButton);
            }
            else if (IsActive(Stage.ClientRejoining))
            {
                mainMenu.OpenMultiplayerSubmenu().OnJoinFriendsPressed();
            }
        }

        // Called by the join screen's LAN list with the games it found.
        public void JoinRestartedGame(IReadOnlyList<LanDiscoveryService.FoundGame> games,
            Action<LanDiscoveryService.FoundGame> join)
        {
            if (!IsActive(Stage.ClientRejoining))
                return;

            var game = games.FirstOrDefault(game => game.Address == _hostAddress);
            if (game == null)
                return;

            _stage = Stage.ClientInLobby;
            join(game);
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
