#pragma warning disable CA1848, CA1873
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight.Hue;

/// <summary>
/// Reads and restores light state around a Hue Entertainment session --
/// split out of <see cref="HueEntertainmentService"/>, which owns the
/// streaming lifecycle itself, not what happens to a light's on/off/colour
/// state before it starts or after it ends. Entertainment streaming changes
/// a light's colour, never its power state, which is why this exists at
/// all: something has to remember what a light looked like before
/// streaming took over, and put it back (or apply the configured
/// alternative) once streaming stops.
/// </summary>
internal sealed class HueSessionSnapshotCoordinator
{
    private readonly IHueLightControl _lightControl;
    private readonly ILogger _logger;

    public HueSessionSnapshotCoordinator(IHueLightControl lightControl, ILogger logger)
    {
        _lightControl = lightControl ?? throw new ArgumentNullException(nameof(lightControl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Maps each channel's own member ids (entertainment service ids) to the
    /// light resource ids CLIP v2 light calls actually need, via
    /// <paramref name="lightIdsByServiceId"/>. A member with no resolved
    /// mapping is dropped with a warning rather than sent to the bridge
    /// as-is, which would just 404 -- exactly the failure this whole
    /// mapping step exists to fix.
    /// </summary>
    public List<Guid> ResolveLightIds(IEnumerable<HueEntertainmentChannel> channels, IReadOnlyDictionary<Guid, Guid> lightIdsByServiceId)
    {
        var lightIds = new List<Guid>();
        foreach (var serviceId in channels.SelectMany(c => c.Members).Select(m => m.ServiceId).Distinct())
        {
            if (lightIdsByServiceId.TryGetValue(serviceId, out var lightId))
            {
                lightIds.Add(lightId);
            }
            else
            {
                _logger.LogWarning("Hue Entertainment could not resolve entertainment service {ServiceId} to a light; skipping it for snapshot/end-of-session commands.", serviceId);
            }
        }

        return lightIds;
    }

    /// <summary>Records on/off, brightness and colour for every light in <paramref name="channels"/>, once per playback session id -- see <see cref="HueEntertainmentService"/>'s own remarks on why capture is session-scoped.</summary>
    public async Task<HueSessionSnapshot?> CaptureAsync(
        IEnumerable<HueEntertainmentChannel> channels,
        IReadOnlyDictionary<Guid, Guid> lightIdsByServiceId,
        PluginConfiguration configuration,
        HueCredentials credentials,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var lightIds = ResolveLightIds(channels, lightIdsByServiceId);
        var entries = new List<HueLightSnapshotEntry>();
        foreach (var lightId in lightIds)
        {
            var entry = await _lightControl
                .ReadStateAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, lightId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return new HueSessionSnapshot(sessionId, entries);
    }

    /// <summary>Applies the configured end-of-session behaviour (restore/turn off/warm-white-dim) to every light in <paramref name="channels"/>.</summary>
    public async Task ApplyEndBehaviourAsync(
        IEnumerable<HueEntertainmentChannel> channels,
        IReadOnlyDictionary<Guid, Guid> lightIdsByServiceId,
        PluginConfiguration configuration,
        HueCredentials credentials,
        HueSessionSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        var lightIds = ResolveLightIds(channels, lightIdsByServiceId);
        foreach (var lightId in lightIds)
        {
            if (configuration.HueEndBehaviour == HueEndBehaviour.RestorePreviousState
                && snapshot?.Lights.FirstOrDefault(l => l.LightId == lightId) is { } entry)
            {
                await _lightControl
                    .RestoreAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, entry, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (configuration.HueEndBehaviour == HueEndBehaviour.TurnOff)
            {
                await _lightControl
                    .TurnOffAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, lightId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _lightControl
                    .ApplyWarmWhiteDimAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, lightId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
