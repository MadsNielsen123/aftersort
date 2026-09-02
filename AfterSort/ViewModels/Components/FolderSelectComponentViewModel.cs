using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AfterSort.ViewModels.Components;

public partial class FolderSelectComponentViewModel : ViewModelBase
{
    public FolderSelectComponentViewModel()
    {
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    [ObservableProperty]
    public partial string Title { get; set; } = "Folders";

    public ObservableCollection<FolderItemViewModelBase> Items { get; } = [];

    /// <summary>
    /// False while the list is empty. The empty folder list is then hidden entirely, so the "+"
    /// tile sits exactly where the first folder will appear once it's filled in.
    /// </summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// Factory that creates new items when the "+" tile is pressed.
    /// Set this from the parent view model to control which item type gets created.
    /// </summary>
    public Func<Task<FolderItemViewModelBase?>>? ItemFactory { get; set; }

    /// <summary>
    /// Adds a new item via the <see cref="ItemFactory"/> delegate.
    /// </summary>
    [RelayCommand]
    private async Task AddItem()
    {
        if (ItemFactory is null)
            return;

        var newItem = await ItemFactory();
        if (newItem is null)
            return;

        newItem.Owner = this;
        Items.Add(newItem);
    }
}
