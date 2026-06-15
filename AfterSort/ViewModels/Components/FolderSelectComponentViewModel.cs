using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AfterSort.ViewModels.Components;

public partial class FolderSelectComponentViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Title { get; set; } = "Folders";

    public ObservableCollection<ViewModelBase> Items { get; } = [];

    /// <summary>
    /// The currently selected item in the list.
    /// </summary>
    [ObservableProperty]
    public partial ViewModelBase? SelectedItem { get; set; }

    /// <summary>
    /// Factory that creates new items when the "+" button is pressed.
    /// Set this from the parent view model to control which item type gets created.
    /// </summary>
    public Func<Task<ViewModelBase?>>? ItemFactory { get; set; }

    /// <summary>
    /// Adds a new item via the <see cref="ItemFactory"/> delegate.
    /// </summary>
    [RelayCommand]
    private async Task AddItem()
    {
        if (ItemFactory is null)
            return;

        var newItem = await ItemFactory();
        if (newItem is not null)
        {
            Items.Add(newItem);
        }
    }

    /// <summary>
    /// Removes the currently selected item from the list.
    /// </summary>
    [RelayCommand]
    private void RemoveItem()
    {
        if (SelectedItem is null)
            return;

        var itemToRemove = SelectedItem;

        SelectedItem = null;
        Items.Remove(itemToRemove);
    }
}