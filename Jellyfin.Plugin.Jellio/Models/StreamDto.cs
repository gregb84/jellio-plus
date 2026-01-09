using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellio.Models;

public class StreamDto
{
    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("behaviorHints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BehaviorHints? BehaviorHints { get; set; }
}

public class BehaviorHints
{
    [JsonPropertyName("notWebReady")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NotWebReady { get; set; }
}
