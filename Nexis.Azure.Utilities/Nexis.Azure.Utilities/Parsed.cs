using System.CommandLine.Parsing;
using System.Runtime.CompilerServices;

namespace Nexis.Azure.Utilities;




public static class Parsed
{
    public static Parsed<T> Create<T>(Func<string, T> parse, string? defaultValue = null)
    {
        return new Parsed<T>(parse, defaultValue);
    }

    public static ParseArgument<T> AsParseArgument<T>(this Func<string, T> parse)
    {
        return arg => parse(arg.GetValueOrDefault<string>());
    }

    public static DateTimeOffset ParseFutureDateTimeOffset(string value)
    {
        return ParseDateTimeOffset(value, future: true);
    }

    public static DateTimeOffset ParsePastDateTimeOffset(string value)
    {
        return ParseDateTimeOffset(value, future: false);
    }

    public static DateTimeOffset ParseDateTimeOffset(string value, bool future = true)
    {
        int sign = future ? 1 : -1;
        if (DateTime.TryParse(value, out var d)) return d.ToUniversalTime();
        if (DateTimeOffset.TryParse(value, out var dto)) return dto;
        if (TimeSpan.TryParse(value, out var ts)) return DateTimeOffset.UtcNow + (sign * ts);
        if (TimeSpanSetting.TryParseReadableTimeSpan(value, out ts)) return DateTimeOffset.UtcNow + (sign * ts);

        throw new FormatException($"Unable to parse '{value}' as DateTimeOffset or TimeSpan");
    }

    public static TimeSpan ParseTimeSpan(string value)
    {
        if (TimeSpan.TryParse(value, out var ts)) return ts;
        if (TimeSpanSetting.TryParseReadableTimeSpan(value, out ts)) return ts;

        throw new FormatException($"Unable to parse '{value}' as TimeSpan");
    }
}

public class Parsed<T>(Func<string, T> parse, string? text = null)
{
    public string Text = text!;

    private Optional<T>? _value;

    public T Value
    {
        get => (_value ??= parse(Text)).Value!;
        set => _value = value;
    }
}

