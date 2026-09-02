using Avalonia.Controls;
using Avalonia.Input;
using AfterSort.Models;
using AfterSort.ViewModels.Pages;

namespace AfterSort.Views.Pages;

public partial class MainPageView : UserControl
{
    public MainPageView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Mirrors the sort mode list selection onto the view model (list order matches the enum).
    /// </summary>
    private void SortMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainPageViewModel vm && sender is ListBox list && list.SelectedIndex >= 0)
            vm.CurrentSortMode = (SortMode)list.SelectedIndex;
    }

    /// <summary>
    /// When the user presses Enter in the file number TextBox, navigate to that file
    /// and move focus away so the keyboard shortcuts work again.
    /// </summary>
    private void FileNumberInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            NavigateToTypedNumber(textBox);

            // Move focus to the parent so the TextBox loses focus
            this.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && sender is TextBox escBox)
        {
            // Revert to current number and defocus
            ResetTextBoxToCurrentNumber(escBox);
            this.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// When the TextBox loses focus (user clicks away), navigate to the typed file number.
    /// If the input is invalid, the text is reset to the current file number.
    /// </summary>
    private void FileNumberInput_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            NavigateToTypedNumber(textBox);
        }
    }

    /// <summary>
    /// Attempts to navigate to the file number typed in the TextBox.
    /// If the input is invalid, resets the TextBox to the current file number.
    /// </summary>
    private void NavigateToTypedNumber(TextBox textBox)
    {
        if (DataContext is MainPageViewModel vm)
        {
            var input = textBox.Text;
            if (int.TryParse(input, out var number) && number >= 1 && number <= vm.TotalFileCount
                && number != vm.CurrentFileNumber)
            {
                vm.GoToFileCommand.Execute(input);
            }
            else
            {
                // Reset to current number (handles invalid input or same number)
                ResetTextBoxToCurrentNumber(textBox);
            }
        }
    }

    /// <summary>
    /// Resets the TextBox text back to the current file number from the ViewModel.
    /// </summary>
    private void ResetTextBoxToCurrentNumber(TextBox textBox)
    {
        if (DataContext is MainPageViewModel vm)
        {
            textBox.Text = vm.CurrentFileNumber.ToString();
        }
    }
}