using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using SlayTheSpire2.LAN.Multiplayer.Services;
using Steamworks;
using SteamClient = MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs.Steam
{
    //Steam invite games resume dropped connections the way LAN games do; see SteamClientResumeService and
    //SteamHostResumeService. Everyone in the game runs this mod, so both ends of every connection take part.

    [HarmonyPatch(typeof(SteamClient), nameof(SteamClient.Update))]
    internal class SteamClientUpdatePatch
    {
        private static bool Prefix(SteamClient __instance)
        {
            SteamClientResumeService.Instance.Update(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(SteamClient), nameof(SteamClient.SendMessageToHost))]
    internal class SteamClientSendMessageToHostPatch
    {
        private static bool Prefix(SteamClient __instance, byte[] bytes, int length, NetTransferMode mode)
        {
            SteamClientResumeService.Instance.SendMessageToHost(__instance, bytes, length, mode);

            return false;
        }
    }

    [HarmonyPatch(typeof(SteamClient), "OnNetStatusChanged")]
    internal class SteamClientOnNetStatusChangedPatch
    {
        private static bool Prefix(SteamClient __instance, SteamNetConnectionStatusChangedCallback_t data)
        {
            return SteamClientResumeService.Instance.OnNetStatusChanged(__instance, data);
        }
    }

    [HarmonyPatch(typeof(SteamClient), nameof(SteamClient.DisconnectFromHost))]
    internal class SteamClientDisconnectFromHostPatch
    {
        private static void Prefix(SteamClient __instance)
        {
            SteamClientResumeService.Instance.BeforeDisconnectFromHost(__instance);
        }
    }

    [HarmonyPatch(typeof(SteamHost), "OnNetStatusChanged")]
    internal class SteamHostOnNetStatusChangedPatch
    {
        private static bool Prefix(SteamHost __instance, SteamNetConnectionStatusChangedCallback_t data)
        {
            return SteamHostResumeService.Instance.OnNetStatusChanged(__instance, data);
        }

        private static void Postfix(SteamHost __instance, SteamNetConnectionStatusChangedCallback_t data)
        {
            SteamHostResumeService.Instance.AfterNetStatusChanged(__instance, data);
        }
    }

    [HarmonyPatch(typeof(SteamHost), nameof(SteamHost.Update))]
    internal class SteamHostUpdatePatch
    {
        private static bool Prefix(SteamHost __instance)
        {
            SteamHostResumeService.Instance.Update(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(SteamHost), nameof(SteamHost.SendMessageToClient))]
    internal class SteamHostSendMessageToClientPatch
    {
        private static bool Prefix(SteamHost __instance, ulong peerId, byte[] bytes, int length, NetTransferMode mode)
        {
            return SteamHostResumeService.Instance.BeforeSendMessageToClient(__instance, peerId, bytes, length, mode);
        }
    }

    [HarmonyPatch(typeof(SteamHost), nameof(SteamHost.DisconnectClient))]
    internal class SteamHostDisconnectClientPatch
    {
        private static void Prefix(SteamHost __instance, ulong peerId)
        {
            SteamHostResumeService.Instance.OnClientDisconnected(__instance, peerId);
        }
    }

    [HarmonyPatch(typeof(SteamHost), nameof(SteamHost.StopHost))]
    internal class SteamHostStopHostPatch
    {
        private static void Postfix(SteamHost __instance)
        {
            SteamHostResumeService.Instance.OnStopped(__instance);
        }
    }

    [HarmonyPatch(typeof(SteamHost), "IsInLobby")]
    internal class SteamHostIsInLobbyPatch
    {
        private static bool Prefix(SteamHost __instance, SteamNetworkingIdentity id, ref bool __result)
        {
            if (!SteamHostResumeService.Instance.HasSession(__instance, id.GetSteamID64()))
                return true;

            __result = true;

            return false;
        }
    }
}
