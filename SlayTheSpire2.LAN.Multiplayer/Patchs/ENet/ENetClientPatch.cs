using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Services;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs.ENet
{
    [HarmonyPatch(typeof(ENetClient), "ConnectToHost")]
    internal class ENetClientConnectToHostPatch
    {
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(ENetClient), "HandleMessageReceived")]
        internal static void HandleMessageReceived(ENetClient instance, ENetServiceData data)
        {
            throw new NotImplementedException();
        }

        private static bool Prefix(ENetClient __instance, ulong netId, string ip, ushort port,
            CancellationToken cancelToken, Logger ____logger, INetClientHandler ____handler,
            ref Task<NetErrorInfo?> __result)
        {
            __result = TaskGenericHelper.RunSafely(ConnectToHost(__instance, ____logger, ____handler, netId, ip, port,
                cancelToken));

            return false;
        }

        private static async Task<NetErrorInfo?> ConnectToHost(ENetClient eNetClient, Logger logger,
            INetClientHandler handler, ulong netId, string ip, ushort port, CancellationToken cancelToken)
        {
            while (true)
            {
                var connection = new ENetConnection();
                Traverse.Create(eNetClient).Field("_connection").SetValue(connection);
                var createError = connection.CreateHost();
                if (createError != Error.Ok)
                {
                    logger.Error($"Couldn't open a socket to connect with: {createError}");
                    return new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: false);
                }

                logger.Info(
                    $"Connecting to {ip}:{port} as net ID {netId}; this device is at {LanDiscoveryService.DescribeLocalAddresses()}");
                var peer = connection.ConnectToHost(ip, port);
                if (peer == null)
                {
                    logger.Error($"Couldn't start a connection to {ip}:{port}");
                    return new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: false);
                }

                Traverse.Create(eNetClient).Field("_peer").SetValue(peer);
                var timeoutTimer = 0;
                while (!connection.TryService(out var output) ||
                       output is not { type: ENetConnection.EventType.Connect })
                {
                    await Task.Delay(100, cancelToken);
                    if (cancelToken.IsCancellationRequested)
                    {
                        eNetClient.DisconnectFromHost(NetError.CancelledJoin);
                        logger.Warn("User cancelled join flow");
                        return null;
                    }

                    timeoutTimer += 100;
                    if (timeoutTimer > 10000)
                    {
                        peer.Reset();
                        logger.Error($"Connection to {ip}:{port} timed out: nothing answered within 10 seconds");
                        return new NetErrorInfo(NetError.Timeout, selfInitiated: false);
                    }
                }

                if (peer.GetState() != ENetPacketPeer.PeerState.Connected)
                {
                    logger.Error($"Connection to {ip}:{port} failed!");
                    return new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: false);
                }

                logger.Info($"Connected to {ip}:{port}");

                var bufferedPackets = new List<ENetServiceData>();
                var (result, newNetId) = await SendAndWaitForNetIdAck(eNetClient, logger, peer, connection, netId,
                    bufferedPackets, cancelToken);
                if (result.HasValue)
                {
                    peer.PeerDisconnect();

                    if (result.Value.GetReason() == NetError.Kicked)
                    {
                        netId = newNetId;
                        continue;
                    }

                    return result;
                }

                Traverse.Create(eNetClient).Field("_netId").SetValue(newNetId);
                Traverse.Create(eNetClient).Field("_isConnected").SetValue(true);
                LanClientResumeService.Instance.OnConnected(eNetClient, ip, port, newNetId);
                handler.OnConnectedToHost();
                foreach (var item in bufferedPackets)
                {
                    LanClientResumeService.Instance.Deliver(eNetClient, item);
                }

                return null;
            }
        }

        private static async Task<(NetErrorInfo?, ulong)> SendAndWaitForNetIdAck(ENetClient eNetClient, Logger logger,
            ENetPacketPeer peer, ENetConnection connection, ulong netId, List<ENetServiceData> bufferedPackets,
            CancellationToken cancelToken)
        {
            logger.Info($"Sending handshake with net ID {netId}");
            var eNetPacket = ENetPacket.FromHandshakeRequest(new ENetHandshakeRequest
            {
                netId = netId
            });
            peer.Send(0, eNetPacket.AllBytes, 1);
            var receivedAck = false;
            var timeoutTimer = 0;
            while (!receivedAck)
            {
                await Task.Delay(100, cancelToken);
                if (cancelToken.IsCancellationRequested)
                {
                    logger.Warn("User cancelled join flow");
                    eNetClient.DisconnectFromHost(NetError.CancelledJoin);
                    return (null, netId);
                }

                if (connection.TryService(out var output) && output is { type: ENetConnection.EventType.Receive })
                {
                    var packetData = output.Value.packetData;
                    var eNetPacket2 = new ENetPacket(packetData);
                    if (eNetPacket2.PacketType == ENetPacketType.ApplicationMessage)
                    {
                        bufferedPackets.Add(output.Value);
                        continue;
                    }

                    var eNetHandshakeResponse = LanHandshakeResponseHelper.AsLanHandshakeResponse(eNetPacket2);
                    if (eNetHandshakeResponse.netId != netId)
                    {
                        logger.Error(
                            $"Received net ID ({eNetHandshakeResponse.netId}) during handshake that did not match ours!");
                        return (new NetErrorInfo(NetError.InternalError, selfInitiated: false), netId);
                    }

                    if (eNetHandshakeResponse.status == ENetHandshakeStatus.IdCollision)
                    {
                        logger.Warn(
                            $"NetID:{netId} already occupied, Next try host send new NetID:{eNetHandshakeResponse.newNetId}");
                        return (new NetErrorInfo(NetError.Kicked, selfInitiated: false),
                            eNetHandshakeResponse.newNetId);
                    }
                    else if (eNetHandshakeResponse.status != ENetHandshakeStatus.Success)
                    {
                        logger.Error($"Received non-success code during handshake ({eNetHandshakeResponse.status})!");
                        return (new NetErrorInfo(NetError.Kicked, selfInitiated: false), netId);
                    }

                    receivedAck = true;
                }

                timeoutTimer += 100;
                if (timeoutTimer > 10000)
                {
                    logger.Error($"Timed out waiting for {peer.GetRemoteAddress()} to acknowledge the handshake!");
                    eNetClient.DisconnectFromHost(NetError.Timeout);
                    return (new NetErrorInfo(NetError.Timeout, selfInitiated: false), netId);
                }
            }

            return (null, netId);
        }
    }

    [HarmonyPatch(typeof(ENetClient), "Update")]
    internal class ENetClientUpdatePatch
    {
        private static bool Prefix(ENetClient __instance)
        {
            LanClientResumeService.Instance.Update(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(ENetClient), "SendMessageToHost")]
    internal class ENetClientSendMessageToHostPatch
    {
        private static bool Prefix(ENetClient __instance, byte[] bytes, int length, NetTransferMode mode, int channel)
        {
            LanClientResumeService.Instance.SendMessageToHost(__instance, bytes, length, mode, channel);

            return false;
        }
    }

    [HarmonyPatch(typeof(ENetClient), "DisconnectFromHost")]
    internal class ENetClientDisconnectFromHostPatch
    {
        private static bool Prefix(ENetClient __instance, NetError reason, bool now)
        {
            LanClientResumeService.Instance.DisconnectFromHost(__instance, reason, now);

            return false;
        }
    }
}