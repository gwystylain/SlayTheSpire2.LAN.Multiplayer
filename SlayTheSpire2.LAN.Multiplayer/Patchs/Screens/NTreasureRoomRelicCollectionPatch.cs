using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs.Screens
{
    //The chest scene has one hand-placed layout per relic count (layout i holds i + 1 relics), up to six. A LAN game
    //can have more players than that, and with no layout for the count the game shows no relics to pick. Before the
    //game picks a layout, add a grid of holders for the count, with empty layouts for any counts in between.
    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), nameof(NTreasureRoomRelicCollection.InitializeRelics))]
    internal class NTreasureRoomRelicCollectionInitializeRelicsPatch
    {
        private const int Separation = 42;

        private static void Prefix(List<Control> ____holderLayouts)
        {
            var relicCount = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics?.Count ?? 0;

            if (____holderLayouts.Count == 0 || relicCount <= ____holderLayouts.Count)
                return;

            var templateHolder = ____holderLayouts
                .SelectMany(layout => layout.GetChildren().OfType<NTreasureRoomRelicHolder>())
                .LastOrDefault();

            if (templateHolder == null)
                return;

            var container = ____holderLayouts[^1].GetParent();

            while (____holderLayouts.Count < relicCount - 1)
            {
                var emptyLayout = new Control { Name = $"LanEmptyLayout{____holderLayouts.Count + 1}", Visible = false };
                container.AddChildSafely(emptyLayout);
                ____holderLayouts.Add(emptyLayout);
            }

            var gridLayout = new GridContainer
            {
                Name = $"LanGridLayout{relicCount}",
                Columns = Math.Min((relicCount + 1) / 2, 6),
                Visible = false
            };
            gridLayout.AddThemeConstantOverride("h_separation", Separation);
            gridLayout.AddThemeConstantOverride("v_separation", Separation);

            //Holders are placed by hand in the scene, so they may have no minimum size for the grid to lay out by
            var holderSize = templateHolder.CustomMinimumSize.Max(templateHolder.Size);

            for (var i = 0; i < relicCount; i++)
            {
                var holder = (NTreasureRoomRelicHolder)templateHolder.Duplicate();
                holder.Name = $"LanRelicHolder{i + 1}";
                holder.Visible = true;
                holder.CustomMinimumSize = holderSize;
                gridLayout.AddChildSafely(holder);
            }

            container.AddChildSafely(gridLayout);
            gridLayout.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
            gridLayout.GrowHorizontal = Control.GrowDirection.Both;
            gridLayout.GrowVertical = Control.GrowDirection.Both;

            ____holderLayouts.Add(gridLayout);
        }
    }
}
