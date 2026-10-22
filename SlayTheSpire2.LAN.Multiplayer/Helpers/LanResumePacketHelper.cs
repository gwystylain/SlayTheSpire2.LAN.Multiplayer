using System.Buffers.Binary;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using SlayTheSpire2.LAN.Multiplayer.Models;

// ReSharper disable ClassNeverInstantiated.Global

namespace SlayTheSpire2.LAN.Multiplayer.Helpers
{
    // Packet types the resume protocol adds after ENetPacketType's 0-3. They lead with the same type
    // byte, so both transports pick them out before handing anything to the game's own handling.
    internal enum LanResumePacketType : byte
    {
        // Host to client, right after the handshake: the token that lets this client resume.
        Hello = 0x40,

        // Either way: how many reliable app messages we've received, so the sender can drop them.
        Ack = 0x41,

        // Client to host, on a fresh ENet connection after the old one died.
        ResumeRequest = 0x42,

        ResumeResponse = 0x43
    }

    internal class LanResumePacketHelper
    {
        // Sent as the ENet disconnect data when a side leaves on purpose, so the other side doesn't wait
        // for it to come back. A timed-out peer disconnects with 0.
        public const int GracefulDisconnectData = 0x4C414E;

        public const int ReliableFlags = 1;

        public static bool TryGetType(byte[] packetData, out LanResumePacketType type)
        {
            type = default;
            if (packetData.Length == 0 || !Enum.IsDefined(typeof(LanResumePacketType), packetData[0]))
                return false;

            type = (LanResumePacketType)packetData[0];
            return true;
        }

        public static bool IsReliableAppMessage(ENetServiceData data)
        {
            return data.channel == 0 && data.packetData.Length > 0 &&
                   data.packetData[0] == (byte)ENetPacketType.ApplicationMessage;
        }

        public static bool IsGracefulDisconnect(ENetServiceData data)
        {
            return data.originalData is { Count: > 2 } &&
                   data.originalData[2].AsInt32() == GracefulDisconnectData;
        }

        public static byte[] FromHello(ulong token)
        {
            return WithULongs(LanResumePacketType.Hello, token);
        }

        public static byte[] FromAck(ulong receivedCount)
        {
            return WithULongs(LanResumePacketType.Ack, receivedCount);
        }

        public static ulong AsULong(byte[] packetData)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(packetData.AsSpan(1, 8));
        }

        public static byte[] FromResumeRequest(ENetLanResumeRequest request)
        {
            return WithULongs(LanResumePacketType.ResumeRequest, request.netId, request.token, request.receivedCount);
        }

        public static ENetLanResumeRequest AsResumeRequest(byte[] packetData)
        {
            var span = packetData.AsSpan();
            return new ENetLanResumeRequest
            {
                netId = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(1, 8)),
                token = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(9, 8)),
                receivedCount = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(17, 8))
            };
        }

        public static byte[] FromResumeResponse(ENetLanResumeResponse response)
        {
            var array = new byte[10];
            array[0] = (byte)LanResumePacketType.ResumeResponse;
            array[1] = (byte)(response.accepted ? 1 : 0);
            BinaryPrimitives.WriteUInt64BigEndian(array.AsSpan(2, 8), response.receivedCount);
            return array;
        }

        public static ENetLanResumeResponse AsResumeResponse(byte[] packetData)
        {
            return new ENetLanResumeResponse
            {
                accepted = packetData[1] == 1,
                receivedCount = BinaryPrimitives.ReadUInt64BigEndian(packetData.AsSpan(2, 8))
            };
        }

        private static byte[] WithULongs(LanResumePacketType type, params ulong[] values)
        {
            var array = new byte[1 + values.Length * 8];
            array[0] = (byte)type;
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(array.AsSpan(1 + i * 8, 8), values[i]);
            }

            return array;
        }
    }
}
