using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlExplorer.Core.Connections;
using SqlExplorer.Infrastructure.Persistence;
using Xunit;

namespace SqlExplorer.Core.Tests.Store;

// The JSON store serialises SavedConnection through a ConnectionDto. These guard that the SE-164
// Origin field round-trips: a plugin-created connection must come back tagged after a restart, or the
// "Managed" badge disappears and origin-scoped IManagedConnections (Mine/Remove) stop recognising it.
public class JsonConnectionStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "se164-conn-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Origin_round_trips_through_a_reopened_store()
    {
        var path = TempFile();
        try
        {
            new JsonConnectionStore(path).Save(new SavedConnection
            {
                Id = "c1", Name = "pg-local", ProviderId = "postgres",
                Values = new Dictionary<string, string?>(), Origin = "local-containers"
            });

            // A fresh instance forces a real read from disk, not a cached in-memory list.
            var reloaded = new JsonConnectionStore(path).GetAll().Single();

            Assert.Equal("local-containers", reloaded.Origin);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Null_origin_stays_null_for_a_user_connection()
    {
        var path = TempFile();
        try
        {
            new JsonConnectionStore(path).Save(new SavedConnection
            {
                Id = "c1", Name = "pg-user", ProviderId = "postgres",
                Values = new Dictionary<string, string?>(), Origin = null
            });

            var reloaded = new JsonConnectionStore(path).GetAll().Single();

            Assert.Null(reloaded.Origin);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
