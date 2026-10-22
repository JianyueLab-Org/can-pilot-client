using System.Security.Cryptography;
using System.Text;
using CanClient.Core.Util;

namespace CanClient.Core.ModelMatching;

public sealed record ScanProgress(int Done, int Total, string Stage);

/// <summary>
/// 扫描已安装的飞机，vPilot 的 ModelScanForm（Type_02000018）去掉界面的那一半。
///
/// 每份配置记下大小、修改时间和 MD5，下次启动时 <see cref="ModelScanStore.NeedsRescan"/> 拿它们
/// 判断要不要重扫——FSLTL 一个包就有两千多份配置，每次启动都全扫一遍太慢。
/// 每个涂装按 ModelMatchingData 定状态：已知 / 推导 / 未知 / 忽略。
/// </summary>
public static class ModelScanner
{
    private static readonly Logger Log = Util.Log.For("models");

    // 这两家的机模不能当 AI 生成，生成了也是一团糟
    private static readonly string[] IgnoredTitleKeywords = ["pmdg", "carenado"];

    public static ModelScanResult Scan(MsfsInstallation installation, ModelMatchingData data,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default)
    {
        var result = new ModelScanResult
        {
            SimulatorType = installation.SimulatorType,
            PackagesPath = installation.PackagesPath ?? ""
        };
        progress?.Report(new ScanProgress(0, 0, "正在查找 SimObjects 文件夹……"));

        var configs = installation.AircraftConfigFiles();
        var byVirtualPath = configs.ToDictionary(c => c.VirtualPath, StringComparer.OrdinalIgnoreCase);

        // 按包分组，组内按虚拟路径排序；包的顺序就是初始优先级（Official 在前）
        var folders = new Dictionary<string, ModelScanResultFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!folders.TryGetValue(config.PackageRelativePath, out var folder))
            {
                folder = new ModelScanResultFolder
                {
                    RelativePath = config.PackageRelativePath,
                    FullPath = config.PackageFullPath
                };
                folders[config.PackageRelativePath] = folder;
                result.Folders.Add(folder);
            }
            try
            {
                var info = new FileInfo(config.FullPath);
                folder.Subfolders.Add(new ModelScanResultSubfolder
                {
                    FullPath = info.DirectoryName ?? "",
                    RelativePath = Path.GetDirectoryName(config.VirtualPath) ?? config.VirtualPath,
                    ConfigFilePath = config.FullPath,
                    ConfigFileSize = info.Length,
                    ConfigFileModificationTime = info.LastWriteTime
                });
            }
            catch (Exception e)
            {
                Log.Error($"Error getting file information for {config.FullPath}: {e.Message}");
            }
        }

        var total = result.Folders.Sum(f => f.Subfolders.Count);
        var done = 0;
        foreach (var folder in result.Folders)
        {
            foreach (var subfolder in folder.Subfolders)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    subfolder.ConfigFileHash = Md5(subfolder.ConfigFilePath);
                    var models = AircraftConfigParser.ReadModels(subfolder.ConfigFilePath,
                        Path.Combine(subfolder.RelativePath, Path.GetFileName(subfolder.ConfigFilePath)),
                        byVirtualPath);
                    foreach (var model in models)
                    {
                        Classify(model, data);
                        subfolder.Models.Add(model);
                    }
                }
                catch (Exception e)
                {
                    Log.Debug($"Error scanning {subfolder.ConfigFilePath}: {e.Message}");
                }
                done++;
                if (done % 25 == 0 || done == total)
                    progress?.Report(new ScanProgress(done, total, $"正在扫描模型 {done:N0} / {total:N0}"));
            }
        }

        // 空的组（全是附加件、地景之类）不留
        result.Folders.RemoveAll(f => f.Subfolders.All(s => s.Models.Count == 0));

        result.Version = ModelScanResult.CurrentVersion;
        result.ScannedOn = DateTime.Now;
        result.AircraftConfigPathsHash = PathsHash(configs.Select(c => c.FullPath));
        Log.Info($"scanned {installation.Name} at {installation.PackagesPath}: {configs.Count} configs, " +
                 $"{result.ModelCount} models, {result.IdentifiedModelCount} identified, {result.Folders.Count} packages");
        return result;
    }

    /// <summary>给一个涂装定状态，并记下推出来的机型/航司（界面显示用）。</summary>
    public static void Classify(ModelScanResultModel model, ModelMatchingData data)
    {
        model.DerivedTypeCodes = null;
        model.DerivedAirlines = null;
        if (string.IsNullOrEmpty(model.Title))
        {
            model.Status = ModelStatus.Unknown;
            return;
        }
        if (IgnoredTitleKeywords.Any(k => model.Title.Contains(k, StringComparison.InvariantCultureIgnoreCase)))
        {
            model.Status = ModelStatus.Ignored;
            return;
        }

        IReadOnlyList<ModelSpec> specs;
        if (data.IsKnownTitle(model.Title))
        {
            model.Status = ModelStatus.Known;
            specs = data.SpecsForTitle(model.Title);
        }
        else
        {
            specs = ModelSpecDeriver.Derive(model, data);
            model.Status = specs.Count > 0 ? ModelStatus.Derived : ModelStatus.Unknown;
        }
        if (specs.Count > 0)
        {
            model.DerivedTypeCodes = string.Join(',', specs.Select(s => s.TypeCode).Distinct());
            var airlines = specs.Select(s => s.CallsignPrefix).Where(p => p.Length > 0).Distinct().ToList();
            model.DerivedAirlines = airlines.Count > 0 ? string.Join(',', airlines) : null;
        }
    }

    public static string Md5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    /// <summary>vPilot Type_0200008F：排序后用 | 连起来取 MD5。</summary>
    public static string PathsHash(IEnumerable<string> paths)
    {
        var sorted = paths.ToList();
        sorted.Sort(StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(string.Join('|', sorted));
        return Convert.ToHexStringLower(MD5.HashData(bytes));
    }
}
