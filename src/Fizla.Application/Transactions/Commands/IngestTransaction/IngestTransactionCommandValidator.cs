using Fizla.Domain.Entities;
using FluentValidation;

namespace Fizla.Application.Transactions.Commands.IngestTransaction;

public sealed class IngestTransactionCommandValidator : AbstractValidator<IngestTransactionCommand>
{
    public IngestTransactionCommandValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.Amount)
            .GreaterThan(0m)
            .LessThan(1_000_000_000m)
            .Must(a => decimal.Round(a, 2) == a)
            .WithMessage("Amount must have at most two decimal places.");

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(3)
            .Matches("^[A-Z]{3}$")
            .WithMessage("Currency must be a 3-letter ISO 4217 code (uppercase).");

        RuleFor(x => x.Timestamp)
            .NotEqual(default(DateTimeOffset));

        RuleFor(x => x.Status)
            .NotEmpty()
            .Must(s => Transaction.AllowedStatuses.Contains(s))
            .WithMessage(
                $"Status must be one of (case-sensitive): {string.Join(", ", Transaction.AllowedStatuses)}.");
    }
}
