using Fizla.Application.Abstractions;
using Fizla.Application.Transactions.Commands.IngestTransaction;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Fizla.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<IngestTransactionCommandValidator>();

        services.AddScoped<
            ICommandHandler<IngestTransactionCommand, IngestTransactionResponse>,
            IngestTransactionCommandHandler>();

        return services;
    }
}
