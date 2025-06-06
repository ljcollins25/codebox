using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nexis.Azure.Utilities;

public record VideoDetails(VideoDetails.DataModel data)
{
    public record DataModel(string video_url, string caption_url, string creator_name, double duration, string title, string id);
}

public record MoveRequest(string project_id, string item_id, string item_type = "video_translate") : IApiRequest<MoveRequest>
{
    public static string Url => "https://api2.heygen.com/v1/heygen_project/item/move";
}

public interface IApiRequest<TSelf> : IApiMessage<TSelf>
    where TSelf : IApiRequest<TSelf>
{
}

public interface IApiGetResponse<TSelf> : IApiMessage<TSelf>
    where TSelf : IApiGetResponse<TSelf>
{
}

public interface IApiMessage<TSelf>
    where TSelf : IApiMessage<TSelf>
{
    static abstract string Url { get; }

    public virtual string GetApiUrl() => TSelf.Url;
}

public record UpdateRequest(string id, UpdateRequest.Parameters @params) : IApiRequest<UpdateRequest>
{
    public static string Url => "https://api2.heygen.com/v1/video_translate/update";

    public record Parameters(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? title,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? low_priority = null);
}

public record TranslateRequest(
    string name,
    string google_url,
    [property: JsonConverter(typeof(ListConverter<LanguageCode, LanguageOptionJsonConverter>))]
    List<LanguageCode> output_languages,
    string input_video_id = "",
    string instruction = "",
    string recaptcha_token = "",
    bool enable_video_stretching = false,
    bool translate_audio_only = true,
    bool captions = true,
    bool keep_the_same_format = true) : IApiRequest<TranslateRequest>
{
    public string[] vocabulary { get; init; } = [];

    public static string Url => "https://api2.heygen.com/v3/video_translate.create";
}

public class DeleteRequest : IApiRequest<DeleteRequest>
{
    public static string Url => "https://api2.heygen.com/v1/video_translate/trash";

    public List<Item> items { get; set; } = new();

    public class Item
    {
        public string item_type { get; set; } = "video_translate";

        public required string id { get; set; }
    }
}

[JsonConverter(typeof(VuidJsonConverter))]
public record struct Vuid(string Value) : IFormattable
{
    public static Vuid FromFileName(string path, bool includeGuid = true) => new Vuid(GetOperationId(path, includeGuid));

    public override string ToString() => Value;

    public string ToString(string? format, IFormatProvider? formatProvider) => Value;
}

//public class LanguageOptionListConverter : JsonConverter<List<LanguageCode>>
//{
//    public override List<LanguageCode> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
//    {
//        string value = reader.GetString()!;
//        return Enum.GetValues<LanguageCode>().First(l => l.ToLanguageOption() == value);
//    }

//    public override void Write(Utf8JsonWriter writer, List<LanguageCode> value, JsonSerializerOptions options)
//    {
//        writer.WriteStringValue(value.ToLanguageOption());
//    }
//}

public class ListConverter<T, TConverter> : JsonConverter<List<T>>
    where TConverter : JsonConverter<T>, new()
{
    private readonly TConverter _itemConverter = new();

    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        options = new(options);
        options.Converters.Add(_itemConverter);
        return JsonSerializer.Deserialize<List<T>>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
    {
        options = new(options);
        options.Converters.Add(_itemConverter);
        JsonSerializer.Serialize(writer, value, options);
    }
}

public class LanguageOptionJsonConverter : JsonConverter<LanguageCode>
{
    public override LanguageCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string value = reader.GetString()!;
        return Enum.GetValues<LanguageCode>().First(l => l.ToLanguageOption().EqualsIgnoreCase(value) || l.ToDisplayName().EqualsIgnoreCase(value));
    }

    public override void Write(Utf8JsonWriter writer, LanguageCode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToLanguageOption());
    }
}

public class VuidJsonConverter : JsonConverter<Vuid>
{
    public override Vuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string value = reader.GetString()!;
        if (Guid.TryParse(value, out var guid))
        {
            value = $"{guid:n}";
        }
        return new Vuid(value);
    }

    public override void Write(Utf8JsonWriter writer, Vuid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

public enum VideoStatus
{
    waiting = 0,
    processing = 1,
    completed = 2,
    unknown = 3,
    unknown2 = 4
}

public class ListDataResponse
{
    public static string Url(int limit = 50) => $"https://api2.heygen.com/v1/heygen_project_item/list?limit={limit}&video_type=all&sort_key=created_ts&sort_order=desc&traverse_deep=true&advanced_filters=true&item_types=video_translate";

    public required Data data { get; init; }
    public record Data(string token, int total, List<VideoDataItem> list);
}

public record VideoDataItem(
    string id,
    string item_type,
    [property: JsonConverter(typeof(JsonStringEnumConverter<VideoStatus>))]
    VideoStatus status,
    double eta,
    bool low_priority,
    [property: JsonConverter(typeof(LanguageOptionJsonConverter))]
    LanguageCode output_language,
    string creator_name,
    double waiting_time,
    bool is_trash,
    string project_id)
{
    public required string name { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<LanguageCode>))]
    public LanguageCode lang => output_language;

    public (Vuid Id, int Index)? displayInfo;

    public bool done { get; set; }

    public (Vuid Id, int Index) GetInfo()
    {
        try
        {
            var d = ExtractFileDescriptor(name);
            return d;
        }
        catch
        {
            return (new (name), -1);
        }
    }

    public string title
    {
        get
        {
            var d = displayInfo ?? GetInfo();
            return $"{d.Id}--{d.Index}";
        }
    }
}