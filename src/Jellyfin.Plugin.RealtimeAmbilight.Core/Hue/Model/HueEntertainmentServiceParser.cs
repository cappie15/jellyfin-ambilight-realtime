using System.Text.Json;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;

/// <summary>
/// Parses the CLIP v2 <c>entertainment</c> resource list -- a different
/// resource from <c>entertainment_configuration</c>. Every physical light has
/// its own <c>entertainment</c> *service* resource alongside its <c>light</c>
/// resource, and an entertainment configuration's
/// <c>channels[].members[].service.rid</c> points at that entertainment
/// service id, not at the light's own id -- calling a plain
/// <c>GET/PUT /clip/v2/resource/light/{id}</c> with it 404s every time, which
/// is exactly what was observed live (every end-of-session light command
/// rejected, on every light, every session). The entertainment service's own
/// <c>renderer_reference</c> field is what actually names the corresponding
/// light resource: confirmed against aiohue's own <c>entertainment.py</c>
/// model ("renderer_reference: Indicates which light service is linked to
/// this entertainment service"), since the primary reference sits behind a
/// developer-portal login this environment cannot reach.
/// </summary>
public static class HueEntertainmentServiceParser
{
    /// <returns>Entertainment-service resource id to the light resource id it renders to.</returns>
    public static IReadOnlyDictionary<Guid, Guid> ParseLightIdsByServiceId(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return ParseLightIdsByServiceId(document.RootElement);
    }

    public static IReadOnlyDictionary<Guid, Guid> ParseLightIdsByServiceId(JsonElement root)
    {
        var result = new Dictionary<Guid, Guid>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idElement.GetString(), out var serviceId))
            {
                continue;
            }

            // A service with no renderer (a proxy-only node, or a light that
            // does not itself support Entertainment streaming) legitimately
            // has no renderer_reference -- skipped, not an error.
            if (entry.TryGetProperty("renderer_reference", out var rendererReference)
                && rendererReference.ValueKind == JsonValueKind.Object
                && rendererReference.TryGetProperty("rid", out var ridElement)
                && ridElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(ridElement.GetString(), out var lightId))
            {
                result[serviceId] = lightId;
            }
        }

        return result;
    }
}
