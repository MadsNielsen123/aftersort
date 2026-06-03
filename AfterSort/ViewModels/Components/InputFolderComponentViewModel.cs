using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels.Components;

public partial class InputFolderComponentViewModel : ViewModelBase
{
    #region Fields

    [ObservableProperty]
    private string _folderName = string.Empty;

    // Only these two fields affect the computed IsCompleted property.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    private int _processedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    private int _totalCount;

    #endregion

    #region Properties

    /// <summary>
    /// True when all files have been processed and there is at least one file.
    /// </summary>
    public bool IsCompleted => ProcessedCount == TotalCount && TotalCount > 0;

    #endregion

    #region Constructors

    public InputFolderComponentViewModel()
    {
    }

    #endregion

    #region Lifecycle
    #endregion

    #region Methods
    #endregion
}