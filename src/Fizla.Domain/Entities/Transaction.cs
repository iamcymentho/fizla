using System.Security.Cryptography;

namespace Fizla.Domain.Entities;

public sealed class Transaction
{
    public const decimal FeeRate = 0.015m;

    public static readonly IReadOnlyCollection<string> AllowedStatuses =
        new[] { "Pending", "Completed", "Failed", "Reversed" };

    public Guid Id { get; private set; }
    public string ExternalId { get; private set; } = default!;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = default!;
    public string Status { get; private set; } = default!;
    public decimal Fee { get; private set; }
    public decimal NetAmount { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Transaction() { }

    public static Transaction Create(
        string externalId,
        decimal amount,
        string currency,
        string status,
        DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("ExternalId is required.", nameof(externalId));
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        if (decimal.Round(amount, 2) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount),
                "Amount must have at most two decimal places.");

        ValidateCurrency(currency);

        if (!AllowedStatuses.Contains(status))
            throw new ArgumentOutOfRangeException(nameof(status),
                $"Status must be one of: {string.Join(", ", AllowedStatuses)}.");
        if (occurredAt == default)
            throw new ArgumentException("OccurredAt must be set.", nameof(occurredAt));

        var fee = ComputeFee(amount);
        return new Transaction
        {
            Id = NewSequentialId(),
            ExternalId = externalId,
            Amount = amount,
            Currency = currency,
            Status = status,
            Fee = fee,
            NetAmount = amount - fee,
            OccurredAt = occurredAt,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static decimal ComputeFee(decimal amount) =>
        Math.Round(amount * FeeRate, 2, MidpointRounding.AwayFromZero);

    private static void ValidateCurrency(string currency)
    {
        if (string.IsNullOrEmpty(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));
        if (currency.Length != 3)
            throw new ArgumentException("Currency must be exactly 3 characters.", nameof(currency));
        foreach (var c in currency)
        {
            if (c is < 'A' or > 'Z')
                throw new ArgumentException(
                    "Currency must be uppercase ISO 4217 (A–Z only).", nameof(currency));
        }
    }

    internal static Guid NewSequentialId()
    {
        Span<byte> bytes = stackalloc byte[16];
        var unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;
        RandomNumberGenerator.Fill(bytes[6..]);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
