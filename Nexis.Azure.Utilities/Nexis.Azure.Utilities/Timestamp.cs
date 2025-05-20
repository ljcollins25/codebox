namespace Nexis.Azure.Utilities;

public record struct Timestamp(DateTime Value) : IComparable<Timestamp>
{
    public static readonly Timestamp Zero = new(default);
    public static implicit operator Timestamp(DateTime d) => new(d.ToUniversalTime());
    public static implicit operator Timestamp(DateTimeOffset d) => new(d.UtcDateTime);
    public static implicit operator Timestamp?(string? d) => d == null ? null : new(DateTime.ParseExact(d, "o", null));
    public static implicit operator string(Timestamp d) => d.ToString();

    public static Timestamp Now => DateTime.UtcNow;

    public static bool operator <(Timestamp left, Timestamp right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator <=(Timestamp left, Timestamp right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >(Timestamp left, Timestamp right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator >=(Timestamp left, Timestamp right)
    {
        return left.CompareTo(right) >= 0;
    }

    public int CompareTo(Timestamp other)
    {
        return Value.CompareTo(other.Value);
    }

    public override string ToString()
    {
        return Value.ToString("o");
    }
}
