// ReSharper disable InconsistentNaming

namespace SlayTheSpire2.LAN.Multiplayer.Models
{
    public struct ENetLanResumeRequest
    {
        public ulong netId;

        public ulong token;

        public ulong receivedCount;
    }
}
