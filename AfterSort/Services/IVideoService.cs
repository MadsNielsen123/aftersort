using Avalonia.Media.Imaging;

namespace AfterSort.Services;

/// <summary>
/// Provides video thumbnail extraction and playback capabilities.
/// </summary>
public interface IVideoService : IDisposable
{
    /// <summary>
    /// Extracts a thumbnail frame from a video file at the specified time offset.
    /// Falls back to the first available frame if the offset is beyond the video duration.
    /// Returns null if extraction fails.
    /// </summary>
    Bitmap? ExtractThumbnail(string videoPath, TimeSpan offset, int targetWidth = 400);
}
