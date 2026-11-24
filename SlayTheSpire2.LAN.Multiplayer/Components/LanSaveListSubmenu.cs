using System.Globalization;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Services;

namespace SlayTheSpire2.LAN.Multiplayer.Components
{
    // The saved LAN runs, newest first, each with a button to host it and one to delete it. Wears the join screen's
    // layout (title, panel, back button, loading overlay) with a scrolling list of rows in place of its friend grid.
    internal partial class LanSaveListSubmenu : NSubmenu
    {
        private static readonly string ScenePath = SceneHelper.GetScenePath("screens/join_friend_submenu");

        private const int RowHeight = 104;

        private Control _loadingOverlay = null!;

        private MegaLabel _emptyLabel = null!;

        private VBoxContainer _rows = null!;

        private Control? _firstButton;

        private bool _busy;

        protected override Control? InitialFocusedControl => _firstButton;

        public static LanSaveListSubmenu? Create()
        {
            if (TestMode.IsOn)
                return null;

            var joinFriendScreen = PreloadManager.Cache.GetScene(ScenePath).Instantiate<NJoinFriendScreen>();
            var saveListSubmenu = new LanSaveListSubmenu();
            saveListSubmenu.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            SceneAdoptionHelper.Adopt(joinFriendScreen, saveListSubmenu);
            return saveListSubmenu;
        }

        public override void _Ready()
        {
            ConnectSignals();

            _loadingOverlay = GetNode<Control>("%LoadingOverlay");
            GetNode<MegaLabel>("TitleLabel").SetTextAutoSize(
                new LocString("main_menu_ui", "SlayTheSpire2.LAN.Multiplayer.SAVES_TITLE").GetFormattedText());
            GetNode<Control>("%RefreshButton").Visible = false;
            GetNode<Control>("%LoadingIndicator").Visible = false;
            GetNode<Control>("%ButtonContainer").Visible = false;

            _emptyLabel = GetNode<MegaLabel>("%NoFriendsText");
            _emptyLabel.SetTextAutoSize(
                new LocString("main_menu_ui", "SlayTheSpire2.LAN.Multiplayer.NO_SAVES").GetFormattedText());
            _emptyLabel.Visible = false;

            var scroll = new ScrollContainer
            {
                Name = "SaveScroll",
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                FollowFocus = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            _rows = new VBoxContainer { Name = "SaveRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _rows.AddThemeConstantOverride("separation", 8);
            scroll.AddChildSafely(_rows);
            GetNode("Panel/MarginContainer").AddChildSafely(scroll);
        }

        public override void OnSubmenuOpened()
        {
            base.OnSubmenuOpened();
            _busy = false;
            Refresh();
        }

        private void Refresh()
        {
            foreach (var row in _rows.GetChildren())
            {
                row.QueueFreeSafely();
            }

            _firstButton = null;
            var saves = LanRunSaveManagerService.Instance.ListSaves();
            _emptyLabel.Visible = saves.Count == 0;

            foreach (var save in saves)
            {
                _rows.AddChildSafely(CreateRow(save));
            }

            Callable.From(() => _firstButton?.TryGrabFocus()).CallDeferred();
        }

        private Control CreateRow(LanRunSaveManagerService.SaveInfo save)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, RowHeight) };
            row.AddThemeConstantOverride("separation", 12);

            var loadButton = CreateButton(DescribeSave(save), new Vector2(560, RowHeight));
            loadButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            loadButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Load(save)));
            row.AddChildSafely(loadButton);
            _firstButton ??= loadButton;

            var deleteButton = CreateButton(
                "[center]" + new LocString("main_menu_ui", "SlayTheSpire2.LAN.Multiplayer.DELETE").GetFormattedText() +
                "[/center]", new Vector2(200, RowHeight));
            deleteButton.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => TaskHelper.RunSafely(Delete(save))));
            row.AddChildSafely(deleteButton);

            return row;
        }

        private static NJoinFriendButton CreateButton(string text, Vector2 minimumSize)
        {
            var button = NJoinFriendButton.Create(0);
            button.CustomMinimumSize = minimumSize;
            button.Ready += () =>
            {
                button.GetNode<MegaRichTextLabel>("%Text").Text = text;

                // The background's end caps are 192 pixels each; a button narrower than both gets smaller ones.
                var image = button.GetNode<NinePatchRect>("Image");
                if (minimumSize.X < image.PatchMarginLeft + image.PatchMarginRight)
                {
                    var margin = (int)(minimumSize.X / 2) - 8;
                    image.PatchMarginLeft = margin;
                    image.PatchMarginRight = margin;
                }
            };
            return button;
        }

        private static string DescribeSave(LanRunSaveManagerService.SaveInfo save)
        {
            if (save.Run is not { } run)
                return $"[center][font_size=28]{save.Slot}\n({save.Status})[/font_size][/center]";

            var characters = string.Join(", ", run.Players.Select(player =>
            {
                try
                {
                    return player.CharacterId is { } characterId
                        ? ModelDb.GetById<CharacterModel>(characterId).Title.GetFormattedText()
                        : "?";
                }
                catch (Exception)
                {
                    return player.CharacterId?.ToString() ?? "?";
                }
            }));

            var modeKey = run.DailyTime.HasValue ? "DAILY_MP"
                : run.IsCustomGame ? "CUSTOM_MP"
                : run.GameMode switch
                {
                    GameMode.Blitz => "BLITZ_MP",
                    GameMode.Dread => "DREAD_MP",
                    _ => "STANDARD_MP"
                };
            var mode = new LocString("main_menu_ui", modeKey + ".title").GetFormattedText();

            var act = new LocString("main_menu_ui", "MULTIPLAYER_LOAD_MENU.ACT");
            act.Add("act", run.CurrentActIndex + 1);
            var floor = new LocString("main_menu_ui", "MULTIPLAYER_LOAD_MENU.FLOOR");
            floor.Add("floor", run.VisitedMapCoords.Count);
            var ascension = new LocString("main_menu_ui", "DAILY_RUN_MENU.ASCENSION");
            ascension.Add("ascension", run.Ascension);
            var savedAt = DateTimeOffset.FromUnixTimeSeconds(run.SaveTime).ToLocalTime()
                .ToString("g", CultureInfo.CurrentCulture);

            return $"[center][font_size=30]{Escape(characters)}  ·  {mode}[/font_size]\n" +
                   $"[font_size=24]{act.GetFormattedText()}  ·  {floor.GetFormattedText()}  ·  " +
                   $"{ascension.GetFormattedText()}  ·  {savedAt}[/font_size][/center]";
        }

        private static string Escape(string text)
        {
            return text.Replace("[", "[lb]");
        }

        private void Load(LanRunSaveManagerService.SaveInfo save)
        {
            if (_busy)
                return;

            var settingsModel = SettingsService.Instance.SettingsModel;
            if (!LanHostHelper.StartLoad(save.Slot, _loadingOverlay, _stack, settingsModel.HostPort,
                    settingsModel.HostMaxPlayers))
            {
                Refresh();
            }
        }

        private async Task Delete(LanRunSaveManagerService.SaveInfo save)
        {
            if (_busy)
                return;

            _busy = true;
            try
            {
                if (await LanHostHelper.TryAbandonLanRun(save.Slot))
                {
                    Refresh();
                }
            }
            finally
            {
                _busy = false;
            }
        }
    }
}
