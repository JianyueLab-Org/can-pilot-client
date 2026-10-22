using System.Text;
using CanClient.Core.ModelMatching;
using CanClient.Core.Util;
using Xunit.Abstractions;

namespace CanClient.Tests;

/// <summary>一个假的 MSFS 包目录：Official 里一架默认机、Community 里一个 FSLTL 风格的 AI 包。</summary>
public sealed class FakeInstallation : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "canclient-test-" + Guid.NewGuid().ToString("N"));

    public FakeInstallation()
    {
        AppPaths.OverrideDataDirectory(Root);
        var official = Path.Combine(Root, "Official2024", "Steam", "asobo-aircraft-a320");
        Write(official, "SimObjects/Airplanes/Asobo_A320/aircraft.cfg", """
            [GENERAL]
            atc_model = "A320"
            icao_type_designator = "A20N"
            Category = "airplane"

            [FLTSIM.0]
            title = "Airbus A320 Neo Asobo" ; default
            icao_airline = ""
            """);

        var fsltl = Path.Combine(Root, "Community", "fsltl-traffic-base");
        Write(fsltl, "SimObjects/Airplanes/FSLTL_A20N/aircraft.cfg", """
            [GENERAL]
            icao_type_designator = "A20N"
            Category = "airplane"
            [FLTSIM.0]
            title = "FSLTL_A20N_ZZZZ"
            atc_parking_codes = ""
            [FLTSIM.1]
            title = "FSLTL_A20N_CCA"
            atc_parking_codes = "CCA,CCAX"
            """);
        Write(fsltl, "SimObjects/Airplanes/FSLTL_A20N_ANA/aircraft.cfg", """
            [VARIATION]
            base_container = "..\FSLTL_A20N"
            [FLTSIM.0]
            title = "FSLTL_A20N_PW_ANA" ; Variation name
            atc_parking_codes = "ANA" ; codes
            """);
        Write(fsltl, "SimObjects/Airplanes/FSLTL_B738/aircraft.cfg", """
            [GENERAL]
            icao_type_designator = "B738"
            Category = "airplane"
            [FLTSIM.0]
            title = "FSLTL_B738_CSN"
            atc_parking_codes = "CSN"
            [FLTSIM.1]
            title = "PMDG 737-800 Test"
            atc_parking_codes = "CSN"
            """);
        Write(fsltl, "SimObjects/GroundVehicles/Tug/aircraft.cfg", """
            [GENERAL]
            Category = "GroundVehicle"
            [FLTSIM.0]
            title = "Tug"
            """);
        File.WriteAllText(Path.Combine(fsltl, "FSLTL_Rules.vmr"), """
            <?xml version="1.0" encoding="utf-8"?>
            <ModelMatchRuleSet>
            <ModelMatchRule CallsignPrefix="ANA" TypeCode = "A20N" ModelName ="FSLTL_A20N_PW_ANA" />
            <ModelMatchRule CallsignPrefix="XXX" TypeCode = "A20N" ModelName ="FSLTL_NOT_INSTALLED" />
            <ModelMatchRule TypeCode = "A21N" ModelName ="FSLTL_A20N_ZZZZ" />
            </ModelMatchRuleSet>
            """);
        WriteLayouts(official);
        WriteLayouts(fsltl);
    }

    private static void Write(string package, string relative, string content)
    {
        var path = Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(true));
    }

    private static void WriteLayouts(string package)
    {
        var files = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(package, f).Replace('\\', '/'))
            .Select(p => $"{{\"path\":\"{p}\",\"size\":1,\"date\":1}}");
        File.WriteAllText(Path.Combine(package, "layout.json"), $"{{\"content\":[{string.Join(',', files)}]}}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // ignore
        }
    }
}

public class ModelMatchingTests : IDisposable
{
    private static readonly ModelMatchingData Data = LoadData();
    private readonly FakeInstallation _install = new();

    private static ModelMatchingData LoadData()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ModelMatchingData.bin");
        return ModelMatchingData.LoadCompressed(path);
    }

    public void Dispose() => _install.Dispose();

    [Fact]
    public void DatabaseLoads()
    {
        Assert.True(Data.ModelSpecs.Count > 10000);
        Assert.Contains("B738", Data.TypeCodes);
        Assert.Contains("CCA", Data.AirlineCodes);
        Assert.True(Data.TryGetSimilar("A20N", out var similar));
        Assert.Contains("A320", similar);
        Assert.NotNull(Data.ResolveAlias("738"));
    }

    [Fact]
    public void ParsesCommentsQuotesAndVariations()
    {
        var installation = new MsfsInstallation(SimulatorType.Msfs2024, _install.Root);
        var configs = installation.AircraftConfigFiles();
        // 地面车辆被 layout 过滤掉
        Assert.DoesNotContain(configs, c => c.VirtualPath.Contains("GroundVehicles"));
        var byVirtual = configs.ToDictionary(c => c.VirtualPath, StringComparer.OrdinalIgnoreCase);
        var variation = configs.Single(c => c.VirtualPath.Contains("FSLTL_A20N_ANA"));
        var models = AircraftConfigParser.ReadModels(variation.FullPath, variation.VirtualPath, byVirtual);
        var model = Assert.Single(models);
        Assert.Equal("FSLTL_A20N_PW_ANA", model.Title);
        Assert.Equal("A20N", model.IcaoTypeDesignator);   // 从 base_container 继承
        Assert.Equal("ANA", model.AtcParkingCodes);
    }

    [Fact]
    public void DeriverHandlesParkingCodes()
    {
        var specs = ModelSpecDeriver.Derive(new ModelScanResultModel
        {
            Title = "X", IcaoTypeDesignator = "A20N", AtcModel = "", AtcParkingCodes = "CCA,CCAX,XX"
        }, Data);
        Assert.Equal(2, specs.Count(s => s.CallsignPrefix == "CCA"));
        Assert.DoesNotContain(specs, s => s.CallsignPrefix == "XX");
        // icao_airline 兜底
        var fallback = ModelSpecDeriver.Derive(new ModelScanResultModel
        {
            Title = "Y", IcaoTypeDesignator = "B738", AtcModel = "", IcaoAirline = "CSN"
        }, Data);
        Assert.Contains(fallback, s => s.CallsignPrefix == "CSN");
    }

    private ModelMatcher ScanAndBuild(out ModelScanResult scan)
    {
        var installation = new MsfsInstallation(SimulatorType.Msfs2024, _install.Root);
        scan = ModelScanner.Scan(installation, Data);
        var matcher = new ModelMatcher(Data);
        matcher.SetSimulator(SimulatorType.Msfs2024);
        matcher.DefaultModel = SimulatorType.Msfs2024.DefaultModel();
        matcher.LoadCustomRuleSets(installation.FindRuleSets());
        matcher.GenerateRules(scan);
        return matcher;
    }

    [Fact]
    public void ScanGroupsByPackageAndClassifies()
    {
        ScanAndBuild(out var scan);
        Assert.Equal(2, scan.Folders.Count);
        Assert.Contains(scan.Folders, f => f.RelativePath.EndsWith("fsltl-traffic-base"));
        var models = scan.AllModels().ToDictionary(m => m.Title!);
        Assert.Equal(ModelStatus.Ignored, models["PMDG 737-800 Test"].Status);
        Assert.NotEqual(ModelStatus.Unknown, models["FSLTL_A20N_CCA"].Status);
        Assert.Equal(ModelStatus.Derived, models["FSLTL_A20N_PW_ANA"].Status);
    }

    [Fact]
    public void FourPassMatching()
    {
        var matcher = ScanAndBuild(out _);

        // 第 1 轮：航司 + 机型（自定义规则集优先）
        var ana = matcher.Match("ANA123", "A20N");
        Assert.Equal("FSLTL_A20N_PW_ANA", ana.ModelName);
        Assert.Equal(1, ana.Pass);
        Assert.Equal("FSLTL_Rules.vmr", ana.Source);

        // 第 1 轮：自动生成的规则（atc_parking_codes = CCA）
        Assert.Equal("FSLTL_A20N_CCA", matcher.Match("CCA1501", "A20N").ModelName);

        // 第 2 轮：航司对、同族机型（A320 → A20N）
        var family = matcher.Match("CCA88", "A320");
        Assert.Equal("FSLTL_A20N_CCA", family.ModelName);
        Assert.Equal(2, family.Pass);

        // 规则集里没装的模型跳过
        Assert.NotEqual("FSLTL_NOT_INSTALLED", matcher.Match("XXX1", "A20N").ModelName);

        // 第 3 轮：无航司、机型一致（B738 只有 CSN 涂装，B738 在有涂装机型表里，没有通用规则 → 第 4 轮或默认）
        var generic = matcher.Match("UNK1", "A21N");
        Assert.Equal("FSLTL_A20N_ZZZZ", generic.ModelName);
        Assert.Equal(3, generic.Pass);

        // 认不出的机型 → 默认模型
        var unknown = matcher.Match("QQQ1", "ZZZZZZ");
        Assert.True(unknown.IsDefault);
    }

    [Fact]
    public void ErroneousModelsAreSkipped()
    {
        var matcher = ScanAndBuild(out _);
        matcher.MarkErroneous("FSLTL_A20N_CCA");
        Assert.NotEqual("FSLTL_A20N_CCA", matcher.Match("CCA1501", "A20N").ModelName);
    }

    [Fact]
    public void ScanCacheRoundTripsAndDetectsChanges()
    {
        var installation = new MsfsInstallation(SimulatorType.Msfs2024, _install.Root);
        var scan = ModelScanner.Scan(installation, Data);
        ModelScanStore.CarryOverPreferences(scan, null);
        ModelScanStore.Save(scan);
        var loaded = ModelScanStore.Load(SimulatorType.Msfs2024);
        Assert.NotNull(loaded);
        Assert.Equal(scan.ModelCount, loaded!.ModelCount);
        Assert.Null(ModelScanStore.NeedsRescan(loaded, installation));

        var cfg = loaded.Folders.SelectMany(f => f.Subfolders).First().ConfigFilePath;
        File.AppendAllText(cfg, "\n[FLTSIM.9]\ntitle = \"New\"\n");
        Assert.NotNull(ModelScanStore.NeedsRescan(loaded, installation));
    }

    [Fact]
    public void FlightNumberRanges()
    {
        var set = ModelMatchRuleSet.Load(new MemoryStream("""
            <ModelMatchRuleSet>
            <ModelMatchRule CallsignPrefix="CCA" FlightNumberRange="1000-1999" TypeCode="A20N" ModelName="A//B" />
            </ModelMatchRuleSet>
            """u8.ToArray()));
        var rule = Assert.Single(set);
        Assert.Equal(1000, rule.MinFlightNumber);
        Assert.Equal(1999, rule.MaxFlightNumber);
        Assert.Equal(["A", "B"], rule.Models());
    }
}

/// <summary>对着这台机器上真实的 MSFS 安装跑一遍（没有安装就跳过）。</summary>
public class RealInstallationTests(ITestOutputHelper output)
{
    [Fact]
    public void ScansRealInstallation()
    {
        var installation = new MsfsInstallation(SimulatorType.Msfs2024);
        if (!installation.IsValid)
        {
            output.WriteLine("no MSFS 2024 installation, skipped");
            return;
        }
        var data = ModelMatchingData.LoadCompressed(Path.Combine(AppContext.BaseDirectory, "ModelMatchingData.bin"));
        var started = DateTime.UtcNow;
        var scan = ModelScanner.Scan(installation, data);
        output.WriteLine($"scanned {installation.PackagesPath} in {(DateTime.UtcNow - started).TotalSeconds:F1} s: " +
                         $"{scan.ModelCount} models, {scan.IdentifiedModelCount} identified, {scan.Folders.Count} packages");
        foreach (var folder in scan.Folders.Take(40))
            output.WriteLine($"  {folder.RelativePath}: {folder.ModelCount} models, {folder.IdentifiedModelCount} identified");

        var matcher = new ModelMatcher(data);
        matcher.SetSimulator(SimulatorType.Msfs2024);
        matcher.DefaultModel = SimulatorType.Msfs2024.DefaultModel();
        var ruleSets = installation.FindRuleSets();
        output.WriteLine("rule sets: " + string.Join(", ", ruleSets));
        matcher.LoadCustomRuleSets(ruleSets);
        matcher.GenerateRules(scan);
        foreach (var (callsign, type) in new[]
                 {
                     ("CCA1501", "A20N"), ("CES5101", "A320"), ("CSN3101", "A21N"), ("CHH7178", "B738"),
                     ("ANA12", "B789"), ("DAL1", "B772"), ("CXA8102", "B38M"), ("CDG4321", "C919"), ("N123AB", "C172"),
                     ("UAL1", "ZZZZ")
                 })
        {
            var result = matcher.Match(callsign, type);
            output.WriteLine($"{callsign} {type} → {result.ModelName} [{result.Description} / {result.Source}]");
        }
        Assert.True(scan.ModelCount > 0);
    }
}
