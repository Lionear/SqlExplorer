using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using SqlExplorer.Core.Completion;

namespace SqlExplorer.App.Completion;

/// <summary>Adapts a <see cref="CompletionItem"/> (1.3) to AvaloniaEdit's <see cref="ICompletionData"/>.</summary>
public sealed class SqlCompletionData(CompletionItem item) : ICompletionData
{
    public IImage? Image => CompletionIcons.For(item.Kind);

    public string Text { get; } = item.Text;

    public object Content => Text;

    public object Description => item.Detail ?? item.Kind.ToString();

    // A FK-derived join condition is almost always what you want right after ON, so it leads; then columns,
    // then tables and functions, then keywords last — matching how often each kind is what you were reaching for.
    public double Priority => item.Kind switch
    {
        CompletionKind.Join => 3,
        CompletionKind.Column => 2,
        CompletionKind.Table => 1,
        CompletionKind.Function => 1,
        _ => 0
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
