namespace CanClient.Core.Sim;

/// <summary>应答机模式。FSD 位置包第一个字符：S 待机、N 正常（模式 C）、Y 识别。</summary>
public enum TransponderMode
{
    Standby = 1,
    ModeC = 2
}

/// <summary>本机灯光，键名和 ACC 配置 JSON 一致。</summary>
public sealed record LightsState(
    bool Beacon = false,
    bool Landing = false,
    bool Taxi = false,
    bool Strobe = false,
    bool Nav = false,
    bool Logo = false);

/// <summary>
/// 从 SimConnect 取到的一份本机状态，FSD 位置包和语音频率都从这里来。
/// 字段含义和 Python 版 simlink.snapshot() 逐项一致：
///
///   姿态      抬头、右坡为正（SimVar 原生是低头、左坡为正，读的时候已经翻过来）
///   速度      世界坐标 东/上/北，米每秒
///   角速度    机体轴 抬头/右转/右坡为正，度每秒
///   高度      Altitude 是真高；NetworkAltitude/PressureAltitude 见 <see cref="Core.Altitude"/>
/// </summary>
public sealed record OwnAircraftState
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int Altitude { get; init; }
    public int NetworkAltitude { get; init; }
    public int PressureAltitude { get; init; }
    public int TemperatureError { get; init; }
    public int PressureDelta { get; init; }
    public int Agl { get; init; }
    public int Groundspeed { get; init; }
    public double Pitch { get; init; }
    public double Bank { get; init; }
    public double Heading { get; init; }
    public int Squawk { get; init; } = 2000;
    public TransponderMode TransponderMode { get; init; } = TransponderMode.ModeC;
    public bool TransponderStateKnown { get; init; }
    public double? Com1 { get; init; }
    public double? Com2 { get; init; }
    public bool Com1Power { get; init; } = true;
    public bool OnGround { get; init; }
    public double VelocityEast { get; init; }
    public double VelocityUp { get; init; }
    public double VelocityNorth { get; init; }
    public double PitchRate { get; init; }
    public double HeadingRate { get; init; }
    public double BankRate { get; init; }
    public double NoseWheel { get; init; }
    public bool GearDown { get; init; } = true;
    public double Flaps { get; init; }
    public bool Spoilers { get; init; }
    public bool EnginesOn { get; init; } = true;
    public LightsState Lights { get; init; } = new();
    public string Title { get; init; } = string.Empty;
}
