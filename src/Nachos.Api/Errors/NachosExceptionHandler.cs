using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;

namespace Nachos.Api.Errors;

internal sealed partial class NachosExceptionHandler(ILogger<NachosExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }
        var status = exception switch
        {
            NotFoundException => 404,
            ConflictException => 409,
            RequestValidationException or NachosValidationException or IdempotencyKeyReusedException => 422,
            AuthException => 401,
            BadHttpRequestException badRequest => badRequest.StatusCode,
            _ => 500,
        };
        context.Response.StatusCode = status;
        var title = ReasonPhrases.GetReasonPhrase(status);
        if (exception is RequestValidationException validation)
        {
            await context.Response.WriteAsJsonAsync(
                new ValidationErrorResponse(validation.Errors, "about:blank", title, status),
                NachosJsonContext.Default.ValidationErrorResponse, cancellationToken: cancellationToken);
        }
        else
        {
            if (status == 500)
            {
                ServerFailure(logger, exception);
            }
            var detail = status == 500 ? "An unexpected error occurred." : exception.Message;
            var type = exception is IdempotencyKeyReusedException ? ProblemTypes.IdempotencyKeyReused : "about:blank";
            await context.Response.WriteAsJsonAsync(new ErrorResponse(detail, type, title, status),
                NachosJsonContext.Default.ErrorResponse, cancellationToken: cancellationToken);
        }
        return true;
    }

    public static Task WriteStatusAsync(HttpContext context, int status) =>
        context.Response.WriteAsJsonAsync(
            new ErrorResponse(ReasonPhrases.GetReasonPhrase(status), "about:blank", ReasonPhrases.GetReasonPhrase(status), status),
            NachosJsonContext.Default.ErrorResponse, cancellationToken: context.RequestAborted);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled Nachos request failure.")]
    private static partial void ServerFailure(ILogger logger, Exception exception);
}
