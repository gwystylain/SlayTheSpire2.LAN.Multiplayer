using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Services;

namespace SlayTheSpire2.LAN.Multiplayer.Components
{
    //The game's host submenu (Standard, Daily, Blitz, Dread, Custom) hosting over LAN instead of Steam. The game's
    //_Ready wires up the buttons; NMultiplayerHostSubmenuStartHostPatch sends their presses to StartLanHost.
    internal partial class LanMultiplayerHostSubmenu : NMultiplayerHostSubmenu
    {
        private static readonly string ScenePath = SceneHelper.GetScenePath("screens/multiplayer_host_submenu");

        private static readonly AccessTools.FieldRef<NMultiplayerHostSubmenu, Control> RefLoadingOverlay =
            AccessTools.FieldRefAccess<NMultiplayerHostSubmenu, Control>("_loadingOverlay");

        public new static LanMultiplayerHostSubmenu? Create()
        {
            if (TestMode.IsOn)
                return null;

            var multiplayerHostSubmenu =
                PreloadManager.Cache.GetScene(ScenePath).Instantiate<NMultiplayerHostSubmenu>();

            var lanMultiplayerHostSubmenu = new LanMultiplayerHostSubmenu();

            lanMultiplayerHostSubmenu.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            SceneAdoptionHelper.Adopt(multiplayerHostSubmenu, lanMultiplayerHostSubmenu);

            return lanMultiplayerHostSubmenu;
        }

        public override void _Ready()
        {
            base._Ready();
        }

        public void StartLanHost(GameMode gameMode, bool isCustom)
        {
            var settingsModel = SettingsService.Instance.SettingsModel;

            LanHostHelper.StartHost(gameMode, isCustom, RefLoadingOverlay(this), _stack, settingsModel.HostPort,
                settingsModel.HostMaxPlayers);
        }
    }
}
