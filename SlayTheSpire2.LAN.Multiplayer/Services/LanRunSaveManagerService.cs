using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Migrations;
using SlayTheSpire2.LAN.Multiplayer.Models;

// ReSharper disable MemberCanBeMadeStatic.Local
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global

namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // LAN runs are saved apart from the game's own multiplayer save, one file per run in the profile's lan_runs folder,
    // each with its players' names beside it. The run being played (hosted new, or loaded from a save) is the current
    // slot, which the host's saves go to and which ending the run deletes.
    internal class LanRunSaveManagerService
    {
        private static readonly Lazy<LanRunSaveManagerService> Lazy = new(() => new LanRunSaveManagerService());

        public static LanRunSaveManagerService Instance => Lazy.Value;

        private const string SavesFolder = "lan_runs";

        private const string SaveExtension = ".save";

        private const string NamesExtension = ".names.json";

        // Before 1.11.0 there was only ever one LAN save, in these files.
        private const string LegacySaveFile = "current_lan_run_mp.save";

        private const string LegacyNamesFile = "current_lan_run_mp_player_names.json";

        private SaveManager ProfileIdProvider => SaveManager.Instance;

        private ISaveStore SaveStore => Traverse.Create(ProfileIdProvider).Field("_saveStore").GetValue<ISaveStore>();

        private MigrationManager MigrationManager => Traverse.Create(ProfileIdProvider).Field("_migrationManager")
            .GetValue<MigrationManager>();

        private string SavesDir => RunSaveManager.GetRunSavePath(ProfileIdProvider.CurrentProfileId, SavesFolder);

        public string? CurrentSlot { get; private set; }

        public string CurrentMultiplayerRunSavePath => SavePath(CurrentSlot ?? StartNewSlot());

        public string CurrentMultiplayerRunPlayerNamesPath => NamesPath(CurrentSlot ?? StartNewSlot());

        public bool HasMultiplayerRunSave => ListSlots().Count > 0;

        private LanRunSaveManagerService()
        {
        }

        public record SaveInfo(string Slot, SerializableRun? Run, ReadSaveStatus Status);

        // A new run gets a new slot when it's hosted; its first save creates the file.
        public string StartNewSlot()
        {
            CurrentSlot = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            return CurrentSlot;
        }

        public void UseSlot(string slot)
        {
            CurrentSlot = slot;
        }

        public bool SlotExists(string slot)
        {
            return SaveStore.FileExists(SavePath(slot));
        }

        public void PrepareToWrite()
        {
            SaveStore.CreateDirectory(SavesDir);
        }

        private string SavePath(string slot)
        {
            return SavesDir + "/" + slot + SaveExtension;
        }

        private string NamesPath(string slot)
        {
            return SavesDir + "/" + slot + NamesExtension;
        }

        public List<string> ListSlots()
        {
            MigrateLegacySave();
            if (!SaveStore.DirectoryExists(SavesDir))
                return [];

            return SaveStore.GetFilesInDirectory(SavesDir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.EndsWith(SaveExtension))
                .Select(name => name[..^SaveExtension.Length])
                .ToList();
        }

        // Every LAN save with what's needed to show it, newest first. A save that can't be read is listed with its
        // status so it can still be deleted.
        public List<SaveInfo> ListSaves()
        {
            return ListSlots()
                .Select(slot =>
                {
                    var result = MigrationManager.LoadSave<SerializableRun>(SavePath(slot));
                    return new SaveInfo(slot, result.Success ? result.SaveData : null, result.Status);
                })
                .OrderByDescending(save => save.Run?.SaveTime ?? 0)
                .ThenByDescending(save => save.Slot)
                .ToList();
        }

        public ReadSaveResult<SerializableRun> LoadAndCanonicalizeMultiplayerRunSave(string slot, ulong localPlayerId)
        {
            var readSaveResult = LoadMultiplayerRunSave(slot);
            if (readSaveResult is { Success: true, SaveData: not null })
            {
                try
                {
                    var lanPlayerNameService = LanPlayerNameService.Instance;

                    var data = RunManager.CanonicalizeSave(readSaveResult.SaveData, localPlayerId);

                    var playerNamesJson = SaveStore.ReadFile(NamesPath(slot));
                    if (!string.IsNullOrEmpty(playerNamesJson))
                    {
                        lanPlayerNameService.PlayerNames = JsonSerializer.Deserialize<PlayerNames>(playerNamesJson) ??
                                                           new PlayerNames();
                    }

                    lanPlayerNameService.SetHostPlayerName();

                    return new ReadSaveResult<SerializableRun>(data, ReadSaveStatus.Success);
                }
                catch (Exception value)
                {
                    Log.Error($"LAN run save {slot} failed validation: {value}");
                    return new ReadSaveResult<SerializableRun>(ReadSaveStatus.ValidationFailed,
                        $"Save file validation failed: {value}");
                }
            }

            return readSaveResult;
        }

        public ReadSaveResult<SerializableRun> LoadMultiplayerRunSave(string slot)
        {
            var path = SavePath(slot);
            var readSaveResult = MigrationManager.LoadSave<SerializableRun>(path);
            if (readSaveResult.Success)
            {
                return readSaveResult;
            }

            if (readSaveResult.Status == ReadSaveStatus.FileNotFound)
            {
                Log.Info("LAN run save file not found at " + path);
            }
            else if (!readSaveResult.Status.IsRecoverable())
            {
                Log.Error(
                    $"Failed to load LAN run save {slot}: status={readSaveResult.Status} msg={readSaveResult.ErrorMessage}");
            }
            else
            {
                Log.Warn(
                    $"LAN run save {slot} had recoverable issues: status={readSaveResult.Status} msg={readSaveResult.ErrorMessage}");
            }

            return readSaveResult;
        }

        public void DeleteCurrentMultiplayerRun()
        {
            if (CurrentSlot != null)
            {
                DeleteSlot(CurrentSlot);
            }
        }

        public void DeleteSlot(string slot)
        {
            // The save store keeps a .backup beside each file it writes.
            SaveStore.DeleteFile(SavePath(slot));
            SaveStore.DeleteFile(SavePath(slot) + ".backup");
            SaveStore.DeleteFile(NamesPath(slot));
            SaveStore.DeleteFile(NamesPath(slot) + ".backup");
            Log.Info($"Deleted LAN run save {slot}");
        }

        // Moves the one save older versions kept into a slot of its own, once.
        private void MigrateLegacySave()
        {
            var profileId = ProfileIdProvider.CurrentProfileId;
            var legacySave = RunSaveManager.GetRunSavePath(profileId, LegacySaveFile);
            if (!SaveStore.FileExists(legacySave))
                return;

            try
            {
                var slot = SaveStore.GetLastModifiedTime(legacySave).UtcDateTime.ToString("yyyyMMdd-HHmmss-fff");
                SaveStore.CreateDirectory(SavesDir);

                SaveStore.WriteFile(SavePath(slot), SaveStore.ReadFile(legacySave) ?? string.Empty);
                var legacyNames = RunSaveManager.GetRunSavePath(profileId, LegacyNamesFile);
                if (SaveStore.FileExists(legacyNames))
                {
                    SaveStore.WriteFile(NamesPath(slot), SaveStore.ReadFile(legacyNames) ?? string.Empty);
                    SaveStore.DeleteFile(legacyNames);
                    SaveStore.DeleteFile(legacyNames + ".backup");
                }

                SaveStore.DeleteFile(legacySave);
                SaveStore.DeleteFile(legacySave + ".backup");
                Log.Info($"Moved the LAN run save into slot {slot}");
            }
            catch (Exception e)
            {
                Log.Error($"Couldn't move the old LAN run save into a slot: {e}");
            }
        }
    }
}
