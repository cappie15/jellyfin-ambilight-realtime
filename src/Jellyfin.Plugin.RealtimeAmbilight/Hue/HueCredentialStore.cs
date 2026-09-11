using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.RealtimeAmbilight.Hue;

/// <summary>
/// The application key and Entertainment client key obtained from one
/// physical bridge pairing, plus enough to detect a spoofed or replaced
/// bridge later without trusting whatever answers at the last-known address.
/// </summary>
public sealed record HueCredentials(
    string BridgeId,
    string ApplicationKey,
    string ClientKey,
    string CertificateThumbprintSha256);

/// <summary>
/// Persists <see cref="HueCredentials"/> server-side only, encrypted at rest.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately never a field on <c>PluginConfiguration</c>: that class is
/// exactly what Jellyfin's own <c>GET /Plugins/{id}/Configuration</c> returns
/// verbatim to any admin browser session, so keeping the credentials
/// structurally outside it is what actually keeps them out of a generic
/// configuration response -- not a UI choice to hide a field that is really
/// still there.
/// </para>
/// <para>
/// Uses ASP.NET Core's own Data Protection API rather than a bespoke
/// encryption scheme: a standalone provider (not the DI-registered one,
/// whose availability from a plugin's own service registration is not
/// guaranteed) with its key ring stored alongside the encrypted file, both
/// under the plugin's own <c>DataFolderPath</c>. <b>Backup behaviour:</b> the
/// key ring and the ciphertext must be backed up and restored together --
/// they live in the same directory, so a directory-level backup naturally
/// keeps them together. If only the ciphertext survives (a partial backup,
/// or a restore onto a host whose key ring was never carried over),
/// decryption fails and this is treated the same as "never paired": the
/// operator goes through pairing again, rather than the plugin crashing or
/// silently producing garbage credentials.
/// </para>
/// </remarks>
/// <summary>
/// <see cref="HueEntertainmentService"/>'s whole dependency on <see cref="HueCredentialStore"/>
/// -- it only ever loads credentials, never saves/deletes them (that is
/// <see cref="Api.HueController"/>'s job, via the concrete class directly).
/// Exists so a test can supply fake credentials without touching disk.
/// </summary>
public interface IHueCredentialStore
{
    Task<HueCredentials?> LoadAsync(CancellationToken cancellationToken);
}

public sealed class HueCredentialStore : IHueCredentialStore
{
    private const string FileName = "hue-credentials.dat";
    private const string Purpose = "Jellyfin.Plugin.RealtimeAmbilight.Hue.Credentials.v1";

    private readonly string _filePath;
    private readonly IDataProtector _protector;

    public HueCredentialStore(string dataFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolderPath);
        Directory.CreateDirectory(dataFolderPath);
        var keyRingDirectory = new DirectoryInfo(Path.Combine(dataFolderPath, "hue-keys"));
        keyRingDirectory.Create();
        var provider = DataProtectionProvider.Create(keyRingDirectory);
        _protector = provider.CreateProtector(Purpose);
        _filePath = Path.Combine(dataFolderPath, FileName);
    }

    public bool HasCredentials => File.Exists(_filePath);

    public async Task SaveAsync(HueCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var json = JsonSerializer.Serialize(credentials);
        var protectedPayload = _protector.Protect(json);
        await File.WriteAllTextAsync(_filePath, protectedPayload, cancellationToken).ConfigureAwait(false);
    }

    /// <returns>
    /// The stored credentials, or <see langword="null"/> when none are
    /// stored, or when they could not be decrypted (a lost or mismatched key
    /// ring) -- both cases are equivalent to "not paired" for every caller.
    /// </returns>
    public async Task<HueCredentials?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var protectedPayload = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var json = _protector.Unprotect(protectedPayload);
            return JsonSerializer.Deserialize<HueCredentials>(json);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException or IOException)
        {
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
