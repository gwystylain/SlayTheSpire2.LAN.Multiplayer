using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace SlayTheSpire2.LAN.Multiplayer.Components
{
    // A banner across the top of the screen while a LAN connection is down. The transport calls Show
    // every frame the connection is down; the banner hides itself a second after the calls stop, so a
    // transport that's torn down mid-reconnect can't leave it behind.
    internal static class ConnectionStatusOverlay
    {
        private const ulong HideAfterMsec = 1000;

        private static CanvasLayer? _layer;

        private static Label? _label;

        private static ulong _lastShownMsec;

        private static bool _subscribed;

        public static void Show(LocString text, ulong elapsedMsec)
        {
            if (!GodotObject.IsInstanceValid(_layer))
            {
                Create();
            }

            var seconds = elapsedMsec / 1000;
            var formatted = $"{text.GetFormattedText()} {seconds / 60}:{seconds % 60:00}";
            if (_label!.Text != formatted)
            {
                _label.Text = formatted;
            }

            _layer!.Visible = true;
            _lastShownMsec = Time.GetTicksMsec();
        }

        public static void Hide()
        {
            if (GodotObject.IsInstanceValid(_layer))
            {
                _layer!.Visible = false;
            }
        }

        private static void Create()
        {
            var tree = (SceneTree)Engine.GetMainLoop();

            _layer = new CanvasLayer { Layer = 100, Visible = false };

            var panel = new PanelContainer();
            panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            panel.GrowHorizontal = Control.GrowDirection.Both;
            panel.OffsetTop = 24;
            panel.MouseFilter = Control.MouseFilterEnum.Ignore;
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(Colors.Black, 0.7f),
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                ContentMarginLeft = 24,
                ContentMarginRight = 24,
                ContentMarginTop = 10,
                ContentMarginBottom = 10
            });

            _label = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            _label.AddThemeColorOverride("font_color", new Color(1.0f, 0.922f, 0.761f));
            _label.AddThemeFontOverride(ThemeConstants.Label.Font,
                GD.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres"));
            _label.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 26);
            _label.ApplyLocaleFontSubstitution(FontType.Regular, ThemeConstants.Label.Font);

            panel.AddChild(_label);
            _layer.AddChild(panel);
            tree.Root.CallDeferred(Node.MethodName.AddChild, _layer);

            if (!_subscribed)
            {
                tree.ProcessFrame += HideWhenStale;
                _subscribed = true;
            }
        }

        private static void HideWhenStale()
        {
            if (GodotObject.IsInstanceValid(_layer) && _layer!.Visible &&
                Time.GetTicksMsec() - _lastShownMsec > HideAfterMsec)
            {
                _layer.Visible = false;
            }
        }
    }
}
