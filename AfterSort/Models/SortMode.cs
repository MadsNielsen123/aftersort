namespace AfterSort.Models;

/// <summary>
/// Available sorting modes for the application.
/// </summary>
public enum SortMode
{
    /// <summary>
    /// Files are displayed one at a time and can be sorted into multiple output folders.
    /// </summary>
    Multiple,

    /// <summary>
    /// Like <see cref="Multiple"/>, but ticking an output folder immediately advances to the next file.
    /// </summary>
    Single,
}
