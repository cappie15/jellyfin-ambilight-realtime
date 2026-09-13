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

        var serviceLocations = ParseServiceLocations(entry);
        configuration = new HueEntertainmentConfiguration(id, name, configurationType, status, channels, serviceLocations);
        return true;
    }

    /// <summary>
    /// <c>locations.service_locations[]</c>: each entertainment service's own
    /// real physical extent -- one position for an ordinary light, two (its
    /// endpoints) for an elongated multi-segment device. Absent entirely on
    /// older bridge firmware, so a missing/malformed <c>locations</c> object
    /// is not an error, just nothing to resolve channel positions from.
    /// </summary>
    private static Dictionary<Guid, IReadOnlyList<HuePosition>> ParseServiceLocations(JsonElement entry)
    {
        var result = new Dictionary<Guid, IReadOnlyList<HuePosition>>();
        if (!entry.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Object
            || !locations.TryGetProperty("service_locations", out var serviceLocationsElement) || serviceLocationsElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entryElement in serviceLocationsElement.EnumerateArray())
        {
            if (entryElement.ValueKind != JsonValueKind.Object
                || !entryElement.TryGetProperty("service", out var serviceElement)
                || serviceElement.ValueKind != JsonValueKind.Object
                || !serviceElement.TryGetProperty("rid", out var ridElement)
                || ridElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(ridElement.GetString(), out var serviceId)
                || !entryElement.TryGetProperty("positions", out var positionsElement)
                || positionsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var positions = new List<HuePosition>();
            foreach (var positionElement in positionsElement.EnumerateArray())
            {
                if (positionElement.ValueKind == JsonValueKind.Object)
                {
                    positions.Add(new HuePosition(
                        ReadDouble(positionElement, "x"),
                        ReadDouble(positionElement, "y"),
                        ReadDouble(positionElement, "z")));
                }
            }

            if (positions.Count > 0)
            {
                result[serviceId] = positions;
            }
        }

        return result;
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

        var members = new List<HueChannelMember>();
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
                    var index = memberElement.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                        ? parsedIndex
                        : 0;
                    members.Add(new HueChannelMember(rid, index));
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
