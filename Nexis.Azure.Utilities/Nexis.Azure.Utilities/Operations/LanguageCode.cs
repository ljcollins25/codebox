namespace Nexis.Azure.Utilities;

public enum LanguageCode
{
    eng,
    jpn,
    kor,
    zho,
}

public enum FileType
{
    mp4,
    mkv,
    srt,
    ass
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

    public static LanguageCode GetLanguageCode(string lang)
    {
        return Enum.GetValues<LanguageCode>().Where(code =>
        {
            return lang.Contains(code.ToLanguageOption(), StringComparison.OrdinalIgnoreCase)
            || lang.Contains(code.ToDisplayName(), StringComparison.OrdinalIgnoreCase);
        }).First();
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

