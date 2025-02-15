public partial class TestSecrets
{
    public static string StorageAccountName = "";
    public static string StorageAccountKey = "";
    public static string TaskUrl = "";
    public static string AdoToken = "";

    static TestSecrets()
    {
        Initialize();
    }

    static partial void Initialize();
}

/*
public partial class Secrets
{
    static partial void Initialize()
    {
        StorageAccountName = "";
        StorageAccountKey = "";
        TaskUrl = "";
        AdoToken = "";
    }

}
*/