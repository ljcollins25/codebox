using DotNext;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YoutubeDLSharp.Metadata;

namespace Nexis.Azure.Utilities;

public class YoutubePlaylist
{
    public static YoutubeFile[] ReadFromVideoData(string content)
    {
        var playlistOrFile = JsonConvert.DeserializeObject<VideoData>(content)!;

        var entries = playlistOrFile.Entries ?? [playlistOrFile];

        return entries.Where(e => e.Duration != null).DistinctBy(e => e.ID).Select(e =>
        {
            return new YoutubeFile(e.Title, e.ID, e.Uploader, e.UploaderID, e.Channel, e.ChannelID, TimeSpan.FromSeconds(e.Duration ?? -1));
        }).ToArray();
    }

    public static IDictionary<string, YoutubeFile> Deserialize(string yaml, int? limit = null)
    {
        var obj = new Deserializer().Deserialize<object>(yaml);
        var json = JsonConvert.SerializeObject(obj);
        return JsonConvert.DeserializeObject<YoutubeFile[]>(json)!
            .DistinctBy(y => y.Id)
            .Where(y => y.Duration > TimeSpan.Zero)
            .Take(limit ?? int.MaxValue)
            .ToDictionary(y => y.Id);
    }

    public static string Serialize(IEnumerable<YoutubeFile> files)
    {
        return new Serializer().Serialize(files);
    }
}

public class PlaylistData
{
    [JsonProperty("entries")]
    public required VideoData[] Entries { get; set; }
}

public record YoutubeFile(string Title, string Id, string Uploader, string UploaderId, string Channel, string ChannelId, TimeSpan Duration)
{
    public string? TranslatedTitle { get; set; }
    public string? ShortTitle { get; set; }
}