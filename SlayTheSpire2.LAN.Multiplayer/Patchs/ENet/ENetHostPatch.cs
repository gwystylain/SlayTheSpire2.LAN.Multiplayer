using System.Collections;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Models;
using SlayTheSpire2.LAN.Multiplayer.Services;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs.ENet
{
    [HarmonyPatch(typeof(ENetHost), "DoClientHandshake")]
    internal class ENetHostDoClientHandshakePatch
    {
        private static bool Prefix(ENetHost __instance, ENetPacketPeer peer, Logger ____logger,
            IList ____receivedHandshakes, IList ____connectedPeers, INetHostHandler ____handler, ref Task __result)
        {
            __result = TaskHelper.RunSafely(DoClientHandshake(__instance, ____logger, ____receivedHandshakes,
                ____connectedPeers, ____handler, peer));

            return false;
        }

        private static async Task DoClientHandshake(ENetHost eNetHost, Logger logger, IList receivedHandshakes,
            IList connectedPeers, INetHostHandler handler, ENetPacketPeer peer)
        {
            peer.SetTimeout(24, 20000, 20000);
            var timeoutTimer = 0;
            object? handshake = null;
            while (handshake == null)
            {
                foreach (var receivedHandshake in receivedHandshakes)
                {
                    if (Traverse.Create(receivedHandshake).Field("conn").Field("peer").GetValue() == peer)
                    {
                        handshake = receivedHandshake;
                        break;
                    }
                }

                if (handshake == null)
                {
                    if (LanHostResumeService.Instance.IsResumedPeer(eNetHost, peer))
                        return;

                    await Task.Delay(100);
                    timeoutTimer += 100;
                    if (timeoutTimer >= 10000)
                    {
                        logger.Error("Timed out waiting for handshake!");
                        LanHostResumeService.RetirePeer(peer);
                        return;
                    }
                }
            }

            var handshakeConn = Traverse.Create(handshake).Field("conn").GetValue();
            var handshakeNetId = Traverse.Create(handshakeConn).Field("netId").GetValue<ulong>();
            var handshakePeer = Traverse.Create(handshakeConn).Field("peer").GetValue<ENetPacketPeer>();

            var connectedPeerIdHashSet = new HashSet<ulong>(eNetHost.ConnectedPeerIds);

            if (connectedPeerIdHashSet.Contains(handshakeNetId))
            {
                var newNetId = 1000u;

                while (connectedPeerIdHashSet.Contains(newNetId))
                {
                    newNetId += 1000u;
                }

                logger.Info(
                    $"Second client attempted to connect with peer ID {handshakeNetId}, disconnecting them and return new NetId:{newNetId}");

                var eNetPacket = LanHandshakeResponseHelper.FromLanHandshakeResponse(new ENetLanHandshakeResponse
                {
                    netId = handshakeNetId,
                    newNetId = newNetId,
                    status = ENetHandshakeStatus.IdCollision
                });
                handshakePeer.Send(0, eNetPacket.AllBytes, 1);
                handshakePeer.PeerDisconnectLater();
            }
            else
            {
                logger.Debug($"Acknowledging handshake for peer with ID {handshakeNetId}");
                var eNetPacket2 = LanHandshakeResponseHelper.FromLanHandshakeResponse(new ENetLanHandshakeResponse
                {
                    netId = handshakeNetId,
                    newNetId = handshakeNetId,
                    status = ENetHandshakeStatus.Success
                });
                handshakePeer.Send(0, eNetPacket2.AllBytes, 1);
                LanHostResumeService.Instance.OnClientAccepted(eNetHost, handshakeNetId, handshakePeer);
                connectedPeers.Add(handshakeConn);
                handler.OnPeerConnected(handshakeNetId);
            }
        }
    }

    [HarmonyPatch(typeof(ENetHost), "StartHost")]
    internal class ENetHostStartHostPatch
    {
        private static bool Prefix(ushort port, int maxClients, Logger ____logger, ref ENetConnection? ____connection,
            ref bool ____isConnected, ref NetErrorInfo? __result)
        {
            ____connection = new ENetConnection();
            var error = ____connection.CreateHostBound("*", port, maxClients);
            if (error != Error.Ok)
            {
                ____logger.Error($"Failed to create host! {error}");
                __result = new NetErrorInfo(error);
                return false;
            }

            ____isConnected = true;
            LanDiscoveryService.Instance.StartResponding(port);

            return false;
        }
    }

    [HarmonyPatch(typeof(ENetHost), "Update")]
    internal class ENetHostUpdatePatch
    {
        private static bool Prefix(ENetHost __instance)
        {
            LanHostResumeService.Instance.Update(__instance);
            LanDiscoveryService.Instance.Respond();

            return false;
        }
    }

    [HarmonyPatch(typeof(ENetHost), "SendMessageToClient")]
    internal class ENetHostSendMessageToClientPatch
    {
        private static bool Prefix(ENetHost __instance, ulong peerId, byte[] bytes, int length, NetTransferMode mode,
            int channel)
        {
            return !LanHostResumeService.Instance.SendMessageToClient(__instance, peerId, bytes, length, mode, channel);
        }
    }

    [HarmonyPatch(typeof(ENetHost), "DisconnectClientInternal")]
    internal class ENetHostDisconnectClientInternalPatch
    {
        private static bool Prefix(ENetHost __instance, ulong peerId, NetError reason, bool now, bool notifyHandler)
        {
            LanHostResumeService.Instance.DisconnectClient(__instance, peerId, reason, now, notifyHandler);

            return false;
        }
    }

    [HarmonyPatch(typeof(ENetHost), "StopHost")]
    internal class ENetHostStopHostPatch
    {
        private static void Postfix(ENetHost __instance)
        {
            LanHostResumeService.Instance.OnStopped(__instance);
            LanDiscoveryService.Instance.StopResponding();
        }
    }

    [HarmonyPatch]
    internal class ENetHostReversePatches
    {
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(ENetHost), "HandlePacketReceived")]
        internal static void HandlePacketReceived(ENetHost instance, ENetServiceData data)
        {
            throw new NotImplementedException();
        }
    }
}