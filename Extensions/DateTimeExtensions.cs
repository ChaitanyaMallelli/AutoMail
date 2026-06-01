namespace JobAutomation.Extensions;

public static class DateTimeExtensions
{
    private static readonly TimeZoneInfo Ist = GetIst();

    private static TimeZoneInfo GetIst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); } catch { }
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); } catch { }
        return TimeZoneInfo.Utc;
    }

    public static DateTime ToIst(this DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, Ist);
    }

    public static DateTime? ToIst(this DateTime? dt) => dt.HasValue ? dt.Value.ToIst() : null;

    // Format non-nullable DateTime in IST
    public static string ToIstString(this DateTime dt, string format = "MMM dd, HH:mm")
        => dt.ToIst().ToString(format);

    // Format nullable DateTime in IST — returns empty string when null
    public static string ToIstString(this DateTime? dt, string format = "MMM dd, HH:mm", bool showLabel = false)
    {
        if (!dt.HasValue) return string.Empty;
        var s = dt.Value.ToIst().ToString(format);
        return showLabel ? s + " IST" : s;
    }
}
