using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using SlayTheSpire2.LAN.Multiplayer.Components;
using SlayTheSpire2.LAN.Multiplayer.Services;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs.Screens
{
    [HarmonyPatch(typeof(NJoinFriendScreen), "_Ready")]
    internal class NJoinFriendScreenReadyPatch
    {
        private static void Prefix(NJoinFriendScreen __instance)
        {
            var panel = __instance.GetNode<NinePatchRect>("Panel");

            var lanPanel = new NinePatchRect
            {
                Name = "LANPanel",
                Texture = panel.Texture,
                SelfModulate = panel.SelfModulate,
                PatchMarginTop = 12,
                PatchMarginBottom = 12,
                PatchMarginLeft = 12,
                PatchMarginRight = 12
            };

            lanPanel.SetAnchorsPreset(Control.LayoutPreset.Center);
            lanPanel.OffsetLeft = 450;
            lanPanel.OffsetTop = -338;
            lanPanel.OffsetRight = 790;
            lanPanel.OffsetBottom = 338;

            var vBoxContainer = new VBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Center
            };

            vBoxContainer.AddThemeConstantOverride("separation", 24);
            lanPanel.AddChildSafely(vBoxContainer);
            vBoxContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

            var addressLineEdit = new AddressLineEdit
            {
                Name = "AddressInput", Text = SettingsService.Instance.SettingsModel.IPAddress,
                Alignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(300, 50),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
            };

            vBoxContainer.AddChildSafely(addressLineEdit);

            var joinButton = JoinButton.Create(__instance.GetNode<NJoinFriendRefreshButton>("RefreshButton"));
            joinButton.Name = "JointButton";
            vBoxContainer.AddChildSafely(joinButton);

            void Join(string address, ushort port)
            {
                SettingsService.Instance.SettingsModel.IPAddress = addressLineEdit.Text;
                SettingsService.Instance.WriteSettings();

                DisplayServer.WindowSetTitle("Slay The Spire 2 (Client)");
                TaskHelper.RunSafely(
                    __instance.JoinGameAsync(new ENetClientConnectionInitializer(
                        SettingsService.Instance.SettingsModel.NetId, address, port)));
            }

            joinButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
            {
                var addressInfo = addressLineEdit.GetAddressInfo();

                if (!addressInfo.IsValid || addressInfo.Address == null)
                    return;

                Join(addressInfo.Address, addressInfo.Port ?? 33771);
            }));

            __instance.AddChildSafely(LanLobbyList.Create(__instance, game =>
            {
                addressLineEdit.Text = game.Port == 33771 ? game.Address : $"{game.Address}:{game.Port}";
                Join(game.Address, game.Port);
            }));

            var ipAddressLabel = (MegaLabel)__instance.GetNode("TitleLabel").Duplicate();
            ipAddressLabel.CustomMinimumSize = new Vector2(300, 0);
            ipAddressLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            lanPanel.AddChildSafely(ipAddressLabel);
            ipAddressLabel.SetTextAutoSize("LAN IP:");

            __instance.AddChildSafely(lanPanel);
        }
    }

    //A tap on the Deck's touchscreen can press a button twice (the touch, then the mouse click Godot emulates from
    //it), and two joins at once collide on the host: the second gets a new net ID that isn't in the save, and the
    //first fails. The loading overlay is up for as long as a join runs, so ignore joins while it is.
    [HarmonyPatch(typeof(NJoinFriendScreen), nameof(NJoinFriendScreen.JoinGameAsync))]
    internal class NJoinFriendScreenJoinGameAsyncPatch
    {
        private static bool Prefix(Control ____loadingOverlay, ref Task __result)
        {
            if (!____loadingOverlay.Visible)
                return true;

            Log.Warn("Already joining a game, ignoring another join");
            __result = Task.CompletedTask;
            return false;
        }
    }
}