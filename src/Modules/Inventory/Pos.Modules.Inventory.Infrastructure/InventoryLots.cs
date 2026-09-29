using System.Globalization;
using Dapper;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Catalog.Contracts;
using Pos.Modules.Inventory.Contracts;
using Pos.Modules.Inventory.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Inventory.Infrastructure;

/// <summary>
/// Lotes de la sucursal local (D5-05): número normalizado en mayúsculas, único por sucursal y producto. Se crean en la
/// transacción del documento que los recibe (compra, ajuste); dos recepciones simultáneas del mismo lote lo comparten.
/// </summary>
internal sealed class InventoryLots(
    PosDbContext context, ICatalogReader catalog, IInstallationContext installation, IActorContext actor, IIdGenerator ids, IClock clock) : IInventoryLots
{
    public const int MaxLotNumberLength = 40;

    public async Task<Result<Guid>> EnsureLotAsync(
        Guid productId, string lotNumber, DateOnly? expiryDate, DateOnly? manufacturedDate, CancellationToken cancellationToken = default)
    {
        var number = Normalize(lotNumber);
        if (number is null || (manufacturedDate is { } made && expiryDate is { } expires && expires < made))
        {
            return InventoryErrors.InvalidLot;
        }

        if ((await catalog.GetProductsAsync([productId], cancellationToken)).GetValueOrDefault(productId) is not { } product)
        {
            return InventoryErrors.ProductNotFound;
        }

        if (!product.TracksLots)
        {
            return InventoryErrors.LotNotTracked;
        }

        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        var existing = await FindAsync(connection, transaction, productId, number, cancellationToken);
        if (existing is null)
        {
            if (product.TracksExpiry && expiryDate is null)
            {
                return InventoryErrors.ExpiryRequired;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO inventory.inventory_lots (id, company_id, branch_id, product_id, lot_number, expiry_date, manufactured_date, status, created_at, created_by)
                VALUES (@id, @companyId, @branchId, @productId, @number, @expiry::date, @manufactured::date, 'AVAILABLE', @now, @userId)
                ON CONFLICT (branch_id, product_id, lot_number) DO NOTHING
                """,
                new
                {
                    id = ids.NewId(), companyId = installation.CompanyId, branchId = installation.BranchId, productId, number,
                    expiry = Text(expiryDate), manufactured = Text(manufacturedDate), now = clock.UtcNow, userId = actor.ActorId,
                },
                transaction, cancellationToken: cancellationToken));
            existing = await FindAsync(connection, transaction, productId, number, cancellationToken);
        }

        if (existing!.ExpiryDate is { } current && expiryDate is { } requested && DateOnly.FromDateTime(current) != requested)
        {
            return InventoryErrors.LotExpiryMismatch;
        }

        if (existing.ExpiryDate is null && expiryDate is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE inventory.inventory_lots SET expiry_date = @expiry::date, updated_at = @now, updated_by = @userId WHERE id = @id",
                new { expiry = Text(expiryDate), now = clock.UtcNow, userId = actor.ActorId, id = existing.Id }, transaction, cancellationToken: cancellationToken));
        }

        return existing.Id;
    }

    public async Task<Guid?> FindLotAsync(Guid productId, string lotNumber, CancellationToken cancellationToken = default)
    {
        if (Normalize(lotNumber) is not { } number)
        {
            return null;
        }

        var (connection, transaction) = await DbSession.OpenAsync(context, cancellationToken);
        return (await FindAsync(connection, transaction, productId, number, cancellationToken))?.Id;
    }

    internal static string? Normalize(string? lotNumber)
    {
        var number = (lotNumber ?? string.Empty).Trim().ToUpperInvariant();
        return number.Length is 0 or > MaxLotNumberLength ? null : number;
    }

    private async Task<LotRow?> FindAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid productId, string number, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<LotRow>(new CommandDefinition(
            """
            SELECT id AS Id, expiry_date AS ExpiryDate FROM inventory.inventory_lots
            WHERE branch_id = @branchId AND product_id = @productId AND lot_number = @number
            """,
            new { branchId = installation.BranchId, productId, number }, transaction, cancellationToken: cancellationToken));

    private static string? Text(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class LotRow
    {
        public Guid Id { get; set; }

        public DateTime? ExpiryDate { get; set; }
    }
}
