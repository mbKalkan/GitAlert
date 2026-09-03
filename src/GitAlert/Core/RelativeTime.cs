namespace GitAlert.Core;

/// <summary>Compact age stamps ("now", "2m", "3h", "5d") used on the alert cards.</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset value) => Format(value, DateTimeOffset.Now);

    public static string Format(DateTimeOffset value, DateTimeOffset now)
    {
        var delta = now - value;

        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta < TimeSpan.FromSeconds(45))
        {
            return "now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)}m";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{(int)delta.TotalHours}h";
        }

        if (delta < TimeSpan.FromDays(7))
        {
            return $"{(int)delta.TotalDays}d";
        }

        if (delta < TimeSpan.FromDays(365))
        {
            return $"{(int)(delta.TotalDays / 7)}w";
        }

        return $"{(int)(delta.TotalDays / 365)}y";
    }
}
