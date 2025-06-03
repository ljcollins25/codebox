using System.Text.Json;
using static Nexis.Azure.Utilities.DeleteRequest;

namespace Nexis.Azure.Utilities;

public class TranslationRecord
{
    public required string event_type { get; set; }

    public required EventData event_data { get; set; }

    public VideoDataItem? ItemInfo { get; set; }

    public class EventData
    {
        public required string output_language { get; set; }

        public required string video_translate_id { get; set; }

        public string url { get; set; }
    }

    public LanguageCode GetLanguageCode() => LanguageCodeExtensions.GetLanguageCode(event_data.output_language);

    public string? InfoFileName()
    {
        if (ItemInfo == null) return null;
        var info = ItemInfo.GetInfo();
        return $"[[{info.Id}-{info.Index.ToString().PadLeft(3, '0')}]].m4a";
    }

    public string FileName => InfoFileName() ?? ExtractFilenameFromContentDispositionUrl(event_data.url)!.Replace(".mp4.mp4", "");

    public static TranslationRecord Parse(string json) => JsonSerializer.Deserialize<TranslationRecord>(json)!;

    public static TranslationRecord ReadFromFile(string path) => Parse(File.ReadAllText(path));

    public static TranslationRecord FromVideoItem(VideoDataItem item)
    {
        var info = item.GetInfo();
        return new TranslationRecord()
        {
            event_type = item.status.ToString(),
            ItemInfo = item,
            event_data = new()
            {
                output_language = item.output_language.ToDisplayName(),
                video_translate_id = item.id
            }
        };
    }
}
