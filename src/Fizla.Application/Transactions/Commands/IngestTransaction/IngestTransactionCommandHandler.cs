using Fizla.Application.Abstractions;
using Fizla.Domain.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fizla.Application.Transactions.Commands.IngestTransaction;

public sealed class IngestTransactionCommandHandler
    : ICommandHandler<IngestTransactionCommand, IngestTransactionResponse>
{
    private readonly IAppDbContext _db;
    private readonly IValidator<IngestTransactionCommand> _validator;
    private readonly IUniqueViolationDetector _uniqueViolationDetector;
    private readonly ILogger<IngestTransactionCommandHandler> _logger;

    public IngestTransactionCommandHandler(
        IAppDbContext db,
        IValidator<IngestTransactionCommand> validator,
        IUniqueViolationDetector uniqueViolationDetector,
        ILogger<IngestTransactionCommandHandler> logger)
    {
        _db = db;
        _validator = validator;
        _uniqueViolationDetector = uniqueViolationDetector;
        _logger = logger;
    }

    public async Task<IngestTransactionResponse> Handle(
        IngestTransactionCommand command,
        CancellationToken cancellationToken)
    {
        await _validator.ValidateAndThrowAsync(command, cancellationToken);

        var existing = await _db.Transactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.ExternalId == command.Id, cancellationToken);

        if (existing is not null)
        {
            LogReplay(existing, command);
            return Map(existing);
        }

        var transaction = Transaction.Create(
            command.Id,
            command.Amount,
            command.Currency,
            command.Status,
            command.Timestamp);

        _db.Transactions.Add(transaction);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Transaction {ExternalId} ingested with fee {Fee}.",
                transaction.ExternalId, transaction.Fee);
            return Map(transaction);
        }
        catch (DbUpdateException ex) when (_uniqueViolationDetector.IsUniqueViolation(ex))
        {
            var raced = await _db.Transactions
                .AsNoTracking()
                .FirstAsync(t => t.ExternalId == command.Id, cancellationToken);

            _logger.LogInformation(
                "Unique-constraint race for {ExternalId}; returning persisted record.",
                command.Id);
            return Map(raced);
        }
    }

    private void LogReplay(Transaction existing, IngestTransactionCommand command)
    {
        if (HasDivergentPayload(existing, command))
        {
            _logger.LogWarning(
                "Replay of {ExternalId} carries divergent payload " +
                "(stored: amount={StoredAmount} {StoredCurrency} status={StoredStatus} occurred={StoredOccurredAt:o}; " +
                "incoming: amount={IncomingAmount} {IncomingCurrency} status={IncomingStatus} occurred={IncomingTimestamp:o}). " +
                "Returning original; new payload ignored.",
                command.Id,
                existing.Amount, existing.Currency, existing.Status, existing.OccurredAt,
                command.Amount, command.Currency, command.Status, command.Timestamp);
            return;
        }

        _logger.LogInformation(
            "Transaction {ExternalId} already exists; returning existing record.",
            command.Id);
    }

    private static bool HasDivergentPayload(Transaction existing, IngestTransactionCommand command) =>
        existing.Amount != command.Amount
        || !string.Equals(existing.Currency, command.Currency, StringComparison.Ordinal)
        || !string.Equals(existing.Status, command.Status, StringComparison.Ordinal)
        || existing.OccurredAt != command.Timestamp;

    private static IngestTransactionResponse Map(Transaction t) =>
        new(t.ExternalId, t.Amount, t.Fee, t.NetAmount);
}
