using AfterSort.Models;

namespace AfterSort.Services;

/// <summary>
/// File operations behind an output folder's live checkbox: what the folder already contains,
/// and adding/removing a single file from it.
/// </summary>
public interface ISortService
{
    /// <summary>
    /// Lists the file names (not paths) currently present in an output folder.
    /// Throws when the folder is missing or unreadable.
    /// </summary>
    Task<HashSet<string>> ScanFolderAsync(string folderPath);

    /// <summary>
    /// Copies a file into an output folder. Copies via a temporary file so a partially written
    /// file is never visible under the final name. No-op when an identical copy already exists.
    /// </summary>
    Task CopyToFolderAsync(FileItem file, string folderPath, CancellationToken token = default);

    /// <summary>
    /// Deletes the file's copy from an output folder. No-op when it isn't there.
    /// </summary>
    Task RemoveFromFolderAsync(FileItem file, string folderPath, CancellationToken token = default);

    /// <summary>True when a copy of the file currently exists in the output folder.</summary>
    bool ExistsInFolder(FileItem file, string folderPath);

    /// <summary>
    /// True when a file name is one of our own in-progress copies rather than a finished file.
    /// </summary>
    bool IsInProgressArtifact(string fileName);
}
