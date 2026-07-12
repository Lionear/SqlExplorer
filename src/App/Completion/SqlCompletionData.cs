using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Lionear.SqlExplorer.Core.Completion;

namespace Lionear.SqlExplorer.App.Completion;

/// <summary>Adapts a <see cref="CompletionItem"/> (1.3) to AvaloniaEdit's <see cref="ICompletionData"/>.</summary>
public sealed class SqlCompletionData(CompletionItem item) : ICompletionData
{
    public IImage? Image => null;

    public string Text { get; } = item.Text;

    public object Content => Text;

    public object Description => item.Detail ?? item.Kind.ToString();

    // Columns are the most common pick, then tables, then keywords last — matches how often each
    // kind is actually what you were reaching for while typing a query.
    public double Priority => item.Kind switch
    {
        CompletionKind.Column => 2,
        CompletionKind.Table => 1,
        _ => 0
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
