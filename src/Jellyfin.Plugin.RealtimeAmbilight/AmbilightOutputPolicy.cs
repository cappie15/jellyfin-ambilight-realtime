using System;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>Evaluates output permission without modifying saved configuration.</summary>
internal static class AmbilightOutputPolicy
{
    internal static bool IsAllowed(PluginConfiguration? configuration, DateTime localNow)
    {
        if (configuration is null || !configuration.Enabled)
        {
            return false;
        }

        if (!configuration.DisableAmbilightDuringDaytime)
        {
            return true;
        }

        var time = localNow.TimeOfDay;
        return time < TimeSpan.FromHours(7) || time >= TimeSpan.FromHours(17);
    }
}
