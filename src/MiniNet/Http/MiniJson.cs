using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniNet.Http;

/// <summary>Shared JSON settings: camelCase names and case-insensitive reading, like ASP.NET Core's web defaults.</summary>
public static class MiniJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
