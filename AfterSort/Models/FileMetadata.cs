namespace AfterSort.Models;

/// <summary>
/// Stores metadata about a file that can be displayed to the user.
/// </summary>
public class FileMetadata
{
    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long SizeInBytes { get; init; }

    /// <summary>
    /// Human-readable file size (e.g. "1.2 MB").
    /// </summary>
    public string FormattedSize => FormatSize(SizeInBytes);

    /// <summary>
    /// File extension including the dot (e.g. ".png").
    /// </summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>
    /// When the file was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// When the file was last modified.
    /// </summary>
    public DateTime ModifiedAt { get; init; }

    /// <summary>
    /// When the file was last accessed.
    /// </summary>
    public DateTime LastAccessedAt { get; init; }

    /// <summary>
    /// Whether the file is read-only on disk.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Creates a <see cref="FileMetadata"/> from a <see cref="FileInfo"/>.
    /// </summary>
    public static FileMetadata FromFileInfo(FileInfo fileInfo)
    {
        return new FileMetadata
        {
            SizeInBytes = fileInfo.Length,
            Extension = fileInfo.Extension,
            CreatedAt = fileInfo.CreationTime,
            ModifiedAt = fileInfo.LastWriteTime,
            LastAccessedAt = fileInfo.LastAccessTime,
            IsReadOnly = fileInfo.IsReadOnly,
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
