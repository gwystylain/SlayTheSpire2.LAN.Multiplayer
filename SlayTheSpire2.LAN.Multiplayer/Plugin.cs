using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

// ReSharper disable UnusedMember.Global
// ReSharper disable ClassNeverInstantiated.Global

namespace SlayTheSpire2.LAN.Multiplayer
{
    [ModInitializer("Initialize")]
    public class Plugin
    {
        private static void Initialize()
        {
            //Harmony's PatchAll stops at the first patch class that fails, leaving every class after it unpatched and
            //logging only the first failure. A game update usually breaks one or two patches, so apply each class on
            //its own and log every one that no longer fits the game.
            var harmony = new Harmony("SlayTheSpire2.LAN.Multiplayer");
            var failed = new List<string>();

            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly)
                         .Where(type => type.HasHarmonyAttribute()))
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    failed.Add(type.Name);
                    Log.Error($"[LAN] Couldn't apply patch {type.FullName}: {e}");
                }
            }

            if (failed.Count > 0)
            {
                Log.Error($"[LAN] {failed.Count} patches failed to apply, so parts of the mod won't work: " +
                          string.Join(", ", failed));
            }
        }
    }
}
