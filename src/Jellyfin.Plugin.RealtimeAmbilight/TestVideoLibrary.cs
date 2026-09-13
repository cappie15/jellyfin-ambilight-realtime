using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// One click, not a manual trip through Dashboard &gt; Libraries: two short
/// synthetic clips ship embedded in the plugin itself (own content,
/// ffmpeg-generated, no licensing question), extracted to a folder under the
/// plugin's own data path and registered, alongside a second, empty
/// operator-owned folder (<see cref="CustomClipsFolderPath"/>), as one real
/// Jellyfin library. A library, not just files on disk, because the
/// Ambilight pipeline only ever reacts to an actual Jellyfin playback
/// session -- a bare <c>&lt;video&gt;</c> tag playing these bytes would never
/// reach it.
/// </summary>
/// <remarks>
/// Only the two clips whose exact black-bar geometry
/// <see cref="Core.Sampling.BlackBorderDetector"/> needs ship embedded --
/// that geometry cannot come from real footage, so only that case stays
/// synthetic. An earlier revision also shipped seven other synthetic clips
/// (brightness range, colour cycling, texture, a left/right split) for
/// broader tuning; retired in favour of <see cref="CustomClipsFolderPath"/>,
/// since a stand-in for footage that has an actual owner is worse than the
/// operator's own real content -- this plugin cannot embed or redistribute
/// third-party video (a sunrise timelapse, a sports clip, a film scene) the
/// way it can its own ffmpeg-generated swatches, no matter how it was
/// acquired, so every operator who wants that kind of tuning material
/// supplies their own by dropping files they hold the rights to into that
/// folder -- one click still picks them up alongside the two embedded
/// clips, without this plugin ever shipping anyone else's content.
/// </remarks>
public sealed class TestVideoLibrary
{
    /// <summary>Fixed so <see cref="IsSetUp"/> and removal both know what to look for, regardless of anything the operator later renames it to.</summary>
    public const string LibraryName = "Ambilight calibration test videos";

    private const string EmbeddedResourcePrefix = "Jellyfin.Plugin.RealtimeAmbilight.Configuration.TestVideos.";

    /// <summary>Embedded resource file name mapped to the friendly name Jellyfin should display it under once extracted.</summary>
    private static readonly IReadOnlyList<(string ResourceFileName, string DisplayFileName)> Clips =
    [
        ("07-pillarbox-4-3.mp4", "Ambilight test 1 - Pillarbox 4-3.mp4"),
        ("08-letterbox-21-9.mp4", "Ambilight test 2 - Letterbox 21-9.mp4"),
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly string _syntheticFolderPath;

    public TestVideoLibrary(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        var dataFolderPath = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "jellyfin-realtime-ambilight");
        _syntheticFolderPath = Path.Combine(dataFolderPath, "TestVideos", "Synthetic");
        CustomClipsFolderPath = Path.Combine(dataFolderPath, "TestVideos", "YourOwnClips");
    }

    /// <summary>
    /// Never written to or deleted by the plugin: the operator's own folder
    /// to drop in real clips they hold the rights to (a sunrise, sports, a
    /// film, motorsport, nature). Included in the same library as the two
    /// embedded synthetic clips, so it shows up in the same list once
    /// scanned, with nothing about its contents ever leaving this server.
    /// </summary>
    public string CustomClipsFolderPath { get; }

    /// <summary>True once the library has been added; false before <see cref="SetupAsync"/> or after <see cref="RemoveAsync"/>.</summary>
    public bool IsSetUp => _libraryManager.GetVirtualFolders().Any(folder => folder.Name == LibraryName);

    /// <summary>
    /// Extracts the embedded clips (skipping any already on disk), ensures
    /// the operator's own custom-clips folder exists, and adds the library.
    /// Safe to call again once already set up -- picks up anything newly
    /// dropped into <see cref="CustomClipsFolderPath"/> with a rescan instead
    /// of failing or duplicating the library.
    /// </summary>
    public async Task SetupAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_syntheticFolderPath);
        Directory.CreateDirectory(CustomClipsFolderPath);
        var assembly = typeof(Plugin).Assembly;
        foreach (var clip in Clips)
        {
            var targetPath = Path.Combine(_syntheticFolderPath, clip.DisplayFileName);
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
            _libraryManager.QueueLibraryScan();
            return;
        }

        var options = new LibraryOptions
        {
            PathInfos = [new MediaPathInfo(_syntheticFolderPath), new MediaPathInfo(CustomClipsFolderPath)],
        };
        await _libraryManager.AddVirtualFolder(LibraryName, CollectionTypeOptions.homevideos, options, refreshLibrary: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the library (if present) and deletes the extracted synthetic
    /// clips. <see cref="CustomClipsFolderPath"/> and anything the operator
    /// put there are left untouched -- it is their content, not the
    /// plugin's, to delete.
    /// </summary>
    public async Task RemoveAsync()
    {
        if (IsSetUp)
        {
            await _libraryManager.RemoveVirtualFolder(LibraryName, refreshLibrary: true).ConfigureAwait(false);
        }

        if (Directory.Exists(_syntheticFolderPath))
        {
            Directory.Delete(_syntheticFolderPath, recursive: true);
        }
    }
}
