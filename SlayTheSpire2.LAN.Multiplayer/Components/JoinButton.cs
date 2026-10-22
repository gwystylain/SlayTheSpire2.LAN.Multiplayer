using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SlayTheSpire2.LAN.Multiplayer.Components
{
    internal partial class JoinButton : NJoinFriendRefreshButton
    {
        protected override string[] Hotkeys => [MegaInput.viewMap];

        public static JoinButton Create(NJoinFriendRefreshButton joinFriendRefreshButton)
        {
            var joinButton = new JoinButton
            {
                CustomMinimumSize = new Vector2(150, 50)
            };

            CopyLook(joinButton, joinFriendRefreshButton);

            //NButton finds the icon as %HotkeyIcon, which only resolves once the button owns it
            var hotkeyIcon = joinButton.GetNode<Control>("HotkeyIcon");
            hotkeyIcon.Owner = joinButton;
            //Its offsets are laid out for the larger Refresh button; keep the same spot relative to the corner
            hotkeyIcon.Position += (joinButton.CustomMinimumSize - joinFriendRefreshButton.Size) / 2;

            return joinButton;
        }

        // Gives a new button the Refresh button's background, label and hotkey icon.
        public static void CopyLook(NJoinFriendRefreshButton button, NJoinFriendRefreshButton joinFriendRefreshButton)
        {
            button.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            button.MouseFilter = MouseFilterEnum.Stop;
            button.Material = (Material)joinFriendRefreshButton.Material.Duplicate();

            var background = new NinePatchRect
            {
                Name = "Background", MouseFilter = MouseFilterEnum.Ignore,
                Texture = GD.Load<CompressedTexture2D>("res://images/ui/tiny_nine_patch.png"), PatchMarginLeft = 12,
                PatchMarginTop = 12, PatchMarginRight = 12, PatchMarginBottom = 12,
                Modulate = joinFriendRefreshButton.SelfModulate,
                Material = button.Material
            };

            button.AddChildSafely(background);
            background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

            foreach (var child in joinFriendRefreshButton.GetChildren())
            {
                button.AddChildSafely(child.Duplicate());
            }
        }

        public override void _Ready()
        {
            base._Ready();

            var node = GetNode<MegaLabel>("Label");
            node.SetTextAutoSize(new LocString("main_menu_ui", "JOIN.title").GetFormattedText());
        }
    }
}