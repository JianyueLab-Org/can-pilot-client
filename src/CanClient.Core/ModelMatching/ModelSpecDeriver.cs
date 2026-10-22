using System.Text.RegularExpressions;

namespace CanClient.Core.ModelMatching;

/// <summary>
/// 不在 ModelMatchingData 里的模型，从它自己的 aircraft.cfg 推出机型和航司。vPilot Type_0200008D。
///
///   机型    icao_type_designator 在 ICAO 机型表里就用它；否则拿 atc_model / ui_type 查别名表。
///   航司    atc_parking_codes 里每个三字母代码各出一条（CCA,CSN → 两条）；
///           四字母以 X / C 结尾的（CCAX、CSNC：货运/包机停机位代码）去掉最后一位。
///   通用    机型不在"有涂装机型"表里、且停机位代码都不是航司码时，再出一条不带航司的——
///           这类模型可以给任何航司当替身。
///
/// 和 vPilot 不同的一处：atc_parking_codes 为空时退到 icao_airline。MSFS 自带涂装和不少
/// 第三方涂装只写 icao_airline，按 vPilot 的规则它们全都推不出航司。
/// </summary>
public static class ModelSpecDeriver
{
    private static readonly Regex ThreeLetters = new("^[A-Z]{3}$", RegexOptions.Compiled);

    public static List<ModelSpec> Derive(ModelScanResultModel model, ModelMatchingData data)
    {
        var list = new List<ModelSpec>();
        var title = model.Title ?? "";
        if (title.Length == 0)
            return list;
        if (string.IsNullOrEmpty(model.AtcModel) && string.IsNullOrEmpty(model.IcaoTypeDesignator)
            && string.IsNullOrEmpty(model.UiType))
            return list;

        var typeCode = (model.AtcModel ?? "").ToUpperInvariant();
        var uiType = (model.UiType ?? "").ToUpperInvariant();
        var alias = data.TypeCodeAliases.FirstOrDefault(a =>
            a.Alias.ToUpperInvariant() == typeCode || a.Alias.ToUpperInvariant() == uiType);
        if (alias is not null)
            typeCode = alias.TypeCode.ToUpperInvariant();

        var icao = (model.IcaoTypeDesignator ?? "").Trim().ToUpperInvariant();
        // 真实安装里见过 "A359 ULR" 这种带后缀的写法，取第一段
        var space = icao.IndexOf(' ');
        if (space > 0)
            icao = icao[..space];
        if (icao.Length > 0 && data.TypeCodes.Contains(icao))
            typeCode = icao;

        if (!data.TypeCodes.Contains(typeCode))
            return list;

        var parkingCodes = model.AtcParkingCodes;
        if (string.IsNullOrWhiteSpace(parkingCodes))
        {
            var airline = (model.IcaoAirline ?? "").Trim().ToUpperInvariant();
            if (ThreeLetters.IsMatch(airline) && data.AirlineCodes.Contains(airline))
                parkingCodes = airline;
        }

        if (string.IsNullOrWhiteSpace(parkingCodes))
        {
            if (data.LiveriedTypeCodes.Contains(typeCode))
                return list;
            list.Add(new ModelSpec(title, typeCode));
            return list;
        }

        var codes = parkingCodes.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(c =>
            {
                var code = c.ToUpperInvariant().Trim();
                if (code.Length == 4 && (code.EndsWith('X') || code.EndsWith('C')))
                    code = code[..3];
                return code;
            })
            .ToList();

        foreach (var code in codes)
        {
            if (ThreeLetters.IsMatch(code))
                list.Add(new ModelSpec(title, typeCode, code));
        }

        if (!data.LiveriedTypeCodes.Contains(typeCode) && !codes.Any(c => data.AirlineCodes.Contains(c)))
            list.Add(new ModelSpec(title, typeCode));
        return list;
    }
}
