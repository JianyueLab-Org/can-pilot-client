using System.Runtime.InteropServices;
using System.Text;

namespace CanClient.SimBridge;

/// <summary>
/// 32 位 SimConnect 桥：客户端从标准输入发来一次调用，这里调 FSX 的 SimConnect.dll，
/// 把 HRESULT 和刚发出去的包号写回标准输出；模拟器发来的消息原样转发。
///
/// 帧格式（两个方向一样）：int32 长度，后面跟这么多字节；第一个字节是类型。
/// 调用的类型和参数顺序见 <see cref="Op"/>，和 CanClient.Core/Sim/SimConnectBridge.cs 一一对应。
/// 标准输入关了（客户端退出或崩溃）就关连接退出，不会留下孤儿进程占着模拟器。
/// </summary>
internal static class Program
{
    private const int EFail = unchecked((int)0x80004005);

    private enum Op : byte
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

    private static readonly object Dll = new();
    private static readonly object Out = new();
    private static Stream _output = Stream.Null;
    private static IntPtr _handle;
    private static AutoResetEvent? _signal;
    private static Thread? _dispatcher;

    private static int Main()
    {
        _output = Console.OpenStandardOutput();
        using var input = new BinaryReader(Console.OpenStandardInput());
        try
        {
            while (true)
            {
                byte[] frame;
                try
                {
                    var length = input.ReadInt32();
                    frame = input.ReadBytes(length);
                    if (frame.Length != length)
                        break;
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                int hr;
                uint packet = 0;
                try
                {
                    hr = Handle(frame, ref packet);
                }
                catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
                {
                    SendLog($"SimConnect.dll (FSX) could not be loaded: {e.Message}");
                    hr = EFail;
                }
                catch (Exception e)
                {
                    SendLog($"bridge call failed: {e.Message}");
                    hr = EFail;
                }
                var reply = new byte[9];
                reply[0] = (byte)Op.Reply;
                BitConverter.TryWriteBytes(reply.AsSpan(1), hr);
                BitConverter.TryWriteBytes(reply.AsSpan(5), packet);
                Send(reply);
            }
        }
        catch (IOException)
        {
            // 客户端那头断了
        }
        CloseHandle();
        return 0;
    }

    private static int Handle(byte[] frame, ref uint packet)
    {
        using var reader = new BinaryReader(new MemoryStream(frame, 1, frame.Length - 1), Encoding.UTF8);
        var op = (Op)frame[0];
        if (op == Op.Open)
            return Open(reader.ReadString());
        if (op == Op.Close)
        {
            CloseHandle();
            return 0;
        }
        lock (Dll)
        {
            var h = _handle;
            if (h == IntPtr.Zero)
                return EFail;
            var hr = op switch
            {
                Op.AddToDataDefinition => AddToDataDefinition(h, reader),
                Op.ClearDataDefinition => Native.SimConnect_ClearDataDefinition(h, reader.ReadUInt32()),
                Op.RequestDataOnSimObject => Native.SimConnect_RequestDataOnSimObject(h, reader.ReadUInt32(),
                    reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
                    reader.ReadUInt32(), reader.ReadUInt32()),
                Op.SetDataOnSimObject => SetDataOnSimObject(h, reader),
                Op.AICreateNonATCAircraft => AICreateNonATCAircraft(h, reader),
                Op.AIRemoveObject => Native.SimConnect_AIRemoveObject(h, reader.ReadUInt32(), reader.ReadUInt32()),
                Op.AIReleaseControl => Native.SimConnect_AIReleaseControl(h, reader.ReadUInt32(), reader.ReadUInt32()),
                Op.MapClientEventToSimEvent => WithString(reader.ReadUInt32(), reader.ReadString(),
                    (id, name) => Native.SimConnect_MapClientEventToSimEvent(h, id, name)),
                Op.TransmitClientEvent => Native.SimConnect_TransmitClientEvent(h, reader.ReadUInt32(), reader.ReadUInt32(),
                    reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()),
                Op.SubscribeToSystemEvent => WithString(reader.ReadUInt32(), reader.ReadString(),
                    (id, name) => Native.SimConnect_SubscribeToSystemEvent(h, id, name)),
                _ => EFail
            };
            if (hr >= 0)
                Native.SimConnect_GetLastSentPacketID(h, out packet);
            return hr;
        }
    }

    private static int Open(string name)
    {
        CloseHandle();
        _signal ??= new AutoResetEvent(false);
        var text = Marshal.StringToHGlobalAnsi(name);
        int hr;
        IntPtr handle;
        try
        {
            lock (Dll)
                hr = Native.SimConnect_Open(out handle, text, IntPtr.Zero, 0, _signal.SafeWaitHandle.DangerousGetHandle(), 0);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
        if (hr < 0 || handle == IntPtr.Zero)
            return hr < 0 ? hr : EFail;
        lock (Dll)
            _handle = handle;
        _dispatcher = new Thread(Dispatch) { IsBackground = true, Name = "dispatch" };
        _dispatcher.Start(handle);
        return hr;
    }

    private static void CloseHandle()
    {
        IntPtr handle;
        lock (Dll)
        {
            handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                Native.SimConnect_Close(handle);
        }
        if (handle != IntPtr.Zero)
        {
            _signal?.Set();
            _dispatcher?.Join(TimeSpan.FromSeconds(2));
            _dispatcher = null;
        }
    }

    /// <summary>模拟器来消息时 SimConnect 置位事件，这里把排着的消息全部转给客户端。</summary>
    private static void Dispatch(object? state)
    {
        var handle = (IntPtr)state!;
        while (true)
        {
            _signal!.WaitOne(250);
            while (true)
            {
                byte[] message;
                lock (Dll)
                {
                    if (_handle != handle)
                        return;
                    var hr = Native.SimConnect_GetNextDispatch(handle, out var data, out var size);
                    if (hr < 0 && hr != EFail)
                    {
                        // 和模拟器的管道断了：告诉客户端，由它关掉重连
                        message = [(byte)Op.Broken, .. BitConverter.GetBytes(hr)];
                        try { Send(message); } catch (IOException) { }
                        return;
                    }
                    if (hr < 0 || data == IntPtr.Zero)
                        break;
                    message = new byte[size + 1];
                    message[0] = (byte)Op.Dispatch;
                    Marshal.Copy(data, message, 1, (int)size);
                }
                try
                {
                    Send(message);
                }
                catch (IOException)
                {
                    return;
                }
            }
        }
    }

    private static int AddToDataDefinition(IntPtr h, BinaryReader reader)
    {
        var definition = reader.ReadUInt32();
        var datum = reader.ReadString();
        var hasUnits = reader.ReadBoolean();
        var units = reader.ReadString();
        var type = reader.ReadUInt32();
        var epsilon = reader.ReadSingle();
        var datumId = reader.ReadUInt32();
        var datumText = Marshal.StringToHGlobalAnsi(datum);
        var unitsText = hasUnits ? Marshal.StringToHGlobalAnsi(units) : IntPtr.Zero;
        try
        {
            return Native.SimConnect_AddToDataDefinition(h, definition, datumText, unitsText, type, epsilon, datumId);
        }
        finally
        {
            Marshal.FreeHGlobal(datumText);
            if (unitsText != IntPtr.Zero)
                Marshal.FreeHGlobal(unitsText);
        }
    }

    private static unsafe int SetDataOnSimObject(IntPtr h, BinaryReader reader)
    {
        var definition = reader.ReadUInt32();
        var objectId = reader.ReadUInt32();
        var flags = reader.ReadUInt32();
        var arrayCount = reader.ReadUInt32();
        var unitSize = reader.ReadUInt32();
        var data = reader.ReadBytes(reader.ReadInt32());
        fixed (byte* pointer = data)
            return Native.SimConnect_SetDataOnSimObject(h, definition, objectId, flags, arrayCount, unitSize, (IntPtr)pointer);
    }

    private static int AICreateNonATCAircraft(IntPtr h, BinaryReader reader)
    {
        var title = reader.ReadString();
        var tail = reader.ReadString();
        var position = new Native.InitPosition
        {
            Latitude = reader.ReadDouble(),
            Longitude = reader.ReadDouble(),
            Altitude = reader.ReadDouble(),
            Pitch = reader.ReadDouble(),
            Bank = reader.ReadDouble(),
            Heading = reader.ReadDouble(),
            OnGround = reader.ReadUInt32(),
            Airspeed = reader.ReadUInt32()
        };
        var requestId = reader.ReadUInt32();
        var titleText = Marshal.StringToHGlobalAnsi(title);
        var tailText = Marshal.StringToHGlobalAnsi(tail);
        try
        {
            return Native.SimConnect_AICreateNonATCAircraft(h, titleText, tailText, position, requestId);
        }
        finally
        {
            Marshal.FreeHGlobal(titleText);
            Marshal.FreeHGlobal(tailText);
        }
    }

    private static int WithString(uint id, string value, Func<uint, IntPtr, int> call)
    {
        var text = Marshal.StringToHGlobalAnsi(value);
        try
        {
            return call(id, text);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static void SendLog(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var frame = new byte[bytes.Length + 1];
        frame[0] = (byte)Op.Log;
        bytes.CopyTo(frame, 1);
        Send(frame);
    }

    private static void Send(byte[] frame)
    {
        lock (Out)
        {
            Span<byte> length = stackalloc byte[4];
            BitConverter.TryWriteBytes(length, frame.Length);
            _output.Write(length);
            _output.Write(frame);
            _output.Flush();
        }
    }
}

/// <summary>FSX SimConnect.dll 的 C 接口（__stdcall），全部用可直接传递的类型，NativeAOT 不需要封送代码。</summary>
internal static class Native
{
    private const string Dll = "SimConnect.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct InitPosition
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Pitch;
        public double Bank;
        public double Heading;
        public uint OnGround;
        public uint Airspeed;
    }

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_Open(out IntPtr handle, IntPtr name, IntPtr hWnd, uint userEventWin32,
        IntPtr hEventHandle, uint configIndex);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_Close(IntPtr handle);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_AddToDataDefinition(IntPtr handle, uint defineId, IntPtr datumName, IntPtr unitsName,
        uint datumType, float epsilon, uint datumId);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_ClearDataDefinition(IntPtr handle, uint defineId);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId,
        uint period, uint flags, uint origin, uint interval, uint limit);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId, uint flags,
        uint arrayCount, uint unitSize, IntPtr data);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_AICreateNonATCAircraft(IntPtr handle, IntPtr containerTitle, IntPtr tailNumber,
        InitPosition initPos, uint requestId);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_AIRemoveObject(IntPtr handle, uint objectId, uint requestId);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_AIReleaseControl(IntPtr handle, uint objectId, uint requestId);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_MapClientEventToSimEvent(IntPtr handle, uint eventId, IntPtr eventName);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_TransmitClientEvent(IntPtr handle, uint objectId, uint eventId, uint data,
        uint groupId, uint flags);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_SubscribeToSystemEvent(IntPtr handle, uint eventId, IntPtr systemEventName);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    public static extern int SimConnect_GetLastSentPacketID(IntPtr handle, out uint packetId);
}
