using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels.Components;

public partial class OutputFolderComponentViewModel : FolderItemViewModelBase
{
    /// <summary>Whether this output folder is currently selected/checked by the user.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
