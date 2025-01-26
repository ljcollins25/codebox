namespace AzureDevops.Pipeline.Utilities;

public record struct Optional<T>(T? Value, bool HasValue = true)
{
    public static implicit operator Optional<T>(T? value) => new(value);

    public Optional<TResult> Then<TResult>(Func<T?, Optional<TResult>> select)
    {
        if (!HasValue) return default;

        return select(Value);
    }
}
