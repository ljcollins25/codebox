using System.Runtime.CompilerServices;

public ref struct Out<T>
{
    private ref T valueRef;

    public ref T ValueRef => ref valueRef;

    public T Value
    {
        get => valueRef;
        set
        {
            if (IsValid) valueRef = value;
        }
    }

    public bool IsValid => !Unsafe.IsNullRef(ref valueRef);

    public unsafe Out(out T value)
    {
        value = default;
        valueRef = ref Unsafe.AsRef<T>(Unsafe.AsPointer(ref value));
    }
}

public static class Out
{
    public static T Var<T>(out T result, T value)
    {
        result = value;
        return value;
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