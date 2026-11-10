using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CanClient.Core.Util;

namespace CanClient.Core.Sim;

/// <summary>
/// FSX / P3D 的 SimConnect 走这里：FSX SP2 / Acceleration 的 SimConnect.dll 只有 32 位，x64 的客户端加载不了，
/// 于是起一个 32 位的 fsx/can-simconnect.exe（CanClient.SimBridge）去加载它，经标准输入输出转发调用。
/// P3D v1–v6 都接受这个老客户端，vPilot 也是这样接 FSX 和 P3D 的。
///
/// 对 <see cref="SimConnection"/> 和 <see cref="TrafficInjector"/> 来说它和直接调 SimConnect.dll 没有区别：
/// 打开时拿到一个假句柄，<see cref="SimConnectNative"/> 认出这个句柄就把调用转到这里。语义也照原生的来——
/// 调用同步返回 HRESULT，包号由 GetLastSentPacketID 取，模拟器的消息排队，有消息就置位 Open 时给的事件，
/// GetNextDispatch 取出的指针在下一次取之前有效。
///
/// 帧格式：int32 长度 + 内容，内容第一个字节是类型。调用参数的顺序和 CanClient.SimBridge/Program.cs 一一对应。
/// 同一时间只有一个连接（客户端只开一条 FSX / P3D 连接），桥接进程在连接之间复用。
/// </summary>
internal static class SimConnectBridge
{
    private static readonly Logger Log = Util.Log.For("sim");

    private const int EFail = unchecked((int)0x80004005);
    private const int EAbort = unchecked((int)0x80004004);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    internal enum Op : byte
    {
        Open = 1,
        Close = 2,
        AddToDataDefinition = 3,
        ClearDataDefinition = 4,
        RequestDataOnSimObject = 5,
        SetDataOnSimObject = 6,
        AICreateNonATCAircraft = 7,
        AIRemoveObject = 8,
        AIReleaseControl = 9,
        MapClientEventToSimEvent = 10,
        TransmitClientEvent = 11,
        SubscribeToSystemEvent = 12,
        Reply = 0x80,
        Dispatch = 0x81,
        Log = 0x82,
        Broken = 0x83
    }

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<byte[]> Messages = new();
    private static readonly AutoResetEvent ReplyReady = new(false);
    private static Process? _process;
    private static Stream? _stdin;
    private static IntPtr _handle;
    private static IntPtr _event;
    private static long _nextHandle;
    private static volatile bool _broken;
    private static int _replyHr;
    private static uint _replyPacket;
    private static uint _lastPacket;
    private static IntPtr _buffer;
    private static int _bufferSize;
    private static bool _missingLogged;

    /// <summary>桥接程序的位置：客户端目录下的 fsx 子目录，和 32 位 SimConnect.dll 放在一起。</summary>
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "fsx", "can-simconnect.exe");

    public static bool Available => OperatingSystem.IsWindows() && File.Exists(ExecutablePath);

    public static bool Owns(IntPtr handle) => handle != IntPtr.Zero && handle == _handle;

    public static int Open(out IntPtr handle, string name, IntPtr eventHandle)
    {
        lock (Gate)
        {
            handle = IntPtr.Zero;
            if (!Available)
            {
                if (!_missingLogged)
                    Log.Error($"the FSX / P3D SimConnect bridge is missing ({ExecutablePath}); FSX and Prepar3D cannot be connected");
                _missingLogged = true;
                return EFail;
            }
            if (_handle != IntPtr.Zero)
                Close(_handle);
            while (Messages.TryDequeue(out _)) { }
            _broken = false;
            _event = eventHandle;
            var hr = Call(Op.Open, w => w.Write(name));
            if (hr < 0)
                return hr;
            _handle = handle = new IntPtr(-Interlocked.Increment(ref _nextHandle) - 1);
            return hr;
        }
    }

    public static int Close(IntPtr handle)
    {
        lock (Gate)
        {
            if (!Owns(handle))
                return EFail;
            var hr = _process is { HasExited: false } ? Call(Op.Close, _ => { }) : 0;
            _handle = IntPtr.Zero;
            _event = IntPtr.Zero;
            while (Messages.TryDequeue(out _)) { }
            return hr;
        }
    }

    /// <summary>不再需要 FSX / P3D 连接时关掉桥接进程（它读到标准输入关闭也会自己退出）。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_handle != IntPtr.Zero)
                Close(_handle);
            StopProcess();
        }
    }

    public static int GetNextDispatch(IntPtr handle, out IntPtr data, out uint size)
    {
        data = IntPtr.Zero;
        size = 0;
        if (!Owns(handle))
            return EFail;
        if (Messages.TryDequeue(out var message))
        {
            // 指针在下一次取消息之前有效，和原生 SimConnect 一样；只有 SimConnect 线程会来取
            if (_bufferSize < message.Length)
            {
                if (_buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(_buffer);
                _bufferSize = Math.Max(message.Length, 4096);
                _buffer = Marshal.AllocHGlobal(_bufferSize);
            }
            Marshal.Copy(message, 0, _buffer, message.Length);
            data = _buffer;
            size = (uint)message.Length;
            return 0;
        }
        return _broken ? EAbort : EFail;
    }

    public static int GetLastSentPacketID(IntPtr handle, out uint packetId)
    {
        packetId = Owns(handle) ? _lastPacket : 0;
        return Owns(handle) ? 0 : EFail;
    }

    public static int AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string? unitsName,
        SimConnectNative.DataType datumType, float epsilon, uint datumId) =>
        Call(handle, Op.AddToDataDefinition, w =>
        {
            w.Write(defineId);
            w.Write(datumName);
            w.Write(unitsName is not null);
            w.Write(unitsName ?? "");
            w.Write((uint)datumType);
            w.Write(epsilon);
            w.Write(datumId);
        });

    public static int ClearDataDefinition(IntPtr handle, uint defineId) =>
        Call(handle, Op.ClearDataDefinition, w => w.Write(defineId));

    public static int RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId,
        SimConnectNative.Period period, uint flags, uint origin, uint interval, uint limit) =>
        Call(handle, Op.RequestDataOnSimObject, w =>
        {
            w.Write(requestId);
            w.Write(defineId);
            w.Write(objectId);
            w.Write((uint)period);
            w.Write(flags);
            w.Write(origin);
            w.Write(interval);
            w.Write(limit);
        });

    public static int SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId, uint flags, uint arrayCount,
        uint unitSize, IntPtr data)
    {
        // arrayCount 为 0 表示一个元素
        var bytes = new byte[unitSize * Math.Max(1, arrayCount)];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        return Call(handle, Op.SetDataOnSimObject, w =>
        {
            w.Write(defineId);
            w.Write(objectId);
            w.Write(flags);
            w.Write(arrayCount);
            w.Write(unitSize);
            w.Write(bytes.Length);
            w.Write(bytes);
        });
    }

    public static int AICreateNonATCAircraft(IntPtr handle, string containerTitle, string tailNumber,
        SimConnectNative.InitPosition position, uint requestId) =>
        Call(handle, Op.AICreateNonATCAircraft, w =>
        {
            w.Write(containerTitle);
            w.Write(tailNumber);
            w.Write(position.Latitude);
            w.Write(position.Longitude);
            w.Write(position.Altitude);
            w.Write(position.Pitch);
            w.Write(position.Bank);
            w.Write(position.Heading);
            w.Write(position.OnGround);
            w.Write(position.Airspeed);
            w.Write(requestId);
        });

    public static int AIRemoveObject(IntPtr handle, uint objectId, uint requestId) =>
        Call(handle, Op.AIRemoveObject, w =>
        {
            w.Write(objectId);
            w.Write(requestId);
        });

    public static int AIReleaseControl(IntPtr handle, uint objectId, uint requestId) =>
        Call(handle, Op.AIReleaseControl, w =>
        {
            w.Write(objectId);
            w.Write(requestId);
        });

    public static int MapClientEventToSimEvent(IntPtr handle, uint eventId, string eventName) =>
        Call(handle, Op.MapClientEventToSimEvent, w =>
        {
            w.Write(eventId);
            w.Write(eventName);
        });

    public static int TransmitClientEvent(IntPtr handle, uint objectId, uint eventId, uint data, uint groupId, uint flags) =>
        Call(handle, Op.TransmitClientEvent, w =>
        {
            w.Write(objectId);
            w.Write(eventId);
            w.Write(data);
            w.Write(groupId);
            w.Write(flags);
        });

    public static int SubscribeToSystemEvent(IntPtr handle, uint eventId, string systemEventName) =>
        Call(handle, Op.SubscribeToSystemEvent, w =>
        {
            w.Write(eventId);
            w.Write(systemEventName);
        });

    private static int Call(IntPtr handle, Op op, Action<BinaryWriter> write)
    {
        lock (Gate)
            return Owns(handle) ? Call(op, write) : EFail;
    }

    /// <summary>发一个调用、等回复。持有 <see cref="Gate"/>，同一时间只有一个调用在路上。</summary>
    private static int Call(Op op, Action<BinaryWriter> write)
    {
        if (!EnsureProcess())
            return EFail;
        var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)op);
            write(writer);
        }
        try
        {
            ReplyReady.Reset();
            var stdin = _stdin!;
            stdin.Write(BitConverter.GetBytes((int)body.Length));
            body.Position = 0;
            body.CopyTo(stdin);
            stdin.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warning($"the SimConnect bridge is gone: {e.Message}");
            StopProcess();
            return EAbort;
        }
        if (!ReplyReady.WaitOne(ReplyTimeout))
        {
            Log.Warning($"the SimConnect bridge did not answer {op} in time, restarting it");
            StopProcess();
            return EAbort;
        }
        if (_process is null or { HasExited: true })
            return EAbort;
        _lastPacket = _replyPacket;
        return _replyHr;
    }

    private static bool EnsureProcess()
    {
        if (_process is { HasExited: false })
            return true;
        StopProcess();
        try
        {
            var info = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!
            };
            var process = Process.Start(info)!;
            _process = process;
            _stdin = process.StandardInput.BaseStream;
            var reader = new Thread(() => Read(process)) { IsBackground = true, Name = "simconnect-bridge" };
            reader.Start();
            Log.Info($"started the FSX / P3D SimConnect bridge (pid {process.Id})");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"could not start the SimConnect bridge {ExecutablePath}: {e.Message}");
            _process = null;
            _stdin = null;
            return false;
        }
    }

    private static void StopProcess()
    {
        var process = _process;
        _process = null;
        _stdin = null;
        if (process is null)
            return;
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(1000))
                process.Kill();
        }
        catch
        {
            // 已经没了
        }
        process.Dispose();
    }

    /// <summary>桥接进程的标准输出：回复交给等着的调用，模拟器消息排队并置位事件。</summary>
    private static void Read(Process process)
    {
        try
        {
            using var reader = new BinaryReader(process.StandardOutput.BaseStream);
            while (true)
            {
                var length = reader.ReadInt32();
                var frame = reader.ReadBytes(length);
                if (frame.Length != length || length == 0)
                    break;
                switch ((Op)frame[0])
                {
                    case Op.Reply:
                        _replyHr = BitConverter.ToInt32(frame, 1);
                        _replyPacket = BitConverter.ToUInt32(frame, 5);
                        ReplyReady.Set();
                        break;
                    case Op.Dispatch:
                        Messages.Enqueue(frame[1..]);
                        Signal();
                        break;
                    case Op.Log:
                        Log.Warning($"SimConnect bridge: {Encoding.UTF8.GetString(frame, 1, frame.Length - 1)}");
                        break;
                    case Op.Broken:
                        Log.Warning($"the connection to the simulator broke: 0x{BitConverter.ToInt32(frame, 1):X8}");
                        _broken = true;
                        Signal();
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException)
        {
            // 进程退出了
        }
        // 是我们自己停掉的旧进程就不管：不能把新连接标成断开，也不能误放行正在等回复的调用
        if (ReferenceEquals(_process, process))
        {
            _broken = true;
            ReplyReady.Set();
            Signal();
        }
    }

    private static void Signal()
    {
        var e = _event;
        if (e != IntPtr.Zero)
            SetEvent(e);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr handle);
}
