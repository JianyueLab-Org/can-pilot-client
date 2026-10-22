using CanClient.Core.Fsd;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanClient.ViewModels;

/// <summary>“连接到网络”对话框，对应 vPilot 的 ConnectForm。</summary>
public sealed partial class ConnectViewModel(IReadOnlyList<string> typeCodes) : ViewModelBase
{
    /// <summary>机型码补全，来自模型匹配数据库的 ICAO 机型表。</summary>
    public IReadOnlyList<string> TypeCodes { get; } = typeCodes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(Problem))]
    private string _callsign = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(Problem))]
    private string _typeCode = string.Empty;

    [ObservableProperty]
    private string _selcal = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(Problem))]
    private bool _isObserver;

    public string Problem => IsObserver ? "" : FsdProtocol.CallsignProblem(Callsign) ?? "";

    public bool IsValid => IsObserver || (Problem.Length == 0 && TypeCode.Trim().Length >= 2);
}
