using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels.Components;

public abstract partial class FolderItemViewModelBase : ViewModelBase
{
    [ObservableProperty]
    public partial string FolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderName { get; set; } = string.Empty;
}
