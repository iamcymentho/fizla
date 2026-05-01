using Fizla.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fizla.Infrastructure.Persistence.Configurations;

public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .HasColumnName("id");

        builder.Property(t => t.ExternalId)
            .HasColumnName("external_id")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(t => t.Amount)
            .HasColumnName("amount")
            .HasColumnType("numeric(18,2)")
            .IsRequired();

        builder.Property(t => t.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(t => t.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(t => t.Fee)
            .HasColumnName("fee")
            .HasColumnType("numeric(18,2)")
            .IsRequired();

        builder.Property(t => t.NetAmount)
            .HasColumnName("net_amount")
            .HasColumnType("numeric(18,2)")
            .IsRequired();

        builder.Property(t => t.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(t => t.ExternalId)
            .IsUnique()
            .HasDatabaseName("ix_transactions_external_id");
    }
}
