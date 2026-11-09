using System.Text.Json;
using System.Text.Json.Serialization;
using Mycroft.Core.Models;

namespace Mycroft.Infrastructure;

public static class MycroftJson
{
    /// <summary>camelCase, enums as strings — the wire format of the API, webhooks and stored JSON.</summary>
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
            options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static IReadOnlyList<CustomCategory> DeserializeCategories(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<CustomCategory>>(json, Options) ?? [];
}
