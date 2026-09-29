using FluentValidation;
using Pos.Api.Abstractions;
using Pos.Application.Abstractions.Messaging;
using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Server.Host.Diagnostics;

/// <summary>
/// Endpoints SOLO de desarrollo para verificar de punta a punta el manejo de errores y el pipeline
/// del despachador sin interfaz gráfica. No se mapean en producción.
/// </summary>
internal static class DevDiagnosticsEndpoints
{
    public static RouteGroupBuilder MapDevDiagnostics(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/dev").WithTags("Diagnóstico (solo desarrollo)")
            .AllowAnonymousByDesign("Solo existe en desarrollo.");

        group.MapGet("/errors/unexpected", () =>
        {
            throw new InvalidOperationException("Error provocado para verificar el manejo global de errores.");
        });

        group.MapGet("/errors/domain", () =>
        {
            throw new DomainException(Error.BusinessRule("DEV.SAMPLE_RULE", "Regla de negocio de ejemplo incumplida."));
        });

        group.MapGet("/errors/not-found", () =>
            Error.NotFound("DEV.SAMPLE_NOT_FOUND", "Recurso de ejemplo no encontrado.").ToProblem());

        group.MapPost("/echo", async (EchoCommand command, IDispatcher dispatcher, CancellationToken ct) =>
            (await dispatcher.Send(command, ct)).ToHttpResult());

        return api;
    }
}

/// <summary>Comando de ejemplo: devuelve el texto en mayúsculas. Demuestra validación + handler + Result.</summary>
internal sealed record EchoCommand(string Text) : ICommand<EchoResponse>;

internal sealed record EchoResponse(string Text, int Length);

internal sealed class EchoCommandValidator : AbstractValidator<EchoCommand>
{
    public EchoCommandValidator()
    {
        RuleFor(c => c.Text)
            .NotEmpty().WithErrorCode("DEV.ECHO_TEXT_REQUIRED").WithMessage("El texto es obligatorio.")
            .MaximumLength(20).WithErrorCode("DEV.ECHO_TEXT_TOO_LONG").WithMessage("El texto admite máximo 20 caracteres.");
    }
}

internal sealed class EchoCommandHandler : ICommandHandler<EchoCommand, EchoResponse>
{
    public Task<Result<EchoResponse>> Handle(EchoCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text.Equals("fallar", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<Result<EchoResponse>>(
                Error.BusinessRule("DEV.ECHO_REJECTED", "El texto 'fallar' se rechaza para demostrar un error de negocio."));
        }

        var upper = request.Text.ToUpperInvariant();
        return Task.FromResult<Result<EchoResponse>>(new EchoResponse(upper, upper.Length));
    }
}
