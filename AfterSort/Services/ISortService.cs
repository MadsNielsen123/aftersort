using AfterSort.Models;
using AfterSort.ViewModels.Components;

namespace AfterSort.Services;

/// <summary>
/// Service responsible for copying files to output folders during sorting.
/// </summary>
public interface ISortService
{
    /// <summary>
    /// Copies a file to all selected (checked) output folders.
    /// Returns the number of folders the file was copied to.
    /// </summary>
    Task<int> SortFileAsync(FileItem file, IEnumerable<OutputFolderComponentViewModel> outputFolders);
}
