using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;

namespace AfterSort.Services;

/// <summary>
/// Owns the single shared <see cref="LibVLC"/> instance and provides video thumbnail
/// extraction plus playback object creation. Centralising LibVLC here keeps native
/// initialisation and disposal in one place.
/// </summary>
public interface IVideoService : IDisposable
{
    /// <summary>
    /// True when the extension (including the leading dot) is a playable video format.
    /// </summary>
    bool IsSupported(string extension);

    /// <summary>
    /// Extracts a thumbnail frame at the given offset, falling back to an early frame for short
    /// videos and to a styled placeholder when extraction fails. Returns null only if VLC is missing.
    /// </summary>
    Bitmap? ExtractThumbnail(string videoPath, TimeSpan offset, int targetWidth = 400);

    /// <summary>
    /// Creates a playback-configured <see cref="MediaPlayer"/> on the shared instance,
    /// or null when LibVLC is unavailable.
    /// </summary>
    MediaPlayer? CreatePlayer();

    /// <summary>
    /// Creates a <see cref="Media"/> for the given path on the shared instance,
    /// or null when LibVLC is unavailable.
    /// </summary>
    Media? CreateMedia(string path);
}
