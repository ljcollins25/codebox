namespace Nexis.Azure.Utilities;

[Flags]
public enum LanguageCode
{
    eng = 1 << 0,
    jpn = 1 << 1,
    kor = 1 << 2,
    zho = 1 << 3,
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
            LanguageCode.zho => "Chinese (Mandarin, Simplified)",
            _ => throw new ArgumentOutOfRangeException(nameof(languageCode), languageCode, null)
        };
    }
}

