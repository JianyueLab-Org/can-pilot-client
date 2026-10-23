using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Google.Protobuf;
using CanClient.Core.Util;

namespace CanClient.Core.Voice;

/// <summary>服务器回了 Reject（密码错、用户名被占……）。</summary>
public sealed class MumbleRejectedException(string message) : Exception(message);

public sealed record MumbleChannel(uint Id, string Name, uint? Parent, bool Temporary);

public sealed record MumbleUser(uint Session, string Name, uint? ChannelId);

/// <summary>
/// 一条 Mumble 连接（协议 1.5）。和 Python 版用的 pymumble-udp 同一套做法：
///
///   控制    TLS 上的 protobuf 消息，6 字节头（类型 u16 + 长度 u32，大端）
///   语音    MumbleUDP.Audio（1.5 的 protobuf 格式），首字节 0 是语音、1 是 ping；
///           拿到 CryptSetup 后走 OCB2 加密的 UDP，UDP 不通就退回 TCP 隧道（UDPTunnel）
///   保活    每秒一个 TCP Ping 和一个 UDP Ping；60 秒收不到 TCP Ping 回复就当掉线
///
/// <see cref="Run"/> 阻塞到连接结束；重连策略在 <see cref="VoiceSession"/>。
/// </summary>
public sealed class MumbleConnection : IDisposable
{
    private static readonly Logger Log = Util.Log.For("mumble");

    public const uint ProtocolMajor = 1;
    public const uint ProtocolMinor = 5;
    public const uint ProtocolPatch = 0;
    private const int Bandwidth = 50_000;
    private const double PingInterval = 1.0;
    private const double PingTimeout = 60.0;
    private const double UdpTimeout = 20.0;

    private enum MessageType : ushort
    {
        Version = 0,
        UdpTunnel = 1,
        Authenticate = 2,
        Ping = 3,
        Reject = 4,
        ServerSync = 5,
        ChannelRemove = 6,
        ChannelState = 7,
        UserRemove = 8,
        UserState = 9,
        BanList = 10,
        TextMessage = 11,
        PermissionDenied = 12,
        Acl = 13,
        QueryUsers = 14,
        CryptSetup = 15,
        ContextActionModify = 16,
        ContextAction = 17,
        UserList = 18,
        VoiceTarget = 19,
        PermissionQuery = 20,
        CodecVersion = 21,
        UserStats = 22,
        RequestBlob = 23,
        ServerConfig = 24,
        SuggestConfig = 25,
        PluginDataTransmission = 26
    }

    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly object _sendLock = new();
    private readonly CryptStateOcb2 _crypt = new();
    private readonly ConcurrentDictionary<uint, MumbleChannel> _channels = new();
    private readonly ConcurrentDictionary<uint, MumbleUser> _users = new();
    private readonly ConcurrentDictionary<uint, byte> _listeningChannels = new();
    private readonly ConcurrentDictionary<ulong, long> _pendingUdpPings = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _tcp;
    private SslStream? _ssl;
    private UdpClient? _udp;
    private long _lastPingReply;
    private long _lastPingSent;
    private long _lastUdpReceived;
    private long _lastResync;
    private bool _pinFailed;

    public MumbleConnection(string host, int port, string username, string password)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
    }

    /// <summary>服务器给的会话号（ServerSync 之后才有）。</summary>
    public uint? MySession { get; private set; }

    public bool Synced { get; private set; }

    public bool UdpActive { get; private set; }

    public double TcpPingAverage { get; private set; }
    public int TcpPingCount { get; private set; }
    public double UdpPingAverage { get; private set; }
    public int UdpPingCount { get; private set; }

    /// <summary>服务器限制的每用户带宽（bit/s），0 是没说。</summary>
    public int MaxBandwidth { get; private set; }

    public event Action? Connected;
    /// <summary>同步完成后服务器上新出现了一个频道。</summary>
    public event Action? ChannelAdded;
    public event Action<string, string>? PermissionDenied;
    /// <summary>有人被移出服务器：(会话号, 是否被踢/被封, 理由)。</summary>
    public event Action<uint, bool, string>? UserRemoved;
    /// <summary>收到语音：(发送者会话号, 帧号, Opus 数据, 是否结束)。</summary>
    public event Action<uint, ulong, byte[], bool, uint>? AudioReceived;
    public event Action<bool>? UdpStateChanged;

    public IReadOnlyCollection<MumbleChannel> Channels => _channels.Values.ToList();

    public MumbleChannel? FindChannel(string name) =>
        _channels.Values.FirstOrDefault(c => c.Name == name);

    public MumbleUser? Myself => MySession is { } session && _users.TryGetValue(session, out var user) ? user : null;

    public MumbleUser? User(uint session) => _users.GetValueOrDefault(session);

    public IReadOnlyCollection<uint> ListeningChannels => _listeningChannels.Keys.ToArray();

    private static long Now() => Stopwatch.GetTimestamp();
    private static double Since(long ticks) => ticks == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(ticks).TotalSeconds;

    /// <summary>连上、认证、收消息，直到连接断开或被取消。</summary>
    public void Run(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cts.Token);
        var token = linked.Token;
        Open(token);
        try
        {
            SendVersionAndAuthenticate();
            var udpThread = new Thread(() => UdpLoop(token)) { IsBackground = true, Name = "mumble-udp" };
            udpThread.Start();
            var pingThread = new Thread(() => PingLoop(token)) { IsBackground = true, Name = "mumble-ping" };
            pingThread.Start();
            ReadLoop(token);
        }
        finally
        {
            Close();
        }
    }

    private void Open(CancellationToken token)
    {
        var tcp = new TcpClient { NoDelay = true };
        var connect = tcp.ConnectAsync(_host, _port, token).AsTask();
        if (!connect.Wait(TimeSpan.FromSeconds(10), token))
        {
            tcp.Dispose();
            throw new TimeoutException($"连接 {_host}:{_port} 超时");
        }
        _tcp = tcp;
        var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            var ok = CertificatePinning.Check(_host, certificate, out _);
            _pinFailed = !ok;
            return ok;
        });
        try
        {
            ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
            {
                TargetHost = _host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            });
        }
        catch (AuthenticationException) when (_pinFailed)
        {
            ssl.Dispose();
            throw new CertificatePinException($"{_host} 的证书指纹和内置的不一致，为保护账号密码，没有登录。");
        }
        _ssl = ssl;

        try
        {
            var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
            _udp = new UdpClient(remote.AddressFamily);
            _udp.Connect(remote);
        }
        catch (Exception e)
        {
            Log.Debug($"UDP unavailable; using TCP: {e.Message}");
            _udp?.Dispose();
            _udp = null;
        }
    }

    private void SendVersionAndAuthenticate()
    {
        var version = new MumbleProto.Version
        {
            VersionV1 = (ProtocolMajor << 16) | (ProtocolMinor << 8) | ProtocolPatch,
            VersionV2 = ((ulong)ProtocolMajor << 48) | ((ulong)ProtocolMinor << 32) | ((ulong)ProtocolPatch << 16),
            Release = $"{AppVersion.ClientName} {AppVersion.Version}",
            Os = "Windows",
            OsVersion = Environment.OSVersion.VersionString
        };
        Send(MessageType.Version, version);
        var authenticate = new MumbleProto.Authenticate
        {
            Username = _username,
            Password = _password,
            Opus = true,
            ClientType = 0
        };
        Send(MessageType.Authenticate, authenticate);
    }

    private void Send(MessageType type, IMessage message)
    {
        var body = message.ToByteArray();
        SendRaw(type, body);
    }

    private void SendRaw(MessageType type, ReadOnlySpan<byte> body)
    {
        var ssl = _ssl ?? throw new IOException("not connected");
        var frame = new byte[6 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)type);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2), (uint)body.Length);
        body.CopyTo(frame.AsSpan(6));
        lock (_sendLock)
        {
            ssl.Write(frame);
            ssl.Flush();
        }
    }

    private void ReadLoop(CancellationToken token)
    {
        var ssl = _ssl!;
        var header = new byte[6];
        using var registration = token.Register(() =>
        {
            try
            {
                _tcp?.Client.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // ignore
            }
        });
        while (!token.IsCancellationRequested)
        {
            if (!ReadExactly(ssl, header))
                return;
            var type = (MessageType)BinaryPrimitives.ReadUInt16BigEndian(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2));
            if (length > 8 * 1024 * 1024)
                throw new InvalidDataException($"message too large ({length} bytes)");
            var body = new byte[length];
            if (!ReadExactly(ssl, body))
                return;
            try
            {
                Handle(type, body);
            }
            catch (MumbleRejectedException)
            {
                throw;
            }
            catch (InvalidProtocolBufferException e)
            {
                Log.Debug($"discarding a malformed {type} message: {e.Message}");
            }
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int n;
            try
            {
                n = stream.Read(buffer, read, buffer.Length - read);
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }

    private void Handle(MessageType type, byte[] body)
    {
        switch (type)
        {
            case MessageType.Reject:
            {
                var reject = MumbleProto.Reject.Parser.ParseFrom(body);
                var reason = reject.HasReason && reject.Reason.Length > 0 ? reject.Reason : reject.Type.ToString();
                throw new MumbleRejectedException(reason);
            }
            case MessageType.ServerSync:
            {
                var sync = MumbleProto.ServerSync.Parser.ParseFrom(body);
                MySession = sync.Session;
                if (sync.HasMaxBandwidth)
                    MaxBandwidth = (int)sync.MaxBandwidth;
                Synced = true;
                Log.Info($"server sync: session {sync.Session}, {_channels.Count} channels, welcome text {(sync.HasWelcomeText ? sync.WelcomeText.Length : 0)} chars");
                Connected?.Invoke();
                break;
            }
            case MessageType.ChannelState:
            {
                var state = MumbleProto.ChannelState.Parser.ParseFrom(body);
                if (!state.HasChannelId)
                    break;
                var added = false;
                _channels.AddOrUpdate(state.ChannelId,
                    _ =>
                    {
                        added = true;
                        return new MumbleChannel(state.ChannelId, state.HasName ? state.Name : "",
                            state.HasParent ? state.Parent : null, state.HasTemporary && state.Temporary);
                    },
                    (_, old) => old with
                    {
                        Name = state.HasName ? state.Name : old.Name,
                        Parent = state.HasParent ? state.Parent : old.Parent,
                        Temporary = state.HasTemporary ? state.Temporary : old.Temporary
                    });
                if (added && Synced)
                    ChannelAdded?.Invoke();
                break;
            }
            case MessageType.ChannelRemove:
            {
                var remove = MumbleProto.ChannelRemove.Parser.ParseFrom(body);
                _channels.TryRemove(remove.ChannelId, out _);
                _listeningChannels.TryRemove(remove.ChannelId, out _);
                break;
            }
            case MessageType.UserState:
            {
                var state = MumbleProto.UserState.Parser.ParseFrom(body);
                if (!state.HasSession)
                    break;
                if (state.Session == MySession)
                {
                    foreach (var id in state.ListeningChannelAdd)
                        _listeningChannels[id] = 0;
                    foreach (var id in state.ListeningChannelRemove)
                        _listeningChannels.TryRemove(id, out _);
                }
                _users.AddOrUpdate(state.Session,
                    _ => new MumbleUser(state.Session, state.HasName ? state.Name : "",
                        state.HasChannelId ? state.ChannelId : 0),
                    (_, old) => old with
                    {
                        Name = state.HasName ? state.Name : old.Name,
                        ChannelId = state.HasChannelId ? state.ChannelId : old.ChannelId
                    });
                break;
            }
            case MessageType.UserRemove:
            {
                var remove = MumbleProto.UserRemove.Parser.ParseFrom(body);
                _users.TryRemove(remove.Session, out _);
                var kicked = remove.HasActor || remove.HasReason || remove.HasBan;
                UserRemoved?.Invoke(remove.Session, kicked, remove.HasReason ? remove.Reason : "");
                break;
            }
            case MessageType.PermissionDenied:
            {
                var denied = MumbleProto.PermissionDenied.Parser.ParseFrom(body);
                var kind = denied.HasType ? denied.Type.ToString() : "Permission";
                PermissionDenied?.Invoke(kind, denied.HasReason ? denied.Reason : "");
                break;
            }
            case MessageType.CryptSetup:
                HandleCryptSetup(MumbleProto.CryptSetup.Parser.ParseFrom(body));
                break;
            case MessageType.Ping:
            {
                _lastPingReply = Now();
                var sent = Interlocked.Read(ref _lastPingSent);
                if (sent != 0)
                {
                    var ms = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                    TcpPingCount++;
                    TcpPingAverage += (ms - TcpPingAverage) / TcpPingCount;
                }
                break;
            }
            case MessageType.UdpTunnel:
                HandleMedia(body);
                break;
            case MessageType.ServerConfig:
            {
                var config = MumbleProto.ServerConfig.Parser.ParseFrom(body);
                if (config.HasMaxBandwidth)
                    MaxBandwidth = (int)config.MaxBandwidth;
                break;
            }
        }
    }

    private void HandleCryptSetup(MumbleProto.CryptSetup message)
    {
        if (message.HasKey)
        {
            if (message.Key.Length != 16 || message.ClientNonce.Length != 16 || message.ServerNonce.Length != 16)
            {
                Log.Warning("Ignoring invalid CryptSetup key or nonce lengths");
                return;
            }
            lock (_crypt)
                _crypt.SetKey(message.Key.ToByteArray(), message.ClientNonce.ToByteArray(), message.ServerNonce.ToByteArray());
            _pendingUdpPings.Clear();
            _lastUdpReceived = 0;
            _lastResync = Now();
            SetUdpActive(false);
        }
        else if (message.HasServerNonce)
        {
            if (!_crypt.IsValid || message.ServerNonce.Length != 16)
                return;
            lock (_crypt)
                _crypt.SetDecryptIv(message.ServerNonce.ToByteArray());
            _pendingUdpPings.Clear();
            SetUdpActive(false);
        }
        else if (_crypt.IsValid)
        {
            byte[] nonce;
            lock (_crypt)
                nonce = _crypt.EncryptIv;
            Send(MessageType.CryptSetup, new MumbleProto.CryptSetup { ClientNonce = ByteString.CopyFrom(nonce) });
        }
        SendUdpPing();
    }

    private void SetUdpActive(bool active)
    {
        if (UdpActive == active)
            return;
        UdpActive = active;
        Log.Info(active ? "voice is using UDP" : "voice is using the TCP tunnel");
        UdpStateChanged?.Invoke(active);
    }

    private void PingLoop(CancellationToken token)
    {
        _lastPingReply = Now();
        while (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(PingInterval)))
        {
            try
            {
                var ping = new MumbleProto.Ping
                {
                    Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    TcpPingAvg = (float)TcpPingAverage,
                    TcpPackets = (uint)TcpPingCount,
                    UdpPingAvg = (float)UdpPingAverage,
                    UdpPackets = (uint)UdpPingCount,
                    Good = (uint)_crypt.Good,
                    Late = (uint)_crypt.Late,
                    Lost = (uint)Math.Max(0, _crypt.Lost)
                };
                SendUdpPing();
                Interlocked.Exchange(ref _lastPingSent, Now());
                Send(MessageType.Ping, ping);
                if (Since(_lastUdpReceived) > UdpTimeout)
                    SetUdpActive(false);
                if (Since(_lastPingReply) > PingTimeout)
                {
                    Log.Warning("no ping reply from the voice server for 60 s, treating the connection as dropped");
                    _cts.Cancel();
                    return;
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _cts.Cancel();
                return;
            }
        }
    }

    private bool SendUdp(byte[] packet)
    {
        var udp = _udp;
        if (udp is null || !_crypt.IsValid)
            return false;
        try
        {
            byte[]? encrypted;
            lock (_crypt)
                encrypted = _crypt.Encrypt(packet);
            if (encrypted is null)
                return false;
            return udp.Send(encrypted, encrypted.Length) == encrypted.Length;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            SetUdpActive(false);
            return false;
        }
    }

    private void SendUdpPing()
    {
        foreach (var (stamp, sent) in _pendingUdpPings)
        {
            if (Since(sent) > UdpTimeout)
                _pendingUdpPings.TryRemove(stamp, out _);
        }
        var timestamp = (ulong)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
        var ping = new MumbleUDP.Ping { Timestamp = timestamp };
        var body = ping.ToByteArray();
        var packet = new byte[body.Length + 1];
        packet[0] = 1;
        body.CopyTo(packet, 1);
        if (SendUdp(packet))
            _pendingUdpPings[timestamp] = Now();
    }

    private void UdpLoop(CancellationToken token)
    {
        var udp = _udp;
        if (udp is null)
            return;
        while (!token.IsCancellationRequested)
        {
            byte[] encrypted;
            try
            {
                var receive = udp.ReceiveAsync(token).AsTask();
                receive.Wait(token);
                encrypted = receive.Result.Buffer;
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested)
                    return;
                SetUdpActive(false);
                Thread.Sleep(200);
                continue;
            }
            if (encrypted.Length is <= 4 or > 1024)
                continue;
            byte[]? packet;
            lock (_crypt)
                packet = _crypt.Decrypt(encrypted);
            if (packet is null)
            {
                // 解不开：nonce 可能失步了，最多每 5 秒请服务器重新给一次
                if (Since(Math.Max(_lastResync, _lastUdpReceived)) > 5)
                {
                    _lastResync = Now();
                    try
                    {
                        Send(MessageType.CryptSetup, new MumbleProto.CryptSetup());
                    }
                    catch
                    {
                        // ignore
                    }
                }
                continue;
            }
            if (packet.Length == 0)
                continue;
            if (packet[0] == 1)
            {
                try
                {
                    var ping = MumbleUDP.Ping.Parser.ParseFrom(packet.AsSpan(1));
                    if (ping.Timestamp == 0 || !_pendingUdpPings.TryRemove(ping.Timestamp, out var sent))
                        continue;
                    _lastUdpReceived = Now();
                    var ms = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                    UdpPingCount++;
                    UdpPingAverage += (ms - UdpPingAverage) / UdpPingCount;
                    SetUdpActive(true);
                }
                catch (InvalidProtocolBufferException)
                {
                    // ignore
                }
            }
            else if (packet[0] == 0)
            {
                _lastUdpReceived = Now();
                HandleMedia(packet);
            }
        }
    }

    /// <summary>语音包（UDP 或 TCP 隧道），首字节 0 是 Audio。</summary>
    private void HandleMedia(byte[] packet)
    {
        if (packet.Length < 2 || packet[0] != 0)
            return;
        MumbleUDP.Audio audio;
        try
        {
            audio = MumbleUDP.Audio.Parser.ParseFrom(packet.AsSpan(1));
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }
        if (audio.SenderSession == 0 || audio.OpusData.IsEmpty)
            return;
        AudioReceived?.Invoke(audio.SenderSession, audio.FrameNumber, audio.OpusData.ToByteArray(), audio.IsTerminator, audio.Context);
    }

    /// <summary>发一帧语音。UDP 可用走 UDP，否则走 TCP 隧道。</summary>
    public void SendAudio(byte[] opus, ulong frameNumber, bool terminator)
    {
        var audio = new MumbleUDP.Audio
        {
            Target = 0,
            FrameNumber = frameNumber,
            OpusData = ByteString.CopyFrom(opus),
            IsTerminator = terminator
        };
        var body = audio.ToByteArray();
        var packet = new byte[body.Length + 1];
        packet[0] = 0;
        body.CopyTo(packet, 1);
        if (Since(_lastUdpReceived) > UdpTimeout)
            SetUdpActive(false);
        if (UdpActive && packet.Length <= 1020 && SendUdp(packet))
            return;
        SendRaw(MessageType.UdpTunnel, packet);
    }

    /// <summary>
    /// 编码比特率：总带宽 50 kbit/s 减去协议开销，和 pymumble 一样。服务器限得更低就按服务器的。
    /// </summary>
    public int EncoderBitrate(double secondsPerPacket = 0.02)
    {
        var overhead = 20 + 3 + (UdpActive ? 12 : 26);
        var bandwidth = MaxBandwidth > 0 ? Math.Min(Bandwidth, MaxBandwidth) : Bandwidth;
        return bandwidth - (int)(overhead * 8 / secondsPerPacket);
    }

    /// <summary>在根频道下建一个临时频道。不等结果，频道出没出现由调用方轮询。</summary>
    public void CreateChannel(string name, uint parent = 0)
    {
        Log.Info($"-> sending ChannelState (create) {name} (parent={parent} temporary=True)");
        Send(MessageType.ChannelState, new MumbleProto.ChannelState { Parent = parent, Name = name, Temporary = true });
    }

    /// <summary>把自己挪进某个频道。还没有会话号时什么也不发，返回 false。</summary>
    public bool MoveTo(uint channelId)
    {
        if (MySession is not { } session)
            return false;
        Log.Info($"-> sending UserState (move) session={session} channel={channelId}");
        Send(MessageType.UserState, new MumbleProto.UserState { Session = session, ChannelId = channelId });
        return true;
    }

    /// <summary>频道监听只接收，发话在实际加入的 TX 电台频道。状态以服务器 UserState 回报为准。</summary>
    public void SetListeningChannels(IEnumerable<uint> wanted)
    {
        if (MySession is not { } session)
            return;
        var desired = wanted.ToHashSet();
        var current = ListeningChannels;
        var state = new MumbleProto.UserState { Session = session };
        state.ListeningChannelAdd.AddRange(desired.Except(current));
        state.ListeningChannelRemove.AddRange(current.Except(desired));
        if (state.ListeningChannelAdd.Count == 0 && state.ListeningChannelRemove.Count == 0)
            return;
        Log.Info($"-> listeners add=[{string.Join(",", state.ListeningChannelAdd)}] remove=[{string.Join(",", state.ListeningChannelRemove)}]");
        Send(MessageType.UserState, state);
    }

    public void Close()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        try
        {
            _ssl?.Dispose();
        }
        catch
        {
            // ignore
        }
        try
        {
            _tcp?.Dispose();
        }
        catch
        {
            // ignore
        }
        try
        {
            _udp?.Dispose();
        }
        catch
        {
            // ignore
        }
        _ssl = null;
        _tcp = null;
        _udp = null;
        SetUdpActive(false);
    }

    public void Dispose()
    {
        Close();
        _crypt.Dispose();
    }
}
