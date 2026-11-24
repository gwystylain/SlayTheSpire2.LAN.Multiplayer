using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using SlayTheSpire2.LAN.Multiplayer.Components;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Models;
using Steamworks;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // LanHostResumeService for Steam invite games. A player whose Steam connection dies without them leaving on
    // purpose stays in the game's player list; what the game sends them is queued. When a new connection from their
    // Steam ID arrives, its first message decides: a resume request with their token swaps it in for the dead one and
    // each side resends what the other missed; anything else is the player joining again from scratch.
    internal class SteamHostResumeService
    {
        private static readonly Lazy<SteamHostResumeService> Lazy = new(() => new SteamHostResumeService());

        public static SteamHostResumeService Instance => Lazy.Value;

        private const ulong PendingTimeoutMsec = 30000;

        private static readonly AccessTools.FieldRef<SteamHost, Logger> RefLogger =
            AccessTools.FieldRefAccess<SteamHost, Logger>("_logger");

        private static readonly AccessTools.FieldRef<NetHost, INetHostHandler> RefHandler =
            AccessTools.FieldRefAccess<NetHost, INetHostHandler>("_handler");

        private static readonly FieldInfo ConnectionsField = AccessTools.Field(typeof(SteamHost), "_connections");

        private static readonly Type ClientConnectionType = AccessTools.Inner(typeof(SteamHost), "ClientConnection");

        private static readonly FieldInfo ConnectionConnField = AccessTools.Field(ClientConnectionType, "conn");

        private static readonly FieldInfo ConnectionNetIdField = AccessTools.Field(ClientConnectionType, "netId");

        private readonly ConditionalWeakTable<SteamHost, HostSessions> _hosts = new();

        private SteamHostResumeService()
        {
        }

        private class ClientSession(ulong token)
        {
            public readonly ulong Token = token;

            public readonly LanReliableLink Link = new();

            // Set once the client acknowledges anything, which clients without this mod never do. Only those clients
            // are waited for when their connection drops.
            public bool ClientResumes;

            public bool Suspended;

            public ulong SuspendedAtMsec;
        }

        // A new connection from a player who already has a session, held back from the game until its first message.
        private class PendingConnection(HSteamNetConnection conn, SteamNetworkingIdentity identity, ulong sinceMsec)
        {
            public readonly HSteamNetConnection Conn = conn;

            public readonly SteamNetworkingIdentity Identity = identity;

            public readonly ulong SinceMsec = sinceMsec;
        }

        private class HostSessions
        {
            public readonly Dictionary<ulong, ClientSession> Clients = new();

            public readonly Dictionary<ulong, PendingConnection> Pending = new();
        }

        // Players with a session can reconnect without being in the Steam lobby, which they may have dropped out of
        // while away, and which the game makes private once a run starts.
        public bool HasSession(SteamHost host, ulong steamId)
        {
            return _hosts.TryGetValue(host, out var sessions) && sessions.Clients.ContainsKey(steamId);
        }

        // Returns false when it handled the change itself, true to let SteamHost.OnNetStatusChanged handle it.
        public bool OnNetStatusChanged(SteamHost host, SteamNetConnectionStatusChangedCallback_t data)
        {
            var sessions = _hosts.GetOrCreateValue(host);
            var logger = RefLogger(host);
            var steamId = data.m_info.m_identityRemote.GetSteamID64();
            var state = data.m_info.m_eState;

            if (sessions.Pending.TryGetValue(steamId, out var pending) &&
                pending.Conn.m_HSteamNetConnection == data.m_hConn.m_HSteamNetConnection)
            {
                if (SteamResumePacketHelper.IsClosed(state))
                {
                    logger.Info($"Player {steamId}'s new connection closed before saying anything");
                    SteamNetworkingSockets.CloseConnection(pending.Conn, (int)SteamDisconnectionReason.AppGeneric,
                        null, false);
                    sessions.Pending.Remove(steamId);
                }

                return false;
            }

            switch (state)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected
                    when sessions.Clients.ContainsKey(steamId):
                    if (sessions.Pending.Remove(steamId, out var older))
                    {
                        SteamNetworkingSockets.CloseConnection(older.Conn, (int)SteamDisconnectionReason.AppGeneric,
                            null, false);
                    }

                    sessions.Pending[steamId] = new PendingConnection(data.m_hConn, data.m_info.m_identityRemote,
                        Time.GetTicksMsec());
                    logger.Info($"Player {steamId} connected again, waiting to see whether they're resuming");
                    return false;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    // The game sends its handshake as soon as it hears of the player, so count from before that.
                    sessions.Clients[steamId] = new ClientSession((ulong)Random.Shared.NextInt64());
                    return true;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    var index = IndexOfConn(host, data.m_hConn);
                    if (index < 0)
                        return true;

                    sessions.Clients.TryGetValue(steamId, out var client);
                    if (client is { ClientResumes: true, Link.Overflowed: false, Suspended: false } &&
                        !SteamResumePacketHelper.IsDeliberateClose(state, data.m_info.m_eEndReason))
                    {
                        SteamNetworkingSockets.CloseConnection(data.m_hConn, SteamResumePacketHelper.ResumingCloseReason,
                            null, false);
                        client.Suspended = true;
                        client.SuspendedAtMsec = Time.GetTicksMsec();
                        logger.Info(
                            $"Lost the connection to player {steamId} ({data.m_info.m_eEndReason}, {data.m_info.m_szEndDebug}), waiting for them to reconnect");
                        return false;
                    }

                    // They left on purpose, or can't resume: SteamHost removes them and tells the game.
                    sessions.Clients.Remove(steamId);
                    return true;
                default:
                    return true;
            }
        }

        // After SteamHost.OnNetStatusChanged: a player the game just accepted gets the token to resume with.
        public void AfterNetStatusChanged(SteamHost host, SteamNetConnectionStatusChangedCallback_t data)
        {
            if (data.m_info.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected ||
                IndexOfConn(host, data.m_hConn) < 0 || !_hosts.TryGetValue(host, out var sessions) ||
                !sessions.Clients.TryGetValue(data.m_info.m_identityRemote.GetSteamID64(), out var client))
                return;

            SteamResumePacketHelper.SendResumePacket(data.m_hConn, LanResumePacketHelper.FromHello(client.Token),
                NetTransferMode.Reliable);
        }

        public void Update(SteamHost host)
        {
            var sessions = _hosts.GetOrCreateValue(host);
            var now = Time.GetTicksMsec();

            foreach (var (conn, steamId) in Connections(host))
            {
                sessions.Clients.TryGetValue(steamId, out var client);
                if (client is { Suspended: true })
                    continue;

                foreach (var message in SteamResumePacketHelper.Receive(conn))
                {
                    Deliver(host, client, message);
                }

                if (client is { ClientResumes: true } && client.Link.ShouldSendAck(now))
                {
                    SteamResumePacketHelper.SendResumePacket(conn,
                        LanResumePacketHelper.FromAck(client.Link.ReceivedCount), NetTransferMode.Unreliable);
                }
            }

            foreach (var (steamId, pending) in sessions.Pending.ToList())
            {
                UpdatePending(host, sessions, steamId, pending, now);
            }

            ulong? waitingFor = null;
            var waitingSinceMsec = now;
            foreach (var (steamId, client) in sessions.Clients)
            {
                if (client.Suspended && client.SuspendedAtMsec <= waitingSinceMsec)
                {
                    waitingFor = steamId;
                    waitingSinceMsec = client.SuspendedAtMsec;
                }
            }

            if (waitingFor.HasValue)
            {
                var text = new LocString("gameplay_ui", "SlayTheSpire2.LAN.Multiplayer.WAITING_FOR_PLAYER");
                text.Add("Player", PlatformUtil.GetPlayerNameRaw(PlatformType.Steam, waitingFor.Value));
                ConnectionStatusOverlay.Show(text, now - waitingSinceMsec);
            }
        }

        private static void Deliver(SteamHost host, ClientSession? client, SteamReceivedMessage message)
        {
            if (SteamResumePacketHelper.TryUnwrap(message.Data, out var type, out var packet))
            {
                if (type == LanResumePacketType.Ack && client != null)
                {
                    client.ClientResumes = true;
                    client.Link.Acknowledge(LanResumePacketHelper.AsULong(packet));
                }
                else
                {
                    RefLogger(host).Warn($"Got unexpected {type} packet from player {message.Sender}");
                }

                return;
            }

            if (client != null && message.Mode == NetTransferMode.Reliable)
            {
                client.Link.RecordReceived();
            }

            RefHandler(host).OnPacketReceived(message.Sender, message.Data, message.Mode, message.Channel);
        }

        private void UpdatePending(SteamHost host, HostSessions sessions, ulong steamId, PendingConnection pending,
            ulong now)
        {
            var messages = SteamResumePacketHelper.Receive(pending.Conn);
            if (messages.Count == 0)
            {
                if (now - pending.SinceMsec >= PendingTimeoutMsec)
                {
                    RefLogger(host).Info($"Player {steamId}'s new connection said nothing, closing it");
                    SteamNetworkingSockets.CloseConnection(pending.Conn, (int)SteamDisconnectionReason.AppGeneric,
                        null, false);
                    sessions.Pending.Remove(steamId);
                }

                return;
            }

            sessions.Pending.Remove(steamId);
            ClientSession? client;
            if (SteamResumePacketHelper.TryUnwrap(messages[0].Data, out var type, out var packet) &&
                type == LanResumePacketType.ResumeRequest)
            {
                client = Resume(host, sessions, steamId, pending, LanResumePacketHelper.AsResumeRequest(packet));
            }
            else
            {
                client = JoinAgain(host, sessions, steamId, pending);
                Deliver(host, client, messages[0]);
            }

            if (client == null)
                return;

            for (var i = 1; i < messages.Count; i++)
            {
                Deliver(host, client, messages[i]);
            }
        }

        private ClientSession? Resume(SteamHost host, HostSessions sessions, ulong steamId, PendingConnection pending,
            ENetLanResumeRequest request)
        {
            var logger = RefLogger(host);
            var index = IndexOfNetId(host, steamId);
            sessions.Clients.TryGetValue(steamId, out var client);
            var refusal = index < 0 || client == null ? "no such session"
                : request.netId != steamId ? "wrong player"
                : client.Token != request.token ? "wrong token"
                : !client.Link.CanResumeFrom(request.receivedCount) ? "messages it missed are gone"
                : null;
            if (refusal != null)
            {
                logger.Warn($"Refused to resume player {steamId}: {refusal}");
                SteamResumePacketHelper.SendResumePacket(pending.Conn, LanResumePacketHelper.FromResumeResponse(
                    new ENetLanResumeResponse { accepted = false }), NetTransferMode.Reliable);
                SteamNetworkingSockets.CloseConnection(pending.Conn, (int)SteamDisconnectionReason.AppGeneric,
                    "Couldn't resume", true);
                return null;
            }

            if (!client!.Suspended)
            {
                SteamNetworkingSockets.CloseConnection(ConnAt(host, index), SteamResumePacketHelper.ResumingCloseReason,
                    "Replaced", false);
            }

            SetConnAt(host, index, pending.Conn);
            var lostForMsec = client.Suspended ? Time.GetTicksMsec() - client.SuspendedAtMsec : 0;
            client.Suspended = false;
            SteamResumePacketHelper.SendResumePacket(pending.Conn, LanResumePacketHelper.FromResumeResponse(
                new ENetLanResumeResponse
                {
                    accepted = true,
                    receivedCount = client.Link.ReceivedCount
                }), NetTransferMode.Reliable);

            var missing = client.Link.Resume(request.receivedCount);
            foreach (var packet in missing)
            {
                SteamResumePacketHelper.Send(pending.Conn, packet, packet.Length, NetTransferMode.Reliable);
            }

            logger.Info(
                $"Player {steamId} reconnected after {lostForMsec / 1000.0:0.0}s, resent {missing.Count} messages");
            return client;
        }

        // The player's game started over (it restarted, or they rejoined from the menu), so their old connection is
        // done: tell the game they left, then let them join like anyone else.
        private ClientSession JoinAgain(SteamHost host, HostSessions sessions, ulong steamId,
            PendingConnection pending)
        {
            RefLogger(host).Info($"Player {steamId} joined again, dropping their old connection");
            var index = IndexOfNetId(host, steamId);
            if (index >= 0)
            {
                SteamNetworkingSockets.CloseConnection(ConnAt(host, index), (int)SteamDisconnectionReason.AppGeneric,
                    null, false);
                ConnectionsList(host).RemoveAt(index);
                RefHandler(host).OnPeerDisconnected(steamId,
                    new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: false));
            }

            var client = new ClientSession((ulong)Random.Shared.NextInt64());
            sessions.Clients[steamId] = client;
            var clientConnection = Activator.CreateInstance(ClientConnectionType)!;
            ConnectionConnField.SetValue(clientConnection, pending.Conn);
            ConnectionNetIdField.SetValue(clientConnection, pending.Identity);
            ConnectionsList(host).Add(clientConnection);
            RefHandler(host).OnPeerConnected(steamId);
            SteamResumePacketHelper.SendResumePacket(pending.Conn, LanResumePacketHelper.FromHello(client.Token),
                NetTransferMode.Reliable);
            return client;
        }

        // Before SteamHost.SendMessageToClient: records reliable messages and holds them back from a player who's
        // away. Returns whether SteamHost should send it.
        public bool BeforeSendMessageToClient(SteamHost host, ulong peerId, byte[] bytes, int length,
            NetTransferMode mode)
        {
            if (!_hosts.TryGetValue(host, out var sessions) || !sessions.Clients.TryGetValue(peerId, out var client) ||
                IndexOfNetId(host, peerId) < 0)
                return true;

            if (mode == NetTransferMode.Reliable)
            {
                client.Link.RecordSent(bytes.AsSpan(0, length).ToArray());
            }

            return !client.Suspended;
        }

        public void OnClientDisconnected(SteamHost host, ulong peerId)
        {
            if (!_hosts.TryGetValue(host, out var sessions))
                return;

            sessions.Clients.Remove(peerId);
            if (sessions.Pending.Remove(peerId, out var pending))
            {
                SteamNetworkingSockets.CloseConnection(pending.Conn, (int)SteamDisconnectionReason.AppGeneric, null,
                    false);
            }
        }

        public void OnStopped(SteamHost host)
        {
            if (_hosts.TryGetValue(host, out var sessions))
            {
                foreach (var pending in sessions.Pending.Values)
                {
                    SteamNetworkingSockets.CloseConnection(pending.Conn, (int)SteamDisconnectionReason.AppGeneric,
                        null, false);
                }
            }

            _hosts.Remove(host);
            ConnectionStatusOverlay.Hide();
        }

        private static IList ConnectionsList(SteamHost host)
        {
            return (IList)ConnectionsField.GetValue(host)!;
        }

        private static List<(HSteamNetConnection Conn, ulong SteamId)> Connections(SteamHost host)
        {
            var result = new List<(HSteamNetConnection, ulong)>();
            foreach (var clientConnection in ConnectionsList(host))
            {
                result.Add(((HSteamNetConnection)ConnectionConnField.GetValue(clientConnection)!,
                    ((SteamNetworkingIdentity)ConnectionNetIdField.GetValue(clientConnection)!).GetSteamID64()));
            }

            return result;
        }

        private static int IndexOfConn(SteamHost host, HSteamNetConnection conn)
        {
            var connections = Connections(host);
            return connections.FindIndex(c => c.Conn.m_HSteamNetConnection == conn.m_HSteamNetConnection);
        }

        private static int IndexOfNetId(SteamHost host, ulong steamId)
        {
            return Connections(host).FindIndex(c => c.SteamId == steamId);
        }

        private static HSteamNetConnection ConnAt(SteamHost host, int index)
        {
            return (HSteamNetConnection)ConnectionConnField.GetValue(ConnectionsList(host)[index])!;
        }

        // ClientConnection is a struct, so set the field on a boxed copy and store the copy back.
        private static void SetConnAt(SteamHost host, int index, HSteamNetConnection conn)
        {
            var connections = ConnectionsList(host);
            var clientConnection = connections[index]!;
            ConnectionConnField.SetValue(clientConnection, conn);
            connections[index] = clientConnection;
        }
    }
}
