using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SlayTheSpire2.LAN.Multiplayer.Services
{
    // Finds LAN games without typing an address. The join screen broadcasts a query to DiscoveryPort on
    // every local network every couple of seconds, and a hosting game answers with its name and port.
    // Where the answer came from is the address to connect to.
    internal class LanDiscoveryService
    {
        private static readonly Lazy<LanDiscoveryService> Lazy = new(() => new LanDiscoveryService());

        public static LanDiscoveryService Instance => Lazy.Value;

        public const int DiscoveryPort = 33770;

        private const byte ProtocolVersion = 1;

        private const ulong QueryIntervalMsec = 2000;

        private const ulong ForgetAfterMsec = 6000;

        private const int MaxNameBytes = 64;

        private static readonly byte[] QueryMagic = "STS2LAN?"u8.ToArray();

        private static readonly byte[] AnswerMagic = "STS2LAN!"u8.ToArray();

        private readonly byte[] _buffer = new byte[512];

        private readonly Dictionary<string, FoundGame> _found = new();

        private Socket? _responder;

        private ushort _hostPort;

        private Socket? _scanner;

        private ulong _lastQueryMsec;

        private LanDiscoveryService()
        {
        }

        public class FoundGame(string address, ushort port, string hostName, bool inRun)
        {
            public readonly string Address = address;

            public readonly ushort Port = port;

            public readonly string HostName = hostName;

            public readonly bool InRun = inRun;

            public ulong LastSeenMsec;
        }

        public void StartResponding(ushort hostPort)
        {
            StopResponding();
            _hostPort = hostPort;
            try
            {
                _responder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false
                };
                _responder.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _responder.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            }
            catch (SocketException ex)
            {
                Log.Warn($"LAN discovery is off, couldn't listen on UDP port {DiscoveryPort}: {ex.Message}");
                StopResponding();
            }
        }

        public void StopResponding()
        {
            _responder?.Dispose();
            _responder = null;
        }

        // Called every frame while hosting.
        public void Respond()
        {
            if (_responder == null)
                return;

            try
            {
                while (_responder.Available > 0)
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    var length = _responder.ReceiveFrom(_buffer, ref remote);
                    if (length >= QueryMagic.Length + 1 && _buffer.AsSpan(0, QueryMagic.Length).SequenceEqual(QueryMagic))
                    {
                        _responder.SendTo(BuildAnswer(), remote);
                    }
                }
            }
            catch (SocketException ex)
            {
                Log.Warn($"LAN discovery couldn't answer a query: {ex.Message}");
            }
        }

        // Called every frame while the join screen is up. Starts scanning on the first call.
        public IReadOnlyCollection<FoundGame> Scan()
        {
            var now = Time.GetTicksMsec();
            if (_scanner == null)
            {
                try
                {
                    _scanner = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                    {
                        Blocking = false,
                        EnableBroadcast = true
                    };
                    _scanner.Bind(new IPEndPoint(IPAddress.Any, 0));
                    _lastQueryMsec = 0;
                }
                catch (SocketException ex)
                {
                    Log.Warn($"LAN discovery couldn't open a socket to scan with: {ex.Message}");
                    StopScanning();
                    return _found.Values;
                }
            }

            if (_lastQueryMsec == 0 || now - _lastQueryMsec >= QueryIntervalMsec)
            {
                SendQueries();
                _lastQueryMsec = now;
            }

            try
            {
                while (_scanner.Available > 0)
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    var length = _scanner.ReceiveFrom(_buffer, ref remote);
                    if (TryReadAnswer(_buffer.AsSpan(0, length), ((IPEndPoint)remote).Address.ToString(),
                            out var game))
                    {
                        game.LastSeenMsec = now;
                        _found[$"{game.Address}:{game.Port}"] = game;
                    }
                }
            }
            catch (SocketException ex)
            {
                Log.Warn($"LAN discovery couldn't read an answer: {ex.Message}");
            }

            foreach (var (key, game) in _found.ToList())
            {
                if (now - game.LastSeenMsec > ForgetAfterMsec)
                {
                    _found.Remove(key);
                }
            }

            return _found.Values;
        }

        public void StopScanning()
        {
            _scanner?.Dispose();
            _scanner = null;
            _found.Clear();
        }

        private void SendQueries()
        {
            var query = new byte[QueryMagic.Length + 1];
            QueryMagic.CopyTo(query, 0);
            query[^1] = ProtocolVersion;

            foreach (var address in BroadcastAddresses())
            {
                try
                {
                    _scanner!.SendTo(query, new IPEndPoint(address, DiscoveryPort));
                }
                catch (SocketException)
                {
                    // That network may be down; the others still get the query.
                }
            }
        }

        // The limited broadcast address only leaves through the default route, so also send to each
        // network's own broadcast address in case the Deck is on more than one.
        private static IEnumerable<IPAddress> BroadcastAddresses()
        {
            var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        var bytes = unicast.Address.GetAddressBytes();
                        var mask = unicast.IPv4Mask.GetAddressBytes();
                        for (var i = 0; i < bytes.Length; i++)
                        {
                            bytes[i] |= (byte)~mask[i];
                        }

                        addresses.Add(new IPAddress(bytes));
                    }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                Log.Warn($"LAN discovery couldn't list network interfaces: {ex.Message}");
            }

            return addresses;
        }

        private byte[] BuildAnswer()
        {
            var name = Encoding.UTF8.GetBytes(HostName());
            if (name.Length > MaxNameBytes)
            {
                name = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(name, 0, MaxNameBytes).TrimEnd('�'));
            }

            var answer = new byte[AnswerMagic.Length + 4 + name.Length];
            AnswerMagic.CopyTo(answer, 0);
            answer[AnswerMagic.Length] = ProtocolVersion;
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(AnswerMagic.Length + 1, 2), _hostPort);
            answer[AnswerMagic.Length + 3] = (byte)(RunManager.Instance.IsInProgress ? 1 : 0);
            name.CopyTo(answer, AnswerMagic.Length + 4);
            return answer;
        }

        private static bool TryReadAnswer(ReadOnlySpan<byte> answer, string address, out FoundGame game)
        {
            game = null!;
            if (answer.Length < AnswerMagic.Length + 4 || !answer[..AnswerMagic.Length].SequenceEqual(AnswerMagic))
                return false;

            var port = BinaryPrimitives.ReadUInt16BigEndian(answer.Slice(AnswerMagic.Length + 1, 2));
            var inRun = answer[AnswerMagic.Length + 3] == 1;
            var hostName = Encoding.UTF8.GetString(answer[(AnswerMagic.Length + 4)..]);
            game = new FoundGame(address, port, hostName, inRun);
            return true;
        }

        private static string HostName()
        {
            return LanPlayerNameService.Instance.PlayerNames.GetValueOrDefault(1u) ??
                   SettingsService.Instance.SettingsModel.PlayerName;
        }
    }
}
