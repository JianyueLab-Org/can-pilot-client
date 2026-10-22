using System.Xml;
using System.Xml.Serialization;
using CanClient.Core.Util;

namespace CanClient.Core.ModelMatching;

/// <summary>
/// 扫描结果的缓存，vPilot Type_02000086。
///
/// 启动时（或者连上模拟器时）先读 ModelScanResult-{模拟器}.xml，再核对一遍：
///   版本号、配置文件路径列表的哈希、每份配置的大小和修改时间（变了再比 MD5）。
/// 有一处对不上就要重扫。大小/时间变了但 MD5 没变的，只把新的大小和时间写回去。
/// </summary>
public static class ModelScanStore
{
    private static readonly Logger Log = Util.Log.For("models");
    private static readonly XmlSerializer Serializer = new(typeof(ModelScanResult));

    public static ModelScanResult? Load(SimulatorType simulator)
    {
        var path = AppPaths.ModelScanResultFile(simulator.ToString());
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { CheckCharacters = false });
            var result = (ModelScanResult?)Serializer.Deserialize(reader);
            if (result is not null)
            {
                foreach (var folder in result.Folders)
                {
                    foreach (var subfolder in folder.Subfolders)
                        subfolder.FullPath = Path.GetDirectoryName(subfolder.ConfigFilePath) ?? "";
                    folder.FullPath = string.IsNullOrEmpty(result.PackagesPath)
                        ? folder.RelativePath
                        : Path.Combine(result.PackagesPath, folder.RelativePath);
                }
            }
            return result;
        }
        catch (Exception e)
        {
            Log.Warning($"could not read {path}: {e.Message}");
            return null;
        }
    }

    public static void Save(ModelScanResult result)
    {
        var path = AppPaths.ModelScanResultFile(result.SimulatorType.ToString());
        try
        {
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, CheckCharacters = false }))
                Serializer.Serialize(writer, result);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warning($"could not save {path}: {e.Message}");
        }
    }

    /// <summary>缓存还能不能用。返回 null 表示可以；否则返回要重扫的原因（写日志、给界面看）。</summary>
    public static string? NeedsRescan(ModelScanResult? cached, MsfsInstallation installation)
    {
        if (cached is null)
            return "还没有扫描过";
        if (cached.Version < ModelScanResult.CurrentVersion)
            return "扫描结果是旧版本";
        if (!string.Equals(cached.PackagesPath, installation.PackagesPath ?? "", StringComparison.OrdinalIgnoreCase))
            return "包目录变了";

        // 扫描时所有配置文件路径的哈希（含没有涂装的那些），装了或删了飞机就对不上
        var current = installation.AircraftConfigFiles().Select(c => c.FullPath);
        if (cached.AircraftConfigPathsHash != ModelScanner.PathsHash(current))
            return "装了新的飞机或删了飞机";

        var resave = false;
        foreach (var subfolder in cached.Folders.SelectMany(f => f.Subfolders))
        {
            if (string.IsNullOrEmpty(subfolder.ConfigFilePath) || !File.Exists(subfolder.ConfigFilePath))
                return $"配置文件不见了：{subfolder.ConfigFilePath}";
            var info = new FileInfo(subfolder.ConfigFilePath);
            if (info.Length == subfolder.ConfigFileSize && info.LastWriteTime == subfolder.ConfigFileModificationTime)
                continue;
            if (ModelScanner.Md5(subfolder.ConfigFilePath) != subfolder.ConfigFileHash)
                return $"配置文件改过了：{subfolder.ConfigFilePath}";
            subfolder.ConfigFileSize = info.Length;
            subfolder.ConfigFileModificationTime = info.LastWriteTime;
            resave = true;
        }
        if (resave)
        {
            Log.Info("MD5 hash is the same, resaving model scan result with new config file sizes and timestamps.");
            Save(cached);
        }
        return null;
    }

    /// <summary>
    /// 重扫之后把用户在"高级"里调过的东西搬过来：包的优先级和排除、飞机目录和涂装的排除。
    /// 新出现的包排在最后。vPilot Method_060000D4。
    /// </summary>
    public static void CarryOverPreferences(ModelScanResult fresh, ModelScanResult? previous)
    {
        if (previous is not null)
        {
            foreach (var old in previous.Folders)
            {
                var folder = fresh.Folders.FirstOrDefault(f => f.RelativePath == old.RelativePath);
                if (folder is null)
                    continue;
                folder.Priority = old.Priority;
                folder.IsExcluded = old.IsExcluded;
                foreach (var oldSub in old.Subfolders)
                {
                    var sub = folder.Subfolders.FirstOrDefault(s => s.RelativePath == oldSub.RelativePath);
                    if (sub is null)
                        continue;
                    sub.IsExcluded = oldSub.IsExcluded;
                    foreach (var oldModel in oldSub.Models)
                    {
                        var model = sub.Models.FirstOrDefault(m => m.Title == oldModel.Title);
                        if (model is not null)
                            model.IsExcluded = oldModel.IsExcluded;
                    }
                }
            }
        }

        var next = fresh.Folders.Count == 0 ? 0 : fresh.Folders.Max(f => f.Priority);
        foreach (var folder in fresh.Folders.Where(f => f.Priority == 0))
            folder.Priority = ++next;
        NormalisePriorities(fresh);
    }

    /// <summary>把优先级重新编成 1..N（按现有顺序），上移下移以后用。</summary>
    public static void NormalisePriorities(ModelScanResult result)
    {
        var ordered = result.Folders.OrderBy(f => f.Priority).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Priority = i + 1;
        result.Folders.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }
}
