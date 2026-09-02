using AfterSort.Models;

namespace AfterSort.Services;

/// <inheritdoc cref="ISortService"/>
public class SortService : ISortService
{
    /// <summary>Suffix for in-progress copies, so a half-written file never looks "present".</summary>
    private const string TempSuffix = ".aftersort-part";

    /// <inheritdoc/>
    public Task<HashSet<string>> ScanFolderAsync(string folderPath) => Task.Run(() =>
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folderPath))
        {
            var name = Path.GetFileName(path);
            if (!IsInProgressArtifact(name))
                names.Add(name);
        }

        return names;
    });

    /// <inheritdoc/>
    public async Task CopyToFolderAsync(FileItem file, string folderPath, CancellationToken token = default)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        if (!File.Exists(file.FullPath))
            throw new FileNotFoundException($"Source file no longer exists: {file.Name}");

        var destination = Path.Combine(folderPath, file.Name);
        if (string.Equals(destination, file.FullPath, StringComparison.OrdinalIgnoreCase))
            return; // Source and destination are the same file — nothing to copy.

        var temp = destination + TempSuffix;

        try
        {
            await using (var source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(target, token);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task RemoveFromFolderAsync(FileItem file, string folderPath, CancellationToken token = default) => Task.Run(() =>
    {
        var destination = Path.Combine(folderPath, file.Name);
        if (string.Equals(destination, file.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Refusing to delete the source file: {file.Name}");

        if (File.Exists(destination))
            File.Delete(destination);

        TryDelete(destination + TempSuffix);
    }, token);

    /// <inheritdoc/>
    public bool ExistsInFolder(FileItem file, string folderPath) =>
        File.Exists(Path.Combine(folderPath, file.Name));

    /// <inheritdoc/>
    public bool IsInProgressArtifact(string fileName) =>
        fileName.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup — never mask the original failure.
        }
    }
}
