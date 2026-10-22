using CommunityToolkit.Mvvm.ComponentModel;

namespace CanClient.ViewModels;

/// <summary>单个无线电（COM1/COM2）的显示状态。</summary>
public sealed partial class RadioViewModel(string name, string frequency) : ViewModelBase
{
    public string Name { get; } = name;

    [ObservableProperty]
    private string _frequency = frequency;

    [ObservableProperty]
    private bool _isTransmitting;

    [ObservableProperty]
    private bool _isReceiving;

    [ObservableProperty]
    private bool _isTransmitEnabled = true;
}
