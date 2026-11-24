using System.Runtime.InteropServices;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using Steamworks;

namespace SlayTheSpire2.LAN.Multiplayer.Helpers
{
    internal readonly record struct SteamReceivedMessage(ulong Sender, byte[] Data, NetTransferMode Mode, int Channel);

    // The resume protocol (see LanResumePacketHelper) over Steam's connections. Steam carries the game's bytes as they
    // are, so resume packets lead with a marker no game traffic starts with: game messages start with their type's
    // index (under 64 today) and the handshake with "STS2". After the marker comes the same packet the LAN uses.
    internal static class SteamResumePacketHelper
    {
        private const byte Marker = 0xF5;

        // Closing a connection on purpose sends an app reason (1000 to 2999), which the other side takes as the player
        // leaving. A side that drops its connection only to reconnect sends this one, which means "come back".
        public const int ResumingCloseReason = 2577;

        private static readonly nint[] MessageBuffer = new nint[64];

        public static byte[] Wrap(byte[] lanResumePacket)
        {
            var array = new byte[lanResumePacket.Length + 1];
            array[0] = Marker;
            lanResumePacket.CopyTo(array, 1);
            return array;
        }

        public static bool TryUnwrap(byte[] message, out LanResumePacketType type, out byte[] lanResumePacket)
        {
            type = default;
            lanResumePacket = [];
            if (message.Length < 2 || message[0] != Marker ||
                !Enum.IsDefined(typeof(LanResumePacketType), message[1]))
                return false;

            type = (LanResumePacketType)message[1];
            lanResumePacket = message[1..];
            return true;
        }

        // Anything but a deliberate close (a network problem, a timeout, or our resuming close) can be resumed.
        public static bool IsDeliberateClose(ESteamNetworkingConnectionState state, int endReason)
        {
            return state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer &&
                   endReason is >= 1000 and < 3000 && endReason != ResumingCloseReason;
        }

        public static bool IsClosed(ESteamNetworkingConnectionState state)
        {
            return state is ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                or ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally;
        }

        public static EResult Send(HSteamNetConnection conn, byte[] bytes, int length, NetTransferMode mode)
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return SteamNetworkingSockets.SendMessageToConnection(conn, handle.AddrOfPinnedObject(), (uint)length,
                    SteamUtil.FlagsFromMode(mode), out _);
            }
            finally
            {
                handle.Free();
            }
        }

        public static EResult SendResumePacket(HSteamNetConnection conn, byte[] lanResumePacket, NetTransferMode mode)
        {
            var packet = Wrap(lanResumePacket);
            return Send(conn, packet, packet.Length, mode);
        }

        // SteamUtil.ProcessMessages, collecting the messages instead of handing them straight to the game.
        public static List<SteamReceivedMessage> Receive(HSteamNetConnection conn)
        {
            var messages = new List<SteamReceivedMessage>();
            int count;
            do
            {
                count = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, MessageBuffer, MessageBuffer.Length);
                for (var i = 0; i < count; i++)
                {
                    var pointer = MessageBuffer[i];
                    try
                    {
                        var message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(pointer);
                        var data = new byte[message.m_cbSize];
                        Marshal.Copy(message.m_pData, data, 0, message.m_cbSize);
                        messages.Add(new SteamReceivedMessage(message.m_identityPeer.GetSteamID().m_SteamID, data,
                            SteamUtil.ModeFromFlags(message.m_nFlags), message.m_nChannel));
                    }
                    catch (Exception e)
                    {
                        Log.Error($"[LAN] Failed to read Steam message {i + 1} of {count}: {e}");
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(pointer);
                    }
                }
            } while (count == MessageBuffer.Length);

            return messages;
        }
    }
}
