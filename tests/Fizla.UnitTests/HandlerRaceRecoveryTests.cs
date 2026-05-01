using Fizla.Application.Abstractions;
using Fizla.Application.Transactions.Commands.IngestTransaction;
using Fizla.Domain.Entities;
using Fizla.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fizla.UnitTests;

public sealed class HandlerRaceRecoveryTests
{
    [Fact]
    public async Task UniqueViolationDuringInsert_ReReadsAndReturnsRaceWinner()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var inner = new AppDbContext(options);
        await inner.Database.EnsureCreatedAsync();

        var externalId = $"tx-race-{Guid.NewGuid()}";
        var winner = Transaction.Create(externalId, 500m, "USD", "Completed", DateTimeOffset.UtcNow);

        var wrapper = new RacingDbContext(inner, ctx =>
        {
            ctx.ChangeTracker.Clear();
            ctx.Transactions.Add(winner);
            ctx.SaveChanges();
            throw new DbUpdateException(
                "Simulated unique violation",
                new SqliteException("UNIQUE constraint failed", 19));
        });

        var handler = new IngestTransactionCommandHandler(
            wrapper,
            new IngestTransactionCommandValidator(),
            new SqliteUniqueViolationDetector(),
            NullLogger<IngestTransactionCommandHandler>.Instance);

        var command = new IngestTransactionCommand(
            Id: externalId,
            Amount: 100m,
            Currency: "USD",
            Timestamp: DateTimeOffset.UtcNow,
            Status: "Completed");

        var response = await handler.Handle(command, CancellationToken.None);

        response.TransactionId.Should().Be(externalId);
        response.Amount.Should().Be(500m, "winner's payload should be returned, not the racer's");
        response.Fee.Should().Be(7.50m);
        response.NetAmount.Should().Be(492.50m);

        var rowCount = await inner.Transactions.CountAsync(t => t.ExternalId == externalId);
        rowCount.Should().Be(1, "the unique index must guarantee exactly one row");
    }

    [Fact]
    public async Task NonUniqueDbUpdateException_PropagatesWithoutReReading()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var inner = new AppDbContext(options);
        await inner.Database.EnsureCreatedAsync();

        var wrapper = new RacingDbContext(inner, _ =>
            throw new DbUpdateException(
                "Some other database failure",
                new InvalidOperationException("connection was reset")));

        var handler = new IngestTransactionCommandHandler(
            wrapper,
            new IngestTransactionCommandValidator(),
            new SqliteUniqueViolationDetector(),
            NullLogger<IngestTransactionCommandHandler>.Instance);

        var command = new IngestTransactionCommand(
            Id: $"tx-nonunique-{Guid.NewGuid()}",
            Amount: 100m,
            Currency: "USD",
            Timestamp: DateTimeOffset.UtcNow,
            Status: "Completed");

        var act = () => handler.Handle(command, CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateException>())
            .WithMessage("*Some other database failure*");
    }
}

internal sealed class RacingDbContext : IAppDbContext
{
    private readonly AppDbContext _inner;
    private readonly Action<AppDbContext> _onFirstSave;
    private bool _hasIntervened;

    public RacingDbContext(AppDbContext inner, Action<AppDbContext> onFirstSave)
    {
        _inner = inner;
        _onFirstSave = onFirstSave;
    }

    public DbSet<Transaction> Transactions => _inner.Transactions;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (_hasIntervened) return _inner.SaveChangesAsync(cancellationToken);
        _hasIntervened = true;
        _onFirstSave(_inner);
        return Task.FromResult(0);
    }
}
