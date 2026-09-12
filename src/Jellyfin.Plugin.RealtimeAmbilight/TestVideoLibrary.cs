using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// One click, not a manual trip through Dashboard &gt; Libraries: the eight
/// short synthetic calibration clips ship embedded in the plugin itself
/// (own content, ffmpeg-generated, no licensing question), extracted to a
/// folder under the plugin's own data path and registered as a real Jellyfin
/// library on request. A library, not just files on disk, because the
/// Ambilight pipeline only ever reacts to an actual Jellyfin playback
/// session -- a bare <c>&lt;video&gt;</c> tag playing these bytes would never
/// reach it.
/// </summary>
public sealed class TestVideoLibrary
{
    /// <summary>Fixed so <see cref="IsSetUp"/> and removal both know what to look for, regardless of anything the operator later renames it to.</summary>
    public const string LibraryName = "Ambilight calibration test videos";

    private const string EmbeddedResourcePrefix = "Jellyfin.Plugin.RealtimeAmbilight.Configuration.TestVideos.";

    /// <summary>Embedded resource file name mapped to the friendly name Jellyfin should display it under once extracted.</summary>
    private static readonly IReadOnlyList<(string ResourceFileName, string DisplayFileName)> Clips =
    [
        ("01-sunrise-warmup.mp4", "Ambilight test 1 - Sunrise warmup.mp4"),
        ("02-action-fastcuts.mp4", "Ambilight test 2 - Action fast cuts.mp4"),
        ("03-nature-calm.mp4", "Ambilight test 3 - Nature calm.mp4"),
        ("04-motorsport-strobe.mp4", "Ambilight test 4 - Motorsport strobe.mp4"),
        ("05-brightness-range.mp4", "Ambilight test 5 - Brightness range.mp4"),
        ("06-monochrome-blue.mp4", "Ambilight test 6 - Monochrome blue.mp4"),
        ("07-pillarbox-4-3.mp4", "Ambilight test 7 - Pillarbox 4-3.mp4"),
        ("08-letterbox-21-9.mp4", "Ambilight test 8 - Letterbox 21-9.mp4"),
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly string _extractedFolderPath;

    public TestVideoLibrary(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        var dataFolderPath = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "jellyfin-realtime-ambilight");
        _extractedFolderPath = Path.Combine(dataFolderPath, "TestVideos");
    }

    /// <summary>True once the library has been added; false before <see cref="SetupAsync"/> or after <see cref="RemoveAsync"/>.</summary>
    public bool IsSetUp => _libraryManager.GetVirtualFolders().Any(folder => folder.Name == LibraryName);

    /// <summary>Extracts the embedded clips (skipping any already on disk) and adds the library, triggering a scan.</summary>
    public async Task SetupAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_extractedFolderPath);
        var assembly = typeof(Plugin).Assembly;
        foreach (var clip in Clips)
        {
            var targetPath = Path.Combine(_extractedFolderPath, clip.DisplayFileName);
            if (File.Exists(targetPath))
            {
                continue;
            }

            using var resourceStream = assembly.GetManifestResourceStream(EmbeddedResourcePrefix + clip.ResourceFileName);
            if (resourceStream is null)
            {
                continue;
            }

            await using var fileStream = File.Create(targetPath);
            await resourceStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        if (IsSetUp)
        {
            // Left over from a previous setup that was never removed cleanly.
            return;
        }

        var options = new LibraryOptions { PathInfos = [new MediaPathInfo(_extractedFolderPath)] };
        await _libraryManager.AddVirtualFolder(LibraryName, CollectionTypeOptions.homevideos, options, refreshLibrary: true).ConfigureAwait(false);
    }

    /// <summary>Removes the library (if present) and deletes the extracted files, leaving no trace behind.</summary>
    public async Task RemoveAsync()
    {
        if (IsSetUp)
        {
            await _libraryManager.RemoveVirtualFolder(LibraryName, refreshLibrary: true).ConfigureAwait(false);
        }

        if (Directory.Exists(_extractedFolderPath))
        {
            Directory.Delete(_extractedFolderPath, recursive: true);
        }
    }
}
