using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CanClient.Core;

namespace CanClient.Views;

/// <summary>航空器列表：每秒从 <see cref="ClientController.AircraftList"/> 取一遍。</summary>
public partial class AircraftWindow : Window
{
    private readonly ClientController? _controller;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<AircraftRow> _rows = [];

    public AircraftWindow()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
    }

    public AircraftWindow(ClientController controller) : this()
    {
        _controller = controller;
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        if (_controller is null)
            return;
        var rows = _controller.AircraftList();
        // 原地更新（按呼号替换、增删），不换数据源——换了的话用户点过的排序和选中行每秒都会丢
        var fresh = rows.ToDictionary(r => r.Callsign);
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (fresh.Remove(_rows[i].Callsign, out var row))
            {
                if (row != _rows[i])
                    _rows[i] = row;
            }
            else
                _rows.RemoveAt(i);
        }
        foreach (var row in rows.Where(r => fresh.ContainsKey(r.Callsign)))
            _rows.Add(row);
        var injected = rows.Count(r => r.SimStatus.StartsWith("已生成", StringComparison.Ordinal));
        CountText.Text = $"共 {rows.Count} 架，模拟器中 {injected} 架";
        var matcher = _controller.Models.Matcher;
        RulesText.Text = $"自定义规则 {matcher.CustomRules.Count:N0} 条 · 自动生成 {matcher.GeneratedRules.Count:N0} 条 · 已扫描模型 {matcher.InstalledModelCount:N0} 个";
    }

    private void OnDeleteAllClick(object? sender, RoutedEventArgs e)
    {
        _controller?.RemoveAllAircraft();
        Refresh();
    }

    private void OnReloadRulesClick(object? sender, RoutedEventArgs e)
    {
        _controller?.RematchAll();
        Refresh();
    }
}
