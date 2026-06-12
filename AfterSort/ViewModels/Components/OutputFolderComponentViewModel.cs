using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels.Components;

public partial class OutputFolderComponentViewModel : FolderItemViewModelBase
{
    #region Fields
    #endregion

    #region Properties

    /// <summary>
    /// Whether this output folder is currently selected/checked by the user.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    #endregion

    #region Constructors
    #endregion

    #region Lifecycle
    #endregion

    #region Methods
    #endregion
}
