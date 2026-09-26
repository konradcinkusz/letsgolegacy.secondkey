using Microsoft.Data.SqlClient;
using SecondKey.Replay.Probes;
using SecondKey.Replay.Resets;
using Testcontainers.MsSql;

namespace SecondKey.Replay.Tests;

/// <summary>One SQL Server container for the SQL tests of this class.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string ConnectionString(string database) =>
        new SqlConnectionStringBuilder(_container!.GetConnectionString()) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("SK_REQUIRE_DOCKER") != "1" && Environment.GetEnvironmentVariable("DOCKER_HOST") is null && !File.Exists("/var/run/docker.sock"))
        {
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(ConnectionString(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(ConnectionString(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class SqlServerTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _sql;

    public SqlServerTests(SqlServerFixture sql) => _sql = sql;

    private async Task<string> CreateShopAsync()
    {
        var database = "Shop" + Guid.NewGuid().ToString("N")[..8];
        await _sql.ExecuteAsync("master", $"CREATE DATABASE [{database}]");
        await _sql.ExecuteAsync(database, """
            CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY, Customer nvarchar(50) NOT NULL, Total decimal(10,2) NOT NULL, PlacedAt datetime2 NULL);
            INSERT INTO dbo.Orders VALUES (1, N'ann', 59.97, '2026-09-26T10:00:00'), (2, N'bob', 12.50, NULL);
            CREATE TABLE dbo.Log (Line nvarchar(100) NOT NULL);
            """);
        return database;
    }

    [DockerFact]
    public async Task Two_consecutive_scenarios_start_from_the_same_state()
    {
        var database = await CreateShopAsync();
        var reset = new SqlServerSnapshotReset(_sql.ConnectionString(database), database, database + "_sk");

        var first = await reset.ResetAsync(CancellationToken.None);
        var startOfFirst = await _sql.ScalarAsync(database, "SELECT COUNT(*) FROM dbo.Orders");
        await _sql.ExecuteAsync(database, "INSERT INTO dbo.Orders VALUES (3, N'cat', 7.25, NULL); DELETE FROM dbo.Orders WHERE Id = 1;");

        var second = await reset.ResetAsync(CancellationToken.None);
        var startOfSecond = await _sql.ScalarAsync(database, "SELECT COUNT(*) FROM dbo.Orders");
        var ann = await _sql.ScalarAsync(database, "SELECT COUNT(*) FROM dbo.Orders WHERE Id = 1");
        var cat = await _sql.ScalarAsync(database, "SELECT COUNT(*) FROM dbo.Orders WHERE Id = 3");

        Assert.True(first.Succeeded, first.Detail);
        Assert.True(second.Succeeded, second.Detail);
        Assert.Equal(2, startOfFirst);
        Assert.Equal(startOfFirst, startOfSecond);
        Assert.Equal(1, ann);
        Assert.Equal(0, cat);
        await reset.DropSnapshotAsync(CancellationToken.None);
    }

    [DockerFact]
    public async Task A_snapshot_that_exists_is_reused()
    {
        var database = await CreateShopAsync();
        var first = new SqlServerSnapshotReset(_sql.ConnectionString(database), database, database + "_sk");
        await first.EnsureSnapshotAsync(CancellationToken.None);

        var second = new SqlServerSnapshotReset(_sql.ConnectionString(database), database, database + "_sk");
        await second.EnsureSnapshotAsync(CancellationToken.None);
        var result = await second.ResetAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(1, await _sql.ScalarAsync("master", $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{database}_sk'"));
        await second.DropSnapshotAsync(CancellationToken.None);
    }

    [DockerFact]
    public async Task A_database_that_does_not_exist_fails_the_reset_with_a_reason()
    {
        var reset = new SqlServerSnapshotReset(_sql.ConnectionString("master"), "NoSuchShop", "NoSuchShop_sk");

        await Assert.ThrowsAsync<InvalidOperationException>(() => reset.EnsureSnapshotAsync(CancellationToken.None));
    }

    [DockerFact]
    public async Task The_probe_reports_inserted_updated_and_deleted_rows_by_primary_key()
    {
        var database = await CreateShopAsync();
        var probe = new SqlServerTableProbe(_sql.ConnectionString(database), ["dbo.Orders", "dbo.Log"]);

        var before = await probe.SnapshotAsync(CancellationToken.None);
        await _sql.ExecuteAsync(database, """
            INSERT INTO dbo.Orders VALUES (3, N'cat', 7.25, NULL);
            UPDATE dbo.Orders SET Total = 0 WHERE Id = 2;
            DELETE FROM dbo.Orders WHERE Id = 1;
            INSERT INTO dbo.Log VALUES (N'order 3 placed');
            """);
        var after = await probe.SnapshotAsync(CancellationToken.None);

        var delta = SqlServerTableProbe.Diff(before, after)!;
        var orders = delta.Tables["dbo.Orders"];
        Assert.Equal(3, orders.Inserted.Single()["Id"]!.GetValue<long>());
        Assert.Equal(1, orders.Deleted.Single()["Id"]!.GetValue<long>());
        var update = orders.Updated.Single();
        Assert.Equal("{\"Id\":2}", update.Key.ToJsonString());
        Assert.Equal(12.50m, update.Before["Total"]!.GetValue<decimal>());
        Assert.Equal(0m, update.After["Total"]!.GetValue<decimal>());
        Assert.Equal("2026-09-26T10:00:00.0000000", orders.Deleted.Single()["PlacedAt"]!.GetValue<string>());
        Assert.Equal("order 3 placed", delta.Tables["dbo.Log"].Inserted.Single()["Line"]!.GetValue<string>());
        Assert.Equal("sqlserver-tables", delta.Probe);
    }

    [DockerFact]
    public async Task No_change_means_no_delta()
    {
        var database = await CreateShopAsync();
        var probe = new SqlServerTableProbe(_sql.ConnectionString(database), ["dbo.Orders"]);

        var before = await probe.SnapshotAsync(CancellationToken.None);
        var after = await probe.SnapshotAsync(CancellationToken.None);

        Assert.Null(SqlServerTableProbe.Diff(before, after));
    }
}
