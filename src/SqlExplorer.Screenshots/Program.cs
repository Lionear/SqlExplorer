using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using HostApp = SqlExplorer.App.App;
using SqlExplorer.App.DependencyInjection;
using SqlExplorer.App.ViewModels;
using SqlExplorer.App.Views;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Formatting;
using SqlExplorer.Core.History;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Logging;
using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Schema;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Shortcuts;

namespace SqlExplorer.Screenshots;

// Renders a scene of the real app to a PNG using Avalonia's headless + Skia backend — no display, no
// real database. Usage:
//   dotnet run --project src/SqlExplorer.Screenshots -- --scene hero --out docs/images/hero.png [--size 1280x820]
// Scenes: hero (main window browsing a synthetic demo DB), store (Plugin Store, installed engines).
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var opts = Options.Parse(args);

        // Isolate every on-disk store (settings, connections, plugin state, history …) into a throwaway
        // config dir so a capture never reads or writes the user's real profile. All stores resolve their
        // root from SpecialFolder.ApplicationData, which on Unix is $XDG_CONFIG_HOME (or $HOME/.config) and
        // on Windows is %APPDATA% — so pointing those at a temp dir, before any store is constructed,
        // isolates them all without touching store code.
        var sandbox = Path.Combine(Path.GetTempPath(), "sqlexplorer-shots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", sandbox);
        Environment.SetEnvironmentVariable("APPDATA", sandbox);

        HostApp.ScreenshotMode = true;

        try
        {
            AppBuilder.Configure<HostApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            var services = AppServices.Build();

            var window = SceneCatalog.BuildAsync(opts.Scene, services, sandbox).GetAwaiter().GetResult();
            if (window is null)
            {
                Console.Error.WriteLine($"Unknown scene '{opts.Scene}'. Known: {SceneCatalog.Names}");
                return 2;
            }

            window.Width = opts.Width;
            window.Height = opts.Height;
            window.Show();
            Settle();

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                Console.Error.WriteLine("Capture returned no frame.");
                return 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.Out))!);
            frame.Save(opts.Out);
            Console.WriteLine($"Wrote {opts.Out} ({frame.PixelSize.Width}x{frame.PixelSize.Height}, scene '{opts.Scene}')");
            return 0;
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // Headless has no render loop, so pump the dispatcher in rounds — draining posted continuations from
    // async schema/data loads between short sleeps that let their thread-pool work progress — until the UI
    // has settled, then let CaptureRenderedFrame drive the final render pass.
    internal static void Settle(int rounds = 40, int millisPerRound = 25)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(millisPerRound);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private sealed record Options(string Scene, string Out, int Width, int Height)
    {
        public static Options Parse(string[] args)
        {
            string scene = "hero", @out = "screenshot.png";
            int width = 1280, height = 820;

            for (var i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--scene": scene = args[++i]; break;
                    case "--out": @out = args[++i]; break;
                    case "--size":
                        var wh = args[++i].Split('x', 'X');
                        if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h))
                        {
                            width = w; height = h;
                        }
                        break;
                }
            }

            return new Options(scene, @out, width, height);
        }
    }
}

// Builds each scene as a Window ready to show, seeding synthetic data as needed.
internal static class SceneCatalog
{
    public static string Names => "hero, store, main";

    public static Task<Window?> BuildAsync(string scene, IServiceProvider services, string sandbox) => scene switch
    {
        "hero" => BuildHeroAsync(services, sandbox),
        "store" => Task.FromResult<Window?>(BuildStore(services)),
        "main" => Task.FromResult<Window?>(BuildMain(services)),
        _ => Task.FromResult<Window?>(null)
    };

    // The empty main window — no connections, no data.
    private static Window BuildMain(IServiceProvider services)
    {
        var viewModel = services.GetRequiredService<MainViewModel>();
        return new MainWindow(
            services.GetRequiredService<IAppSettingsStore>(),
            services.GetRequiredService<KeymapService>()) { DataContext = viewModel };
    }

    // The hero shot: the main window browsing a synthetic SQLite "shop" database, so the schema tree and
    // an editable result grid are populated — with data that is obviously fake.
    private static async Task<Window?> BuildHeroAsync(IServiceProvider services, string sandbox)
    {
        var dbPath = Path.Combine(sandbox, "demo-shop.db");
        DemoData.CreateShopDatabase(dbPath);

        var connections = services.GetRequiredService<ConnectionService>();
        var connection = connections.Save(
            id: "demo-shop",
            name: "Demo shop",
            providerId: "sqlite",
            values: new Dictionary<string, string?> { ["path"] = dbPath });

        var viewModel = services.GetRequiredService<MainViewModel>();
        viewModel.SyncConnectionsFromStore();

        // Expand the connection root so the sidebar shows its tables (lazy load; Settle() pumps it).
        if (viewModel.ConnectionNodes.Count > 0)
        {
            viewModel.ConnectionNodes[0].IsExpanded = true;
        }

        // Open a browse tab on the customers table exactly as the app's own browse flow does
        // (NewDocument → InitBrowse → add → LoadPage), but built directly from DI services.
        var document = new DocumentViewModel(
            services.GetRequiredService<IDbProviderRegistry>(),
            connections,
            services.GetRequiredService<ISqlFormatter>(),
            services.GetRequiredService<IQueryHistoryStore>(),
            services.GetRequiredService<IQueryLog>(),
            services.GetRequiredService<ISchemaCache>(),
            services.GetRequiredService<IServerVersionCache>(),
            services.GetRequiredService<IAppSettingsStore>(),
            services.GetRequiredService<ILocalizer>());
        document.InitBrowse(connection, database: null, schema: null, table: "customers");
        viewModel.Documents.Add(document);
        viewModel.SelectedDocument = document;
        await document.LoadPageAsync();

        // Give the lazy tree-expansion a chance to resolve before the window is captured.
        Program.Settle(rounds: 20);

        return new MainWindow(
            services.GetRequiredService<IAppSettingsStore>(),
            services.GetRequiredService<KeymapService>()) { DataContext = viewModel };
    }

    // The Plugin Store on its Installed tab, listing the bundled database engines (no network needed).
    private static Window BuildStore(IServiceProvider services)
    {
        var viewModel = services.GetRequiredService<PluginStoreViewModel>();
        viewModel.SelectedTab = PluginStoreViewModel.TabInstalled;
        return new PluginStoreWindow { DataContext = viewModel };
    }
}

// Creates a throwaway SQLite database with a small, obviously-synthetic "shop" dataset. No real data:
// names are famous scientists, e-mails use example.com.
internal static class DemoData
{
    public static void CreateShopDatabase(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        connection.Open();

        Execute(connection, """
            CREATE TABLE customers (
                id          INTEGER PRIMARY KEY,
                name        TEXT NOT NULL,
                email       TEXT NOT NULL,
                city        TEXT NOT NULL,
                country     TEXT NOT NULL,
                signed_up   TEXT NOT NULL
            );
            CREATE TABLE products (
                id     INTEGER PRIMARY KEY,
                sku    TEXT NOT NULL,
                name   TEXT NOT NULL,
                price  REAL NOT NULL,
                stock  INTEGER NOT NULL
            );
            CREATE TABLE orders (
                id           INTEGER PRIMARY KEY,
                customer_id  INTEGER NOT NULL REFERENCES customers(id),
                product_id   INTEGER NOT NULL REFERENCES products(id),
                quantity     INTEGER NOT NULL,
                total        REAL NOT NULL,
                status       TEXT NOT NULL,
                ordered_at   TEXT NOT NULL
            );
            """);

        string[] names =
        [
            "Ada Lovelace", "Alan Turing", "Grace Hopper", "Katherine Johnson", "Edsger Dijkstra",
            "Barbara Liskov", "Donald Knuth", "Margaret Hamilton", "Tim Berners-Lee", "Radia Perlman",
            "Ken Thompson", "Dennis Ritchie", "Frances Allen", "John McCarthy", "Adele Goldberg",
            "Vint Cerf", "Leslie Lamport", "Shafi Goldwasser", "Bjarne Stroustrup", "Anita Borg",
            "Linus Torvalds", "Sophie Wilson", "Guido van Rossum", "Carol Shaw", "Alan Kay",
            "Hedy Lamarr", "Claude Shannon", "Karen Spärck Jones", "Niklaus Wirth", "Joan Clarke",
        ];
        string[] cities = ["Amsterdam", "Berlin", "London", "Paris", "Madrid", "Rome", "Oslo", "Vienna"];
        string[] countries = ["NL", "DE", "GB", "FR", "ES", "IT", "NO", "AT"];

        using (var tx = connection.BeginTransaction())
        {
            for (var i = 0; i < names.Length; i++)
            {
                var handle = names[i].ToLowerInvariant().Replace(' ', '.').Replace("ä", "a").Replace("ø", "o");
                var day = 1 + (i * 7) % 27;
                var month = 1 + (i % 12);
                Execute(connection,
                    $"INSERT INTO customers (id, name, email, city, country, signed_up) VALUES " +
                    $"({i + 1}, '{names[i].Replace("'", "''")}', '{handle}@example.com', " +
                    $"'{cities[i % cities.Length]}', '{countries[i % countries.Length]}', " +
                    $"'2024-{month:D2}-{day:D2}');");
            }

            string[] products =
            [
                "Mechanical Keyboard", "USB-C Hub", "Laptop Stand", "Noise-cancelling Headset",
                "4K Monitor", "Ergonomic Mouse", "Webcam 1080p", "Desk Mat", "Docking Station", "Cable Kit",
            ];
            for (var i = 0; i < products.Length; i++)
            {
                var price = 19.95 + i * 12.5;
                Execute(connection,
                    $"INSERT INTO products (id, sku, name, price, stock) VALUES " +
                    $"({i + 1}, 'SKU-{1000 + i}', '{products[i]}', {price:0.00}, {20 + i * 5});");
            }

            string[] statuses = ["paid", "shipped", "delivered", "refunded", "pending"];
            for (var i = 0; i < 40; i++)
            {
                var cust = 1 + i % names.Length;
                var prod = 1 + i % products.Length;
                var qty = 1 + i % 4;
                var total = (19.95 + (prod - 1) * 12.5) * qty;
                Execute(connection,
                    $"INSERT INTO orders (id, customer_id, product_id, quantity, total, status, ordered_at) VALUES " +
                    $"({i + 1}, {cust}, {prod}, {qty}, {total:0.00}, '{statuses[i % statuses.Length]}', " +
                    $"'2024-06-{1 + i % 27:D2}');");
            }

            tx.Commit();
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
