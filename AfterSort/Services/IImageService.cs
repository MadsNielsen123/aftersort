using Avalonia.Media.Imaging;

namespace AfterSort.Services;

/// <summary>
/// Decodes image files (standard raster, HEIC/HEIF and SVG) into Avalonia bitmaps.
/// All methods run synchronously and are safe to call from background threads.
/// </summary>
public interface IImageService
{
    /// <summary>
    /// True when the extension (including the leading dot) is a format this service can decode.
    /// </summary>
    bool IsSupported(string extension);

    /// <summary>
    /// Decodes the image at full quality with EXIF orientation applied. Returns null on failure.
    /// </summary>
    Bitmap? LoadFull(string path);

    /// <summary>
    /// Decodes a downscaled preview no wider than <paramref name="targetWidth"/>, preserving aspect
    /// ratio and EXIF orientation. Returns null on failure.
    /// </summary>
    Bitmap? LoadThumbnail(string path, int targetWidth);
}
