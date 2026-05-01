using Fizla.Application.Abstractions;
using Fizla.Application.Transactions.Commands.IngestTransaction;
using Microsoft.AspNetCore.Mvc;

namespace Fizla.Api.Controllers;

[ApiController]
[Route("webhooks")]
public sealed class WebhooksController : ControllerBase
{
    private readonly ICommandHandler<IngestTransactionCommand, IngestTransactionResponse> _handler;

    public WebhooksController(
        ICommandHandler<IngestTransactionCommand, IngestTransactionResponse> handler)
    {
        _handler = handler;
    }

    [HttpPost("transactions")]
    [ProducesResponseType(typeof(IngestTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> IngestTransaction(
        [FromBody] IngestTransactionCommand command,
        CancellationToken cancellationToken)
    {
        var response = await _handler.Handle(command, cancellationToken);
        return Ok(response);
    }
}
