using Avalonia.Media.Imaging;
using ImageMagick;
using SkiaSharp;
using Svg.Skia;

namespace AfterSort.Services;

/// <summary>
/// Routes decoding by format: Skia for standard raster (with EXIF orientation),
/// Svg.Skia for vector SVG, and Magick.NET for HEIC/HEIF (which Skia's Windows build can't decode).
/// </summary>
public class ImageService : IImageService
{
    // Longest side used when rasterising an SVG for the full-quality view.
    private const int SvgFullSize = 1600;

    private static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp",
    };

    private static readonly HashSet<string> HeifExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif",
    };

    public bool IsSupported(string extension) =>
        RasterExtensions.Contains(extension) || HeifExtensions.Contains(extension) || IsSvg(extension);

    public Bitmap? LoadFull(string path) => Load(path, targetWidth: null);

    public Bitmap? LoadThumbnail(string path, int targetWidth) => Load(path, targetWidth);

    private static Bitmap? Load(string path, int? targetWidth)
    {
        var extension = Path.GetExtension(path);

        if (IsSvg(extension))
            return LoadSvg(path, targetWidth);

        if (HeifExtensions.Contains(extension))
            return LoadHeif(path, targetWidth);

        return targetWidth is int width ? LoadRasterThumbnail(path, width) : LoadRaster(path);
    }

    private static bool IsSvg(string extension) =>
        extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);

    // === HEIC / HEIF via Magick.NET ===

    private static Bitmap? LoadHeif(string path, int? targetWidth)
    {
        try
        {
            using var image = new MagickImage(path);
            image.AutoOrient();

            if (targetWidth is int width && width < image.Width)
                image.Resize(new MagickGeometry((uint)width, 0) { IgnoreAspectRatio = false });

            using var ms = new MemoryStream();
            image.Write(ms, MagickFormat.Png);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    // === SVG via Svg.Skia ===

    private static Bitmap? LoadSvg(string path, int? targetWidth)
    {
        try
        {
            using var svg = new SKSvg();
            if (svg.Load(path) is null || svg.Picture is not { } picture)
                return null;

            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            // Thumbnails fit the requested width; the full view fits a fixed longest side.
            var target = targetWidth ?? (int)Math.Max(bounds.Width, bounds.Height);
            var cap = targetWidth is null ? SvgFullSize : target;
            var scale = Math.Min(cap / Math.Max(bounds.Width, bounds.Height), targetWidth is null ? 4f : 1f);

            var pixelW = Math.Max(1, (int)(bounds.Width * scale));
            var pixelH = Math.Max(1, (int)(bounds.Height * scale));

            var info = new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(scale);
            surface.Canvas.DrawPicture(picture);
            surface.Canvas.Flush();

            return Encode(surface.Snapshot(), SKEncodedImageFormat.Png, 100);
        }
        catch
        {
            return null;
        }
    }

    // === Standard raster via Skia (with EXIF orientation) ===

    private static Bitmap? LoadRaster(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
                return DecodeDirect(path);

            if (codec.EncodedOrigin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
                return DecodeDirect(path);

            using var decoded = new SKBitmap(codec.Info);
            codec.GetPixels(codec.Info, decoded.GetPixels());

            var oriented = ApplyExifOrientation(decoded, codec.EncodedOrigin);
            var bitmap = Encode(SKImage.FromBitmap(oriented), SKEncodedImageFormat.Jpeg, 100);

            if (oriented != decoded)
                oriented.Dispose();

            return bitmap;
        }
        catch
        {
            return DecodeDirect(path);
        }
    }

    private static Bitmap? LoadRasterThumbnail(string path, int targetWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
                return DecodeToWidth(path, targetWidth);

            var ratio = (float)targetWidth / codec.Info.Width;
            if (ratio >= 1f)
                return LoadRaster(path); // Already smaller than the target — decode at native size.

            using var decoded = new SKBitmap(codec.Info);
            codec.GetPixels(codec.Info, decoded.GetPixels());

            var targetInfo = new SKImageInfo((int)(codec.Info.Width * ratio), (int)(codec.Info.Height * ratio));
            using var resized = decoded.Resize(targetInfo, new SKSamplingOptions(SKFilterMode.Linear));

            var oriented = ApplyExifOrientation(resized, codec.EncodedOrigin);
            var bitmap = Encode(SKImage.FromBitmap(oriented), SKEncodedImageFormat.Jpeg, 80);

            if (oriented != resized)
                oriented.Dispose();

            return bitmap;
        }
        catch
        {
            return DecodeToWidth(path, targetWidth);
        }
    }

    private static Bitmap? DecodeDirect(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? DecodeToWidth(string path, int targetWidth)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, targetWidth);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap Encode(SKImage image, SKEncodedImageFormat format, int quality)
    {
        using (image)
        using (var data = image.Encode(format, quality))
        {
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
    }

    /// <summary>
    /// Applies EXIF orientation via canvas transforms. Returns the original bitmap when no transform
    /// is needed, otherwise a new transformed bitmap the caller must dispose.
    /// </summary>
    private static SKBitmap ApplyExifOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return bitmap;

        // 90°/270° rotations and transposes swap width and height.
        var needsSwap = origin is SKEncodedOrigin.LeftBottom
                              or SKEncodedOrigin.RightTop
                              or SKEncodedOrigin.LeftTop
                              or SKEncodedOrigin.RightBottom;

        var w = needsSwap ? bitmap.Height : bitmap.Width;
        var h = needsSwap ? bitmap.Width : bitmap.Height;

        var result = new SKBitmap(w, h);
        using var canvas = new SKCanvas(result);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight: // Flip horizontal
                canvas.Scale(-1, 1, w / 2f, 0);
                break;
            case SKEncodedOrigin.BottomRight: // Rotate 180
                canvas.RotateDegrees(180, w / 2f, h / 2f);
                break;
            case SKEncodedOrigin.BottomLeft: // Flip vertical
                canvas.Scale(1, -1, 0, h / 2f);
                break;
            case SKEncodedOrigin.LeftTop: // Transpose
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1, 0, 0);
                break;
            case SKEncodedOrigin.RightTop: // Rotate 90 CW
                canvas.Translate(w, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom: // Transverse
                canvas.Translate(w, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1, 0, 0);
                break;
            case SKEncodedOrigin.LeftBottom: // Rotate 270 CW
                canvas.Translate(0, h);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(bitmap, 0, 0);
        canvas.Flush();
        return result;
    }
}
