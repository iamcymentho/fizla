using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace Fizla.Api.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("Validation failed: {Errors}", ex.Message);
            await WriteProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Validation failed",
                ex.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Bad request");
            await WriteProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Bad request",
                detail: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Path}", context.Request.Path);
            await WriteProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred.");
        }
    }

    private static async Task WriteProblem(
        HttpContext context,
        int status,
        string title,
        IDictionary<string, string[]>? errors = null,
        string? detail = null)
    {
        if (context.Response.HasStarted) return;

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        object payload = errors is null
            ? new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Instance = context.Request.Path
            }
            : new ValidationProblemDetails(errors)
            {
                Status = status,
                Title = title,
                Instance = context.Request.Path
            };

        var json = JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await context.Response.WriteAsync(json);
    }
}
