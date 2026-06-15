using System.Collections.ObjectModel;
using AfterSort.ViewModels.Components;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.Models;

/// <summary>
/// Represents a file within an input folder. Stores identity, parent reference,
/// optional in-memory data, output directory save states, and metadata.
/// </summary>
public partial class FileItem : ObservableObject
{
    /// <summary>
    /// The file name including extension (e.g. "photo.jpg").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The full absolute path to the file on disk.
    /// </summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// Reference to the parent folder that contains this file.
    /// </summary>
    public required FolderItemViewModelBase ParentFolder { get; init; }

    /// <summary>
    /// The file's raw data loaded into memory.
    /// Null when the data has not been loaded yet — a background task can populate this
    /// on demand (e.g. when the user selects the file for preview in the main content area).
    /// </summary>
    [ObservableProperty]
    public partial byte[]? Data { get; set; }

    /// <summary>
    /// Whether <see cref="Data"/> has been loaded into memory.
    /// </summary>
    public bool IsDataLoaded => Data is not null;

    /// <summary>
    /// Per-output-folder save states for this file.
    /// Each entry tracks whether this file was saved into a given output directory.
    /// </summary>
    public ObservableCollection<FileOutputState> OutputStates { get; } = [];

    /// <summary>
    /// Metadata about the file (size, extension, dates, etc.).
    /// </summary>
    public required FileMetadata Metadata { get; init; }

    /// <summary>
    /// Snappy preload data (e.g. low-res Bitmap, or truncated string).
    /// </summary>
    [ObservableProperty]
    public partial object? PreloadData { get; set; }

    /// <summary>
    /// Whether this file has been sorted (copied to at least one output folder).
    /// </summary>
    [ObservableProperty]
    public partial bool IsSorted { get; set; }

    /// <summary>
    /// Whether this file is a supported image type for preview (including HEIC/HEIF and SVG).
    /// </summary>
    public bool IsImage => Metadata.Extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico" or ".webp"
            or ".heic" or ".heif" or ".svg" => true,
        _ => false,
    };

    /// <summary>
    /// Whether this file is a supported video type.
    /// </summary>
    public bool IsVideo => Metadata.Extension.ToLowerInvariant() switch
    {
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".wmv" or ".flv" => true,
        _ => false,
    };

    /// <summary>
    /// Creates a <see cref="FileItem"/> from a <see cref="FileInfo"/> and a parent folder.
    /// File data is not loaded (left null).
    /// </summary>
    public static FileItem FromFileInfo(FileInfo fileInfo, FolderItemViewModelBase parentFolder)
    {
        return new FileItem
        {
            Name = fileInfo.Name,
            FullPath = fileInfo.FullName,
            ParentFolder = parentFolder,
            Metadata = FileMetadata.FromFileInfo(fileInfo),
        };
    }
}
