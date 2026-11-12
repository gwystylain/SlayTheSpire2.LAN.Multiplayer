using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Saves.Runs;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs
{
    //Between rooms every peer waits for every other peer's combat sync, with no time limit, so one lost message (from
    //a Deck that just woke up, say) hangs the run. Watch each sync the game starts: while the host is still waiting it
    //resends its RNG every few seconds, and after 30 seconds the wait is released so the run goes on. The game's own
    //WaitForSync then applies whatever arrived and resets the synchronizer for the next sync.
    //Whether it's a LAN game isn't checked, because the sync issue may also occur when connected via Steam.
    [HarmonyPatch(typeof(CombatStateSynchronizer), nameof(CombatStateSynchronizer.StartSync))]
    internal class CombatStateSynchronizerStartSyncPatch
    {
        private const int TimeoutSeconds = 30;

        private const int ResendIntervalSeconds = 5;

        private static readonly AccessTools.FieldRef<CombatStateSynchronizer, SerializableRunRngSet?> RefRngSet =
            AccessTools.FieldRefAccess<CombatStateSynchronizer, SerializableRunRngSet?>("_rngSet");

        private static readonly AccessTools.FieldRef<CombatStateSynchronizer, SerializableRelicGrabBag?>
            RefSharedRelicGrabBag =
                AccessTools.FieldRefAccess<CombatStateSynchronizer, SerializableRelicGrabBag?>("_sharedRelicGrabBag");

        private static void Postfix(CombatStateSynchronizer __instance, Logger ____logger,
            INetGameService ____netService, TaskCompletionSource? ____syncCompletionSource)
        {
            if (____syncCompletionSource == null || ____syncCompletionSource.Task.IsCompleted)
                return;

            TaskHelper.RunSafely(WatchSync(__instance, ____logger, ____netService, ____syncCompletionSource));
        }

        private static async Task WatchSync(CombatStateSynchronizer instance, Logger logger,
            INetGameService netService, TaskCompletionSource syncCompletionSource)
        {
            var startTime = DateTime.Now;
            var lastResendTime = DateTime.Now;

            while (!syncCompletionSource.Task.IsCompleted)
            {
                await Task.Delay(100);

                if (syncCompletionSource.Task.IsCompleted)
                    return;

                if ((DateTime.Now - startTime).TotalSeconds > TimeoutSeconds)
                {
                    logger.Warn("Receive all sync messages timeout, skip waiting for all clients");
                    syncCompletionSource.TrySetResult();
                    return;
                }

                if (netService.Type != NetGameType.Host ||
                    (DateTime.Now - lastResendTime).TotalSeconds <= ResendIntervalSeconds)
                    continue;

                var rngSet = RefRngSet(instance);
                var sharedRelicGrabBag = RefSharedRelicGrabBag(instance);

                if (rngSet == null || sharedRelicGrabBag == null)
                    continue;

                logger.Debug("Resend rng sync message");

                netService.SendMessage(new SyncRngMessage
                {
                    rng = rngSet,
                    sharedRelicGrabBag = sharedRelicGrabBag
                });

                lastResendTime = DateTime.Now;
            }
        }
    }
}
