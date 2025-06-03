using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexis.Azure.Utilities;

public record VideoDetails(VideoDetails.DataModel data)
{
    public record DataModel(string video_url, string caption_url, string creator_name, double duration, string title, string id);
}

public record MoveRequest(string project_id, string item_id, string item_type = "video_translate")
{
}

public record UpdateRequest(string id, UpdateRequest.Parameters @params)
{
    public const string Url = "https://api2.heygen.com/v1/video_translate/update";

    public record Parameters(string title);
}

public class DeleteRequest
{
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
    public static Vuid FromFileName(string path) => new Vuid(GetOperationId(path));

    public override string ToString() => Value;

    public string ToString(string? format, IFormatProvider? formatProvider) => Value;
}

public class VuidJsonConverter : JsonConverter<Vuid>
{
    public override Vuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string value = reader.GetString()!;
        return new Vuid(value);
    }

    public override void Write(Utf8JsonWriter writer, Vuid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
