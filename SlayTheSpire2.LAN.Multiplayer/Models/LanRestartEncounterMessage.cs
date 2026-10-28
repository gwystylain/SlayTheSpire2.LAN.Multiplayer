using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace SlayTheSpire2.LAN.Multiplayer.Models
{
    // Host to clients: the host is about to quit and host the save again to restart the current encounter.
    public struct LanRestartEncounterMessage : INetMessage
    {
        public bool ShouldBroadcast => false;
        public NetTransferMode Mode => NetTransferMode.Reliable;
        public LogLevel LogLevel => LogLevel.Info;
        public bool ShouldBuffer => false;

        public void Serialize(PacketWriter writer)
        {
        }

        public void Deserialize(PacketReader reader)
        {
        }
    }
}
