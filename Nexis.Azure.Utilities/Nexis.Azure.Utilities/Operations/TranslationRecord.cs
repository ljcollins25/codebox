using System.Text.Json;

namespace Nexis.Azure.Utilities;

public class TranslationRecord
{
    public required string event_type { get; set; }

    public required EventData event_data { get; set; }

    public class EventData
    {
        public required string output_language { get; set; }

        public required string video_translate_id { get; set; }

        public string url { get; set; }

    }

    public LanguageCode GetLanguageCode() => LanguageCodeExtensions.GetLanguageCode(event_data.output_language);

    public string FileName => ExtractFilenameFromContentDispositionUrl(event_data.url)!.Replace(".mp4.mp4", "");

    public static TranslationRecord Parse(string json) => JsonSerializer.Deserialize<TranslationRecord>(json)!;

    public static TranslationRecord ReadFromFile(string path) => Parse(File.ReadAllText(path));
}
