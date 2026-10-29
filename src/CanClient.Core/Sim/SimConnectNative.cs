using System.Runtime.InteropServices;

namespace CanClient.Core.Sim;

/// <summary>
/// SimConnect 的 C 接口。MSFS 直接 P/Invoke 随包的 x64 SimConnect.dll（MSFS SDK 自带的原生库）；
/// FSX / P3D 的句柄是 <see cref="SimConnectBridge"/> 发的，同名函数认出句柄就转给 32 位桥接进程。
/// 调用方（<see cref="SimConnection"/>、<see cref="TrafficInjector"/>）不用管自己连的是哪一种。
///
/// 不用托管包装（Microsoft.FlightSimulator.SimConnect.dll）：它不在 NuGet 上、版本和模拟器绑死，
/// 而 C 接口在 MSFS 2020 和 2024 上是同一套。结构体按 SimConnect.h 的 #pragma pack(1) 排。
/// </summary>
internal static class SimConnectNative
{
    private const string Dll = "SimConnect.dll";

    public const uint Unused = 0xFFFFFFFF;
    public const uint ObjectIdUser = 0;

    public const int E_FAIL = unchecked((int)0x80004005);

    public static int SimConnect_Open(out IntPtr handle, string name, IntPtr hWnd, uint userEventWin32,
        IntPtr hEventHandle, uint configIndex) =>
        Direct.SimConnect_Open(out handle, name, hWnd, userEventWin32, hEventHandle, configIndex);

    public static int SimConnect_Close(IntPtr handle) =>
        SimConnectBridge.Owns(handle) ? SimConnectBridge.Close(handle) : Direct.SimConnect_Close(handle);

    public static int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.GetNextDispatch(handle, out data, out size)
            : Direct.SimConnect_GetNextDispatch(handle, out data, out size);

    public static int SimConnect_AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string? unitsName,
        DataType datumType, float epsilon, uint datumId) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.AddToDataDefinition(handle, defineId, datumName, unitsName, datumType, epsilon, datumId)
            : Direct.SimConnect_AddToDataDefinition(handle, defineId, datumName, unitsName, datumType, epsilon, datumId);

    public static int SimConnect_ClearDataDefinition(IntPtr handle, uint defineId) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.ClearDataDefinition(handle, defineId)
            : Direct.SimConnect_ClearDataDefinition(handle, defineId);

    public static int SimConnect_RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId,
        uint objectId, Period period, uint flags, uint origin, uint interval, uint limit) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.RequestDataOnSimObject(handle, requestId, defineId, objectId, period, flags, origin, interval, limit)
            : Direct.SimConnect_RequestDataOnSimObject(handle, requestId, defineId, objectId, period, flags, origin, interval, limit);

    public static int SimConnect_SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId,
        uint flags, uint arrayCount, uint unitSize, IntPtr data) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.SetDataOnSimObject(handle, defineId, objectId, flags, arrayCount, unitSize, data)
            : Direct.SimConnect_SetDataOnSimObject(handle, defineId, objectId, flags, arrayCount, unitSize, data);

    public static int SimConnect_AICreateNonATCAircraft(IntPtr handle, string containerTitle, string tailNumber,
        InitPosition initPos, uint requestId) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.AICreateNonATCAircraft(handle, containerTitle, tailNumber, initPos, requestId)
            : Direct.SimConnect_AICreateNonATCAircraft(handle, containerTitle, tailNumber, initPos, requestId);

    public static int SimConnect_AIRemoveObject(IntPtr handle, uint objectId, uint requestId) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.AIRemoveObject(handle, objectId, requestId)
            : Direct.SimConnect_AIRemoveObject(handle, objectId, requestId);

    public static int SimConnect_AIReleaseControl(IntPtr handle, uint objectId, uint requestId) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.AIReleaseControl(handle, objectId, requestId)
            : Direct.SimConnect_AIReleaseControl(handle, objectId, requestId);

    public static int SimConnect_MapClientEventToSimEvent(IntPtr handle, uint eventId, string eventName) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.MapClientEventToSimEvent(handle, eventId, eventName)
            : Direct.SimConnect_MapClientEventToSimEvent(handle, eventId, eventName);

    public static int SimConnect_TransmitClientEvent(IntPtr handle, uint objectId, uint eventId,
        uint data, uint groupId, uint flags) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.TransmitClientEvent(handle, objectId, eventId, data, groupId, flags)
            : Direct.SimConnect_TransmitClientEvent(handle, objectId, eventId, data, groupId, flags);

    public static int SimConnect_SubscribeToSystemEvent(IntPtr handle, uint eventId, string systemEventName) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.SubscribeToSystemEvent(handle, eventId, systemEventName)
            : Direct.SimConnect_SubscribeToSystemEvent(handle, eventId, systemEventName);

    public static int SimConnect_GetLastSentPacketID(IntPtr handle, out uint packetId) =>
        SimConnectBridge.Owns(handle)
            ? SimConnectBridge.GetLastSentPacketID(handle, out packetId)
            : Direct.SimConnect_GetLastSentPacketID(handle, out packetId);

    /// <summary>随包的 x64 SimConnect.dll（MSFS）。</summary>
    private static class Direct
    {
        [DllImport(Dll, CharSet = CharSet.Ansi)]
        public static extern int SimConnect_Open(out IntPtr handle, string name, IntPtr hWnd, uint userEventWin32,
            IntPtr hEventHandle, uint configIndex);

        [DllImport(Dll)]
        public static extern int SimConnect_Close(IntPtr handle);

        [DllImport(Dll)]
        public static extern int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

        [DllImport(Dll)]
        public static extern int SimConnect_AddToDataDefinition(IntPtr handle, uint defineId,
            [MarshalAs(UnmanagedType.LPStr)] string datumName, [MarshalAs(UnmanagedType.LPStr)] string? unitsName,
            DataType datumType, float epsilon, uint datumId);

        [DllImport(Dll)]
        public static extern int SimConnect_ClearDataDefinition(IntPtr handle, uint defineId);

        [DllImport(Dll)]
        public static extern int SimConnect_RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId,
            uint objectId, Period period, uint flags, uint origin, uint interval, uint limit);

        [DllImport(Dll)]
        public static extern int SimConnect_SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId,
            uint flags, uint arrayCount, uint unitSize, IntPtr data);

        [DllImport(Dll)]
        public static extern int SimConnect_AICreateNonATCAircraft(IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string containerTitle,
            [MarshalAs(UnmanagedType.LPStr)] string tailNumber, InitPosition initPos, uint requestId);

        [DllImport(Dll)]
        public static extern int SimConnect_AIRemoveObject(IntPtr handle, uint objectId, uint requestId);

        [DllImport(Dll)]
        public static extern int SimConnect_AIReleaseControl(IntPtr handle, uint objectId, uint requestId);

        [DllImport(Dll)]
        public static extern int SimConnect_MapClientEventToSimEvent(IntPtr handle, uint eventId,
            [MarshalAs(UnmanagedType.LPStr)] string eventName);

        [DllImport(Dll)]
        public static extern int SimConnect_TransmitClientEvent(IntPtr handle, uint objectId, uint eventId,
            uint data, uint groupId, uint flags);

        [DllImport(Dll)]
        public static extern int SimConnect_SubscribeToSystemEvent(IntPtr handle, uint eventId,
            [MarshalAs(UnmanagedType.LPStr)] string systemEventName);

        [DllImport(Dll)]
        public static extern int SimConnect_GetLastSentPacketID(IntPtr handle, out uint packetId);
    }

    public enum DataType : uint
    {
        Invalid = 0,
        Int32 = 1,
        Int64 = 2,
        Float32 = 3,
        Float64 = 4,
        String8 = 5,
        String32 = 6,
        String64 = 7,
        String128 = 8,
        String256 = 9,
        String260 = 10,
        StringV = 11
    }

    public enum Period : uint
    {
        Never = 0,
        Once = 1,
        VisualFrame = 2,
        SimFrame = 3,
        Second = 4
    }

    public enum RecvId : uint
    {
        Null = 0,
        Exception = 1,
        Open = 2,
        Quit = 3,
        Event = 4,
        EventObjectAddRemove = 5,
        EventFilename = 6,
        EventFrame = 7,
        SimObjectData = 8,
        SimObjectDataByType = 9,
        AssignedObjectId = 12
    }

    public const uint GroupPriorityHighest = 1;
    public const uint EventFlagGroupIdIsPriority = 0x10;
    public const uint DataRequestFlagChanged = 0x1;

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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Recv
    {
        public uint Size;
        public uint Version;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecvException
    {
        public Recv Header;
        public uint Exception;
        public uint SendId;
        public uint Index;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct RecvOpen
    {
        public Recv Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ApplicationName;
        public uint ApplicationVersionMajor;
        public uint ApplicationVersionMinor;
        public uint ApplicationBuildMajor;
        public uint ApplicationBuildMinor;
        public uint SimConnectVersionMajor;
        public uint SimConnectVersionMinor;
        public uint SimConnectBuildMajor;
        public uint SimConnectBuildMinor;
        public uint Reserved1;
        public uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecvEvent
    {
        public Recv Header;
        public uint GroupId;
        public uint EventId;
        public uint Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecvEventFrame
    {
        public RecvEvent Event;
        public float FrameRate;
        public float SimSpeed;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecvSimObjectData
    {
        public Recv Header;
        public uint RequestId;
        public uint ObjectId;
        public uint DefineId;
        public uint Flags;
        public uint EntryNumber;
        public uint OutOf;
        public uint DefineCount;
        // 后面紧跟数据，第一个字节在这里
    }

    public static readonly int SimObjectDataOffset = Marshal.SizeOf<RecvSimObjectData>();

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecvAssignedObjectId
    {
        public Recv Header;
        public uint RequestId;
        public uint ObjectId;
    }

    /// <summary>SIMCONNECT_EXCEPTION 里用得上的几个。</summary>
    public static class Exceptions
    {
        public const uint UnrecognizedId = 3;
        public const uint NameUnrecognized = 7;
        public const uint DataError = 20;
        public const uint CreateObjectFailed = 22;
        public const uint AlreadyCreated = 32;
        public const uint ObjectOutsideRealityBubble = 33;
        public const uint ObjectContainer = 34;

        public static string Name(uint code) => code switch
        {
            0 => "NONE",
            1 => "ERROR",
            2 => "SIZE_MISMATCH",
            3 => "UNRECOGNIZED_ID",
            4 => "UNOPENED",
            5 => "VERSION_MISMATCH",
            6 => "TOO_MANY_GROUPS",
            7 => "NAME_UNRECOGNIZED",
            8 => "TOO_MANY_EVENT_NAMES",
            9 => "EVENT_ID_DUPLICATE",
            10 => "TOO_MANY_MAPS",
            11 => "TOO_MANY_OBJECTS",
            12 => "TOO_MANY_REQUESTS",
            18 => "INVALID_DATA_TYPE",
            19 => "INVALID_DATA_SIZE",
            20 => "DATA_ERROR",
            21 => "INVALID_ARRAY",
            22 => "CREATE_OBJECT_FAILED",
            24 => "OPERATION_INVALID_FOR_OBJECT_TYPE",
            25 => "ILLEGAL_OPERATION",
            26 => "ALREADY_SUBSCRIBED",
            27 => "INVALID_ENUM",
            28 => "DEFINITION_ERROR",
            29 => "DUPLICATE_ID",
            30 => "DATUM_ID",
            31 => "OUT_OF_BOUNDS",
            32 => "ALREADY_CREATED",
            33 => "OBJECT_OUTSIDE_REALITY_BUBBLE",
            34 => "OBJECT_CONTAINER",
            35 => "OBJECT_AI",
            36 => "OBJECT_ATC",
            37 => "OBJECT_SCHEDULE",
            _ => $"EXCEPTION_{code}"
        };
    }
}
