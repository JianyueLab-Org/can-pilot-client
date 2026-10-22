using System.IO.Compression;
using System.Xml;

namespace CanClient.Core.ModelMatching;

/// <summary>vPilot RossCarlson.Vatsim.ModelMatching.ModelSpec：一个已知模型对应的机型和航司。</summary>
public sealed record ModelSpec(string Title, string TypeCode, string CallsignPrefix = "");

/// <summary>atc_model / ui_type 里常见的非 ICAO 写法 → ICAO 机型码（如 "738" → B738）。</summary>
public sealed record TypeCodeAlias(string Alias, string TypeCode);

/// <summary>一个 ICAO 机型码的名称和制造商。</summary>
public sealed record TypeInfo(string TypeCode, string Name, string Manufacturer)
{
    public override string ToString() => $"{Manufacturer} {Name} ({TypeCode})";
}

/// <summary>
/// vPilot 的 ModelMatchingData.bin：Deflate 压缩的 XML，里面是
///
///   ModelSpecs          一万多个已知模型 title → 机型/航司（AI 包、默认机）
///   TypeCodeAliases     非标准机型写法 → ICAO 码
///   AirlineCodes        所有 ICAO 航司码
///   TypeCodes           所有 ICAO 机型码
///   TypeInfo            机型名称和制造商（连接对话框的机型补全也用它）
///   SimilarTypeCodes    同族机型（"A318 A319 A320 A321 A19N A20N A21N"）
///   LiveriedTypeCodes   有航司涂装的机型（这些机型的无涂装模型不当成通用模型用）
///
/// 数据随程序发布，不在运行时去 vPilot 的服务器下载。
/// </summary>
public sealed class ModelMatchingData
{
    private readonly Dictionary<string, List<ModelSpec>> _specsByTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _similar = new(StringComparer.Ordinal);

    public int Serial { get; private set; }
    public List<ModelSpec> ModelSpecs { get; } = new();
    public List<TypeCodeAlias> TypeCodeAliases { get; } = new();
    public HashSet<string> AirlineCodes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> TypeCodes { get; } = new(StringComparer.Ordinal);
    public List<TypeInfo> TypeInfo { get; } = new();
    public List<string> SimilarTypeCodes { get; } = new();
    public HashSet<string> LiveriedTypeCodes { get; } = new(StringComparer.Ordinal);

    public static ModelMatchingData Empty { get; } = new();

    public bool IsEmpty => TypeCodes.Count == 0 && ModelSpecs.Count == 0;

    /// <summary>读 vPilot 格式的 .bin（原始 Deflate 流，不带 zlib 头）。</summary>
    public static ModelMatchingData LoadCompressed(string path)
    {
        using var file = File.OpenRead(path);
        using var deflate = new DeflateStream(file, CompressionMode.Decompress);
        return Load(deflate);
    }

    public static ModelMatchingData Load(Stream xml)
    {
        var data = new ModelMatchingData();
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
        string? section = null;
        // ReadElementContentAsString 读完会停在下一个节点上，所以只有没读内容的时候才 Read()，
        // 否则每隔一个 <string> 就会被跳过去。
        var advanced = false;
        while (advanced || reader.Read())
        {
            advanced = false;
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            switch (reader.Depth)
            {
                case 0:
                    if (int.TryParse(reader.GetAttribute("Serial"), out var serial))
                        data.Serial = serial;
                    continue;
                case 1:
                    section = reader.LocalName;
                    continue;
            }

            switch (section)
            {
                case "ModelSpecs" when reader.LocalName == "ModelSpec":
                    var title = reader.GetAttribute("Title");
                    var type = reader.GetAttribute("TypeCode");
                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(type))
                        data.ModelSpecs.Add(new ModelSpec(title, type, reader.GetAttribute("CallsignPrefix") ?? ""));
                    break;
                case "TypeCodeAliases" when reader.LocalName == "TypeCodeAlias":
                    var alias = reader.GetAttribute("Alias");
                    var target = reader.GetAttribute("TypeCode");
                    if (!string.IsNullOrEmpty(alias) && !string.IsNullOrEmpty(target))
                        data.TypeCodeAliases.Add(new TypeCodeAlias(alias, target));
                    break;
                case "TypeInfo" when reader.LocalName == "TypeInfo":
                    data.TypeInfo.Add(new TypeInfo(reader.GetAttribute("TypeCode") ?? "",
                        reader.GetAttribute("Name") ?? "", reader.GetAttribute("Manufacturer") ?? ""));
                    break;
                case "AirlineCodes" when reader.LocalName == "string":
                    data.AirlineCodes.Add(reader.ReadElementContentAsString().Trim());
                    advanced = true;
                    break;
                case "TypeCodes" when reader.LocalName == "string":
                    data.TypeCodes.Add(reader.ReadElementContentAsString().Trim());
                    advanced = true;
                    break;
                case "SimilarTypeCodes" when reader.LocalName == "string":
                    data.SimilarTypeCodes.Add(reader.ReadElementContentAsString().Trim());
                    advanced = true;
                    break;
                case "LiveriedTypeCodes" when reader.LocalName == "string":
                    data.LiveriedTypeCodes.Add(reader.ReadElementContentAsString().Trim());
                    advanced = true;
                    break;
            }
        }
        data.Index();
        return data;
    }

    private void Index()
    {
        foreach (var spec in ModelSpecs)
        {
            if (!_specsByTitle.TryGetValue(spec.Title, out var list))
                _specsByTitle[spec.Title] = list = new List<ModelSpec>();
            list.Add(spec);
        }

        foreach (var alias in TypeCodeAliases)
            _aliases.TryAdd(alias.Alias.ToUpperInvariant(), alias.TypeCode.ToUpperInvariant());

        // vPilot Type_02000088.Method_0600063C：每个机型映射到它所在的那一整组
        foreach (var line in SimilarTypeCodes)
        {
            var group = line.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
            if (group.Count < 2)
                continue;
            foreach (var code in group)
                _similar.TryAdd(code, group);
        }
    }

    /// <summary>这个 title 是不是数据库里的已知模型。</summary>
    public bool IsKnownTitle(string? title) => !string.IsNullOrEmpty(title) && _specsByTitle.ContainsKey(title);

    public IReadOnlyList<ModelSpec> SpecsForTitle(string? title) =>
        !string.IsNullOrEmpty(title) && _specsByTitle.TryGetValue(title, out var list) ? list : [];

    /// <summary>同族机型（含自己）。不在任何一组里返回 false。</summary>
    public bool TryGetSimilar(string typeCode, out IReadOnlyList<string> similar)
    {
        if (_similar.TryGetValue(typeCode, out var list))
        {
            similar = list;
            return true;
        }
        similar = [];
        return false;
    }

    /// <summary>别名 → ICAO 机型码，没有返回 null。</summary>
    public string? ResolveAlias(string? alias) =>
        !string.IsNullOrEmpty(alias) && _aliases.TryGetValue(alias.ToUpperInvariant(), out var code) ? code : null;

    /// <summary>机型码的说明（第一条），连接对话框和航空器列表显示用。</summary>
    public TypeInfo? Describe(string? typeCode) =>
        string.IsNullOrEmpty(typeCode) ? null : TypeInfo.FirstOrDefault(t => t.TypeCode == typeCode);
}
