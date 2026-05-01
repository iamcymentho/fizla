using Fizla.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace Fizla.UnitTests;

public sealed class TransactionFeeTests
{
    private static readonly DateTimeOffset SomeOccurredAt = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(100.00, 1.50, 98.50)]
    [InlineData(250.00, 3.75, 246.25)]
    [InlineData(33.33, 0.50, 32.83)]
    [InlineData(1.00, 0.02, 0.98)]
    public void Create_DerivesFeeAndNetAmount(decimal amount, decimal expectedFee, decimal expectedNet)
    {
        var tx = Transaction.Create(
            externalId: "ext-1",
            amount: amount,
            currency: "USD",
            status: "Completed",
            occurredAt: SomeOccurredAt);

        tx.Fee.Should().Be(expectedFee);
        tx.NetAmount.Should().Be(expectedNet);
        (tx.Fee + tx.NetAmount).Should().Be(tx.Amount);
    }

    [Fact]
    public void Create_RejectsNonPositiveAmount()
    {
        var act = () => Transaction.Create("ext-2", 0m, "USD", "Completed", SomeOccurredAt);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_RejectsAmountWithMoreThanTwoDecimals()
    {
        var act = () => Transaction.Create("ext-3", 0.123m, "USD", "Completed", SomeOccurredAt);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*two decimal places*");
    }

    [Theory]
    [InlineData("us")]      // too short
    [InlineData("USDX")]    // too long
    [InlineData("usd")]     // lowercase
    [InlineData("US1")]     // non-letter
    [InlineData("")]        // empty
    public void Create_RejectsMalformedCurrency(string currency)
    {
        var act = () => Transaction.Create("ext-4", 100m, currency, "Completed", SomeOccurredAt);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("completed")]   // wrong case
    [InlineData("Unknown")]
    [InlineData("")]
    public void Create_RejectsUnknownOrMiscasedStatus(string status)
    {
        var act = () => Transaction.Create("ext-5", 100m, "USD", status, SomeOccurredAt);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsDefaultOccurredAt()
    {
        var act = () => Transaction.Create("ext-6", 100m, "USD", "Completed", default);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeFee_UsesOnePointFivePercent()
    {
        Transaction.ComputeFee(1000m).Should().Be(15.00m);
    }

    [Fact]
    public void Create_ProducesUuidV7()
    {
        var tx = Transaction.Create("ext-7", 100m, "USD", "Completed", SomeOccurredAt);
        var bytes = tx.Id.ToByteArray(bigEndian: true);

        var version = (bytes[6] & 0xF0) >> 4;
        version.Should().Be(7, "the PK must be a UUID v7 for sortable B-tree inserts");

        var variant = (bytes[8] & 0xC0) >> 6;
        variant.Should().Be(2, "RFC 4122 variant bits must be 10");
    }
}
