using System.Globalization;
using KAONOMI.SearchEngine.Http;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>
/// The dates of Elasticsearch: kept as milliseconds since the epoch (UTC), parsed with the formats of a mapping
/// (<c>strict_date_optional_time||epoch_millis</c> by default), and the date math of the range queries
/// (<c>now-1d/d</c>, <c>2026-01-01||+1M</c>).
/// </summary>
public static class EsDates
{
    public const string DefaultFormat = "strict_date_optional_time||epoch_millis";

    /// <summary>The format given to a field detected from <c>yyyy/MM/dd</c> strings.</summary>
    public const string SlashFormat = "yyyy/MM/dd HH:mm:ss||yyyy/MM/dd||epoch_millis";

    /// <summary>
    /// The dynamic date detection of Elasticsearch: <c>strict_date_optional_time</c> with at least the month
    /// (<c>2015</c> alone stays a text), or <c>yyyy/MM/dd[ HH:mm:ss]</c>. Returns the format of the new field or null.
    /// </summary>
    public static string? Detect(string value)
    {
        if (value.Length >= 7 && value[4] == '-' && TryIsoDate(value, out _))
            return DefaultFormat;
        if (value.Length >= 10 && value[4] == '/' && (TrySlash(value, out _)))
            return SlashFormat;
        return null;
    }

    /// <summary>Parses <paramref name="value"/> with the formats of a field (<c>a||b</c>), or throws.</summary>
    public static long Parse(string value, string? format)
    {
        if (TryParse(value, format, out var millis))
            return millis;
        throw new EsException(400, "illegal_argument_exception",
            $"failed to parse date field [{value}] with format [{format ?? DefaultFormat}]")
        {
            CausedBy = new EsException(400, "date_time_parse_exception", "Failed to parse with all enclosed parsers")
        };
    }

    public static bool TryParse(string value, string? format, out long millis)
    {
        foreach (var raw in (format ?? DefaultFormat).Split("||"))
        {
            var f = raw.Trim();
            switch (f)
            {
                case "strict_date_optional_time" or "date_optional_time" or "strict_date_optional_time_nanos"
                    or "strict_date_time" or "date_time" or "strict_date" or "date" or "strict_date_hour_minute_second"
                    or "strict_date_hour_minute_second_millis" or "strict_date_hour_minute_second_fraction":
                    if (TryIsoDate(value, out millis))
                        return true;
                    break;
                case "epoch_millis":
                    if (TryEpoch(value, 1, out millis))
                        return true;
                    break;
                case "epoch_second":
                    if (TryEpoch(value, 1000, out millis))
                        return true;
                    break;
                case "yyyy/MM/dd HH:mm:ss" or "yyyy/MM/dd":
                    if (TrySlash(value, out millis))
                        return true;
                    break;
                case "basic_date" or "yyyyMMdd":
                    if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var basic))
                    {
                        millis = ToMillis(new DateTimeOffset(basic, TimeSpan.Zero));
                        return true;
                    }
                    break;
                default:
                    if (TryJavaPattern(value, f, out millis))
                        return true;
                    break;
            }
        }
        millis = 0;
        return false;
    }

    /// <summary>
    /// <c>yyyy[-MM[-dd]][Thh[:mm[:ss[.fraction]]][Z|±hh[:mm]]]</c> (the strict ISO 8601 of Elasticsearch: two digits for
    /// the month, the day, the hours…, up to nine digits of fraction, a zone only after a time). No zone: UTC.
    /// </summary>
    public static bool TryIsoDate(string s, out long millis)
    {
        millis = 0;
        var i = 0;
        if (!Digits(s, ref i, 4, out var year))
            return false;
        int month = 1, day = 1, hour = 0, minute = 0, second = 0;
        long ticksFraction = 0;
        var offset = TimeSpan.Zero;
        if (i < s.Length && s[i] == '-')
        {
            i++;
            if (!Digits(s, ref i, 2, out month))
                return false;
            if (i < s.Length && s[i] == '-')
            {
                i++;
                if (!Digits(s, ref i, 2, out day))
                    return false;
            }
        }
        if (i < s.Length && s[i] == 'T')
        {
            i++;
            if (!Digits(s, ref i, 2, out hour))
                return false;
            if (i < s.Length && s[i] == ':')
            {
                i++;
                if (!Digits(s, ref i, 2, out minute))
                    return false;
                if (i < s.Length && s[i] == ':')
                {
                    i++;
                    if (!Digits(s, ref i, 2, out second))
                        return false;
                    if (i < s.Length && (s[i] == '.' || s[i] == ','))
                    {
                        i++;
                        var start = i;
                        while (i < s.Length && char.IsAsciiDigit(s[i]))
                            i++;
                        var count = i - start;
                        if (count is 0 or > 9)
                            return false;
                        var digits = s.Substring(start, Math.Min(count, 7)).PadRight(7, '0');
                        ticksFraction = long.Parse(digits, CultureInfo.InvariantCulture);
                    }
                }
            }
            if (i < s.Length)
            {
                if (s[i] == 'Z')
                    i++;
                else if (s[i] is '+' or '-')
                {
                    var sign = s[i] == '-' ? -1 : 1;
                    i++;
                    if (!Digits(s, ref i, 2, out var oh))
                        return false;
                    var om = 0;
                    if (i < s.Length && s[i] == ':')
                        i++;
                    if (i < s.Length && !Digits(s, ref i, 2, out om))
                        return false;
                    offset = sign * new TimeSpan(oh, om, 0);
                }
            }
        }
        if (i != s.Length)
            return false;
        try
        {
            var date = new DateTimeOffset(year, month, day, hour, minute, second, offset).AddTicks(ticksFraction);
            millis = ToMillis(date);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TrySlash(string s, out long millis)
    {
        millis = 0;
        var formats = new[] { "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd" };
        if (!DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return false;
        millis = ToMillis(new DateTimeOffset(date, TimeSpan.Zero));
        return true;
    }

    private static bool TryEpoch(string s, long factor, out long millis)
    {
        millis = 0;
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            millis = n * factor;
            return true;
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            millis = (long)Math.Floor(d * factor);
            return true;
        }
        return false;
    }

    /// <summary>A Java pattern (<c>dd/MM/yyyy HH:mm</c>…) read with the .NET equivalent.</summary>
    private static bool TryJavaPattern(string s, string pattern, out long millis)
    {
        millis = 0;
        var net = pattern.Replace("SSSSSSSSS", "fffffff").Replace("SSSSSS", "ffffff").Replace("SSS", "fff")
            .Replace("XXX", "zzz").Replace("xxx", "zzz").Replace("Z", "zzz").Replace("'T'", "'T'").Replace("uuuu", "yyyy").Replace("a", "tt");
        if (!DateTimeOffset.TryParseExact(s, net, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return false;
        millis = ToMillis(date);
        return true;
    }

    private static bool Digits(string s, ref int i, int count, out int value)
    {
        value = 0;
        if (i + count > s.Length)
            return false;
        for (var k = 0; k < count; k++)
        {
            var c = s[i + k];
            if (!char.IsAsciiDigit(c))
                return false;
            value = value * 10 + (c - '0');
        }
        i += count;
        return true;
    }

    /// <summary>Milliseconds since the epoch, the sub-millisecond part dropped (rounded down, as Elasticsearch).</summary>
    public static long ToMillis(DateTimeOffset date)
    {
        var ticks = date.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var millis = ticks / TimeSpan.TicksPerMillisecond;
        return ticks % TimeSpan.TicksPerMillisecond < 0 ? millis - 1 : millis;
    }

    public static DateTimeOffset FromMillis(long millis) => DateTimeOffset.UnixEpoch.AddMilliseconds(millis);

    /// <summary>
    /// A bound of a date range: a date of the field (or <paramref name="format"/>), a number of milliseconds, or date
    /// math (<c>now</c>, <c>…||</c> then <c>+1d</c>, <c>-2h</c>, <c>/d</c>…). <paramref name="roundUp"/> for
    /// <c>gt</c> / <c>lte</c>: a rounding goes to the end of the unit, as Elasticsearch does.
    /// </summary>
    public static long ParseBound(string value, string? format, bool roundUp, TimeZoneInfo? zone = null)
    {
        string anchor, math;
        if (value.StartsWith("now", StringComparison.Ordinal))
        {
            anchor = "now";
            math = value[3..];
        }
        else
        {
            var bars = value.IndexOf("||", StringComparison.Ordinal);
            anchor = bars < 0 ? value : value[..bars];
            math = bars < 0 ? string.Empty : value[(bars + 2)..];
        }
        DateTimeOffset date;
        if (anchor == "now")
            date = DateTimeOffset.UtcNow;
        else
        {
            var millis = zone is null || HasZone(anchor) ? Parse(anchor, format) : ParseInZone(anchor, format, zone);
            date = FromMillis(millis);
        }
        if (math.Length == 0)
            return ToMillis(date);
        var local = zone is null ? date : TimeZoneInfo.ConvertTime(date, zone);
        var i = 0;
        while (i < math.Length)
        {
            var op = math[i++];
            if (op is '+' or '-')
            {
                var start = i;
                while (i < math.Length && char.IsAsciiDigit(math[i]))
                    i++;
                var n = i > start ? int.Parse(math[start..i], CultureInfo.InvariantCulture) : 1;
                if (i >= math.Length)
                    throw EsException.Parsing($"truncated date math [{value}]");
                var unit = math[i++];
                if (op == '-')
                    n = -n;
                local = Add(local, unit, n, value);
            }
            else if (op == '/')
            {
                if (i >= math.Length)
                    throw EsException.Parsing($"truncated date math [{value}]");
                var unit = math[i++];
                var floor = Floor(local, unit, value);
                local = roundUp ? Add(floor, unit, 1, value).AddMilliseconds(-1) : floor;
            }
            else
                throw EsException.Parsing($"operator not supported for date math [{value}]");
        }
        return ToMillis(local);
    }

    private static bool HasZone(string s) => s.EndsWith('Z') || (s.Length > 6 && s[^6] is '+' or '-' && s.Contains('T'));

    private static long ParseInZone(string anchor, string? format, TimeZoneInfo zone)
    {
        var utc = FromMillis(Parse(anchor, format));
        var shift = zone.GetUtcOffset(utc.UtcDateTime);
        return ToMillis(utc - shift);
    }

    private static DateTimeOffset Add(DateTimeOffset date, char unit, int n, string value) => unit switch
    {
        'y' => date.AddYears(n),
        'M' => date.AddMonths(n),
        'w' => date.AddDays(7 * n),
        'd' => date.AddDays(n),
        'h' or 'H' => date.AddHours(n),
        'm' => date.AddMinutes(n),
        's' => date.AddSeconds(n),
        _ => throw EsException.Parsing($"unit [{unit}] not supported for date math [{value}]")
    };

    private static DateTimeOffset Floor(DateTimeOffset d, char unit, string value) => unit switch
    {
        'y' => new DateTimeOffset(d.Year, 1, 1, 0, 0, 0, d.Offset),
        'M' => new DateTimeOffset(d.Year, d.Month, 1, 0, 0, 0, d.Offset),
        'w' => new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, d.Offset).AddDays(-(((int)d.DayOfWeek + 6) % 7)),
        'd' => new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, d.Offset),
        'h' or 'H' => new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, 0, 0, d.Offset),
        'm' => new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Offset),
        's' => new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second, d.Offset),
        _ => throw EsException.Parsing($"unit [{unit}] not supported for date math [{value}]")
    };
}
