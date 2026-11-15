namespace ChipL.Core;

/// <summary>中文数值展示：给工程数值加上合适的单位与千分位，纯数字保持原样。</summary>
public static class ValueFormat
{
    public static string Pretty(FieldSpec spec, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (s is "-" or "None" or "null") return "";
        if (spec.IsBoolean)
        {
            return s switch
            {
                "1" => "是",
                "0" => "否",
                "-1" => "未知",
                _ => s
            };
        }
        if (spec.IsLarge) return s;

        if (!string.IsNullOrEmpty(spec.Unit) && double.TryParse(s,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var num))
        {
            string text = Math.Abs(num - Math.Round(num)) < 0.0001
                ? num.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                : num.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (num >= 10000 && spec.Unit is "MB" or "KB")
                text = num.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            return text + " " + spec.Unit;
        }
        return s;
    }

    public static string Launch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (s.Length == 6 && s.All(char.IsDigit))
            return $"{s[..4]} 年 {int.Parse(s[4..6])} 月";
        if (s.Length == 5 && s.All(char.IsDigit))
            return $"{s[..4]} 年 {s[4]} 季度";
        return s;
    }
}
