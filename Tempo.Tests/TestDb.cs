using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Data;

namespace Tempo.Tests;

/// <summary>SQLite in-memory TempoDbContext for unit tests; the connection must stay open for the db to live.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection connection;

    public TempoDbContext Context { get; }

    public TestDb()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        Context = new TempoDbContext(new DbContextOptionsBuilder<TempoDbContext>().UseSqlite(connection).Options);
        Context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Context.Dispose();
        connection.Dispose();
    }
}
