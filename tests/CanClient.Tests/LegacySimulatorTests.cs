using CanClient.Core.Fsd;
using CanClient.Core.ModelMatching;
using CanClient.Core.Settings;
using CanClient.Core.Sim;

namespace CanClient.Tests;

/// <summary>FSX / P3D：版本识别、BCD 调频、SimObjects 路径和模型扫描。</summary>
public sealed class LegacySimulatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "canclient-legacy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData(10, 61637, SimulatorType.Fsx)]
    [InlineData(10, 62615, SimulatorType.FsxSe)]
    [InlineData(1, 0, SimulatorType.Prepar3d)]
    [InlineData(4, 5, SimulatorType.Prepar3dV4)]
    [InlineData(5, 4, SimulatorType.Prepar3dV5)]
    [InlineData(6, 0, SimulatorType.Prepar3dV6)]
    [InlineData(11, 0, SimulatorType.Msfs)]
    [InlineData(12, 0, SimulatorType.Msfs2024)]
    [InlineData(99, 0, SimulatorType.None)]
    public void RecognisesSimulatorVersions(int major, int build, SimulatorType expected) =>
        Assert.Equal(expected, SimulatorTypeExtensions.FromApplicationVersion(major, build));

    [Fact]
    public void ClassifiesSimulatorFamilies()
    {
        Assert.All(SimulatorTypeExtensions.Legacy, s => Assert.True(s.IsLegacy() && s.UsesSimConnect() && !s.IsMsfs()));
        Assert.True(SimulatorType.Msfs2024.UsesSimConnect());
        Assert.False(SimulatorType.XPlane.UsesSimConnect());
        Assert.False(SimulatorType.None.UsesSimConnect());
        Assert.Equal("Airbus A321", SimulatorType.FsxSe.DefaultModel());
        Assert.Equal("Bombardier CRJ 700", SimulatorType.Prepar3dV5.DefaultModel());
        Assert.Equal("P3D v4", SimulatorType.Prepar3dV4.DisplayName());
    }

    [Fact]
    public void UnknownLegacyVersionsAreGuessedFromTheName()
    {
        Assert.Equal(SimulatorType.Prepar3dV6, SimConnection.DetectSimulator(new Version(7, 0, 0, 0), "Lockheed Martin® Prepar3D® v7", legacy: true));
        Assert.Equal(SimulatorType.Fsx, SimConnection.DetectSimulator(new Version(9, 0, 0, 0), "Something", legacy: true));
        Assert.Equal(SimulatorType.Msfs2024, SimConnection.DetectSimulator(new Version(13, 0, 0, 0), "SunRise", legacy: false));
        Assert.Equal(SimulatorType.Prepar3dV4, SimConnection.DetectSimulator(new Version(4, 5, 14, 0), "Lockheed Martin® Prepar3D® v4", legacy: true));
    }

    [Theory]
    [InlineData(122.800, 0x2280u)]
    [InlineData(118.025, 0x1802u)]
    [InlineData(136.975, 0x3697u)]
    [InlineData(121.500, 0x2150u)]
    public void TunesLegacyRadiosWithBcd(double frequency, uint expected) =>
        Assert.Equal(expected, SimConnection.ComBcd(frequency));

    [Fact]
    public void ReportsFsxAndP3dToFsd()
    {
        Assert.Equal(9, FsdProtocol.SimulatorFsx);
        Assert.Equal(30, FsdProtocol.SimulatorP3d);
    }

    [Fact]
    public void ReadsSimObjectsCfgAndFsxCfg()
    {
        var simobjects = Write("cfg/simobjects.cfg", """
            [Entry.0]
            Title=Airplanes
            Path=SimObjects\Airplanes
            Active=True

            [Entry.1]
            Title=Disabled
            Path="D:\AI\Disabled"
            Active=False

            [Entry.2]
            Path="D:\AI\Traffic" // AI 包
            """);
        Assert.Equal([@"SimObjects\Airplanes", @"D:\AI\Traffic"], LegacyInstallation.ConfiguredSimObjectsPaths(simobjects).ToArray());

        var fsx = Write("cfg/fsx.CFG", """
            [Main]
            User Objects=Airplane, Helicopter
            SimObjectPaths.0=SimObjects\Airplanes
            SimObjectPaths.1=SimObjects\Rotorcraft
            [Display]
            SimObjectPaths.9=ignored
            """);
        Assert.Equal([@"SimObjects\Airplanes", @"SimObjects\Rotorcraft"], LegacyInstallation.ConfiguredSimObjectsPaths(fsx).ToArray());
    }

    [Fact]
    public void ReadsAddOnPackages()
    {
        // FSX / P3D 只在 Windows 上，路径按 Windows 的规则拼
        if (!OperatingSystem.IsWindows())
            return;
        var package = Path.Combine(_root, "addons", "AIG");
        Write("addons/AIG/add-on.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <SimBase.Document Type="AddOnXml" version="4,0" id="add-on">
              <AddOn.Name>AIG Traffic</AddOn.Name>
              <AddOn.Component>
                <Category>SimObjects</Category>
                <Path>SimObjects\Airplanes</Path>
              </AddOn.Component>
              <AddOn.Component>
                <Category>Scenery</Category>
                <Path>Scenery</Path>
              </AddOn.Component>
            </SimBase.Document>
            """);
        Write("discover/Other/add-on.xml", """
            <SimBase.Document><AddOn.Component><Category>SimObjects</Category><Path>D:\Other\Planes</Path></AddOn.Component></SimBase.Document>
            """);
        var addOns = Write("cfg/add-ons.cfg", $"""
            [Package.0]
            PATH={package}
            ACTIVE=true

            [Package.1]
            PATH={Path.Combine(_root, "addons", "Off")}
            ACTIVE=false

            [DiscoveryPath.0]
            PATH={Path.Combine(_root, "discover")}
            """);

        var packages = LegacyInstallation.AddOnPackages(addOns, _root).ToList();
        Assert.Equal([package, Path.Combine(_root, "discover", "Other")], packages);
        Assert.Equal([Path.Combine(package, @"SimObjects\Airplanes")], LegacyInstallation.AddOnSimObjects(package));
        Assert.Equal([@"D:\Other\Planes"], LegacyInstallation.AddOnSimObjects(packages[1]));
    }

    [Fact]
    public void ScansAPortableP3dInstall()
    {
        if (!OperatingSystem.IsWindows())
            return;
        // P3D v1 的配置文件在测试机上不会有，于是只扫 SimObjects\Airplanes（便携安装的默认）
        Write(@"p3d/SimObjects/Airplanes/CRJ700/aircraft.cfg", """
            [General]
            atc_type=BOMBARDIER
            atc_model=CRJ7
            category=airplane

            [fltsim.0]
            title=Bombardier CRJ 700
            atc_parking_codes=
            [fltsim.1]
            title=Bombardier CRJ 700 Air Canada Jazz
            atc_airline=JAZZ
            atc_parking_codes=JZA
            """);
        Write(@"p3d/SimObjects/Airplanes/Notes/readme.txt", "no config here");
        Write(@"p3d/SimObjects/Rotorcraft/Bell/aircraft.cfg", "[General]\ncategory=helicopter\n[fltsim.0]\ntitle=Bell 206\n");

        var installation = new LegacyInstallation(SimulatorType.Prepar3d, Path.Combine(_root, "p3d"));
        Assert.True(installation.IsValid);
        var folders = installation.SimObjectsFolders();
        Assert.Equal([@"SimObjects\Airplanes"], folders.Select(f => f.RelativePath).ToArray());

        var result = ModelScanner.Scan(installation, ModelMatchingData.Empty);
        Assert.Equal(SimulatorType.Prepar3d, result.SimulatorType);
        Assert.Equal(["Bombardier CRJ 700", "Bombardier CRJ 700 Air Canada Jazz"], result.AllModels().Select(m => m.Title!).ToArray());
        Assert.Null(ModelScanStore.NeedsRescan(result, installation));
    }

    [Fact]
    public void MissingLegacyInstallIsInvalid()
    {
        var installation = new LegacyInstallation(SimulatorType.Prepar3dV3, Path.Combine(_root, "does-not-exist"));
        Assert.Equal(installation.IsValid, installation.PackagesPath is not null);
        Assert.NotEqual(Path.Combine(_root, "does-not-exist"), installation.PackagesPath);
    }

    [Fact]
    public void KeepsAPathPerLegacySimulator()
    {
        var settings = new AppSettings();
        settings.SetSimulatorPath(SimulatorType.Prepar3dV5, @"  E:\P3D v5 ");
        settings.SetSimulatorPath(SimulatorType.Msfs2024, @"D:\MSFS");
        Assert.Equal(@"E:\P3D v5", settings.SimulatorPathFor(SimulatorType.Prepar3dV5));
        Assert.Equal("", settings.SimulatorPathFor(SimulatorType.Prepar3dV4));
        Assert.Equal(@"D:\MSFS", settings.Msfs2024PackagesPath);
        settings.SetSimulatorPath(SimulatorType.Prepar3dV5, "");
        Assert.DoesNotContain(SimulatorType.Prepar3dV5.ToString(), settings.SimulatorPaths.Keys);
    }

    [Fact]
    public void LegacySimulatorsHaveNoBuiltInRules()
    {
        var matcher = new ModelMatcher(ModelMatchingData.Empty);
        matcher.SetSimulator(SimulatorType.FsxSe);
        matcher.GenerateRules(null, raise: false);
        Assert.Empty(matcher.GeneratedRules);
        matcher.SetSimulator(SimulatorType.Msfs);
        matcher.GenerateRules(null, raise: false);
        Assert.NotEmpty(matcher.GeneratedRules);
    }
}
