using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AfterSort.ViewModels.Components;

public abstract partial class FolderItemViewModelBase : ViewModelBase
{
    [ObservableProperty]
    public partial string FolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderName { get; set; } = string.Empty;

    /// <summary>The list this folder belongs to. Set by <see cref="FolderSelectComponentViewModel"/>.</summary>
    public FolderSelectComponentViewModel? Owner { get; set; }

    /// <summary>Removes this folder from its list — bound to the folder row's X button.</summary>
    [RelayCommand]
    private void Remove() => Owner?.Items.Remove(this);
}
