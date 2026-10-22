using System.Text.Json.Nodes;
using CanClient.Core.Fsd;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanClient.ViewModels;

/// <summary>飞行计划表单。内容存在设置里，下次打开还在。</summary>
public sealed partial class FlightPlanViewModel : ViewModelBase
{
    public IReadOnlyList<string> FlightRules { get; } = ["IFR 仪表飞行", "VFR 目视飞行", "Y（先仪表后目视）", "Z（先目视后仪表）"];
    private static readonly string[] RuleCodes = ["I", "V", "Y", "Z"];

    [ObservableProperty] private int _flightRuleIndex;
    [ObservableProperty] private string _departure = "";
    [ObservableProperty] private string _arrival = "";
    [ObservableProperty] private string _alternate = "";
    [ObservableProperty] private string _aircraftType = "";
    [ObservableProperty] private double _cruiseSpeed;
    [ObservableProperty] private string _cruiseAltitude = "";
    [ObservableProperty] private string _departureTime = "";
    [ObservableProperty] private string _enrouteTime = "";
    [ObservableProperty] private string _fuelTime = "";
    [ObservableProperty] private string _route = "";
    [ObservableProperty] private string _remarks = "";

    public static FlightPlanViewModel FromJson(JsonObject json, string aircraft)
    {
        string S(string key) => json[key]?.ToString() ?? "";
        var vm = new FlightPlanViewModel
        {
            FlightRuleIndex = Math.Max(0, Array.IndexOf(RuleCodes, S("rules"))),
            Departure = S("departure"),
            Arrival = S("arrival"),
            Alternate = S("alternate"),
            AircraftType = S("aircraft") is { Length: > 0 } a ? a : aircraft,
            CruiseSpeed = double.TryParse(S("cruise_speed"), out var speed) ? speed : 0,
            CruiseAltitude = S("cruise_altitude"),
            DepartureTime = S("departure_time"),
            EnrouteTime = Hhmm(S("enroute_hours"), S("enroute_minutes")),
            FuelTime = Hhmm(S("fuel_hours"), S("fuel_minutes")),
            Route = S("route"),
            Remarks = S("remarks")
        };
        return vm;
    }

    private static string Hhmm(string hours, string minutes) =>
        hours.Length == 0 && minutes.Length == 0 ? "" : $"{(int.TryParse(hours, out var h) ? h : 0):D2}{(int.TryParse(minutes, out var m) ? m : 0):D2}";

    private static (string Hours, string Minutes) Split(string hhmm)
    {
        var digits = new string(hhmm.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
            return ("0", "0");
        digits = digits.PadLeft(4, '0');
        return (int.Parse(digits[..^2]).ToString(), int.Parse(digits[^2..]).ToString());
    }

    public FlightPlan ToPlan()
    {
        var (eh, em) = Split(EnrouteTime);
        var (fh, fm) = Split(FuelTime);
        return new FlightPlan
        {
            Rules = RuleCodes[Math.Clamp(FlightRuleIndex, 0, RuleCodes.Length - 1)],
            Aircraft = AircraftType.Trim().ToUpperInvariant(),
            CruiseSpeed = CruiseSpeed > 0 ? ((int)CruiseSpeed).ToString() : "",
            Departure = Departure.Trim(),
            DepartureTime = DepartureTime.Trim(),
            CruiseAltitude = CruiseAltitude.Trim().ToUpperInvariant(),
            Arrival = Arrival.Trim(),
            EnrouteHours = eh,
            EnrouteMinutes = em,
            FuelHours = fh,
            FuelMinutes = fm,
            Alternate = Alternate.Trim(),
            Remarks = Remarks.Trim(),
            Route = Route.Trim()
        };
    }

    public JsonObject ToJson()
    {
        var plan = ToPlan();
        return new JsonObject
        {
            ["rules"] = plan.Rules,
            ["aircraft"] = plan.Aircraft,
            ["cruise_speed"] = plan.CruiseSpeed,
            ["departure"] = plan.Departure,
            ["departure_time"] = plan.DepartureTime,
            ["cruise_altitude"] = plan.CruiseAltitude,
            ["arrival"] = plan.Arrival,
            ["enroute_hours"] = plan.EnrouteHours,
            ["enroute_minutes"] = plan.EnrouteMinutes,
            ["fuel_hours"] = plan.FuelHours,
            ["fuel_minutes"] = plan.FuelMinutes,
            ["alternate"] = plan.Alternate,
            ["remarks"] = plan.Remarks,
            ["route"] = plan.Route
        };
    }
}
