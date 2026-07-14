using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lionear.SqlExplorer.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace Lionear.SqlExplorer.Providers.MsSql;

/// <summary>
/// Route-B info view (Notes §4.4, third capability): SQL Server's "Database Properties" dialog, General
/// page. Mirrors SSMS' layout — a page rail on the left (only General is filled; the other SSMS pages are
/// listed but disabled so they are visibly "later, additive") and a read-only property grid on the right.
/// The view queries its own live data through the <see cref="NodeInfoContext"/>'s profile; built in code
/// (no XAML) so the plugin stays self-contained across the ALC boundary, same as <see cref="MsSqlAdvancedView"/>.
/// </summary>
public sealed class DatabasePropertiesView : UserControl
{
    // SSMS' General-page rail. Only "General" is active in v1; the rest are placeholders (disabled) so the
    // extension points are visible without promising behaviour that isn't there.
    private static readonly string[] Pages =
        ["General", "Files", "Filegroups", "Options", "Change Tracking", "Permissions", "Extended Properties", "Query Store"];

    public DatabasePropertiesView(NodeInfoContext context)
    {
        var database = context.Node.Name;

        var rail = new ListBox
        {
            Width = 150,
            ItemsSource = Pages,
            SelectedIndex = 0,
            Background = Brushes.Transparent
        };
        // Only General carries content in v1; the others are shown greyed to advertise the future pages.
        rail.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem item && e.Index > 0)
            {
                item.IsEnabled = false;
                item.Opacity = 0.45;
            }
        };

        var (grid, values) = BuildGeneralGrid();
        _ = LoadAsync(context, database, values);

        var content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 0, 0, 0),
                Spacing = 4,
                Children = { grid }
            }
        };

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), MinWidth = 560, MinHeight = 380 };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(content, 1);
        layout.Children.Add(rail);
        layout.Children.Add(content);
        Content = layout;
    }

    // Section-grouped read-only rows, mirroring SSMS' Backup / Database / Maintenance groups. Each value
    // TextBlock is handed back keyed by field so LoadAsync can fill it once the queries return.
    private static (Control Grid, Dictionary<string, TextBlock> Values) BuildGeneralGrid()
    {
        var values = new Dictionary<string, TextBlock>();
        var stack = new StackPanel { Spacing = 2 };

        void Section(string header)
        {
            stack.Children.Add(new TextBlock
            {
                Text = header,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 12, 0, 4)
            });
        }

        void Row(string label, string key)
        {
            var value = new TextBlock { Text = "…", TextWrapping = TextWrapping.Wrap, Opacity = 0.9 };
            values[key] = value;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), Margin = new Thickness(0, 1, 0, 1) };
            var name = new TextBlock { Text = label, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(value, 1);
            row.Children.Add(name);
            row.Children.Add(value);
            stack.Children.Add(row);
        }

        Section("Backup");
        Row("Last Database Backup", "lastBackup");
        Row("Last Database Log Backup", "lastLogBackup");

        Section("Database");
        Row("Name", "name");
        Row("Status", "status");
        Row("Owner", "owner");
        Row("Date Created", "created");
        Row("Size", "size");
        Row("Space Available", "free");
        Row("Number of Users", "users");
        Row("Memory Allocated To Memory Optimized Objects", "xtpAlloc");
        Row("Memory Used By Memory Optimized Objects", "xtpUsed");

        Section("Maintenance");
        Row("Collation", "collation");

        return (stack, values);
    }

    private static async Task LoadAsync(NodeInfoContext context, string database, Dictionary<string, TextBlock> values)
    {
        void Set(string key, string? text)
        {
            if (values.TryGetValue(key, out var tb))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => tb.Text = string.IsNullOrEmpty(text) ? "—" : text);
            }
        }

        values["name"].Text = database;

        try
        {
            // Repoint at the target database so FILEPROPERTY/sys.database_files resolve to it.
            var connectionString = new SqlConnectionStringBuilder(context.Profile.ConnectionString) { InitialCatalog = database }.ConnectionString;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // Metadata from the server-wide catalog (works from any database context).
            await RunAsync(connection,
                """
                SELECT d.state_desc, SUSER_SNAME(d.owner_sid), d.create_date, d.collation_name
                FROM sys.databases d WHERE d.name = @db
                """,
                cmd => cmd.Parameters.AddWithValue("@db", database),
                reader =>
                {
                    Set("status", reader.IsDBNull(0) ? null : reader.GetString(0));
                    Set("owner", reader.IsDBNull(1) ? null : reader.GetString(1));
                    Set("created", reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("g"));
                    Set("collation", reader.IsDBNull(3) ? null : reader.GetString(3));
                });

            // Allocated / free space from the current database's data files (FILEPROPERTY is per-current-db).
            await RunAsync(connection,
                """
                SELECT
                    CAST(SUM(CAST(size AS bigint)) * 8.0 / 1024 AS decimal(18,2)),
                    CAST(SUM(CAST(size - FILEPROPERTY(name, 'SpaceUsed') AS bigint)) * 8.0 / 1024 AS decimal(18,2))
                FROM sys.database_files WHERE type IN (0, 1)
                """,
                _ => { },
                reader =>
                {
                    Set("size", reader.IsDBNull(0) ? null : $"{reader.GetDecimal(0):N2} MB");
                    Set("free", reader.IsDBNull(1) ? null : $"{reader.GetDecimal(1):N2} MB");
                });

            // Number of database users (exclude the fixed built-in principals id 0-4: public/dbo/guest/…).
            await RunAsync(connection,
                "SELECT COUNT(*) FROM sys.database_principals WHERE type IN ('S', 'U', 'G') AND principal_id > 4",
                _ => { },
                reader => Set("users", reader.GetInt32(0).ToString()));

            // Memory-optimized (In-Memory OLTP) allocation; empty/absent → 0.00 MB. Best-effort.
            await TryAsync(() => RunAsync(connection,
                    "SELECT CAST(ISNULL(SUM(allocated_bytes), 0) / 1024.0 / 1024 AS decimal(18,2)), CAST(ISNULL(SUM(used_bytes), 0) / 1024.0 / 1024 AS decimal(18,2)) FROM sys.dm_db_xtp_table_memory_stats",
                    _ => { },
                    reader =>
                    {
                        Set("xtpAlloc", $"{reader.GetDecimal(0):N2} MB");
                        Set("xtpUsed", $"{reader.GetDecimal(1):N2} MB");
                    }),
                () => { Set("xtpAlloc", "0.00 MB"); Set("xtpUsed", "0.00 MB"); });

            // Last full / log backup from msdb — best-effort (needs msdb access); "None" when unavailable.
            await TryAsync(() => RunAsync(connection,
                    """
                    SELECT type, MAX(backup_finish_date)
                    FROM msdb.dbo.backupset WHERE database_name = @db AND type IN ('D', 'L')
                    GROUP BY type
                    """,
                    cmd => cmd.Parameters.AddWithValue("@db", database),
                    reader =>
                    {
                        var finish = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                        var text = finish?.ToString("g") ?? "None";
                        if (reader.GetString(0) == "D") Set("lastBackup", text); else Set("lastLogBackup", text);
                    }),
                () => { });

            // Backups with no row at all → None.
            if (values["lastBackup"].Text is "…") Set("lastBackup", "None");
            if (values["lastLogBackup"].Text is "…") Set("lastLogBackup", "None");
        }
        catch (Exception ex)
        {
            foreach (var key in values.Keys)
            {
                if (values[key].Text is "…") Set(key, "—");
            }
            Set("status", $"(unavailable: {ex.Message})");
        }
    }

    private static async Task RunAsync(SqlConnection connection, string sql, Action<SqlCommand> configure, Action<SqlDataReader> read)
    {
        await using var command = new SqlCommand(sql, connection);
        configure(command);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            read(reader);
        }
    }

    private static async Task TryAsync(Func<Task> action, Action onFail)
    {
        try { await action(); }
        catch { onFail(); }
    }
}
