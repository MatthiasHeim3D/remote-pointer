using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAnnotate.Contracts.Serialization;

public static class RemoteAnnotateJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AllowTrailingCommas = false;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.PropertyNameCaseInsensitive = false;
        options.ReadCommentHandling = JsonCommentHandling.Disallow;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }
}
