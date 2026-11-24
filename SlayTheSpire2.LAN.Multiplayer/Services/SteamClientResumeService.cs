using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform.Steam;
using SlayTheSpire2.LAN.Multiplayer.Components;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Models;
using Steamworks;
using SteamClient = MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // LanClientResumeService for Steam invite games. When the Steam connection to the host dies without the host
    // closing it on purpose (a Deck going to sleep, Wi-Fi dropping), the game is still told it's connected: what it
    // sends is queued, and the client keeps opening new connections to the host's Steam ID until the host takes it
    // back with the token it gave out, then each side resends what the other missed.
    internal class SteamClientResumeService
    {
        private static readonly Lazy<SteamClientResumeService> Lazy = new(() => new SteamClientResumeService());

        public static SteamClientResumeService Instance => Lazy.Value;

        private const ulong AttemptTimeoutMsec = 15000;

        private const ulong RetryDelayMsec = 2000;

        private const long SleepGapMsec = 5000;

        private static readonly AccessTools.FieldRef<SteamClient, HSteamNetConnection?> RefConn =
            AccessTools.FieldRefAccess<SteamClient, HSteamNetConnection?>("_conn");

        private static readonly AccessTools.FieldRef<SteamClient, bool> RefIsConnected =
            AccessTools.FieldRefAccess<SteamClient, bool>("_isConnected");

        private static readonly AccessTools.FieldRef<SteamClient, Logger> RefLogger =
            AccessTools.FieldRefAccess<SteamClient, Logger>("_logger");

        private static readonly AccessTools.FieldRef<NetClient, INetClientHandler> RefHandler =
            AccessTools.FieldRefAccess<NetClient, INetClientHandler>("_handler");

        // Non-null while ConnectToLobby is still waiting for the first connection.
        private static readonly FieldInfo ConnectingField =
            AccessTools.Field(typeof(SteamClient), "_connectingTaskCompletionSource");

        private static readonly MethodInfo DisconnectFromHostInternalMethod =
            AccessTools.Method(typeof(SteamClient), "DisconnectFromHostInternal");

        private readonly ConditionalWeakTable<SteamClient, Session> _sessions = new();

        private SteamClientResumeService()
        {
        }

        private class Session(uint connection, ulong hostId)
        {
            public readonly LanReliableLink Link = new();

            // The connection this session counts messages on; a new one (a fresh join) gets a new session.
            public uint Connection = connection;

            public readonly ulong HostId = hostId;

            // From the host's Hello. Without it the host can't resume us, so a drop disconnects as before.
            public ulong? Token;

            public bool Reconnecting;

            public ulong LostAtMsec;

            public int Attempts;

            public ulong NextAttemptMsec;

            public ulong AttemptStartMsec;

            public HSteamNetConnection? Pending;

            public bool PendingConnected;

            public ulong LastUpdateTicksMsec;

            public long LastUpdateWallMsec;
        }

        // The session counts from the moment the client has a connection, before anything can be sent or received
        // on it, so both sides count the same messages. The host counts from when it accepts the connection.
        private Session? EnsureSession(SteamClient client)
        {
            _sessions.TryGetValue(client, out var session);
            if (session is { Reconnecting: true })
                return session;

            if (RefConn(client) is not { } conn)
                return session;

            if (session == null || session.Connection != conn.m_HSteamNetConnection)
            {
                session = new Session(conn.m_HSteamNetConnection, client.HostNetId);
                _sessions.AddOrUpdate(client, session);
            }

            return session;
        }

        private static bool IsEstablished(SteamClient client)
        {
            return RefIsConnected(client) && ConnectingField.GetValue(client) == null;
        }

        public void Update(SteamClient client)
        {
            var session = EnsureSession(client);
            var logger = RefLogger(client);

            // Steam takes a while to notice a connection that slept through its timeout, so reconnect as soon as the
            // Deck wakes.
            if (session != null && SleptSinceLastUpdate(session) is { } sleptMsec &&
                session is { Token: not null, Link.Overflowed: false } && IsEstablished(client))
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

            if (RefConn(client) is not { } conn)
                return;

            foreach (var message in SteamResumePacketHelper.Receive(conn))
            {
                Deliver(client, session, message);
            }

            if (session is { Token: not null } && IsEstablished(client) &&
                session.Link.ShouldSendAck(Time.GetTicksMsec()))
            {
                SendAck(conn, session);
            }
        }

        private static void Deliver(SteamClient client, Session? session, SteamReceivedMessage message)
        {
            if (SteamResumePacketHelper.TryUnwrap(message.Data, out var type, out var packet))
            {
                switch (type)
                {
                    case LanResumePacketType.Hello when session != null:
                        session.Token = LanResumePacketHelper.AsULong(packet);
                        RefLogger(client).Info("Host can resume this session if the connection drops");
                        if (RefConn(client) is { } conn)
                        {
                            SendAck(conn, session);
                        }

                        break;
                    case LanResumePacketType.Ack when session != null:
                        session.Link.Acknowledge(LanResumePacketHelper.AsULong(packet));
                        break;
                    default:
                        RefLogger(client).Warn($"Got unexpected {type} packet while connected to the host");
                        break;
                }

                return;
            }

            if (session != null && message.Mode == NetTransferMode.Reliable)
            {
                session.Link.RecordReceived();
            }

            RefHandler(client).OnPacketReceived(message.Sender, message.Data, message.Mode, message.Channel);
        }

        public void SendMessageToHost(SteamClient client, byte[] bytes, int length, NetTransferMode mode)
        {
            var session = EnsureSession(client);
            if (session != null && mode == NetTransferMode.Reliable)
            {
                session.Link.RecordSent(bytes.AsSpan(0, length).ToArray());
            }

            if (session is { Reconnecting: true })
                return;

            var result = SteamResumePacketHelper.Send(RefConn(client)!.Value, bytes, length, mode);
            if (result != EResult.k_EResultOK)
            {
                RefLogger(client).Warn($"Failed to send message length {length}: {result}");
            }
        }

        // Returns false when it handled the change itself, true to let SteamClient.OnNetStatusChanged handle it.
        public bool OnNetStatusChanged(SteamClient client, SteamNetConnectionStatusChangedCallback_t data)
        {
            if (!_sessions.TryGetValue(client, out var session))
                return true;

            var state = data.m_info.m_eState;
            if (session.Reconnecting)
            {
                if (session.Pending is not { } pending || pending.m_HSteamNetConnection != data.m_hConn.m_HSteamNetConnection)
                    return false;

                if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                {
                    session.PendingConnected = true;
                    SteamResumePacketHelper.SendResumePacket(pending, LanResumePacketHelper.FromResumeRequest(
                        new ENetLanResumeRequest
                        {
                            netId = client.NetId,
                            token = session.Token!.Value,
                            receivedCount = session.Link.ReceivedCount
                        }), NetTransferMode.Reliable);
                }
                else if (SteamResumePacketHelper.IsClosed(state))
                {
                    RefLogger(client).Info(
                        $"Reconnect attempt {session.Attempts} failed: {data.m_info.m_eEndReason}, {data.m_info.m_szEndDebug}");
                    EndAttempt(session, Time.GetTicksMsec());
                }

                return false;
            }

            if (RefConn(client) is not { } conn || conn.m_HSteamNetConnection != data.m_hConn.m_HSteamNetConnection ||
                !SteamResumePacketHelper.IsClosed(state) || !IsEstablished(client) ||
                session is not { Token: not null, Link.Overflowed: false } ||
                SteamResumePacketHelper.IsDeliberateClose(state, data.m_info.m_eEndReason))
                return true;

            RefLogger(client).Info(
                $"Steam connection to the host dropped ({data.m_info.m_eEndReason}, {data.m_info.m_szEndDebug})");
            BeginReconnect(client, session);
            return false;
        }

        // Before SteamClient.DisconnectFromHost, which then closes the connection and tells the game.
        public void BeforeDisconnectFromHost(SteamClient client)
        {
            if (!_sessions.TryGetValue(client, out var session))
                return;

            _sessions.Remove(client);
            if (!session.Reconnecting)
                return;

            if (session.Pending is { } pending)
            {
                SteamNetworkingSockets.CloseConnection(pending, (int)SteamDisconnectionReason.AppGeneric, null, false);
            }

            ConnectionStatusOverlay.Hide();
        }

        private static void SendAck(HSteamNetConnection conn, Session session)
        {
            SteamResumePacketHelper.SendResumePacket(conn, LanResumePacketHelper.FromAck(session.Link.ReceivedCount),
                NetTransferMode.Unreliable);
        }

        // Godot's clock stops while the Deck sleeps and the wall clock doesn't, so the wall clock running ahead since
        // the last frame is time spent asleep.
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

        private static void BeginReconnect(SteamClient client, Session session)
        {
            var now = Time.GetTicksMsec();
            session.Reconnecting = true;
            session.LostAtMsec = now;
            session.NextAttemptMsec = now;
            session.Attempts = 0;
            if (RefConn(client) is { } conn)
            {
                SteamNetworkingSockets.CloseConnection(conn, SteamResumePacketHelper.ResumingCloseReason,
                    "Reconnecting", false);
            }

            RefLogger(client).Info($"Lost the connection to the host ({session.HostId}), reconnecting");
        }

        private void UpdateReconnect(SteamClient client, Session session)
        {
            var now = Time.GetTicksMsec();
            ConnectionStatusOverlay.Show(
                new LocString("gameplay_ui", "SlayTheSpire2.LAN.Multiplayer.RECONNECTING_TO_HOST"),
                now - session.LostAtMsec);

            if (session.Pending == null)
            {
                if (now < session.NextAttemptMsec)
                    return;

                StartAttempt(client, session, now);
                return;
            }

            if (session.PendingConnected)
            {
                var pending = session.Pending.Value;
                var messages = SteamResumePacketHelper.Receive(pending);
                for (var i = 0; i < messages.Count; i++)
                {
                    if (!SteamResumePacketHelper.TryUnwrap(messages[i].Data, out var type, out var packet) ||
                        type != LanResumePacketType.ResumeResponse)
                        continue;

                    if (!FinishAttempt(client, session, LanResumePacketHelper.AsResumeResponse(packet), now))
                        return;

                    // Whatever the host sent after its answer is the conversation carrying on.
                    for (var j = i + 1; j < messages.Count; j++)
                    {
                        Deliver(client, session, messages[j]);
                    }

                    return;
                }
            }

            if (session.Pending != null && now - session.AttemptStartMsec >= AttemptTimeoutMsec)
            {
                RefLogger(client).Info($"Reconnect attempt {session.Attempts} timed out");
                EndAttempt(session, now);
            }
        }

        private static void StartAttempt(SteamClient client, Session session, ulong now)
        {
            var identity = new CSteamID(session.HostId).ToNetId();
            session.Pending = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
            session.PendingConnected = false;
            session.AttemptStartMsec = now;
            session.Attempts++;
            if (session.Attempts <= 3 || session.Attempts % 10 == 0)
            {
                RefLogger(client).Info($"Reconnect attempt {session.Attempts} to the host ({session.HostId})");
            }
        }

        private static void EndAttempt(Session session, ulong now)
        {
            if (session.Pending is { } pending)
            {
                SteamNetworkingSockets.CloseConnection(pending, SteamResumePacketHelper.ResumingCloseReason, null,
                    false);
            }

            session.Pending = null;
            session.PendingConnected = false;
            session.NextAttemptMsec = now + RetryDelayMsec;
        }

        // Returns whether the session carries on.
        private bool FinishAttempt(SteamClient client, Session session, ENetLanResumeResponse response, ulong now)
        {
            var logger = RefLogger(client);
            var pending = session.Pending!.Value;
            if (!response.accepted || !session.Link.CanResumeFrom(response.receivedCount))
            {
                logger.Warn($"The host wouldn't resume this session (accepted: {response.accepted}), disconnecting");
                _sessions.Remove(client);
                ConnectionStatusOverlay.Hide();
                // SteamClient's own disconnect closes this connection, leaves the lobby and tells the game.
                RefConn(client) = pending;
                DisconnectFromHostInternalMethod.Invoke(client,
                    [SteamDisconnectionReason.MiscTimeout, "The host couldn't resume the session", true, false]);
                return false;
            }

            RefConn(client) = pending;
            session.Connection = pending.m_HSteamNetConnection;
            session.Pending = null;
            session.PendingConnected = false;
            session.Reconnecting = false;

            var missing = session.Link.Resume(response.receivedCount);
            foreach (var packet in missing)
            {
                SteamResumePacketHelper.Send(pending, packet, packet.Length, NetTransferMode.Reliable);
            }

            SendAck(pending, session);
            ConnectionStatusOverlay.Hide();
            logger.Info(
                $"Reconnected to the host after {(now - session.LostAtMsec) / 1000.0:0.0}s awake, resent {missing.Count} messages");
            return true;
        }
    }
}
