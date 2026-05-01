using Fizla.Application.Abstractions;

namespace Fizla.Application.Transactions.Commands.IngestTransaction;

public sealed record IngestTransactionCommand(
    string Id,
    decimal Amount,
    string Currency,
    DateTimeOffset Timestamp,
    string Status);
