using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CanClient.Core;
using CanClient.Core.Fsd;
using CanClient.Core.Settings;
using CanClient.Core.Sim;
using CanClient.Core.Traffic;

namespace CanClient.Tests;

public class XPlaneTests
{
    [Fact]
    public void IncomingNetworkingIsReadFromPrfAndEnabledInPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), "canclient-xp-" + Guid.NewGuid().ToString("N"));
        var prefs = Path.Combine(root, "Output", "preferences");
        Directory.CreateDirectory(prefs);
        try
        {
            Assert.Null(XPlaneInstallation.IncomingNetworkingDisabled(root));
            var file = XPlaneInstallation.PreferencesFile(root);
            const string original = "I\r\n10 Version\r\n_rcv_port 49000\r\n_disable_incoming_networking 1\r\n_snd_track_view_19 1\r\n";
            File.WriteAllText(file, original);
            Assert.True(XPlaneInstallation.IncomingNetworkingDisabled(root));
            // 运行着的 X-Plane 退出时会覆盖 prf，所以那种情况下拒绝修改；这台机器正开着 X-Plane 时跳过
            if (SimulatorProcesses.XPlaneRunning) return;
            XPlaneInstallation.EnableIncomingNetworking(root);
            Assert.False(XPlaneInstallation.IncomingNetworkingDisabled(root));
            Assert.Equal(original.Replace("_disable_incoming_networking 1", "_disable_incoming_networking 0"), File.ReadAllText(file));
            Assert.Equal(original, File.ReadAllText(file + ".can-backup"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SilentXPlaneProblemNamesTheNetworkSetting()
    {
        Assert.Contains("关闭了", XPlaneConnection.SilentProblem(true));
        Assert.Contains("Accept incoming connections", XPlaneConnection.SilentProblem(true));
        Assert.Contains("Accept incoming connections", XPlaneConnection.SilentProblem(null));
        Assert.Contains("防火墙", XPlaneConnection.SilentProblem(false));
    }

    [Fact]
    public void XPlaneStateConvertsUnitsRadiosAndIdent()
    {
        var state = XPlaneConnection.BuildState(new Dictionary<string, double>
        {
            ["lat"] = 31, ["lon"] = 121, ["elevation"] = 3048, ["agl"] = 304.8,
            ["pressure"] = 10100, ["temperature"] = 100, ["gs"] = 100,
            ["south"] = 30, ["heading"] = -10, ["pitch"] = 5, ["bank"] = 3,
            ["squawk"] = 0x1234, ["com1"] = 121805, ["com2legacy"] = 12540,
            ["tx"] = 7, ["ident"] = 1, ["com2rx"] = 0, ["com1power"] = 0
        });
        Assert.Equal(10000, state.Altitude);
        Assert.Equal(10100, state.NetworkAltitude);
        Assert.Equal(1000, state.Agl);
        Assert.Equal(194, state.Groundspeed);
        Assert.Equal(-30, state.VelocityNorth);
        Assert.Equal(350, state.Heading);
        Assert.Equal(5, state.Pitch);
        Assert.Equal(3, state.Bank);
        Assert.Equal(1234, state.Squawk);
        Assert.Equal(121.805, state.Com1);
        Assert.Equal(125.4, state.Com2);
        Assert.False(state.Com1Power);
        Assert.False(state.Com2Receiving);
        Assert.Equal(2, state.TransmitRadio);
        Assert.True(state.TransponderIdent);
        Assert.Equal('Y', FsdClient.PositionMode(state, false));
        Assert.Equal(16, FsdProtocol.SimulatorXPlane);
    }

    [Theory]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(0, 0)]
    public void XPlaneTxIsExclusive(int selection, int expected) => Assert.Equal(expected,
        XPlaneConnection.BuildState(new Dictionary<string, double> { ["tx"] = selection }).TransmitRadio);

    [Fact]
    public void RrefRejectsMalformedPacketsAndRequiresPosition()
    {
        using var xp = new XPlaneConnection(new AppSettings());
        Assert.False(xp.Handle("RREF\0"u8.ToArray()));
        var packet = Rref((0, 31), (1, 121), (2, 1000), (-1, 1), (999, 3));
        Assert.True(xp.Handle(packet));
        Assert.NotNull(xp.Snapshot());
        Assert.Equal(31, xp.Snapshot()!.Latitude);
        Assert.False(xp.Handle(packet[..^1]));
    }

    [Fact]
    public void BeaconAndWritePacketsMatchXPlaneWireFormat()
    {
        var beacon = new byte[21];
        "BECN\0"u8.CopyTo(beacon);
        BinaryPrimitives.WriteInt32LittleEndian(beacon.AsSpan(7), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(beacon.AsSpan(19), 49000);
        Assert.Equal(49000, XPlaneConnection.Beacon(beacon, IPAddress.Loopback)!.Port);
        BinaryPrimitives.WriteInt32LittleEndian(beacon.AsSpan(7), 2);
        Assert.Null(XPlaneConnection.Beacon(beacon, IPAddress.Loopback));
        var write = XPlaneConnection.WritePacket("sim/test", 123.5f);
        Assert.Equal(509, write.Length);
        Assert.Equal(123.5f, BinaryPrimitives.ReadSingleLittleEndian(write.AsSpan(5)));
        Assert.Equal("sim/test", Encoding.ASCII.GetString(write, 9, 8));
    }

    [Fact]
    public async Task LocalUdpSimulatorFeedsUnifiedConnectionAndAcceptsRadioWrites()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var settings = new AppSettings { SimulatorConnection = "XPlane", XPlaneHost = "127.0.0.1", XPlanePort = port };
        using var sim = new SimulatorConnection(settings);
        sim.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscription = await server.ReceiveAsync(timeout.Token);
        Assert.Equal(413, subscription.Buffer.Length);
        await server.SendAsync(Rref((0, 31), (1, 121), (2, 1000)), subscription.RemoteEndPoint, timeout.Token);
        while (!sim.Connected) await Task.Delay(20, timeout.Token);
        Assert.Equal(CanClient.Core.ModelMatching.SimulatorType.XPlane, sim.Simulator);
        Assert.True(sim.TuneCom(2, 125.4));
        UdpReceiveResult command;
        do { command = await server.ReceiveAsync(timeout.Token); }
        while (!command.Buffer.AsSpan(0, 4).SequenceEqual("DREF"u8));
        Assert.Equal(125400f, BinaryPrimitives.ReadSingleLittleEndian(command.Buffer.AsSpan(5)));
        Assert.Contains("com2_frequency_hz_833", Encoding.ASCII.GetString(command.Buffer));
    }

    [Fact]
    public void TrafficBridgeFragmentsUnicodeAndKeepsPluginFields()
    {
        var entry = new TrafficEntry
        {
            Callsign = "CCA123", Equipment = "A320", Airline = "CCA", Latitude = 31, Longitude = 121,
            Altitude = 5000, Pitch = 2, Bank = 3, Heading = 90, Squawk = 1234, Mode = "Y",
            Lights = new Dictionary<string, bool> { ["landing"] = true }
        };
        var message = XPlaneTrafficBridge.Frame([entry], new OwnAircraftState(), "D:/中文模型/" + new string('机', 10000));
        var packets = XPlaneTrafficBridge.Encode(message, 65536);
        Assert.True(packets.Length > 1);
        using var stream = new MemoryStream();
        foreach (var packet in packets)
        {
            using var part = JsonDocument.Parse(packet);
            Assert.Equal(3, part.RootElement.GetProperty("v").GetInt32());
            Assert.Equal(0, part.RootElement.GetProperty("seq").GetInt32());
            stream.Write(Convert.FromBase64String(part.RootElement.GetProperty("data").GetString()!));
        }
        using var frame = JsonDocument.Parse(stream.ToArray());
        var plane = frame.RootElement.GetProperty("aircraft")[0];
        Assert.Equal("A320", plane.GetProperty("equipment").GetString());
        Assert.Equal("", plane.GetProperty("model").GetString());
        Assert.Equal(5000, plane.GetProperty("pose")[2].GetDouble());
        Assert.True(plane.GetProperty("lights").GetProperty("landing_on").GetBoolean());
        Assert.Contains("中文模型", frame.RootElement.GetProperty("csl").GetString());
    }

    [Fact]
    public void CslScanDiscoversModelDeclarations()
    {
        var root = Path.Combine(Path.GetTempPath(), "can-csl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "xsb_aircraft.txt"), "EXPORT_NAME TEST\nOBJ8_AIRCRAFT A320_CCA\nAIRCRAFT B738_CSN\n");
            Assert.Equal(new[] { "A320_CCA", "B738_CSN" }, XPlaneInstallation.ScanCsl(root));
        }
        finally { Directory.Delete(root, true); }
    }


    private static byte[] Rref(params (int Index, float Value)[] values)
    {
        var bytes = new byte[5 + values.Length * 8];
        "RREF\0"u8.CopyTo(bytes);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5 + i * 8), values[i].Index);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(9 + i * 8), values[i].Value);
        }
        return bytes;
    }
}
