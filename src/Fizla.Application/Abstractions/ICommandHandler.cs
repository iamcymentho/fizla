namespace Fizla.Application.Abstractions;

public interface ICommandHandler<in TCommand, TResponse>
{
    Task<TResponse> Handle(TCommand command, CancellationToken cancellationToken);
}
