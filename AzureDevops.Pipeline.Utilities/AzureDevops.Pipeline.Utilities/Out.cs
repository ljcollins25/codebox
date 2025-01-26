using System.Runtime.CompilerServices;

public ref struct Out<T>
{
    public ref T Value;

    public unsafe Out(out T value)
    {
        value = default;
        Value = ref Unsafe.AsRef<T>(Unsafe.AsPointer(ref value));
    }
}

public ref struct Ref<T>
{
    public ref T Value;

    public unsafe Ref(ref T value)
    {
        Value = ref value;
    }
}