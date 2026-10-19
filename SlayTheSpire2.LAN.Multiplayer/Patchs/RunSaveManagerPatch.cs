using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using SlayTheSpire2.LAN.Multiplayer.Models;
using SlayTheSpire2.LAN.Multiplayer.Services;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs
{
    [HarmonyPatch(typeof(RunSaveManager), "SaveRun", typeof(SerializableRun), typeof(bool))]
    internal class RunSaveManagerSaveRunPatch
    {
        private static bool Prefix(SerializableRun save, bool isMultiplayer, bool ____forceSynchronous,
            ISaveStore ____saveStore, Action? ___Saved, ref Task __result)
        {
            //Every run save comes through here, including the reload count written before RunManager has the run's
            //NetService, so a LAN run is told apart by the platform recorded in the save itself
            if (!isMultiplayer || save.PlatformType != PlatformType.None)
                return true;

            __result = TaskHelper.RunSafely(SaveRun(save, ____forceSynchronous, ____saveStore, ___Saved));

            return false;
        }

        private static async Task SaveRun(SerializableRun value, bool forceSynchronous, ISaveStore saveStore,
            Action? saved)
        {
            var savePath = LanRunSaveManagerService.Instance.CurrentMultiplayerRunSavePath;
            using var stream = new MemoryStream();
            if (!forceSynchronous)
            {
                await JsonSerializer.SerializeAsync(stream, value,
                    JsonSerializationUtility.GetTypeInfo<SerializableRun>(), CancellationToken.None);
            }
            else
            {
                await JsonSerializer.SerializeAsync(stream, value,
                    JsonSerializationUtility.GetTypeInfo<SerializableRun>());
            }

            stream.Seek(0L, SeekOrigin.Begin);
            await saveStore.WriteFileAsync(savePath, stream.ToArray());

            var lanPlayerNameService = LanPlayerNameService.Instance;

            using var playerNamesStream = new MemoryStream();
            if (!forceSynchronous)
            {
                await JsonSerializer.SerializeAsync(playerNamesStream, lanPlayerNameService.PlayerNames,
                    PlayerNamesContext.Default.PlayerNames, CancellationToken.None);
            }
            else
            {
                await JsonSerializer.SerializeAsync(playerNamesStream, lanPlayerNameService.PlayerNames,
                    PlayerNamesContext.Default.PlayerNames);
            }

            playerNamesStream.Seek(0L, SeekOrigin.Begin);
            await saveStore.WriteFileAsync(LanRunSaveManagerService.Instance.CurrentMultiplayerRunPlayerNamesPath,
                playerNamesStream.ToArray());

            saved?.Invoke();
        }
    }
}