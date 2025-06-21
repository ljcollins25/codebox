namespace Nexis.Azure.Utilities;

/// <summary>
/// Converter type using for consistent roundtripable serialization and deserialization of
/// metadata values to/from string representation for <see cref="BlobDataEntry"/>
/// </summary>
public record struct MetadataValue(string StringValue)
{
    public static implicit operator string(MetadataValue value) => value.StringValue;
    public static implicit operator MetadataValue(string value) => new(value);

    public static implicit operator Timestamp?(MetadataValue value) => value.StringValue;
    public static implicit operator MetadataValue(Timestamp? value) => new(value ?? null!);

    public static implicit operator DateTimeOffset(MetadataValue value) => value.Parse<DateTimeOffset>();
    public static implicit operator MetadataValue(DateTimeOffset value) => new(value.ToString("o"));

    public static implicit operator TimeSpan(MetadataValue value) => value.Parse<TimeSpan>();
    public static implicit operator MetadataValue(TimeSpan value) => new(value.ToString("c"));

    public static implicit operator long(MetadataValue value) => value.Parse<long>();
    public static implicit operator MetadataValue(long value) => new(value.ToString());

    public static implicit operator bool(MetadataValue value) => value.Parse<bool>();
    public static implicit operator MetadataValue(bool value) => new(value.ToString());

    public static implicit operator Guid(MetadataValue value) => value.Parse<Guid>();
    public static implicit operator MetadataValue(Guid value) => new(value.ToString());

    public static implicit operator Uri(MetadataValue value) => new Uri(value.StringValue);
    public static implicit operator MetadataValue(Uri value) => new(value.ToString());

    private T Parse<T>()
        where T : IParsable<T>
    {
        if (StringValue == null)
        {
            return default!;
        }

        return T.Parse(StringValue, provider: null);
    }
}
