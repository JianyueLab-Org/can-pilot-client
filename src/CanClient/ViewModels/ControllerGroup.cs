using System.Collections.ObjectModel;
using CanClient.Core.Fsd;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanClient.ViewModels;

/// <summary>“范围内管制员”树中的一个分组（区域、进近、塔台……）。</summary>
public sealed class ControllerGroup : ObservableObject
{
    public ControllerGroup(string name)
    {
        Name = name;
        // 人数变了只刷新标题；别重建分组集合，那样树会把展开的分组收起来
        Stations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Header));
    }

    public string Name { get; }
    public ObservableCollection<ControllerStation> Stations { get; } = [];
    public string Header => Stations.Count > 0 ? $"{Name}（{Stations.Count}）" : Name;

    /// <summary>换成新的席位列表；没变就不动，免得树里的选中和滚动位置跟着跳。</summary>
    public void SetStations(IReadOnlyList<ControllerStation> stations)
    {
        if (Stations.SequenceEqual(stations))
            return;
        Stations.Clear();
        foreach (var station in stations)
            Stations.Add(station);
    }

    public static readonly string[] Names = ["区域", "进近/离场", "塔台", "地面", "机坪", "放行", "ATIS", "观察员"];

    /// <summary>按呼号后缀分组，后缀认不出再看 FSD 的席位类型。</summary>
    public static int IndexFor(ControllerInfo info)
    {
        var callsign = info.Callsign.ToUpperInvariant();
        if (callsign.EndsWith("_ATIS"))
            return 6;
        if (callsign.EndsWith("_CTR") || callsign.EndsWith("_FSS"))
            return 0;
        if (callsign.EndsWith("_APP") || callsign.EndsWith("_DEP"))
            return 1;
        if (callsign.EndsWith("_TWR"))
            return 2;
        if (callsign.EndsWith("_GND"))
            return 3;
        if (callsign.EndsWith("_RMP") || callsign.EndsWith("_RAMP"))
            return 4;
        if (callsign.EndsWith("_DEL"))
            return 5;
        if (callsign.EndsWith("_OBS"))
            return 7;
        return info.Facility switch
        {
            1 or 6 => 0,
            5 => 1,
            4 => 2,
            3 => 3,
            2 => 5,
            _ => 7
        };
    }
}

public sealed record ControllerStation(string Callsign, string Frequency, string RealName)
{
    public string Tip => RealName.Length > 0 ? $"{Callsign} · {RealName}" : Callsign;
}
