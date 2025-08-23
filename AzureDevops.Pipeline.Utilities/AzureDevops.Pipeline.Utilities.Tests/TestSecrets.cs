using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;

public partial class TestSecrets
{
    public static string StorageAccountName { get => GetSecret(); set => SetSecret(value); }
    public static string StorageAccountKey { get => GetSecret(); set => SetSecret(value); }
    public static string StorageBlobSasUrl { get => GetSecret(); set => SetSecret(value); }
    public static string TaskUrl { get => GetSecret(); set => SetSecret(value); }
    public static string AdoToken { get => GetSecret(); set => SetSecret(value); }
    public static string ProdTaskUrl { get => GetSecret(); set => SetSecret(value); }
    public static string ProdAdoToken { get => GetSecret(); set => SetSecret(value); }
    public static string PersonalToken { get => GetSecret(); set => SetSecret(value); }
    public static string WebhookSecret { get => GetSecret(); set => SetSecret(value); }

    public static IConfiguration Secrets { get; private set; }

    static TestSecrets()
    {
        var builder = new ConfigurationBuilder()
            .AddUserSecrets<TestSecrets>();

        Secrets = builder.Build();
        Initialize();
    }

    private static void SetSecret(string value, [CallerMemberName] string name = null!)
    {
        Secrets[name] = value;
    }

    private static string GetSecret([CallerMemberName] string name = null!)
    {
        return Secrets[name]!;
    }

    static partial void Initialize();
}

/*
public partial class TestSecrets
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