using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// The scrollable object-tree host shared by <see cref="BackupSelectionView"/> and
/// <see cref="RestoreSelectionView"/>. Padding lives on the tree's <c>Margin</c>, not the ScrollViewer's
/// <c>Padding</c>: Avalonia 12's ScrollViewer excludes its own Padding from <c>Extent</c> while still
/// shifting the rendered content by it, which permanently strands the last <c>Padding.Bottom</c> pixels
/// below the reachable scroll range (the last row in the list was unreachable no matter how far you
/// scrolled). <c>HorizontalScrollBarVisibility</c> is Disabled so a long object name is ellipsized instead
/// of measuring the tree at infinite width and triggering that overflow.
/// </summary>
internal static class ObjectTreeScaffold
{
    public static Border BuildScrollHost(StackPanel tree)
    {
        tree.Margin = new Thickness(8, 8, 8, 12);
        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            CornerRadius = new CornerRadius(4),
            MinHeight = 120,
            Margin = new Thickness(0, 4, 0, 0),
            Child = new ScrollViewer
            {
                Content = tree,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
    }
}
