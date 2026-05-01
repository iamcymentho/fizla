using Fizla.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fizla.Infrastructure.Persistence;

public sealed class PostgresUniqueViolationDetector : IUniqueViolationDetector
{
    public bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation;
}
