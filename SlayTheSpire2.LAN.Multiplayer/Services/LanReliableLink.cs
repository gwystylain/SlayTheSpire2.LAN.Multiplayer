namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // One side of a connection's reliable stream (channel 0 app messages): what we sent that the peer
    // hasn't acknowledged yet, and how many we've received. ENet delivers channel 0 reliably and in
    // order, so when a connection dies and resumes, each side sends the other its received count and
    // resends everything past it.
    internal class LanReliableLink
    {
        private const int MaxUnackedBytes = 16 * 1024 * 1024;

        private const ulong AckIntervalMsec = 500;

        private const ulong AckRepeatMsec = 5000;

        private readonly Queue<byte[]> _unacked = new();

        private int _unackedBytes;

        private ulong _sentCount;

        private ulong _lastAckedCount;

        private ulong _lastAckMsec;

        public ulong ReceivedCount { get; private set; }

        // Set when the peer stopped acknowledging long enough for the buffer to hit its cap. The link
        // can't resume after that, because it no longer holds everything the peer might be missing.
        public bool Overflowed { get; private set; }

        private ulong FirstUnacked => _sentCount - (ulong)_unacked.Count;

        public void RecordSent(byte[] packet)
        {
            _sentCount++;
            if (Overflowed)
                return;

            _unacked.Enqueue(packet);
            _unackedBytes += packet.Length;
            if (_unackedBytes > MaxUnackedBytes)
            {
                Overflowed = true;
                _unacked.Clear();
                _unackedBytes = 0;
            }
        }

        public void RecordReceived()
        {
            ReceivedCount++;
        }

        public void Acknowledge(ulong peerReceivedCount)
        {
            while (_unacked.Count > 0 && FirstUnacked < peerReceivedCount)
            {
                _unackedBytes -= _unacked.Dequeue().Length;
            }
        }

        public bool CanResumeFrom(ulong peerReceivedCount)
        {
            return !Overflowed && peerReceivedCount >= FirstUnacked && peerReceivedCount <= _sentCount;
        }

        // Everything the peer is missing, oldest first.
        public List<byte[]> Resume(ulong peerReceivedCount)
        {
            Acknowledge(peerReceivedCount);
            return _unacked.ToList();
        }

        public bool ShouldSendAck(ulong nowMsec)
        {
            var sinceLast = nowMsec - _lastAckMsec;
            if (sinceLast < AckIntervalMsec || (ReceivedCount == _lastAckedCount && sinceLast < AckRepeatMsec))
                return false;

            _lastAckedCount = ReceivedCount;
            _lastAckMsec = nowMsec;
            return true;
        }
    }
}
