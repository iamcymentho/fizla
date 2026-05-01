using Fizla.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fizla.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Transaction> Transactions { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
