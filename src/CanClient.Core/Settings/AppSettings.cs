using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CanClient.Core.Fsd;
using CanClient.Core.ModelMatching;
using CanClient.Core.Ptt;
using CanClient.Core.Util;

namespace CanClient.Core.Settings;

/// <summary>每个模拟器各一份的模型匹配设置，vPilot 的 ModelMatchingConfig。</summary>
public sealed class ModelMatchingSettings
{
    [JsonPropertyName("default_model")] public string DefaultModel { get; set; } = "";
    /// <summary>自定义规则集（.vmr），顺序就是优先级。</summary>
    [JsonPropertyName("custom_rule_sets")] public List<string> CustomRuleSets { get; set; } = new();
    /// <summary>自动发现过、又被用户从列表里移除的规则集，下次扫描不再自动加回来。</summary>
    [JsonPropertyName("dismissed_rule_sets")] public List<string> DismissedRuleSets { get; set; } = new();
    /// <summary>模拟器拒绝生成过的模型。重新扫描后清空，和 vPilot 一致。</summary>
    [JsonPropertyName("erroneous_models")] public List<string> ErroneousModels { get; set; } = new();
}

/// <summary>
/// 配置，存成程序目录下的 msfs_settings.json，键名和 Python 版一致，老配置直接读得进来。
///
    /// **密码默认不落盘，主动记住时仅保存到操作系统凭据库。** 这里的密码就是成员的网站密码（can-api 的 VerifyNetworkCredential 两个列都认），
/// 这个文件泄露一次，泄露的是整个账号。所以 remember_password 默认关，<see cref="Save"/> 只在它为
/// true 时写密码。老配置里已经有明文密码（没有 remember_password 这个键）的，读进内存照常能连，然后
/// 立刻把文件重写掉并置上 <see cref="PasswordMigrated"/> 让界面说一声。
/// </summary>
public sealed class AppSettings
{
    public const string MumbleHost = "audio.ceruleanavi.net";
    public const int MumblePort = 64738;
    public const string FsdHost = "fsd.ceruleanavi.net";
    public const int FsdPort = 6809;

    // 旧域名。只认这几个旧值：用户自己填的别的地址是有意为之，不能动。
    private static readonly HashSet<string> OldMumbleHosts = new(StringComparer.OrdinalIgnoreCase) { "hjdczy.top", "audio.airwaysn.org" };
    private static readonly HashSet<string> OldFsdHosts = new(StringComparer.OrdinalIgnoreCase) { "fsd.airwaysn.org" };

    private static readonly Logger Log = Util.Log.For("settings");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [JsonIgnore] public string Path { get; private set; } = AppPaths.SettingsFile;

    /// <summary>这一次启动是不是刚把老配置里的明文密码清掉了。不落盘。</summary>
    [JsonIgnore] public bool PasswordMigrated { get; private set; }

    // —— 账号 ——
    [JsonPropertyName("cid")] public string Cid { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("remember_password")] public bool RememberPassword { get; set; }
    [JsonPropertyName("real_name")] public string RealName { get; set; } = "";
    [JsonPropertyName("callsign")] public string Callsign { get; set; } = "";
    [JsonPropertyName("aircraft")] public string Aircraft { get; set; } = "";
    [JsonPropertyName("selcal")] public string Selcal { get; set; } = "";
    [JsonPropertyName("rating")] public int Rating { get; set; } = FsdProtocol.RatingObserver;

    // —— 服务器 ——
    [JsonPropertyName("mumble_host")] public string MumbleHostName { get; set; } = MumbleHost;
    [JsonPropertyName("mumble_port")] public int MumblePortNumber { get; set; } = MumblePort;
    [JsonPropertyName("fsd_host")] public string FsdHostName { get; set; } = FsdHost;
    [JsonPropertyName("fsd_port")] public int FsdPortNumber { get; set; } = FsdPort;
    [JsonPropertyName("connect_fsd")] public bool ConnectFsd { get; set; } = true;
    [JsonPropertyName("connect_voice")] public bool ConnectVoice { get; set; } = true;
    // 模拟器接入选择和 X-Plane 原生交通插件。
    [JsonPropertyName("simulator_connection")] public string SimulatorConnection { get; set; } = "Auto";
    [JsonPropertyName("show_all_simulators")] public bool ShowAllSimulators { get; set; }
    [JsonPropertyName("msfs2020_packages_path")] public string Msfs2020PackagesPath { get; set; } = "";
    [JsonPropertyName("msfs2024_packages_path")] public string Msfs2024PackagesPath { get; set; } = "";
    /// <summary>手动指定的 FSX / P3D 安装目录（模拟器名 → 目录），留空就读注册表。</summary>
    [JsonPropertyName("simulator_paths")] public Dictionary<string, string> SimulatorPaths { get; set; } = new();
    [JsonPropertyName("xplane_path")] public string XPlanePath { get; set; } = "";
    [JsonPropertyName("csl_path")] public string CslPath { get; set; } = "";
    [JsonPropertyName("xplane_host")] public string XPlaneHost { get; set; } = "";
    [JsonPropertyName("xplane_port")] public int XPlanePort { get; set; } = 49000;

    // —— 观察员（双人机组的右座：只连语音，不连 FSD） ——
    [JsonPropertyName("observer_mode")] public bool ObserverMode { get; set; }
    [JsonPropertyName("observer_frequency")] public string ObserverFrequency { get; set; } = "";

    // —— 音频 ——
    /// <summary>设备名。Python 版存的是 PyAudio 的序号，换了库就对不上了，所以改存名字。</summary>
    [JsonPropertyName("input_device")] public string InputDevice { get; set; } = "";
    [JsonPropertyName("output_device")] public string OutputDevice { get; set; } = "";
    /// <summary>麦克风增益（dB），设置里的"麦克风音量"滑块。</summary>
    [JsonPropertyName("mic_gain_db")] public double MicGainDb { get; set; }
    /// <summary>输出音量（dB），设置里的"输出音量"滑块。</summary>
    [JsonPropertyName("output_gain_db")] public double OutputGainDb { get; set; }
    /// <summary>Python 版的校准记录（设备名 → {gain_db, denoise, ...}）。第一次用某个设备时拿来当初值。</summary>
    [JsonPropertyName("mic_calibration")] public JsonObject MicCalibration { get; set; } = new();
    [JsonPropertyName("mic_denoise")] public bool MicDenoise { get; set; } = true;
    /// <summary>距离音频范围：按 data.json 的坐标把超出范围的人静音。</summary>
    [JsonPropertyName("range_audio")] public bool RangeAudio { get; set; } = true;
    /// <summary>Python 版的扬声器音量百分比，迁移到 output_gain_db 后不再写。</summary>
    [JsonPropertyName("speaker_volume")] public int? LegacySpeakerVolume { get; set; }
    [JsonPropertyName("message_sound")] public bool MessageSound { get; set; } = true;
    [JsonPropertyName("message_sound_all")] public bool MessageSoundAll { get; set; }
    [JsonPropertyName("message_sound_volume")] public int MessageSoundVolume { get; set; } = 100;

    // —— PTT ——
    [JsonPropertyName("ptt_bindings")] public List<PttBinding>? PttBindings { get; set; }
    [JsonPropertyName("ptt_key")] public string? LegacyPttKey { get; set; }
    [JsonPropertyName("joystick_ptt")] public JsonNode? LegacyJoystickPtt { get; set; }

    // —— 他机与模型匹配 ——
    [JsonPropertyName("render_traffic")] public bool RenderTraffic { get; set; } = true;
    [JsonPropertyName("traffic_range_nm")] public int TrafficRangeNm { get; set; } = 60;
    [JsonPropertyName("max_aircraft")] public int MaxAircraft { get; set; } = 40;
    /// <summary>手动指定的 MSFS 包目录（含 Official 和 Community 的那一层）。留空就读 UserCfg.opt。</summary>
    [JsonPropertyName("packages_path")] public string PackagesPath { get; set; } = "";
    /// <summary>Python 版的包目录列表，只用来迁移成 packages_path。</summary>
    [JsonPropertyName("package_roots")] public List<string>? LegacyPackageRoots { get; set; }
    /// <summary>没连模拟器时，模型匹配设置按哪个模拟器显示。</summary>
    [JsonPropertyName("simulator")] public SimulatorType LastSimulator { get; set; } = SimulatorType.None;
    [JsonPropertyName("model_matching")] public Dictionary<string, ModelMatchingSettings> ModelMatching { get; set; } = new();

    // —— 通知 ——
    [JsonPropertyName("flash_private_message")] public bool FlashOnPrivateMessage { get; set; } = true;
    [JsonPropertyName("flash_radio_message")] public bool FlashOnRadioMessage { get; set; }
    [JsonPropertyName("flash_selcal")] public bool FlashOnSelcal { get; set; } = true;
    [JsonPropertyName("flash_disconnected")] public bool FlashOnDisconnect { get; set; } = true;

    // —— 字体 ——
    [JsonPropertyName("controller_list_font")] public string ControllerListFont { get; set; } = "";
    [JsonPropertyName("controller_list_font_size")] public double ControllerListFontSize { get; set; } = 12;
    [JsonPropertyName("text_messages_font")] public string TextMessagesFont { get; set; } = "";
    [JsonPropertyName("text_messages_font_size")] public double TextMessagesFontSize { get; set; } = 12;

    // —— 杂项 ——
    [JsonPropertyName("auto_squawk_mode_c")] public bool AutoSquawkModeC { get; set; } = true;
    [JsonPropertyName("keep_window_visible")] public bool KeepWindowVisible { get; set; }
    [JsonPropertyName("debug")] public bool Debug { get; set; }
    [JsonPropertyName("flight_plan")] public JsonObject FlightPlan { get; set; } = new();

    // —— 更新 ——
    [JsonPropertyName("update_check")] public bool UpdateCheck { get; set; } = true;
    [JsonPropertyName("update_url")] public string UpdateUrl { get; set; } = "";

    /// <summary>老版本写进来、这个版本不认识的键，原样保留。</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public ModelMatchingSettings MatchingFor(SimulatorType simulator)
    {
        var key = simulator.ToString();
        if (!ModelMatching.TryGetValue(key, out var settings))
            ModelMatching[key] = settings = new ModelMatchingSettings();
        return settings;
    }

    /// <summary>设置里给这个模拟器手动指定的模型目录：MSFS 是包目录，FSX / P3D 是安装目录。没指定返回空串。</summary>
    public string SimulatorPathFor(SimulatorType simulator) => simulator switch
    {
        SimulatorType.Msfs => Msfs2020PackagesPath,
        SimulatorType.Msfs2024 => Msfs2024PackagesPath,
        _ => SimulatorPaths.GetValueOrDefault(simulator.ToString(), "")
    };

    public void SetSimulatorPath(SimulatorType simulator, string path)
    {
        path = (path ?? "").Trim();
        switch (simulator)
        {
            case SimulatorType.Msfs: Msfs2020PackagesPath = path; break;
            case SimulatorType.Msfs2024: Msfs2024PackagesPath = path; break;
            default:
                if (path.Length == 0) SimulatorPaths.Remove(simulator.ToString());
                else SimulatorPaths[simulator.ToString()] = path;
                break;
        }
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        AppSettings settings;
        JsonObject? raw = null;
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                raw = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonObject;
                settings = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions) ?? new AppSettings();
                Log.Info($"read the settings from {path}");
            }
            catch (Exception e)
            {
                Log.Warning($"could not read the settings, using the defaults: {e.Message}");
                settings = new AppSettings();
            }
        }
        else
            settings = new AppSettings();

        settings.Path = path;
        Privacy.Protect(settings.Cid, settings.Password, settings.RealName, settings.Callsign);
        settings.Migrate(raw);
        if (settings.RememberPassword)
        {
            try
            {
                var stored = CredentialStore.Read();
                if (stored is null) settings.RememberPassword = false;
                else settings.Password = stored;
            }
            catch { settings.RememberPassword = false; Log.Warning("系统凭据库不可用，请在连接前重新输入密码。"); }
        }
        Privacy.Protect(settings.Cid, settings.Password, settings.RealName, settings.Callsign);
        return settings;
    }

    private void Migrate(JsonObject? raw)
    {
        if (OldMumbleHosts.Contains((MumbleHostName ?? "").Trim()))
        {
            Log.Info($"the voice server was renamed, using {MumbleHost} instead of {MumbleHostName}");
            MumbleHostName = MumbleHost;
        }
        if (OldFsdHosts.Contains((FsdHostName ?? "").Trim()))
        {
            Log.Info($"the FSD server was renamed, using {FsdHost} instead of {FsdHostName}");
            FsdHostName = FsdHost;
        }
        if (string.IsNullOrWhiteSpace(MumbleHostName))
            MumbleHostName = MumbleHost;
        if (string.IsNullOrWhiteSpace(FsdHostName))
            FsdHostName = FsdHost;
        if (MumblePortNumber is <= 0 or > 65535)
            MumblePortNumber = MumblePort;
        if (FsdPortNumber is <= 0 or > 65535)
            FsdPortNumber = FsdPort;

        MessageSoundVolume = Math.Clamp(MessageSoundVolume, 0, 200);
        MicGainDb = Math.Clamp(MicGainDb, -60, 18);
        OutputGainDb = Math.Clamp(OutputGainDb, -18, 18);
        TrafficRangeNm = Math.Clamp(TrafficRangeNm, 1, 200);
        MaxAircraft = Math.Clamp(MaxAircraft, 0, 200);

        if (LegacySpeakerVolume is { } percent && raw?["output_gain_db"] is null)
        {
            // Python 版是 0-200 的百分比
            var clamped = Math.Clamp(percent, 1, 200);
            OutputGainDb = Math.Clamp(Math.Round(20.0 * Math.Log10(clamped / 100.0)), -18, 18);
        }
        LegacySpeakerVolume = null;

        if (string.IsNullOrWhiteSpace(PackagesPath) && LegacyPackageRoots is { Count: > 0 } roots)
            PackagesPath = roots.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? "";
        LegacyPackageRoots = null;

        if (PttBindings is null)
        {
            // 老配置里没有 ptt_bindings：拿 ptt_key + joystick_ptt 升上来，否则升级之后 PTT 悄悄失效
            var bindings = new List<PttBinding>();
            var key = string.IsNullOrEmpty(LegacyPttKey) && raw is null ? "`" : LegacyPttKey;
            var vk = KeyNames.FromPynput((key ?? "").ToLowerInvariant());
            if (vk > 0)
                bindings.Add(PttBinding.Keyboard(vk));
            if (LegacyJoystickPtt is JsonValue joystick && joystick.TryGetValue(out int button) && button >= 0)
                bindings.Add(PttBinding.Joystick(0, button, ""));
            PttBindings = bindings;
        }
        else
            PttBindings = PttBindings.Where(b => b is not null).Distinct().ToList();
        LegacyPttKey = null;
        LegacyJoystickPtt = null;

        // 老版本存下来的明文密码：本次还能用，但从文件里清掉
        if (raw is not null && !string.IsNullOrWhiteSpace(Password))
        {
            RememberPassword = false;
            PasswordMigrated = true;
            Log.Info($"an older version had stored the network password in cleartext in {Path}; it is being " +
                     "removed from the file. This session still has it.");
            Save();
        }
    }

    /// <summary>某个输入设备在 Python 版里校准出来的基准增益，没有返回 null。</summary>
    public double? CalibratedGainFor(string device)
    {
        if (string.IsNullOrEmpty(device) || MicCalibration[device] is not JsonObject entry)
            return null;
        if (entry["gain_db"] is JsonValue value && value.TryGetValue(out double gain) && !double.IsNaN(gain))
            return Math.Clamp(gain, -12, 20);
        return null;
    }

    public void Save(bool throwOnError = false)
    {
        Privacy.Protect(Cid, Password, RealName, Callsign);
        try
        {
            var node = JsonSerializer.SerializeToNode(this, JsonOptions)!.AsObject();
            node["password"] = "";
            try { CredentialStore.Save(RememberPassword ? Password : null); }
            catch
            {
                RememberPassword = false;
                node["remember_password"] = false;
                Log.Warning("系统凭据库不可用，密码仅保留在当前会话，未保存到配置文件。");
            }
            foreach (var legacy in new[] { "speaker_volume", "package_roots", "ptt_key", "joystick_ptt", "mic_volume" })
                node.Remove(legacy);
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temp = Path + ".tmp";
            PrivateFiles.Write(temp, node.ToJsonString(JsonOptions));
            File.Move(temp, Path, overwrite: true);
            PrivateFiles.Restrict(Path);
        }
        catch (Exception e)
        {
            if (throwOnError) throw;
            Log.Warning($"could not save the settings: {e.Message}");
        }
    }
}
