using System.Text.Json;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;

/// <summary>
/// Parses the CLIP v2 <c>entertainment_configuration</c> resource list. Every
/// CLIP v2 response wraps its payload as <c>{"errors": [...], "data": [...]}</c>;
/// only <c>data</c> is read here.
/// </summary>
/// <remarks>
/// Deliberately hand-parsed against <see cref="JsonElement"/> rather than
/// attribute-decorated POCOs deserialized in one call: a field this plugin
/// does not recognise must be ignored, and a single malformed channel entry
/// must not discard the rest of a configuration -- easier to guarantee by
/// reading defensively field-by-field than to rely on exactly how a generic
/// deserializer degrades. Field names are those aiohue's own
/// <c>entertainment_configuration.py</c> model uses against the same
/// resource, since the primary source (developers.meethue.com's API
/// reference) sits behind a developer-portal login.
/// </remarks>
public static class HueEntertainmentConfigurationParser
{
    public static IReadOnlyList<HueEntertainmentConfiguration> ParseList(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return ParseList(document.RootElement);
    }

    public static IReadOnlyList<HueEntertainmentConfiguration> ParseList(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<HueEntertainmentConfiguration>();
        foreach (var entry in data.EnumerateArray())
        {
            if (TryParseOne(entry, out var configuration))
            {
                results.Add(configuration);
            }
        }

        return results;
    }

    private static bool TryParseOne(JsonElement entry, out HueEntertainmentConfiguration configuration)
    {
        configuration = null!;
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var id))
        {
            return false;
        }

        var name = entry.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : (entry.TryGetProperty("name", out var legacyName) && legacyName.ValueKind == JsonValueKind.String
                    ? legacyName.GetString() ?? string.Empty
                    : string.Empty);

        var configurationType = ReadString(entry, "configuration_type") ?? "other";
        var status = ReadString(entry, "status") ?? "inactive";

        var channels = new List<HueEntertainmentChannel>();
        if (entry.TryGetProperty("channels", out var channelsElement) && channelsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var channelElement in channelsElement.EnumerateArray())
            {
                if (TryParseChannel(channelElement, out var channel))
                {
                    channels.Add(channel);
                }
            }
        }

        configuration = new HueEntertainmentConfiguration(id, name, configurationType, status, channels);
        return true;
    }

    private static bool TryParseChannel(JsonElement channelElement, out HueEntertainmentChannel channel)
    {
        channel = null!;
        if (channelElement.ValueKind != JsonValueKind.Object
            || !channelElement.TryGetProperty("channel_id", out var channelIdElement)
            || !channelIdElement.TryGetInt32(out var channelId))
        {
            return false;
        }

        var position = channelElement.TryGetProperty("position", out var positionElement) && positionElement.ValueKind == JsonValueKind.Object
            ? new HuePosition(
                ReadDouble(positionElement, "x"),
                ReadDouble(positionElement, "y"),
                ReadDouble(positionElement, "z"))
            : default;

        var members = new List<Guid>();
        if (channelElement.TryGetProperty("members", out var membersElement) && membersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var memberElement in membersElement.EnumerateArray())
            {
                if (memberElement.ValueKind == JsonValueKind.Object
                    && memberElement.TryGetProperty("service", out var serviceElement)
                    && serviceElement.ValueKind == JsonValueKind.Object
                    && serviceElement.TryGetProperty("rid", out var ridElement)
                    && ridElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(ridElement.GetString(), out var rid))
                {
                    members.Add(rid);
                }
            }
        }

        channel = new HueEntertainmentChannel(channelId, position, members);
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double ReadDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var parsed) ? parsed : 0d;
}
