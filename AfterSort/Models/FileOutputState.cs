using AfterSort.ViewModels.Components;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.Models;

/// <summary>
/// Tracks whether a file has been saved into a specific output directory.
/// </summary>
public partial class FileOutputState : ObservableObject
{
    #region Properties

    /// <summary>
    /// The output folder this state refers to.
    /// </summary>
    public required OutputFolderComponentViewModel OutputFolder { get; init; }

    /// <summary>
    /// Whether the file has been saved into this output folder.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSaved { get; set; }

    #endregion
}
