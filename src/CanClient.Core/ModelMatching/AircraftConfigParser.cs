using System.Text.RegularExpressions;

namespace CanClient.Core.ModelMatching;

/// <summary>
/// 读 aircraft.cfg / sim.cfg，vPilot Type_02000077。
///
/// 这些文件是人手写的，格式相当随意：重复的节和键、没有值的行、BOM、各种编码都见得到。
/// 照 vPilot 用的 IniParser 配置：节可以重复（合并）、键可以重复（后者覆盖）、坏行跳过、
/// 行注释是 //；取值时再把 // 和 ; 之后的东西去掉、剥掉引号。
///
/// MSFS 的涂装包常常只有一个 [VARIATION] base_container="..\基础机型"，机型码和
/// category 要到基础机型的配置里去读——那份配置用虚拟路径在扫描到的列表里找。
/// </summary>
public static class AircraftConfigParser
{
    private static readonly Regex SectionPattern = new(@"^\s*\[(.+?)\]", RegexOptions.Compiled);

    /// <summary>一份解析过的 ini：节名（不分大小写）→ 键（小写）→ 值（原样）。</summary>
    public sealed class IniData
    {
        public List<(string Name, Dictionary<string, string> Keys)> Sections { get; } = new();

        public IEnumerable<(string Name, Dictionary<string, string> Keys)> Named(string name) =>
            Sections.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static IniData ReadIni(string path) => ParseIni(File.ReadAllText(path));

    public static IniData ParseIni(string text)
    {
        var data = new IniData();
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var current = global;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            var section = SectionPattern.Match(line);
            if (section.Success)
            {
                var name = section.Groups[1].Value.Trim();
                var existing = data.Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing.Keys is not null)
                    current = existing.Keys;
                else
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    data.Sections.Add((name, current));
                }
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            if (key.Length == 0)
                continue;
            current[key] = line[(eq + 1)..];
        }
        return data;
    }

    /// <summary>vPilot Method_06000601：去掉 // 和 ; 之后的注释，剥掉成对的引号。</summary>
    public static string CleanValue(string? value)
    {
        value ??= "";
        var slash = value.IndexOf("//", StringComparison.Ordinal);
        if (slash >= 0)
            value = value[..slash];
        var semicolon = value.IndexOf(';');
        if (semicolon >= 0)
            value = value[..semicolon];
        value = value.Trim();
        if (value.Length >= 1 && value.StartsWith('"') && value.EndsWith('"'))
            value = value.Trim('"', ' ');
        return value;
    }

    private static (string AtcModel, string IcaoType, string Category, string IcaoAirline) ReadGeneral(IniData data)
    {
        string atcModel = "", icao = "", category = "", airline = "";
        foreach (var (_, keys) in data.Named("General"))
        {
            foreach (var (key, value) in keys)
            {
                if (key.Equals("atc_model", StringComparison.OrdinalIgnoreCase))
                    atcModel = CleanValue(value);
                else if (key.Equals("icao_type_designator", StringComparison.OrdinalIgnoreCase))
                    icao = CleanValue(value);
                else if (key.Equals("category", StringComparison.OrdinalIgnoreCase))
                    category = CleanValue(value);
                else if (key.Equals("icao_airline", StringComparison.OrdinalIgnoreCase))
                    airline = CleanValue(value);
            }
        }
        return (atcModel, icao, category, airline);
    }

    /// <summary>
    /// 读出一份配置里的所有涂装。
    /// </summary>
    /// <param name="configPath">磁盘上的配置文件。</param>
    /// <param name="virtualPath">它在包里的虚拟路径；有 base_container 时要靠它找基础机型。</param>
    /// <param name="configsByVirtualPath">扫描到的所有配置，按虚拟路径索引。</param>
    public static List<ModelScanResultModel> ReadModels(string configPath, string? virtualPath = null,
        IReadOnlyDictionary<string, AircraftConfigFile>? configsByVirtualPath = null)
    {
        var list = new List<ModelScanResultModel>();
        var data = ReadIni(configPath);

        var baseContainer = "";
        foreach (var (_, keys) in data.Named("VARIATION"))
        {
            foreach (var (key, value) in keys)
            {
                if (key.Equals("base_container", StringComparison.OrdinalIgnoreCase))
                    baseContainer = CleanValue(value);
            }
        }

        string baseAtcModel = "", baseIcao = "", baseCategory = "", baseAirline = "";
        if (baseContainer.Length > 0)
        {
            if (string.IsNullOrEmpty(virtualPath) || configsByVirtualPath is null)
                return list;
            var baseVirtual = ResolveVirtualPath(VirtualDirectory(virtualPath), baseContainer,
                Path.GetFileName(configPath));
            if (!configsByVirtualPath.TryGetValue(baseVirtual, out var baseConfig))
                return list;
            (baseAtcModel, baseIcao, baseCategory, baseAirline) = ReadGeneral(ReadIni(baseConfig.FullPath));
        }

        var (atcModel, icao, category, generalAirline) = ReadGeneral(data);
        if (atcModel.Length == 0)
            atcModel = baseAtcModel;
        if (icao.Length == 0)
            icao = baseIcao;
        if (category.Length == 0)
            category = baseCategory;
        if (generalAirline.Length == 0)
            generalAirline = baseAirline;

        if (!category.Equals("airplane", StringComparison.OrdinalIgnoreCase)
            && !category.Equals("helicopter", StringComparison.OrdinalIgnoreCase))
            return list;

        foreach (var (name, keys) in data.Sections)
        {
            if (!name.StartsWith("fltsim.", StringComparison.OrdinalIgnoreCase))
                continue;
            var model = new ModelScanResultModel();
            foreach (var (key, value) in keys)
            {
                var clean = CleanValue(value);
                switch (key.ToLowerInvariant())
                {
                    case "title": model.Title = clean; break;
                    case "model": model.Model = clean; break;
                    case "panel": model.Panel = clean; break;
                    case "sound": model.Sound = clean; break;
                    case "texture": model.Texture = clean; break;
                    case "sim": model.Sim = clean; break;
                    case "atc_model": model.AtcModel = clean; break;
                    case "icao_type_designator": model.IcaoTypeDesignator = clean; break;
                    case "icao_airline": model.IcaoAirline = clean; break;
                    case "atc_airline": model.AtcAirline = clean; break;
                    case "atc_flight_number": model.AtcFlightNumber = clean; break;
                    case "atc_id": model.AtcId = clean; break;
                    case "atc_heavy": model.AtcHeavy = clean; break;
                    case "ui_manufacturer": model.UiManufacturer = clean; break;
                    case "ui_typerole": model.UiTypeRole = clean; break;
                    case "ui_createdby": model.UiCreatedBy = clean; break;
                    case "ui_type": model.UiType = clean; break;
                    case "ui_variation": model.UiVariation = clean; break;
                    case "description": model.Description = clean; break;
                    case "atc_parking_types": model.AtcParkingTypes = clean; break;
                    case "atc_parking_codes": model.AtcParkingCodes = clean; break;
                }
            }
            if (string.IsNullOrEmpty(model.AtcModel))
                model.AtcModel = atcModel;
            if (string.IsNullOrEmpty(model.IcaoTypeDesignator))
                model.IcaoTypeDesignator = icao;
            if (string.IsNullOrEmpty(model.IcaoAirline))
                model.IcaoAirline = generalAirline;
            list.Add(model);
        }
        return list;
    }

    /// <summary>
    /// 把相对路径拼到虚拟目录上再规范化：SimObjects\Airplanes\A_ANA + ..\A → SimObjects\Airplanes\A\aircraft.cfg。
    /// 虚拟路径固定用反斜杠，自己按段处理 . 和 ..，不依赖本机的路径规则（macOS / Linux 上一样跑得通）。
    /// </summary>
    public static string ResolveVirtualPath(string virtualDirectory, string relative, string fileName)
    {
        var parts = new List<string>();
        foreach (var segment in $"{virtualDirectory}\\{relative}\\{fileName}".Split('\\', '/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return string.Join('\\', parts);
    }

    /// <summary>虚拟路径的上一级目录（反斜杠分隔，和本机路径规则无关）。</summary>
    public static string VirtualDirectory(string virtualPath)
    {
        var slash = virtualPath.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? "" : virtualPath[..slash];
    }
}
