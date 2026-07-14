using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lionear.SqlExplorer.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace Lionear.SqlExplorer.Providers.MsSql;

/// <summary>
/// Route-B cell-action view: the details of a blocking session (opened from the Activity Monitor's
/// <c>blocking_session_id</c> cell). Shows who the blocker is and the statement it is running, with a Kill
/// button that terminates it and closes the dialog — the monitor then refreshes and the block clears.
/// Built in code (no XAML) so the plugin stays self-contained across the ALC boundary, same as
/// <see cref="DatabasePropertiesView"/>.
/// </summary>
public sealed class BlockingSessionView : UserControl
{
    private readonly int _sessionId;
    private readonly SqlConnectionStringBuilder _connectionString;

    public BlockingSessionView(CellActionContext context)
    {
        _sessionId = int.Parse(Convert.ToString(context.CellValue, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        _connectionString = new SqlConnectionStringBuilder(context.Profile.ConnectionString);

        var (grid, values) = BuildGrid();

        var query = new TextBox
        {
            Text = "…",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 220,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var status = new TextBlock { Opacity = 0.8, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };

        var killButton = new Button
        {
            Content = $"Kill session {_sessionId}",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0)
        };
        killButton.Click += async (_, _) => await KillAsync(killButton, status);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(4),
                MinWidth = 560,
                Spacing = 2,
                Children =
                {
                    grid,
                    new TextBlock { Text = "Running statement", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) },
                    query,
                    killButton,
                    status
                }
            }
        };

        _ = LoadAsync(values, query);
    }

    private static (Control Grid, Dictionary<string, TextBlock> Values) BuildGrid()
    {
        var values = new Dictionary<string, TextBlock>();
        var stack = new StackPanel { Spacing = 2 };

        void Row(string label, string key)
        {
            var value = new TextBlock { Text = "…", TextWrapping = TextWrapping.Wrap, Opacity = 0.9 };
            values[key] = value;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), Margin = new Thickness(0, 1, 0, 1) };
            var name = new TextBlock { Text = label, Opacity = 0.65, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(value, 1);
            row.Children.Add(name);
            row.Children.Add(value);
            stack.Children.Add(row);
        }

        Row("Session ID", "session_id");
        Row("Login", "login_name");
        Row("Host", "host_name");
        Row("Database", "database");
        Row("Status", "status");
        Row("Command", "command");
        Row("CPU time", "cpu_time");
        Row("Elapsed", "total_elapsed_time");
        return (stack, values);
    }

    private async Task LoadAsync(Dictionary<string, TextBlock> values, TextBox query)
    {
        void Set(string key, string? text) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (values.TryGetValue(key, out var tb))
                {
                    tb.Text = string.IsNullOrEmpty(text) ? "—" : text;
                }
            });

        try
        {
            await using var connection = new SqlConnection(_connectionString.ConnectionString);
            await connection.OpenAsync();

            const string sql = """
                SELECT s.session_id, s.login_name, s.host_name, DB_NAME(r.database_id) AS [database],
                       s.status, r.command, r.cpu_time, r.total_elapsed_time, t.text AS query
                FROM sys.dm_exec_sessions s
                LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
                OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE s.session_id = @spid
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@spid", _sessionId);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                string? S(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                Set("session_id", S(0));
                Set("login_name", S(1));
                Set("host_name", S(2));
                Set("database", S(3));
                Set("status", S(4));
                Set("command", S(5));
                string? Ms(int i) => reader.IsDBNull(i) ? null : HumanDuration(Convert.ToInt64(reader.GetValue(i)));
                Set("cpu_time", Ms(6));
                Set("total_elapsed_time", Ms(7));
                var queryText = S(8);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => query.Text = string.IsNullOrEmpty(queryText) ? "(no active statement — session is idle)" : queryText);
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => query.Text = "(session not found — it may have ended)");
            }
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => query.Text = $"(unavailable: {ex.Message})");
        }
    }

    // Render a millisecond duration (cpu_time / total_elapsed_time) as human-readable time — sub-second
    // stays in ms, otherwise h/m/s with the zero units dropped (e.g. 90000 -> "1m 30s", 3660000 -> "1h 1m").
    private static string HumanDuration(long milliseconds)
    {
        if (milliseconds < 1000)
        {
            return $"{milliseconds} ms";
        }

        var span = TimeSpan.FromMilliseconds(milliseconds);
        var parts = new List<string>(3);
        var hours = (int)span.TotalHours;
        if (hours > 0)
        {
            parts.Add($"{hours}h");
        }

        if (span.Minutes > 0)
        {
            parts.Add($"{span.Minutes}m");
        }

        if (span.Seconds > 0 || parts.Count == 0)
        {
            parts.Add($"{span.Seconds}s");
        }

        return string.Join(" ", parts);
    }

    private async Task KillAsync(Button killButton, TextBlock status)
    {
        killButton.IsEnabled = false;
        try
        {
            await using var connection = new SqlConnection(_connectionString.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand($"KILL {_sessionId}", connection);
            await command.ExecuteNonQueryAsync();

            // Killed → close the dialog; the monitor refreshes on return and the block clears.
            (TopLevel.GetTopLevel(this) as Window)?.Close();
        }
        catch (Exception ex)
        {
            status.IsVisible = true;
            status.Text = $"Kill failed: {ex.Message}";
            killButton.IsEnabled = true;
        }
    }
}
