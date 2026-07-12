using System.Collections.ObjectModel;
using Lionear.SqlExplorer.Core.Editing;
using Lionear.SqlExplorer.Core.Import;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>One CSV column's target — auto-matched by exact name (case-insensitive), user-editable.</summary>
public sealed partial class ImportColumnMapping(string csvColumn, IReadOnlyList<string> targetOptions, string selectedTarget) : ObservableObject
{
    public string CsvColumn { get; } = csvColumn;

    public IReadOnlyList<string> TargetOptions { get; } = targetOptions;

    [ObservableProperty]
    private string _selectedTarget = selectedTarget;
}

/// <summary>
/// Backs the CSV-import dialog: shows a preview of the parsed file, lets the user map each CSV column
/// to a target-table column (or skip it), then builds parameterised INSERTs the same way the editable
/// grid's save-flow does (<see cref="CrudStatementBuilder.Coerce"/>) — MainViewModel runs them via
/// <c>ExecuteBatchAsync</c>, this dialog never touches the database itself.
/// </summary>
public partial class ImportCsvDialogViewModel : ViewModelBase
{
    private CsvDocument _csv = new([], []);
    private IReadOnlyList<ResultColumn> _targetColumns = [];

    public ImportCsvDialogViewModel(ILocalizer localizer)
    {
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public ObservableCollection<ImportColumnMapping> Mappings { get; } = [];

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowCountText))]
    private int _rowCount;

    public string RowCountText => Loc.Get("ImportRowCount", RowCount);

    public void Configure(CsvDocument csv, IReadOnlyList<ResultColumn> targetColumns)
    {
        _csv = csv;
        _targetColumns = targetColumns;

        var skip = Loc["ImportSkip"];
        var options = new List<string> { skip };
        options.AddRange(targetColumns.Select(TargetName));

        Mappings.Clear();
        foreach (var header in csv.Headers)
        {
            var match = options.FirstOrDefault(o => o != skip && string.Equals(o, header, StringComparison.OrdinalIgnoreCase)) ?? skip;
            Mappings.Add(new ImportColumnMapping(header, options, match));
        }

        PreviewText = string.Join('\n', csv.Rows.Take(5).Select(r => string.Join(" | ", r)));
        RowCount = csv.Rows.Count;
    }

    private static string TargetName(ResultColumn column) => column.BaseColumn ?? column.Name;

    /// <summary>One parameterised INSERT per CSV row, mapped columns only. Empty when every column
    /// was skipped.</summary>
    public IReadOnlyList<SqlStatement> BuildInsertStatements(ISqlDialect dialect, string qualifiedTable)
    {
        var skip = Loc["ImportSkip"];
        var mapped = Mappings
            .Select((m, i) => (Index: i, Target: m.SelectedTarget))
            .Where(m => m.Target != skip)
            .ToList();

        if (mapped.Count == 0)
        {
            return [];
        }

        var columnList = string.Join(", ", mapped.Select(m => dialect.QuoteIdentifier(m.Target)));
        var statements = new List<SqlStatement>();
        foreach (var row in _csv.Rows)
        {
            var parameters = new List<SqlParam>();
            var placeholders = new List<string>();
            foreach (var (index, target) in mapped)
            {
                var targetColumn = _targetColumns.First(c => TargetName(c) == target);
                var raw = index < row.Length ? row[index] : null;
                var value = string.IsNullOrEmpty(raw) ? null : CrudStatementBuilder.Coerce(raw, targetColumn.ClrType);

                var name = $"p{parameters.Count}";
                placeholders.Add($"@{name}");
                parameters.Add(new SqlParam(name, value));
            }

            statements.Add(new SqlStatement(
                $"INSERT INTO {qualifiedTable} ({columnList}) VALUES ({string.Join(", ", placeholders)})", parameters));
        }

        return statements;
    }
}
