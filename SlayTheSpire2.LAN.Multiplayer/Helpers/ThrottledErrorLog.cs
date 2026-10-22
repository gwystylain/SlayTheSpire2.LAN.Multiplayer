using Godot;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace SlayTheSpire2.LAN.Multiplayer.Helpers
{
    // ENet reports an error every frame its socket can't send, such as while a Deck's network comes back
    // after sleep. Logs the first of a burst, then one line every few seconds saying how many it skipped.
    internal class ThrottledErrorLog
    {
        private const ulong IntervalMsec = 10000;

        private ulong? _lastLogMsec;

        private int _skipped;

        public void Error(Logger logger, string message)
        {
            var now = Time.GetTicksMsec();
            if (_lastLogMsec.HasValue && now - _lastLogMsec.Value < IntervalMsec)
            {
                _skipped++;
                return;
            }

            logger.Error(_skipped > 0 ? $"{message} ({_skipped} more since the last one)" : message);
            _lastLogMsec = now;
            _skipped = 0;
        }
    }
}
