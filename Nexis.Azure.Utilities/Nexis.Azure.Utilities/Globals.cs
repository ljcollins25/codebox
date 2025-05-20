namespace Nexis.Azure.Utilities;

public static class Globals
{
    public static string? GeneratedSas;

    public static bool AllowOverrideResponseFile { get; set; } = Environment.GetEnvironmentVariable("NEXUTILS_BYPASS_OVERRIDE_RESPONSEFILE") != "1";
}