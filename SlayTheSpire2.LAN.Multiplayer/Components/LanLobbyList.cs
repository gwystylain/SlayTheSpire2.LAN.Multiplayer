using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using SlayTheSpire2.LAN.Multiplayer.Services;

namespace SlayTheSpire2.LAN.Multiplayer.Components
{
    // Puts games hosted on the local network at the top of the join screen's lobby list, as the same buttons
    // Steam friends' lobbies get but tagged LAN, so a controller reaches them like any other lobby. Scans while
    // the screen is up. The screen's refresh clears the list, so missing buttons are added back each frame.
    internal partial class LanLobbyList : Node
    {
        private static readonly AccessTools.FieldRef<NJoinFriendScreen, Control> RefButtonContainer =
            AccessTools.FieldRefAccess<NJoinFriendScreen, Control>("_buttonContainer");

        private static readonly AccessTools.FieldRef<NJoinFriendScreen, MegaLabel> RefNoFriendsLabel =
            AccessTools.FieldRefAccess<NJoinFriendScreen, MegaLabel>("_noFriendsLabel");

        private readonly Dictionary<string, NJoinFriendButton> _buttons = new();

        private NJoinFriendScreen _screen = null!;

        private Action<LanDiscoveryService.FoundGame> _join = null!;

        public static LanLobbyList Create(NJoinFriendScreen screen, Action<LanDiscoveryService.FoundGame> join)
        {
            return new LanLobbyList
            {
                Name = "LanLobbyList",
                _screen = screen,
                _join = join
            };
        }

        public override void _Process(double delta)
        {
            if (!_screen.IsVisibleInTree())
            {
                LanDiscoveryService.Instance.StopScanning();
                return;
            }

            var container = RefButtonContainer(_screen);
            if (!IsInstanceValid(container))
                return;

            var games = LanDiscoveryService.Instance.Scan()
                .Where(game => !game.InRun)
                .OrderBy(game => game.HostName)
                .ThenBy(game => game.Address)
                .ToList();
            var keys = games.Select(Key).ToHashSet();

            var changed = false;
            foreach (var (key, button) in _buttons.ToList())
            {
                var gone = !IsInstanceValid(button) || button.IsQueuedForDeletion();
                if (!gone && keys.Contains(key))
                    continue;

                if (!gone)
                {
                    button.QueueFreeSafely();
                }

                _buttons.Remove(key);
                changed = true;
            }

            foreach (var game in games.Where(game => !_buttons.ContainsKey(Key(game))))
            {
                var button = CreateButton(game);
                container.AddChildSafely(button);
                _buttons[Key(game)] = button;
                changed = true;
            }

            if (changed)
            {
                var index = 0;
                foreach (var game in games)
                {
                    var button = _buttons[Key(game)];
                    if (button.GetParent() == container)
                    {
                        container.MoveChild(button, index++);
                    }
                }

                RefNoFriendsLabel(_screen).Visible =
                    container.GetChildren().All(child => child.IsQueuedForDeletion());
                ActiveScreenContext.Instance.Update();
            }

            LanRestartService.Instance.JoinRestartedGame(games, _join);
        }

        public override void _ExitTree()
        {
            LanDiscoveryService.Instance.StopScanning();
            base._ExitTree();
        }

        private NJoinFriendButton CreateButton(LanDiscoveryService.FoundGame game)
        {
            var button = NJoinFriendButton.Create(0);
            button.Ready += () => button.GetNode<MegaRichTextLabel>("%Text").Text =
                $"[center]{game.HostName.Replace("[", "[lb]")}  [color=#8fd0ff]LAN[/color][/center]";
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => _join(game)));
            return button;
        }

        private static string Key(LanDiscoveryService.FoundGame game)
        {
            return $"{game.Address}:{game.Port} {game.HostName}";
        }
    }
}
