#nullable disable
namespace VectorSearch;

/// <summary>
/// Simple mutable box for value types.
/// </summary>
public class Box<T>
{
    public T Value;

    public Box(out Box<T> box)
    {
        box = this;
    }

    public Box() { }

    public void SetValue(T value) => Value = value;
}
