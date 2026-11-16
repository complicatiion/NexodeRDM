namespace Nexode.Core.Theming;

/// <summary>Sunrise/sunset (NOAA approximation, ±2 min) – computed locally, no network.</summary>
public static class SunTimes
{
    /// <summary>Sunrise and sunset in UTC; null for polar day/night.</summary>
    public static (DateTime Sunrise, DateTime Sunset)? Calculate(DateOnly date, double latitude, double longitude)
    {
        var rise = Event(date, latitude, longitude, true);
        var set = Event(date, latitude, longitude, false);
        return rise is null || set is null ? null : (rise.Value, set.Value);
    }

    private static DateTime? Event(DateOnly date, double lat, double lon, bool sunrise)
    {
        const double zenith = 90.833;
        var n = date.DayOfYear;
        var lngHour = lon / 15;
        var t = n + ((sunrise ? 6 : 18) - lngHour) / 24;
        var m = 0.9856 * t - 3.289;
        var l = Norm(m + 1.916 * Sin(m) + 0.020 * Sin(2 * m) + 282.634);
        var ra = Norm(Atan(0.91764 * Tan(l)));
        ra += Math.Floor(l / 90) * 90 - Math.Floor(ra / 90) * 90;
        ra /= 15;
        var sinDec = 0.39782 * Sin(l);
        var cosDec = Math.Cos(Math.Asin(sinDec));
        var cosH = (Cos(zenith) - sinDec * Sin(lat)) / (cosDec * Cos(lat));
        if (cosH is > 1 or < -1) return null;
        var h = sunrise ? 360 - Acos(cosH) : Acos(cosH);
        h /= 15;
        var localMean = h + ra - 0.06571 * t - 6.622;
        var ut = ((localMean - lngHour) % 24 + 24) % 24;
        return date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(ut);
    }

    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;
    private static double Sin(double d) => Math.Sin(Rad(d));
    private static double Cos(double d) => Math.Cos(Rad(d));
    private static double Tan(double d) => Math.Tan(Rad(d));
    private static double Atan(double x) => Deg(Math.Atan(x));
    private static double Acos(double x) => Deg(Math.Acos(x));
    private static double Norm(double d) => ((d % 360) + 360) % 360;
}

public enum ThemeMode
{
    /// <summary>Nexode's own violet-tinted dark theme (default; was called "Dark" up to 0.0.5).</summary>
    Nexode = 0,
    Light = 1,
    System = 2,
    Auto = 3,
    Amoled = 4,
    /// <summary>Neutral dark gray (was called "Shadow" up to 0.0.5); the dark side of System/Auto by default.</summary>
    Dark = 5,
    Mica = 6,
    Lightweight = 7,
    /// <summary>Medium dark gray.</summary>
    Shadow = 8
}

public enum AutoThemeSource
{
    FixedTimes = 0,
    Sun = 1
}

/// <summary>Decides light or dark for the "Auto" theme mode.</summary>
public static class ThemeSchedule
{
    public static bool IsLight(DateTime nowLocal, AutoThemeSource source, TimeOnly lightFrom, TimeOnly darkFrom, double? lat, double? lon)
    {
        if (source == AutoThemeSource.Sun && lat is { } la && lon is { } lo)
        {
            var sun = SunTimes.Calculate(DateOnly.FromDateTime(nowLocal), la, lo);
            if (sun is { } s)
            {
                var utc = nowLocal.ToUniversalTime();
                return utc >= s.Sunrise && utc < s.Sunset;
            }
        }

        var time = TimeOnly.FromDateTime(nowLocal);
        return lightFrom <= darkFrom ? time >= lightFrom && time < darkFrom : time >= lightFrom || time < darkFrom;
    }
}
