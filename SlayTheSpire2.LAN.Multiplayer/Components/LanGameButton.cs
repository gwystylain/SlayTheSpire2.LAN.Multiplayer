using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SlayTheSpire2.LAN.Multiplayer.Components
{
    // One game found on the network, in the join screen's list.
    internal partial class LanGameButton : NJoinFriendRefreshButton
    {
        private string _text = "";

        // No hotkey: several of these can be on screen at once.
        protected override string[] Hotkeys => [];

        public static LanGameButton Create(NJoinFriendRefreshButton joinFriendRefreshButton, string text)
        {
            var lanGameButton = new LanGameButton
            {
                _text = text,
                CustomMinimumSize = new Vector2(300, 50)
            };

            JoinButton.CopyLook(lanGameButton, joinFriendRefreshButton);
            lanGameButton.GetNodeOrNull("HotkeyIcon")?.Free();

            return lanGameButton;
        }

        public override void _Ready()
        {
            base._Ready();

            GetNode<MegaLabel>("Label").SetTextAutoSize(_text);
        }
    }
}
