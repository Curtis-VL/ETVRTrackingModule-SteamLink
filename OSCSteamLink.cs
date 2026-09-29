using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Types;

namespace ETVRTrackingModule {
    // a single OSC message with all of its arguments decoded, e.g. /tracking/eye/CenterVecFull ,fff -> [x, y, z]
    public readonly record struct OSCBundleMessage(string Address, object[] Arguments);

    public class OSCSteamLink {
        private const int SteamLinkPort = 9015;
        private const string CenterGazeAddress = "/tracking/eye/CenterVecFull";

        // if Steam Link stops sending for this long, we stop overriding and ETVR's gaze is used again
        private static readonly TimeSpan GazeTimeout = TimeSpan.FromMilliseconds(500);
        private static readonly byte[] BundleHeader = "#bundle\0"u8.ToArray();

        private Socket? _receiver;

        private ILogger _logger;

        private readonly ManualResetEvent _terminate = new(false);
        private Thread? _listeningThread;

        public OSCState State { get; private set; }
        private const int ConnectionTimeout = 10000;

        private ETVRConfigManager _configManager;

        private readonly object _gazeLock = new();
        private Vector2 _gaze;
        private DateTime _lastGazeUpdate = DateTime.MinValue;
        private bool _loggedFirstPacket;

        // only touched by the listening thread, a zero timestamp forces the filters to be recreated on the next sample
        private OneEuroFilter? _gazeXFilter;
        private OneEuroFilter? _gazeYFilter;
        private long _lastSampleTimestamp;
        private bool _wasActive;

        public OSCSteamLink(ILogger iLogger) {
            _logger = iLogger;
        }

        public void RegisterSelf(ref ETVRConfigManager configManager) {
            _configManager = configManager;
            configManager.RegisterListener(HandleConfigUpdate);
        }

        private void HandleConfigUpdate(Config config) {
            _terminate.Set();
            _receiver?.Dispose();
            State = OSCState.IDLE;
            // the filter settings may have changed
            _lastSampleTimestamp = 0;
            _terminate.Reset();
            Start();
        }

        public void Start() {
            _receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try {
                _receiver.Bind(new System.Net.IPEndPoint(_configManager.Config.ListeningAddress, SteamLinkPort));
                _receiver.ReceiveTimeout = ConnectionTimeout;
                State = OSCState.CONNECTED;
            }
            catch (Exception e) {
                _logger.LogError($"Connecting to SteamLink OSC with {SteamLinkPort} port at address {_configManager.Config.ListeningAddress} failed, with error: {e}");
                State = OSCState.ERROR;
            }

            _listeningThread = new Thread(OSCListen);
            _listeningThread.Start();
        }

        private void OSCListen() {
            var buffer = new byte[4096];
            var messages = new List<OSCBundleMessage>();
            while (!_terminate.WaitOne(0)) {
                try {
                    if (_receiver is null) {
                        continue;
                    }

                    if (_receiver.IsBound) {
                        // only the first `length` bytes are this packet, the rest of the buffer is stale data
                        var length = _receiver.Receive(buffer);
                        messages.Clear();
                        ParsePacket(buffer, 0, length, messages);
                        HandleMessages(messages);
                    }
                }
                catch (Exception) {
                    // we purposefully ignore any exceptions
                }
            }
        }

        public void TearDown() {
            _terminate.Set();
            _receiver?.Close();
            _receiver?.Dispose();
            _listeningThread?.Join();
        }

        // returns true and the latest combined gaze if Steam Link has sent eye data recently
        public bool TryGetGaze(out Vector2 gaze) {
            bool isActive;
            lock (_gazeLock) {
                gaze = _gaze;
                isActive = DateTime.UtcNow - _lastGazeUpdate < GazeTimeout;
            }

            if (isActive != _wasActive) {
                _logger.LogInformation(isActive
                    ? "Steam Link gaze data received, overriding ETVR gaze"
                    : "Steam Link gaze data timed out, falling back to ETVR gaze");
                _wasActive = isActive;
            }

            return isActive;
        }

        private void HandleMessages(List<OSCBundleMessage> messages) {
            if (!_loggedFirstPacket) {
                _loggedFirstPacket = true;
                _logger.LogInformation("-- First SteamLink OSC packet --");
                foreach (var message in messages)
                    _logger.LogInformation("{} [{}]", message.Address, string.Join(", ", message.Arguments));
            }

            foreach (var message in messages) {
                if (message.Address != CenterGazeAddress || message.Arguments.Length < 3)
                    continue;

                if (message.Arguments[0] is not float x || message.Arguments[1] is not float y ||
                    message.Arguments[2] is not float z)
                    continue;

                // CenterVecFull uses VRChat's convention: +x right, +y up, +z forward, not normalized.
                // VRCFT expects gaze as (tan(yaw), tan(pitch)), see Vector2.ToYaw / ToPitch
                if (z <= 0.0001f)
                    continue;

                var gaze = FilterGaze(x / z, y / z);
                lock (_gazeLock) {
                    _gaze = gaze;
                    _lastGazeUpdate = DateTime.UtcNow;
                }
            }
        }

        private Vector2 FilterGaze(float x, float y) {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, now);

            // start fresh after a gap, otherwise the filter would glide over from wherever the eyes were last seen
            if (_gazeXFilter is null || _gazeYFilter is null || _lastSampleTimestamp == 0 || elapsed > GazeTimeout) {
                var config = _configManager.Config;
                _gazeXFilter = new OneEuroFilter(config.SteamLinkGazeMinCutoff!.Value, config.SteamLinkGazeBeta!.Value);
                _gazeYFilter = new OneEuroFilter(config.SteamLinkGazeMinCutoff!.Value, config.SteamLinkGazeBeta!.Value);
            }
            _lastSampleTimestamp = now;

            // the filter works in samples per second, packets can arrive in bursts so keep the rate sane
            var rate = 1.0 / Math.Clamp(elapsed.TotalSeconds, 0.001, 0.1);
            return new Vector2((float)_gazeXFilter.Filter(x, rate), (float)_gazeYFilter.Filter(y, rate));
        }

        // a packet is either a single message, or a bundle of messages / nested bundles
        private static void ParsePacket(byte[] buffer, int start, int end, List<OSCBundleMessage> output) {
            if (end - start >= 16 && buffer.AsSpan(start, BundleHeader.Length).SequenceEqual(BundleHeader)) {
                // "#bundle\0" (8 bytes), time tag (8 bytes), then repeated [int32 element size][element]
                int pos = start + 16;
                while (pos + 4 <= end) {
                    int size = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(pos, 4));
                    pos += 4;
                    if (size <= 0 || pos + size > end)
                        break;

                    ParsePacket(buffer, pos, pos + size, output);
                    pos += size;
                }
                return;
            }

            if (end > start && buffer[start] == '/')
                output.Add(ParseMessage(buffer, start, end));
        }

        private static OSCBundleMessage ParseMessage(byte[] buffer, int start, int end) {
            int pos = start;
            string address = ReadString(buffer, start, ref pos, end);
            var arguments = new List<object>();

            // OSC adresses are composed of /address ,types values, so we need to check if we have a type
            if (pos >= end || buffer[pos] != ',')
                return new OSCBundleMessage(address, arguments.ToArray());

            string types = ReadString(buffer, start, ref pos, end);
            foreach (char type in types.AsSpan(1)) {
                switch (type) {
                    case 'f':
                        if (pos + 4 > end) return new OSCBundleMessage(address, arguments.ToArray());
                        arguments.Add(BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(pos, 4)));
                        pos += 4;
                        break;
                    case 'i':
                        if (pos + 4 > end) return new OSCBundleMessage(address, arguments.ToArray());
                        arguments.Add(BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(pos, 4)));
                        pos += 4;
                        break;
                    case 's':
                        arguments.Add(ReadString(buffer, start, ref pos, end));
                        break;
                    case 'T':
                        arguments.Add(true);
                        break;
                    case 'F':
                        arguments.Add(false);
                        break;
                    default:
                        // we don't know the size of other types, so we can't decode anything after them
                        return new OSCBundleMessage(address, arguments.ToArray());
                }
            }

            return new OSCBundleMessage(address, arguments.ToArray());
        }

        // strings are null terminated and padded to a multiple of 4 bytes, relative to the message start
        private static string ReadString(byte[] buffer, int messageStart, ref int pos, int end) {
            int terminator = Array.IndexOf(buffer, (byte)0, pos, end - pos);
            if (terminator < 0)
                terminator = end;

            var value = Encoding.ASCII.GetString(buffer, pos, terminator - pos);
            int consumed = terminator + 1 - messageStart;
            pos = messageStart + ((consumed + 3) & ~3);
            return value;
        }
    }
}
