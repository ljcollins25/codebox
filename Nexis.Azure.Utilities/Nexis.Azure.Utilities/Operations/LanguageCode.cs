namespace Nexis.Azure.Utilities;

public enum LanguageCode
{
    eng,
    jpn,
    kor,
    zho,
}

public static class LanguageCodeExtensions
{
    public static string ToDisplayName(this LanguageCode languageCode)
    {
        return languageCode switch
        {
            LanguageCode.eng => "English",
            LanguageCode.jpn => "Japanese",
            LanguageCode.kor => "Korean",
            LanguageCode.zho => "Mandarin",
            _ => throw new ArgumentOutOfRangeException(nameof(languageCode), languageCode, null)
        };
    }

    public static string ToLanguageOption(this LanguageCode languageCode)
    {
        return languageCode switch
        {
            LanguageCode.eng => "English (United States)",
            LanguageCode.jpn => "Japanese",
            LanguageCode.kor => "Korean",
            LanguageCode.zho => "Chinese (Mandarin",
            _ => throw new ArgumentOutOfRangeException(nameof(languageCode), languageCode, null)
        };
    }
}

