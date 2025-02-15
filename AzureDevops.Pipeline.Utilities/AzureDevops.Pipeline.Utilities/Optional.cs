namespace AzureDevops.Pipeline.Utilities;

public record struct Optional<T>(T? Value, bool HasValue = true)
{
    public static implicit operator Optional<T>(T? value) => new(value);

    public Optional<TResult> Then<TResult>(Func<T?, Optional<TResult>> select)
    {
        if (!HasValue) return default;

        return select(Value);
    }

    public Optional<TResult> ThenTry<TResult>(Func<T?, (bool, TResult)> select)
    {
        if (!HasValue) return default;

        return select(Value);
    }

    public static implicit operator Optional<T>((bool hasValue, T value) t) => new(t.value, t.hasValue);

    public static implicit operator Optional<T>(Optional o) => default;
}

public class Optional
{
    public static Optional Default { get; } = new();

    private Optional()
    {
    }

    public static Optional<T> Try<T>(bool hasValue, T value) => new(value, hasValue);
}
