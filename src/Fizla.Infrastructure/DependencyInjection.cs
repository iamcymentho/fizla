using Fizla.Application.Abstractions;
using Fizla.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fizla.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "Missing connection string 'Postgres'.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history")));

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton<IUniqueViolationDetector, PostgresUniqueViolationDetector>();

        return services;
    }
}
