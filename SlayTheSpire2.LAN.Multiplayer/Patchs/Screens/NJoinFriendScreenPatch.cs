using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
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

            var refreshButton = __instance.GetNode<NJoinFriendRefreshButton>("RefreshButton");
            var joinButton = JoinButton.Create(refreshButton);
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

            var titleLabel = __instance.GetNode<MegaLabel>("TitleLabel");

            vBoxContainer.AddChildSafely(LanGameList.Create(refreshButton, titleLabel, game =>
            {
                addressLineEdit.Text = game.Port == 33771 ? game.Address : $"{game.Address}:{game.Port}";
                Join(game.Address, game.Port);
            }));

            var ipAddressLabel = (MegaLabel)titleLabel.Duplicate();
            ipAddressLabel.CustomMinimumSize = new Vector2(300, 0);
            ipAddressLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            lanPanel.AddChildSafely(ipAddressLabel);
            ipAddressLabel.SetTextAutoSize("LAN IP:");

            __instance.AddChildSafely(lanPanel);
        }
    }
}