using System.Xml.Serialization;

namespace CanClient.Core.ModelMatching;

/// <summary>连上的是哪个模拟器。决定扫哪套目录、用哪套默认模型和内置规则。</summary>
public enum SimulatorType
{
    None,
    Msfs,
    Msfs2024,
    XPlane
}

/// <summary>
/// 一个模型在扫描时的识别结果，vPilot 的 ModelStatus：
///   Known    title 在 ModelMatchingData 里
///   Derived  不在库里，但从 aircraft.cfg 的 icao_type_designator / atc_parking_codes 推出了机型和航司
///   Unknown  推不出来（缺机型码），不参与自动匹配，只能靠自定义规则用
///   Ignored  PMDG、Carenado 这类不能当 AI 生成的
/// </summary>
public enum ModelStatus
{
    Unknown,
    Known,
    Derived,
    Ignored
}

public static class SimulatorTypeExtensions
{
    public static string DisplayName(this SimulatorType type) => type switch
    {
        SimulatorType.Msfs => "MSFS",
        SimulatorType.Msfs2024 => "MSFS 2024",
        SimulatorType.XPlane => "X-Plane",
        _ => "未连接"
    };

    public static string FullName(this SimulatorType type) => type switch
    {
        SimulatorType.Msfs => "Microsoft Flight Simulator",
        SimulatorType.Msfs2024 => "Microsoft Flight Simulator 2024",
        SimulatorType.XPlane => "X-Plane 11 / 12",
        _ => "未知模拟器"
    };

    /// <summary>找不到合适的模型时用的默认模型，和 vPilot 一致。</summary>
    public static string DefaultModel(this SimulatorType type) => type switch
    {
        SimulatorType.Msfs2024 => "Asobo PassiveAircraft E190",
        _ => "Generic Airliner Twin Engines Asobo 00"
    };

    /// <summary>SIMCONNECT_RECV_OPEN 里的程序主版本号 → 模拟器。11 是 MSFS 2020，12 是 2024。</summary>
    public static SimulatorType FromApplicationVersion(int major) => major switch
    {
        11 => SimulatorType.Msfs,
        12 => SimulatorType.Msfs2024,
        _ => SimulatorType.None
    };
}

/// <summary>一份扫描结果，缓存到 ModelScanResult-{模拟器}.xml，格式和 vPilot 的版本 4 对应。</summary>
[XmlRoot("ModelScanResult")]
public sealed class ModelScanResult
{
    public const int CurrentVersion = 4;

    [XmlAttribute] public int Version { get; set; }
    [XmlAttribute] public DateTime ScannedOn { get; set; }
    [XmlAttribute] public SimulatorType SimulatorType { get; set; }
    [XmlAttribute] public string AircraftConfigPathsHash { get; set; } = "";
    /// <summary>扫描时用的包目录（InstalledPackagesPath 或手动指定的目录）。</summary>
    [XmlAttribute] public string PackagesPath { get; set; } = "";

    public List<ModelScanResultFolder> Folders { get; set; } = new();

    [XmlIgnore] public bool IsEmpty => Folders.All(f => f.Subfolders.All(s => s.Models.Count == 0));
    [XmlIgnore] public int ModelCount => Folders.Sum(f => f.ModelCount);
    [XmlIgnore] public int IdentifiedModelCount => Folders.Sum(f => f.IdentifiedModelCount);

    public IEnumerable<ModelScanResultModel> AllModels() =>
        Folders.SelectMany(f => f.Subfolders).SelectMany(s => s.Models);

    public bool ContainsTitle(string title) =>
        AllModels().Any(m => !string.IsNullOrEmpty(m.Title) && m.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
}

/// <summary>一个包（或 Official 下的一组），vPilot 里的"顶层文件夹"，带优先级和排除标记。</summary>
public sealed class ModelScanResultFolder
{
    [XmlAttribute] public string RelativePath { get; set; } = "";
    [XmlIgnore] public string FullPath { get; set; } = "";
    [XmlAttribute] public int Priority { get; set; }
    [XmlAttribute] public bool IsExcluded { get; set; }

    public List<ModelScanResultSubfolder> Subfolders { get; set; } = new();

    [XmlIgnore] public int ModelCount => Subfolders.Sum(s => s.ModelCount);
    [XmlIgnore] public int IdentifiedModelCount => Subfolders.Sum(s => s.IdentifiedModelCount);
}

/// <summary>一个飞机目录（一份 aircraft.cfg / sim.cfg）。</summary>
public sealed class ModelScanResultSubfolder
{
    [XmlAttribute] public string RelativePath { get; set; } = "";
    [XmlIgnore] public string FullPath { get; set; } = "";
    [XmlAttribute] public string ConfigFilePath { get; set; } = "";
    [XmlAttribute] public long ConfigFileSize { get; set; }
    [XmlAttribute] public DateTime ConfigFileModificationTime { get; set; }
    [XmlAttribute] public string ConfigFileHash { get; set; } = "";
    [XmlAttribute] public bool IsExcluded { get; set; }

    public List<ModelScanResultModel> Models { get; set; } = new();

    [XmlIgnore] public int ModelCount => Models.Count;

    [XmlIgnore]
    public int IdentifiedModelCount => Models.Count(m => m.Status is ModelStatus.Known or ModelStatus.Derived);
}

/// <summary>aircraft.cfg 里的一个 [FLTSIM.n]。</summary>
public sealed class ModelScanResultModel
{
    [XmlAttribute] public string? Title { get; set; }
    [XmlAttribute] public string? Model { get; set; }
    [XmlAttribute] public string? Panel { get; set; }
    [XmlAttribute] public string? Sound { get; set; }
    [XmlAttribute] public string? Texture { get; set; }
    [XmlAttribute] public string? Sim { get; set; }
    [XmlAttribute] public string? AtcModel { get; set; }
    [XmlAttribute] public string? IcaoTypeDesignator { get; set; }
    [XmlAttribute] public string? IcaoAirline { get; set; }
    [XmlAttribute] public string? AtcAirline { get; set; }
    [XmlAttribute] public string? AtcFlightNumber { get; set; }
    [XmlAttribute] public string? AtcId { get; set; }
    [XmlAttribute] public string? AtcHeavy { get; set; }
    [XmlAttribute] public string? UiManufacturer { get; set; }
    [XmlAttribute] public string? UiTypeRole { get; set; }
    [XmlAttribute] public string? UiCreatedBy { get; set; }
    [XmlAttribute] public string? UiType { get; set; }
    [XmlAttribute] public string? UiVariation { get; set; }
    [XmlAttribute] public string? Description { get; set; }
    [XmlAttribute] public string? AtcParkingTypes { get; set; }
    [XmlAttribute] public string? AtcParkingCodes { get; set; }
    [XmlAttribute] public ModelStatus Status { get; set; }
    [XmlAttribute] public bool IsExcluded { get; set; }

    /// <summary>推出来的（或库里的）机型/航司，界面显示用，不落盘也能重新算。</summary>
    [XmlAttribute] public string? DerivedTypeCodes { get; set; }
    [XmlAttribute] public string? DerivedAirlines { get; set; }
}
