using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using SlayTheSpire2.LAN.Multiplayer.Components;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Models;
using SlayTheSpire2.LAN.Multiplayer.Patchs.ENet;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // Carries an ENetClient's session across a dead connection, such as both Steam Decks going to sleep.
    // When ENet drops the host without a goodbye, the game is still told it's connected: what it sends
    // is queued, and the client keeps reconnecting to the same address until the host takes it back.
    internal class LanClientResumeService
    {
        private static readonly Lazy<LanClientResumeService> Lazy = new(() => new LanClientResumeService());

        public static LanClientResumeService Instance => Lazy.Value;

        private const ulong AttemptTimeoutMsec = 5000;

        private const ulong RetryDelayMsec = 1000;

        private const long SleepGapMsec = 5000;

        private static readonly AccessTools.FieldRef<ENetClient, ENetConnection?> RefConnection =
            AccessTools.FieldRefAccess<ENetClient, ENetConnection?>("_connection");

        private static readonly AccessTools.FieldRef<ENetClient, ENetPacketPeer?> RefPeer =
            AccessTools.FieldRefAccess<ENetClient, ENetPacketPeer?>("_peer");

        private static readonly AccessTools.FieldRef<ENetClient, bool> RefIsConnected =
            AccessTools.FieldRefAccess<ENetClient, bool>("_isConnected");

        private static readonly AccessTools.FieldRef<ENetClient, Logger> RefLogger =
            AccessTools.FieldRefAccess<ENetClient, Logger>("_logger");

        private static readonly AccessTools.FieldRef<NetClient, INetClientHandler> RefHandler =
            AccessTools.FieldRefAccess<NetClient, INetClientHandler>("_handler");

        private static readonly int ReliableChannel = NetTransferMode.Reliable.ToChannelId();

        private static readonly int ReliableFlags = ENetUtil.FlagsFromMode(NetTransferMode.Reliable);

        private readonly ConditionalWeakTable<ENetClient, Session> _sessions = new();

        private readonly ThrottledErrorLog _errorLog = new();

        private LanClientResumeService()
        {
        }

        private class Session(string ip, ushort port, ulong netId)
        {
            public readonly LanReliableLink Link = new();

            public readonly string Ip = ip;

            public readonly ushort Port = port;

            public readonly ulong NetId = netId;

            // From the host's Hello. Without it the host can't resume us, so a drop disconnects as before.
            public ulong? Token;

            public bool Reconnecting;

            public ulong LostAtMsec;

            public int Attempts;

            public ulong NextAttemptMsec;

            public ulong AttemptStartMsec;

            public ENetConnection? PendingConnection;

            public ENetPacketPeer? PendingPeer;

            public ulong LastUpdateTicksMsec;

            public long LastUpdateWallMsec;
        }

        public void OnConnected(ENetClient client, string ip, ushort port, ulong netId)
        {
            _sessions.AddOrUpdate(client, new Session(ip, port, netId));
        }

        public string? HostAddress(INetGameService netService)
        {
            return netService is NetClientGameService { NetClient: ENetClient client } &&
                   _sessions.TryGetValue(client, out var session)
                ? session.Ip
                : null;
        }

        public void Deliver(ENetClient client, ENetServiceData data)
        {
            if (RefIsConnected(client) && LanResumePacketHelper.IsReliableAppMessage(data) &&
                _sessions.TryGetValue(client, out var session))
            {
                session.Link.RecordReceived();
            }

            ENetClientConnectToHostPatch.HandleMessageReceived(client, data);
        }

        public void Update(ENetClient client)
        {
            if (RefConnection(client) == null)
                throw new InvalidOperationException("Must call ConnectToHost first and wait for connection!");

            _sessions.TryGetValue(client, out var session);
            var logger = RefLogger(client);

            // The host has usually given up on a connection that slept through its timeout, and ENet here
            // would take another 30 seconds to notice, so reconnect as soon as the Deck wakes.
            if (session != null && SleptSinceLastUpdate(session) is { } sleptMsec &&
                session is { Token: not null, Link.Overflowed: false } && RefIsConnected(client))
            {
                logger.Info($"Woke after about {sleptMsec / 1000}s asleep, reconnecting to the host now");
                if (session.Reconnecting)
                {
                    EndAttempt(session, 0);
                }
                else
                {
                    BeginReconnect(client, session);
                }
            }

            if (session is { Reconnecting: true })
            {
                UpdateReconnect(client, session);
                return;
            }

            while (true)
            {
                var peer = RefPeer(client);
                if (peer == null || !peer.IsActive())
                    break;

                var connection = RefConnection(client);
                if (connection == null || !connection.TryService(out var output))
                    break;

                var data = output!.Value;
                switch (data.type)
                {
                    case ENetConnection.EventType.Error:
                        _errorLog.Error(logger, "Got error from ENetConnection!");
                        break;
                    case ENetConnection.EventType.Connect:
                        logger.Debug("Received connect on client");
                        break;
                    case ENetConnection.EventType.Disconnect:
                        logger.Debug($"Received disconnect on client. Already disconnected: {!RefIsConnected(client)}");
                        if (!RefIsConnected(client))
                            break;

                        if (session is { Token: not null, Link.Overflowed: false } &&
                            !LanResumePacketHelper.IsGracefulDisconnect(data))
                        {
                            BeginReconnect(client, session);
                            return;
                        }

                        RefIsConnected(client) = false;
                        RefHandler(client).OnDisconnectedFromHost(client.HostNetId,
                            new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: false));
                        break;
                    case ENetConnection.EventType.Receive:
                        if (LanResumePacketHelper.TryGetType(data.packetData, out var type))
                        {
                            HandleResumePacket(client, session, type, data.packetData);
                        }
                        else
                        {
                            Deliver(client, data);
                        }

                        break;
                }
            }

            if (session is { Token: not null } && RefIsConnected(client) &&
                session.Link.ShouldSendAck(Time.GetTicksMsec()))
            {
                SendAck(RefPeer(client), session);
            }
        }

        public void SendMessageToHost(ENetClient client, byte[] bytes, int length, NetTransferMode mode, int channel)
        {
            if (!RefIsConnected(client))
                throw new InvalidOperationException("Tried to send message to host while disconnected!");

            if (RefConnection(client) == null)
                throw new InvalidOperationException("Must call ConnectToHost first and wait for connection!");

            var packet = ENetPacket.FromAppMessage(bytes, length).AllBytes;
            _sessions.TryGetValue(client, out var session);
            if (session != null && channel == ReliableChannel)
            {
                session.Link.RecordSent(packet);
            }

            if (session is { Reconnecting: true })
                return;

            RefPeer(client)?.Send(channel, packet, ENetUtil.FlagsFromMode(mode));
        }

        // ENetClient.DisconnectFromHost, plus the goodbye marker and leaving mid-reconnect.
        public void DisconnectFromHost(ENetClient client, NetError reason, bool now)
        {
            _sessions.TryGetValue(client, out var session);
            _sessions.Remove(client);

            var connection = RefConnection(client);
            var peer = RefPeer(client);
            var reconnecting = session is { Reconnecting: true };
            if (reconnecting)
            {
                session!.PendingPeer?.PeerDisconnectNow(LanResumePacketHelper.GracefulDisconnectData);
                session.PendingConnection?.Destroy();
                ConnectionStatusOverlay.Hide();
            }
            else
            {
                if (now)
                {
                    peer?.PeerDisconnectNow(LanResumePacketHelper.GracefulDisconnectData);
                }
                else
                {
                    // The connection is destroyed right after this, so nothing gets a second chance: flush the
                    // reason out first, then the disconnect itself. PeerDisconnectLater would wait for pending
                    // packets and never send the disconnect, leaving the host to time us out as if we'd slept.
                    var eNetPacket = ENetPacket.FromDisconnection(new ENetDisconnection
                    {
                        reason = reason
                    });
                    peer?.Send(ReliableChannel, eNetPacket.AllBytes, ReliableFlags);
                    connection?.Flush();
                    peer?.PeerDisconnect(LanResumePacketHelper.GracefulDisconnectData);
                }

                connection?.Flush();
            }

            if (RefIsConnected(client))
            {
                RefIsConnected(client) = false;
                RefHandler(client).OnDisconnectedFromHost(client.HostNetId, new NetErrorInfo(reason, selfInitiated: true));
                if (!reconnecting)
                {
                    connection?.Destroy();
                }
            }
        }

        private void HandleResumePacket(ENetClient client, Session? session, LanResumePacketType type,
            byte[] packetData)
        {
            switch (type)
            {
                case LanResumePacketType.Hello when session != null:
                    session.Token = LanResumePacketHelper.AsULong(packetData);
                    RefLogger(client).Info("Host can resume this session if the connection drops");
                    SendAck(RefPeer(client), session);
                    break;
                case LanResumePacketType.Ack when session != null:
                    session.Link.Acknowledge(LanResumePacketHelper.AsULong(packetData));
                    break;
                default:
                    RefLogger(client).Warn($"Got unexpected {type} packet while connected to the host");
                    break;
            }
        }

        private static void SendAck(ENetPacketPeer? peer, Session session)
        {
            peer?.Send(NetTransferMode.Unreliable.ToChannelId(), LanResumePacketHelper.FromAck(session.Link.ReceivedCount),
                ENetUtil.FlagsFromMode(NetTransferMode.Unreliable));
        }

        // Godot's clock stops while the Deck sleeps and the wall clock doesn't, so the wall clock running
        // ahead since the last frame is time spent asleep.
        private static long? SleptSinceLastUpdate(Session session)
        {
            var ticksMsec = Time.GetTicksMsec();
            var wallMsec = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            var isFirst = session.LastUpdateTicksMsec == 0;
            var sleptMsec = wallMsec - session.LastUpdateWallMsec -
                            (long)(ticksMsec - session.LastUpdateTicksMsec);
            session.LastUpdateTicksMsec = ticksMsec;
            session.LastUpdateWallMsec = wallMsec;
            return !isFirst && sleptMsec >= SleepGapMsec ? sleptMsec : null;
        }

        private static void BeginReconnect(ENetClient client, Session session)
        {
            var now = Time.GetTicksMsec();
            session.Reconnecting = true;
            session.LostAtMsec = now;
            session.NextAttemptMsec = now;
            session.Attempts = 0;
            RefConnection(client)?.Destroy();
            RefLogger(client).Info($"Lost the connection to the host at {session.Ip}:{session.Port}, reconnecting");
        }

        private void UpdateReconnect(ENetClient client, Session session)
        {
            var now = Time.GetTicksMsec();
            ConnectionStatusOverlay.Show(new LocString("gameplay_ui", "SlayTheSpire2.LAN.Multiplayer.RECONNECTING_TO_HOST"),
                now - session.LostAtMsec);

            if (session.PendingConnection == null)
            {
                if (now < session.NextAttemptMsec)
                    return;

                StartAttempt(client, session, now);
            }

            while (session.PendingConnection != null && session.PendingConnection.TryService(out var output))
            {
                var data = output!.Value;
                switch (data.type)
                {
                    case ENetConnection.EventType.Connect:
                        var request = LanResumePacketHelper.FromResumeRequest(new ENetLanResumeRequest
                        {
                            netId = session.NetId,
                            token = session.Token!.Value,
                            receivedCount = session.Link.ReceivedCount
                        });
                        session.PendingPeer!.Send(ReliableChannel, request, ReliableFlags);
                        break;
                    case ENetConnection.EventType.Disconnect:
                        RefLogger(client).Info($"Reconnect attempt {session.Attempts} was turned away");
                        EndAttempt(session, now);
                        return;
                    case ENetConnection.EventType.Receive
                        when LanResumePacketHelper.TryGetType(data.packetData, out var type) &&
                             type == LanResumePacketType.ResumeResponse:
                        FinishAttempt(client, session, LanResumePacketHelper.AsResumeResponse(data.packetData), now);
                        return;
                }
            }

            if (session.PendingConnection != null && now - session.AttemptStartMsec >= AttemptTimeoutMsec)
            {
                EndAttempt(session, now);
            }
        }

        private static void StartAttempt(ENetClient client, Session session, ulong now)
        {
            var logger = RefLogger(client);
            var connection = new ENetConnection();
            var error = connection.CreateHost();
            if (error != Error.Ok)
            {
                logger.Warn($"Couldn't create an ENet host to reconnect with: {error}");
                session.NextAttemptMsec = now + RetryDelayMsec;
                return;
            }

            var peer = connection.ConnectToHost(session.Ip, session.Port);
            if (peer == null)
            {
                connection.Destroy();
                session.NextAttemptMsec = now + RetryDelayMsec;
                return;
            }

            session.PendingConnection = connection;
            session.PendingPeer = peer;
            session.AttemptStartMsec = now;
            session.Attempts++;
            if (session.Attempts <= 3 || session.Attempts % 10 == 0)
            {
                logger.Info($"Reconnect attempt {session.Attempts} to {session.Ip}:{session.Port}");
            }
        }

        private static void EndAttempt(Session session, ulong now)
        {
            session.PendingConnection?.Destroy();
            session.PendingPeer = null;
            session.PendingConnection = null;
            session.NextAttemptMsec = now + RetryDelayMsec;
        }

        private void FinishAttempt(ENetClient client, Session session, ENetLanResumeResponse response, ulong now)
        {
            var logger = RefLogger(client);
            if (!response.accepted || !session.Link.CanResumeFrom(response.receivedCount))
            {
                logger.Warn($"The host wouldn't resume this session (accepted: {response.accepted}), disconnecting");
                session.PendingPeer?.PeerDisconnectNow(LanResumePacketHelper.GracefulDisconnectData);
                session.PendingConnection?.Destroy();
                session.PendingPeer = null;
                session.PendingConnection = null;
                session.Reconnecting = false;
                _sessions.Remove(client);
                ConnectionStatusOverlay.Hide();
                RefIsConnected(client) = false;
                RefHandler(client).OnDisconnectedFromHost(client.HostNetId,
                    new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: false));
                return;
            }

            RefConnection(client) = session.PendingConnection;
            RefPeer(client) = session.PendingPeer;
            session.PendingConnection = null;
            session.PendingPeer = null;
            session.Reconnecting = false;

            var peer = RefPeer(client)!;
            var missing = session.Link.Resume(response.receivedCount);
            foreach (var packet in missing)
            {
                peer.Send(ReliableChannel, packet, ReliableFlags);
            }

            SendAck(peer, session);
            ConnectionStatusOverlay.Hide();
            logger.Info(
                $"Reconnected to the host after {(now - session.LostAtMsec) / 1000.0:0.0}s awake, resent {missing.Count} messages");
        }
    }
}
