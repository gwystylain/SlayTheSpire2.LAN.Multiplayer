using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using SlayTheSpire2.LAN.Multiplayer.Services;

namespace SlayTheSpire2.LAN.Multiplayer.Components
{
    // The join screen's list of games hosted on the local network. It scans while it's on screen and
    // shows a button per game that's still in its lobby.
    internal partial class LanGameList : VBoxContainer
    {
        private const string LocKeyPrefix = "SlayTheSpire2.LAN.Multiplayer.";

        private const ushort DefaultPort = 33771;

        private NJoinFriendRefreshButton _buttonTemplate = null!;

        private Action<LanDiscoveryService.FoundGame> _join = null!;

        private MegaLabel _title = null!;

        private MegaLabel _searching = null!;

        private VBoxContainer _buttons = null!;

        private string? _shownGames;

        public static LanGameList Create(NJoinFriendRefreshButton buttonTemplate, MegaLabel labelTemplate,
            Action<LanDiscoveryService.FoundGame> join)
        {
            var lanGameList = new LanGameList
            {
                _buttonTemplate = buttonTemplate,
                _join = join,
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter
            };
            lanGameList.AddThemeConstantOverride("separation", 12);

            lanGameList._title = (MegaLabel)labelTemplate.Duplicate();
            lanGameList._title.CustomMinimumSize = new Vector2(300, 0);
            lanGameList.AddChildSafely(lanGameList._title);

            lanGameList._buttons = new VBoxContainer();
            lanGameList._buttons.AddThemeConstantOverride("separation", 12);
            lanGameList.AddChildSafely(lanGameList._buttons);

            lanGameList._searching = (MegaLabel)labelTemplate.Duplicate();
            lanGameList._searching.CustomMinimumSize = new Vector2(300, 0);
            lanGameList._searching.AutoSizeEnabled = false;
            lanGameList._searching.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 22);
            lanGameList._searching.Modulate = new Color(Colors.White, 0.6f);
            lanGameList.AddChildSafely(lanGameList._searching);

            return lanGameList;
        }

        public override void _Ready()
        {
            base._Ready();
            RefreshLabels();
        }

        public override void _Process(double delta)
        {
            if (!IsVisibleInTree())
            {
                LanDiscoveryService.Instance.StopScanning();
                _shownGames = null;
                return;
            }

            var games = LanDiscoveryService.Instance.Scan()
                .Where(game => !game.InRun)
                .OrderBy(game => game.HostName)
                .ThenBy(game => game.Address)
                .ToList();
            var shownGames = string.Join("\n", games.Select(game => $"{game.Address}:{game.Port} {game.HostName}"));
            if (shownGames == _shownGames)
                return;

            _shownGames = shownGames;
            foreach (var child in _buttons.GetChildren())
            {
                child.QueueFreeSafely();
            }

            foreach (var game in games)
            {
                var address = game.Port == DefaultPort ? game.Address : $"{game.Address}:{game.Port}";
                var button = LanGameButton.Create(_buttonTemplate, $"{game.HostName}  {address}");
                _buttons.AddChildSafely(button);
                button.Connect(NClickableControl.SignalName.Released,
                    Callable.From<NClickableControl>(_ => _join(game)));
            }

            _searching.Visible = games.Count == 0;
        }

        public override void _ExitTree()
        {
            LanDiscoveryService.Instance.StopScanning();
            base._ExitTree();
        }

        public override void _Notification(int what)
        {
            if ((long)what == NotificationTranslationChanged && IsNodeReady())
            {
                RefreshLabels();
            }

            base._Notification(what);
        }

        private void RefreshLabels()
        {
            _title.SetTextAutoSize(new LocString("main_menu_ui", LocKeyPrefix + "LAN_GAMES_TITLE").GetFormattedText());
            _searching.SetTextAutoSize(new LocString("main_menu_ui", LocKeyPrefix + "LAN_GAMES_SEARCHING")
                .GetFormattedText());
        }
    }
}
