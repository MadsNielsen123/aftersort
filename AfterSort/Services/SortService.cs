using AfterSort.Models;
using AfterSort.ViewModels.Components;

namespace AfterSort.Services;

/// <summary>
/// Default implementation of <see cref="ISortService"/>.
/// Copies files to selected output folders and updates their sort state.
/// </summary>
public class SortService : ISortService
{
    /// <inheritdoc/>
    public async Task<int> SortFileAsync(FileItem file, IEnumerable<OutputFolderComponentViewModel> outputFolders)
    {
        var copiedCount = 0;

        foreach (var outputFolder in outputFolders)
        {
            if (!outputFolder.IsSelected)
                continue;

            var destinationPath = Path.Combine(outputFolder.FolderPath, file.Name);

            // Don't overwrite if the file already exists
            if (!File.Exists(destinationPath))
            {
                await Task.Run(() => File.Copy(file.FullPath, destinationPath));
            }

            // Track the output state
            var existingState = file.OutputStates.FirstOrDefault(s => s.OutputFolder == outputFolder);
            if (existingState is not null)
            {
                existingState.IsSaved = true;
            }
            else
            {
                file.OutputStates.Add(new FileOutputState
                {
                    OutputFolder = outputFolder,
                    IsSaved = true,
                });
            }

            copiedCount++;
        }

        if (copiedCount > 0)
        {
            file.IsSorted = true;
        }

        return copiedCount;
    }
}
