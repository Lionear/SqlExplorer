using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Providers.MsSql.Security;

/// <summary>
/// Backs the SQL Server login view (Route B). Self-contained: it reads databases through the provider and
/// runs its own DDL via <see cref="Microsoft.Data.SqlClient"/> against the profile's connection string.
/// v1 focuses on creating a login (General + Server Roles + User Mapping); editing prefills the name only,
/// with full state prefill a follow-up.
/// </summary>
public sealed class LoginPropertiesViewModel : INotifyPropertyChanged
{
    // Fixed SQL Server principals — no need to query for them; public is always a member.
    private static readonly string[] FixedServerRoles =
        ["sysadmin", "securityadmin", "serveradmin", "setupadmin", "processadmin", "diskadmin", "dbcreator", "bulkadmin"];

    private static readonly string[] FixedDbRoles =
        ["db_owner", "db_securityadmin", "db_accessadmin", "db_backupoperator", "db_ddladmin",
         "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter"];

    private readonly SecurityUiContext _context;
    private readonly string _connectionString;

    public LoginPropertiesViewModel(SecurityUiContext context)
    {
        _context = context;
        _connectionString = context.Profile.ConnectionString;
        IsNew = context.Action == SecurityUiAction.NewLogin;
        _loginName = context.Target?.Name ?? string.Empty;

        ServerRoles = new ObservableCollection<RoleRow>(
            FixedServerRoles.Select(r => new RoleRow(r, RecomputePreview)));

        RecomputePreview();
    }

    public bool IsNew { get; }
    public string PrimaryAction => IsNew ? "Create" : "Apply";

    // --- General ---
    private string _loginName;
    public string LoginName { get => _loginName; set { if (Set(ref _loginName, value)) RecomputePreview(); } }

    private bool _isSqlAuth = true;
    public bool IsSqlAuth
    {
        get => _isSqlAuth;
        set { if (Set(ref _isSqlAuth, value)) { OnPropertyChanged(nameof(IsWindowsAuth)); RecomputePreview(); } }
    }
    public bool IsWindowsAuth { get => !_isSqlAuth; set => IsSqlAuth = !value; }

    private string _password = string.Empty;
    public string Password { get => _password; set { if (Set(ref _password, value)) RecomputePreview(); } }

    private string _confirmPassword = string.Empty;
    public string ConfirmPassword { get => _confirmPassword; set { if (Set(ref _confirmPassword, value)) RecomputePreview(); } }

    public ObservableCollection<string> Databases { get; } = [];

    private string? _defaultDatabase;
    public string? DefaultDatabase { get => _defaultDatabase; set { if (Set(ref _defaultDatabase, value)) RecomputePreview(); } }

    private bool _enforcePolicy = true;
    public bool EnforcePolicy { get => _enforcePolicy; set { if (Set(ref _enforcePolicy, value)) RecomputePreview(); } }

    // --- Server roles ---
    public ObservableCollection<RoleRow> ServerRoles { get; }

    // --- User mapping ---
    public ObservableCollection<MappingRow> Mappings { get; } = [];

    private MappingRow? _selectedMapping;
    public MappingRow? SelectedMapping
    {
        get => _selectedMapping;
        set { if (Set(ref _selectedMapping, value)) OnPropertyChanged(nameof(SelectedDbRoles)); }
    }

    public IReadOnlyList<RoleRow> SelectedDbRoles => _selectedMapping?.DbRoles ?? [];

    // --- SQL preview + status ---
    private string _sqlPreview = string.Empty;
    public string SqlPreview { get => _sqlPreview; private set => Set(ref _sqlPreview, value); }

    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Set by the view so buttons can close the hosting window.</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>Loads databases (and roles per database) once the view is shown.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var dbs = await _context.Provider.GetDatabasesAsync(_context.Profile, CancellationToken.None);
            foreach (var db in dbs)
            {
                Databases.Add(db);
                Mappings.Add(new MappingRow(db, FixedDbRoles, RecomputePreview));
            }

            DefaultDatabase = dbs.Contains("master") ? "master" : dbs.FirstOrDefault();
            SelectedMapping = Mappings.FirstOrDefault();
            RecomputePreview();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void RecomputePreview()
    {
        var name = QuoteId(LoginName);
        var sql = new StringBuilder();

        if (IsSqlAuth)
        {
            sql.Append($"CREATE LOGIN {name} WITH PASSWORD = {QuoteStr(Password)}");
            if (!string.IsNullOrEmpty(DefaultDatabase)) sql.Append($", DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)}");
            sql.Append($", CHECK_POLICY = {(EnforcePolicy ? "ON" : "OFF")};");
        }
        else
        {
            sql.Append($"CREATE LOGIN {name} FROM WINDOWS");
            if (!string.IsNullOrEmpty(DefaultDatabase)) sql.Append($" WITH DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)}");
            sql.Append(';');
        }

        foreach (var role in ServerRoles.Where(r => r.IsChecked))
        {
            sql.Append($"\nALTER SERVER ROLE {QuoteId(role.Name)} ADD MEMBER {name};");
        }

        foreach (var map in Mappings.Where(m => m.IsMapped))
        {
            var user = QuoteId(string.IsNullOrWhiteSpace(map.UserName) ? LoginName : map.UserName);
            sql.Append($"\nUSE {QuoteId(map.Database)};");
            sql.Append($"\nCREATE USER {user} FOR LOGIN {name};");
            foreach (var dbRole in map.DbRoles.Where(r => r.IsChecked))
            {
                sql.Append($"\nALTER ROLE {QuoteId(dbRole.Name)} ADD MEMBER {user};");
            }
        }

        SqlPreview = sql.ToString();
    }

    /// <summary>Runs the login DDL, then the per-database mapping in each database's own context.</summary>
    public async Task<bool> ApplyAsync()
    {
        try
        {
            Status = null;

            if (string.IsNullOrWhiteSpace(LoginName))
            {
                Status = "Enter a login name.";
                return false;
            }
            if (IsSqlAuth && Password != ConfirmPassword)
            {
                Status = "The passwords do not match.";
                return false;
            }

            // Login + server-role membership run in the connection's default (server) context.
            var loginScript = new StringBuilder();
            var name = QuoteId(LoginName);
            if (IsSqlAuth)
            {
                loginScript.Append($"CREATE LOGIN {name} WITH PASSWORD = {QuoteStr(Password)}");
                if (!string.IsNullOrEmpty(DefaultDatabase)) loginScript.Append($", DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)}");
                loginScript.Append($", CHECK_POLICY = {(EnforcePolicy ? "ON" : "OFF")};");
            }
            else
            {
                loginScript.Append($"CREATE LOGIN {name} FROM WINDOWS");
                if (!string.IsNullOrEmpty(DefaultDatabase)) loginScript.Append($" WITH DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)}");
                loginScript.Append(';');
            }
            foreach (var role in ServerRoles.Where(r => r.IsChecked))
            {
                loginScript.Append($"\nALTER SERVER ROLE {QuoteId(role.Name)} ADD MEMBER {name};");
            }

            await using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                await using var cmd = new SqlCommand(loginScript.ToString(), connection);
                await cmd.ExecuteNonQueryAsync();
            }

            // Mapping runs per database, in that database's own context (a fresh connection per db).
            foreach (var map in Mappings.Where(m => m.IsMapped))
            {
                var user = QuoteId(string.IsNullOrWhiteSpace(map.UserName) ? LoginName : map.UserName);
                var mapScript = new StringBuilder($"CREATE USER {user} FOR LOGIN {name};");
                foreach (var dbRole in map.DbRoles.Where(r => r.IsChecked))
                {
                    mapScript.Append($"\nALTER ROLE {QuoteId(dbRole.Name)} ADD MEMBER {user};");
                }

                var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = map.Database };
                await using var dbConnection = new SqlConnection(builder.ConnectionString);
                await dbConnection.OpenAsync();
                await using var dbCmd = new SqlCommand(mapScript.ToString(), dbConnection);
                await dbCmd.ExecuteNonQueryAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return false;
        }
    }

    private static string QuoteId(string name) => $"[{name.Replace("]", "]]")}]";
    private static string QuoteStr(string value) => $"N'{value.Replace("'", "''")}'";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>A checkable role membership row (server or database), notifying the VM to re-preview on toggle.</summary>
public sealed class RoleRow : INotifyPropertyChanged
{
    private readonly Action _onChange;
    public RoleRow(string name, Action onChange) { Name = name; _onChange = onChange; }

    public string Name { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); _onChange(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One database's mapping: whether the login is mapped, the user name, and its db-role memberships.</summary>
public sealed class MappingRow : INotifyPropertyChanged
{
    private readonly Action _onChange;
    public MappingRow(string database, string[] dbRoles, Action onChange)
    {
        Database = database;
        _onChange = onChange;
        DbRoles = dbRoles.Select(r => new RoleRow(r, onChange)).ToList();
    }

    public string Database { get; }
    public IReadOnlyList<RoleRow> DbRoles { get; }

    private bool _isMapped;
    public bool IsMapped
    {
        get => _isMapped;
        set { if (_isMapped != value) { _isMapped = value; OnPropertyChanged(nameof(IsMapped)); _onChange(); } }
    }

    private string _userName = string.Empty;
    public string UserName
    {
        get => _userName;
        set { if (_userName != value) { _userName = value; OnPropertyChanged(nameof(UserName)); _onChange(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
