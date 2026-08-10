using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.TestSupport;

/// <summary>
/// Boots the real API against a real PostgreSQL database (docs/09: no
/// in-memory substitute). Each factory instance creates its own database,
/// migrates it, seeds the test owner, and drops the database on dispose.
/// Background workers are off by default so tests drive outbox dispatch and
/// job runs deterministically; e2e tests opt back in.
/// </summary>
public class SalesHubApiFactory : WebApplicationFactory<Program>
{
    public const string OwnerUsername = "test-owner";
    public const string OwnerPassword = "test-owner-password-1";

    private static readonly string ServerConnectionString =
        Environment.GetEnvironmentVariable("SALESHUB_TEST_PG")
        ?? "Host=127.0.0.1;Port=5432;Username=saleshub;Password=saleshub_dev";

    private readonly string _databaseName = $"saleshub_test_{Guid.NewGuid():N}";
    private readonly bool _workersEnabled;

    public SalesHubApiFactory(bool workersEnabled = false)
    {
        _workersEnabled = workersEnabled;
    }

    public string ConnectionString =>
        $"{ServerConnectionString};Database={_databaseName}";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("environment", "Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["Database:MigrateOnStartup"] = "true",
                ["Workers:Enabled"] = _workersEnabled ? "true" : "false",
                ["Seed:Owner:Username"] = OwnerUsername,
                ["Seed:Owner:Password"] = OwnerPassword,
                ["Seed:Owner:DisplayName"] = "Test Owner",
                ["DataProtection:KeyRingPath"] = "",
                // Generous auth rate limit so test loops never trip 429s.
                ["Security:FreshAuthWindow"] = "00:15:00",
            }));
    }

    /// <summary>A client with a cookie jar, the way the PWA talks to the API.</summary>
    public HttpClient CreateCookieClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Runs work in a fresh DI scope (DbContext, services).</summary>
    public async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider);
    }

    public async Task<T> WithDbAsync<T>(Func<SalesHubDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesHubDbContext>();
        return await action(db);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            await using var connection = new NpgsqlConnection(
                $"{ServerConnectionString};Database=postgres");
            await connection.OpenAsync();
            await using var terminate = new NpgsqlCommand(
                $"""
                 SELECT pg_terminate_backend(pid) FROM pg_stat_activity
                 WHERE datname = '{_databaseName}' AND pid <> pg_backend_pid()
                 """, connection);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\"", connection);
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup; a leaked test database is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}
