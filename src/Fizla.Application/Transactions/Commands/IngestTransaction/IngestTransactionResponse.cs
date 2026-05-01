namespace Fizla.Application.Transactions.Commands.IngestTransaction;

public sealed record IngestTransactionResponse(
    string TransactionId,
    decimal Amount,
    decimal Fee,
    decimal NetAmount);
