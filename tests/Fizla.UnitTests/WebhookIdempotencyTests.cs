using System.Net;
using System.Net.Http.Json;
using Fizla.Application.Abstractions;
using Fizla.Application.Transactions.Commands.IngestTransaction;
using Fizla.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fizla.UnitTests;

public sealed class WebhookIdempotencyTests : IClassFixture<SqliteWebApplicationFactory>
{
    private readonly SqliteWebApplicationFactory _factory;

    public WebhookIdempotencyTests(SqliteWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostingSamePayloadTwice_PersistsOnce_AndReturnsSameResponse()
    {
        var client = _factory.CreateClient();
        var payload = new
        {
            id = $"tx-{Guid.NewGuid()}",
            amount = 200.00m,
            currency = "USD",
            timestamp = DateTimeOffset.UtcNow,
            status = "Completed"
        };

        var first = await client.PostAsJsonAsync("/webhooks/transactions", payload);
        var second = await client.PostAsJsonAsync("/webhooks/transactions", payload);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstBody = await first.Content.ReadFromJsonAsync<IngestTransactionResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<IngestTransactionResponse>();

        firstBody.Should().NotBeNull();
        secondBody.Should().NotBeNull();
        secondBody!.TransactionId.Should().Be(firstBody!.TransactionId);
        secondBody.Amount.Should().Be(200.00m);
        secondBody.Fee.Should().Be(3.00m);
        secondBody.NetAmount.Should().Be(197.00m);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.Transactions.CountAsync(t => t.ExternalId == payload.id);
        count.Should().Be(1);
    }

    [Fact]
    public async Task InvalidPayload_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var invalid = new
        {
            id = "",
            amount = -1m,
            currency = "us",
            timestamp = default(DateTimeOffset),
            status = "unknown"
        };

        var response = await client.PostAsJsonAsync("/webhooks/transactions", invalid);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("COMPLETED")]
    [InlineData("Pending ")]
    public async Task NonCanonicalStatusCasing_IsRejected(string status)
    {
        var client = _factory.CreateClient();
        var payload = new
        {
            id = $"tx-{Guid.NewGuid()}",
            amount = 100m,
            currency = "USD",
            timestamp = DateTimeOffset.UtcNow,
            status
        };

        var response = await client.PostAsJsonAsync("/webhooks/transactions", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AmountWithMoreThanTwoDecimals_IsRejected()
    {
        var client = _factory.CreateClient();
        var payload = new
        {
            id = $"tx-{Guid.NewGuid()}",
            amount = 0.123m,
            currency = "USD",
            timestamp = DateTimeOffset.UtcNow,
            status = "Completed"
        };

        var response = await client.PostAsJsonAsync("/webhooks/transactions", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReplayWithDivergentAmount_ReturnsOriginal_AndLogsWarning()
    {
        var client = _factory.CreateClient();
        var id = $"tx-{Guid.NewGuid()}";
        var timestamp = DateTimeOffset.UtcNow;

        var first = await client.PostAsJsonAsync("/webhooks/transactions", new
        {
            id,
            amount = 100.00m,
            currency = "USD",
            timestamp,
            status = "Completed"
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Logs.Clear();

        var divergent = await client.PostAsJsonAsync("/webhooks/transactions", new
        {
            id,
            amount = 999.00m,
            currency = "USD",
            timestamp,
            status = "Completed"
        });

        divergent.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await divergent.Content.ReadFromJsonAsync<IngestTransactionResponse>();
        body!.Amount.Should().Be(100.00m);
        body.Fee.Should().Be(1.50m);

        _factory.Logs.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(id)
            && e.Message.Contains("divergent"));
    }
}

public sealed class SqliteWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    public CapturingLoggerProvider Logs { get; } = new();

    public SqliteWebApplicationFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(Logs);
            logging.SetMinimumLevel(LogLevel.Information);
        });

        builder.ConfigureServices(services =>
        {
            RemoveDescriptor<DbContextOptions<AppDbContext>>(services);
            RemoveDescriptor<DbContextOptions>(services);
            RemoveDescriptor<AppDbContext>(services);
            RemoveDescriptor<IUniqueViolationDetector>(services);

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            services.AddSingleton<IUniqueViolationDetector, SqliteUniqueViolationDetector>();

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    private static void RemoveDescriptor<T>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null) services.Remove(descriptor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SqliteUniqueViolationDetector : IUniqueViolationDetector
{
    public bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19;
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _gate = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose() { }

    private void Add(LogEntry entry)
    {
        lock (_gate) _entries.Add(entry);
    }

    public sealed record LogEntry(LogLevel Level, string Category, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly CapturingLoggerProvider _provider;

        public CapturingLogger(string category, CapturingLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _provider.Add(new LogEntry(logLevel, _category, formatter(state, exception)));
        }
    }
}
