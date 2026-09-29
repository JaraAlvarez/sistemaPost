using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Abstractions.Numbering;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Numbering;

/// <summary>
/// Numeración interna sin duplicados ni huecos en operación normal (docs/fases/fase-02-propuesta.md §14).
/// Un solo UPDATE … RETURNING bloquea la fila de la serie hasta el fin de la transacción del documento: si la
/// transacción se revierte, el número no se consume. No se usan secuencias de PostgreSQL (no son transaccionales).
/// Las series de venta son por caja: dos cajas nunca esperan una por la otra.
/// </summary>
internal sealed class DocumentNumberAllocator(PosDbContext context, IActorContext actor) : IDocumentNumberAllocator
{
    public const string SeriesNotFoundMessage = "No hay una serie de numeración activa";

    public Task<DocumentNumber> NextForTerminalAsync(string documentType, Guid posTerminalId, CancellationToken cancellationToken = default) =>
        AllocateAsync("pos_terminal_id = @scope", documentType, posTerminalId, cancellationToken);

    public Task<DocumentNumber> NextForBranchAsync(string documentType, Guid branchId, CancellationToken cancellationToken = default) =>
        AllocateAsync("branch_id = @scope AND pos_terminal_id IS NULL", documentType, branchId, cancellationToken);

    public static string Format(string prefix, long number, int padding) =>
        $"{prefix}-{number.ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0')}";

    private async Task<DocumentNumber> AllocateAsync(string scopeFilter, string documentType, Guid scopeId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        var transaction = context.Database.CurrentTransaction
            ?? throw new InvalidOperationException("La numeración exige la transacción del documento (TransactionBehavior).");

        var connection = context.Database.GetDbConnection();
        var row = await connection.QuerySingleOrDefaultAsync<(Guid Id, long Number, string Prefix, short Padding)?>(new CommandDefinition(
            $"""
            UPDATE system.document_series
            SET next_number = next_number + 1, updated_at = now(), updated_by = @actor
            WHERE document_type = @type AND status = 'ACTIVE' AND {scopeFilter}
            RETURNING id, next_number - 1, prefix, padding
            """,
            new { type = documentType, scope = scopeId, actor = actor.ActorId },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken));

        if (row is not { } series)
        {
            throw new InvalidOperationException($"{SeriesNotFoundMessage} de tipo {documentType} para {scopeId}.");
        }

        return new DocumentNumber(series.Id, series.Number, Format(series.Prefix, series.Number, series.Padding));
    }
}
