public partial class TestSecrets
{
    public static string StorageAccountName = "";
    public static string StorageAccountKey = "";
    public static string StorageBlobSasUrl = "";
    public static string TaskUrl = "";
    public static string AdoToken = "";
    public static string ProdTaskUrl = "";
    public static string ProdAdoToken = "";

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