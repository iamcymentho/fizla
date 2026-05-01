using Microsoft.EntityFrameworkCore;

namespace Fizla.Application.Abstractions;

public interface IUniqueViolationDetector
{
    bool IsUniqueViolation(DbUpdateException exception);
}
