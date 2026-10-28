using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using SlayTheSpire2.LAN.Multiplayer.Components;
using SlayTheSpire2.LAN.Multiplayer.Helpers;
using SlayTheSpire2.LAN.Multiplayer.Models;
using SlayTheSpire2.LAN.Multiplayer.Patchs.ENet;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // The host's half of resuming a session. A client whose connection dies without saying goodbye stays
    // in the game's player list; what the game sends it is queued until it reconnects with its token, at
    // which point its connection is swapped for the new one and each side resends what the other missed.
    // A client that leaves on purpose, or can't resume, is reported to the game as disconnected.
    internal class LanHostResumeService
    {
        private static readonly Lazy<LanHostResumeService> Lazy = new(() => new LanHostResumeService());

        public static LanHostResumeService Instance => Lazy.Value;

        private static readonly AccessTools.FieldRef<ENetHost, ENetConnection?> RefConnection =
            AccessTools.FieldRefAccess<ENetHost, ENetConnection?>("_connection");

        private static readonly AccessTools.FieldRef<ENetHost, Logger> RefLogger =
            AccessTools.FieldRefAccess<ENetHost, Logger>("_logger");

        private static readonly FieldInfo ConnectedPeersField = AccessTools.Field(typeof(ENetHost), "_connectedPeers");

        private static readonly Type ClientConnectionType = AccessTools.Inner(typeof(ENetHost), "ClientConnection");

        private static readonly FieldInfo ConnectionNetIdField = AccessTools.Field(ClientConnectionType, "netId");

        private static readonly FieldInfo ConnectionPeerField = AccessTools.Field(ClientConnectionType, "peer");

        private static readonly MethodInfo DoClientHandshakeMethod =
            AccessTools.Method(typeof(ENetHost), "DoClientHandshake");

        private static readonly MethodInfo HandleClientDisconnectionMethod =
            AccessTools.Method(typeof(ENetHost), "HandleClientDisconnection");

        private static readonly int ReliableChannel = NetTransferMode.Reliable.ToChannelId();

        private static readonly int ReliableFlags = ENetUtil.FlagsFromMode(NetTransferMode.Reliable);

        private readonly ConditionalWeakTable<ENetHost, HostSessions> _hosts = new();

        private readonly ThrottledErrorLog _errorLog = new();

        private LanHostResumeService()
        {
        }

        private class ClientSession(ulong token)
        {
            public readonly ulong Token = token;

            public readonly LanReliableLink Link = new();

            // Set once the client acknowledges anything, which older versions never do. Only those
            // clients are waited for when their connection drops.
            public bool ClientResumes;

            public bool Suspended;

            public ulong SuspendedAtMsec;
        }

        private class HostSessions
        {
            public readonly Dictionary<ulong, ClientSession> Clients = new();

            // Connections that came in to resume a session rather than to join, which the handshake
            // patch must leave alone.
            public readonly HashSet<ENetPacketPeer> ResumedPeers = new();
        }

        public void OnClientAccepted(ENetHost host, ulong netId, ENetPacketPeer peer)
        {
            var token = (ulong)Random.Shared.NextInt64();
            _hosts.GetOrCreateValue(host).Clients[netId] = new ClientSession(token);
            peer.Send(ReliableChannel, LanResumePacketHelper.FromHello(token), ReliableFlags);
        }

        public bool IsResumedPeer(ENetHost host, ENetPacketPeer peer)
        {
            return _hosts.TryGetValue(host, out var sessions) && sessions.ResumedPeers.Contains(peer);
        }

        public void Update(ENetHost host)
        {
            var connection = RefConnection(host) ?? throw new InvalidOperationException("Must call StartHost first!");
            var logger = RefLogger(host);
            var sessions = _hosts.GetOrCreateValue(host);
            while (connection.TryService(out var output))
            {
                var data = output!.Value;
                switch (data.type)
                {
                    case ENetConnection.EventType.Error:
                        _errorLog.Error(logger, "Got error from ENetConnection!");
                        break;
                    case ENetConnection.EventType.Connect:
                        logger.Info($"Incoming connection from {data.peer.GetRemoteAddress()}:{data.peer.GetRemotePort()}");
                        TaskHelper.RunSafely((Task)DoClientHandshakeMethod.Invoke(host, [data.peer])!);
                        break;
                    case ENetConnection.EventType.Disconnect:
                        OnPeerDisconnected(host, sessions, data);
                        break;
                    case ENetConnection.EventType.Receive:
                        OnPacketReceived(host, sessions, data);
                        break;
                }
            }

            var now = Time.GetTicksMsec();
            ulong? waitingFor = null;
            ulong waitingSinceMsec = now;
            foreach (var (netId, client) in sessions.Clients.ToList())
            {
                var index = IndexOfNetId(host, netId);
                if (index < 0)
                {
                    sessions.Clients.Remove(netId);
                    continue;
                }

                if (client.Suspended)
                {
                    if (client.SuspendedAtMsec <= waitingSinceMsec)
                    {
                        waitingFor = netId;
                        waitingSinceMsec = client.SuspendedAtMsec;
                    }

                    continue;
                }

                if (client.ClientResumes && client.Link.ShouldSendAck(now))
                {
                    PeerAt(host, index).Send(NetTransferMode.Unreliable.ToChannelId(),
                        LanResumePacketHelper.FromAck(client.Link.ReceivedCount),
                        ENetUtil.FlagsFromMode(NetTransferMode.Unreliable));
                }
            }

            if (waitingFor.HasValue)
            {
                var text = new LocString("gameplay_ui", "SlayTheSpire2.LAN.Multiplayer.WAITING_FOR_PLAYER");
                text.Add("Player", LanPlayerNameService.Instance.PlayerNames.GetValueOrDefault(waitingFor.Value) ??
                                   waitingFor.Value.ToString());
                ConnectionStatusOverlay.Show(text, now - waitingSinceMsec);
            }
        }

        // Returns false to let ENetHost log that no such client is connected.
        public bool SendMessageToClient(ENetHost host, ulong peerId, byte[] bytes, int length, NetTransferMode mode,
            int channel)
        {
            if (RefConnection(host) == null)
                throw new InvalidOperationException("Must call StartHost first!");

            var index = IndexOfNetId(host, peerId);
            if (index < 0)
                return false;

            var packet = ENetPacket.FromAppMessage(bytes, length).AllBytes;
            ClientSession? client = null;
            if (_hosts.TryGetValue(host, out var sessions) && sessions.Clients.TryGetValue(peerId, out client) &&
                channel == ReliableChannel)
            {
                client.Link.RecordSent(packet);
            }

            if (client is not { Suspended: true })
            {
                PeerAt(host, index).Send(channel, packet, ENetUtil.FlagsFromMode(mode));
            }

            return true;
        }

        // ENetHost.DisconnectClientInternal, plus the goodbye marker and dropping a client that's away.
        public void DisconnectClient(ENetHost host, ulong peerId, NetError reason, bool now, bool notifyHandler)
        {
            RefLogger(host).Info($"Disconnecting client {peerId}, reason: {reason}. Now: {now}");
            if (RefConnection(host) == null)
                throw new InvalidOperationException("Must call StartHost first!");

            var index = IndexOfNetId(host, peerId);
            if (index < 0)
                return;

            ClientSession? client = null;
            _hosts.TryGetValue(host, out var sessions);
            sessions?.Clients.Remove(peerId, out client);

            var clientConnection = ConnectedPeers(host)[index]!;
            if (client is not { Suspended: true })
            {
                var peer = PeerAt(host, index);
                if (now)
                {
                    RetirePeer(peer);
                }
                else
                {
                    var eNetPacket = ENetPacket.FromDisconnection(new ENetDisconnection
                    {
                        reason = reason
                    });
                    peer.Send(0, eNetPacket.AllBytes, 1);
                    peer.PeerDisconnectLater(LanResumePacketHelper.GracefulDisconnectData);
                }
            }

            HandleClientDisconnectionMethod.Invoke(host, [clientConnection, reason, notifyHandler]);
        }

        public void OnStopped(ENetHost host)
        {
            _hosts.Remove(host);
            ConnectionStatusOverlay.Hide();
        }

        // When a player joins again from scratch (their game restarted, or an earlier join failed) while their old
        // connection is still listed but dead or waiting to resume, drops the old one so the join keeps its net ID.
        // Otherwise the join counts as a second player with the same ID and gets a new one, which a loaded save
        // doesn't know.
        public bool DropStaleClient(ENetHost host, ulong netId)
        {
            var index = IndexOfNetId(host, netId);
            if (index < 0)
                return false;

            ClientSession? client = null;
            _hosts.TryGetValue(host, out var sessions);
            sessions?.Clients.TryGetValue(netId, out client);
            if (client is not { Suspended: true } && PeerAt(host, index).IsActive())
                return false;

            sessions?.Clients.Remove(netId);
            RefLogger(host).Info($"Player {netId} joined again, dropping their old connection");
            HandleClientDisconnectionMethod.Invoke(host,
                [ConnectedPeers(host)[index], NetError.UnknownNetworkError, true]);
            return true;
        }

        private static void OnPeerDisconnected(ENetHost host, HostSessions sessions, ENetServiceData data)
        {
            sessions.ResumedPeers.Remove(data.peer);
            var index = IndexOfPeer(host, data.peer);
            if (index < 0)
                return;

            var netId = NetIdAt(host, index);
            var graceful = LanResumePacketHelper.IsGracefulDisconnect(data);
            if (sessions.Clients.TryGetValue(netId, out var client) && client is
                    { ClientResumes: true, Link.Overflowed: false } && !graceful)
            {
                client.Suspended = true;
                client.SuspendedAtMsec = Time.GetTicksMsec();
                RefLogger(host).Info($"Lost the connection to player {netId}, waiting for them to reconnect");
                return;
            }

            // It left on purpose, or it can't resume: tell the game, as the Steam transport does. The game's own
            // ENetHost ignored these, which kept the player's slot (and net ID) taken until the host restarted.
            sessions.Clients.Remove(netId);
            RefLogger(host).Info($"Player {netId} disconnected{(graceful ? "" : " and can't resume")}");
            HandleClientDisconnectionMethod.Invoke(host,
                [ConnectedPeers(host)[index], graceful ? NetError.Quit : NetError.UnknownNetworkError, true]);
        }

        private static void OnPacketReceived(ENetHost host, HostSessions sessions, ENetServiceData data)
        {
            if (LanResumePacketHelper.TryGetType(data.packetData, out var type))
            {
                switch (type)
                {
                    case LanResumePacketType.Ack:
                        if (ClientByPeer(host, sessions, data.peer) is { } client)
                        {
                            client.ClientResumes = true;
                            client.Link.Acknowledge(LanResumePacketHelper.AsULong(data.packetData));
                        }

                        break;
                    case LanResumePacketType.ResumeRequest:
                        Resume(host, sessions, data.peer, LanResumePacketHelper.AsResumeRequest(data.packetData));
                        break;
                    default:
                        RefLogger(host).Warn($"Got unexpected {type} packet from a client");
                        break;
                }

                return;
            }

            if (LanResumePacketHelper.IsReliableAppMessage(data))
            {
                ClientByPeer(host, sessions, data.peer)?.Link.RecordReceived();
            }

            ENetHostReversePatches.HandlePacketReceived(host, data);
        }

        private static void Resume(ENetHost host, HostSessions sessions, ENetPacketPeer peer,
            ENetLanResumeRequest request)
        {
            var logger = RefLogger(host);
            sessions.ResumedPeers.Add(peer);

            var index = IndexOfNetId(host, request.netId);
            sessions.Clients.TryGetValue(request.netId, out var client);
            var refusal = index < 0 || client == null ? "no such session"
                : client.Token != request.token ? "wrong token"
                : !client.Link.CanResumeFrom(request.receivedCount) ? "messages it missed are gone"
                : null;
            if (refusal != null)
            {
                logger.Warn($"Refused to resume player {request.netId}: {refusal}");
                peer.Send(ReliableChannel, LanResumePacketHelper.FromResumeResponse(new ENetLanResumeResponse
                {
                    accepted = false
                }), ReliableFlags);
                peer.PeerDisconnectLater(LanResumePacketHelper.GracefulDisconnectData);
                return;
            }

            var oldPeer = PeerAt(host, index);
            if (!ReferenceEquals(oldPeer, peer) && oldPeer.IsActive())
            {
                RetirePeer(oldPeer);
            }

            SetPeerAt(host, index, peer);

            var lostForMsec = client!.Suspended ? Time.GetTicksMsec() - client.SuspendedAtMsec : 0;
            client.Suspended = false;
            peer.Send(ReliableChannel, LanResumePacketHelper.FromResumeResponse(new ENetLanResumeResponse
            {
                accepted = true,
                receivedCount = client.Link.ReceivedCount
            }), ReliableFlags);

            var missing = client.Link.Resume(request.receivedCount);
            foreach (var packet in missing)
            {
                peer.Send(ReliableChannel, packet, ReliableFlags);
            }

            logger.Info(
                $"Player {request.netId} reconnected after {lostForMsec / 1000.0:0.0}s awake, resent {missing.Count} messages");
        }

        // Disconnects a peer of a connection that keeps being serviced. Never Reset() or PeerDisconnectNow()
        // such a peer: Godot's ENetConnection.service() then drops it from its peer list by erasing the list
        // node and reading that node's next pointer after freeing it, which crashes the game. A plain
        // disconnect leaves the peer in the list until its Disconnect event, which removes it safely.
        public static void RetirePeer(ENetPacketPeer peer)
        {
            peer.PeerDisconnect(LanResumePacketHelper.GracefulDisconnectData);
        }

        private static ClientSession? ClientByPeer(ENetHost host, HostSessions sessions, ENetPacketPeer peer)
        {
            var index = IndexOfPeer(host, peer);
            return index < 0 ? null : sessions.Clients.GetValueOrDefault(NetIdAt(host, index));
        }

        private static IList ConnectedPeers(ENetHost host)
        {
            return (IList)ConnectedPeersField.GetValue(host)!;
        }

        private static int IndexOfNetId(ENetHost host, ulong netId)
        {
            var connectedPeers = ConnectedPeers(host);
            for (var i = 0; i < connectedPeers.Count; i++)
            {
                if ((ulong)ConnectionNetIdField.GetValue(connectedPeers[i])! == netId)
                    return i;
            }

            return -1;
        }

        private static int IndexOfPeer(ENetHost host, ENetPacketPeer peer)
        {
            var connectedPeers = ConnectedPeers(host);
            for (var i = 0; i < connectedPeers.Count; i++)
            {
                if (ReferenceEquals(ConnectionPeerField.GetValue(connectedPeers[i]), peer))
                    return i;
            }

            return -1;
        }

        private static ulong NetIdAt(ENetHost host, int index)
        {
            return (ulong)ConnectionNetIdField.GetValue(ConnectedPeers(host)[index])!;
        }

        private static ENetPacketPeer PeerAt(ENetHost host, int index)
        {
            return (ENetPacketPeer)ConnectionPeerField.GetValue(ConnectedPeers(host)[index])!;
        }

        // ClientConnection is a struct, so set the field on a boxed copy and store the copy back.
        private static void SetPeerAt(ENetHost host, int index, ENetPacketPeer peer)
        {
            var connectedPeers = ConnectedPeers(host);
            var clientConnection = connectedPeers[index]!;
            ConnectionPeerField.SetValue(clientConnection, peer);
            connectedPeers[index] = clientConnection;
        }
    }
}
