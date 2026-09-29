using System.Security.Cryptography;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Infrastructure.Persistence;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Security;

/// <summary>Resultado de la recuperación de emergencia.</summary>
public sealed record OwnerResetResult(bool Succeeded, string Message, string? TemporaryPassword);

/// <summary>
/// Recuperación de emergencia del Propietario (D3-10): solo se ejecuta en el servidor, con la credencial del migrador
/// (protegida con DPAPI, legible solo por un administrador de Windows). Asigna una contraseña temporal, obliga a
/// cambiarla, desbloquea al usuario, cierra sus sesiones y deja una auditoría CRÍTICA. Es una herramienta de
/// mantenimiento: trabaja con SQL directo sobre las tablas de identidad.
/// </summary>
public sealed class OwnerEmergencyReset(IServiceProvider services)
{
    public const string TemporaryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public async Task<OwnerResetResult> ResetAsync(string username, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        await services.GetRequiredService<IInstallationContext>().RefreshAsync(cancellationToken);

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        var user = await connection.QuerySingleOrDefaultAsync<(Guid Id, string DisplayName)?>(new CommandDefinition(
            """
            SELECT u.id, u.display_name FROM identity.users u
            WHERE u.username = @username AND u.kind = 'HUMAN' AND u.deleted_at IS NULL
              AND EXISTS (SELECT 1 FROM identity.user_roles ur JOIN identity.roles r ON r.id = ur.role_id
                          WHERE ur.user_id = u.id AND r.code = 'OWNER' AND r.is_system)
            """,
            new { username = username.Trim().ToLowerInvariant() },
            dbTransaction,
            cancellationToken: cancellationToken));
        if (user is not { } owner)
        {
            return new OwnerResetResult(false, $"No existe un Propietario con el usuario '{username}'.", null);
        }

        var temporary = RandomNumberGenerator.GetString(TemporaryAlphabet, 16);
        var hash = await scope.ServiceProvider.GetRequiredService<ISecretHasher>().HashAsync(temporary, SecretKind.Password, cancellationToken);
        var now = clock.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE identity.users
            SET password_hash = @hash, must_change_password = true, password_changed_at = @now, status = 'ACTIVE',
                failed_login_count = 0, locked_until = NULL, locked_reason = NULL, security_version = security_version + 1,
                row_version = row_version + 1, updated_at = @now, updated_by = id
            WHERE id = @id;
            UPDATE identity.user_sessions SET revoked_at = @now, revoked_reason = 'OWNER_EMERGENCY_RESET'
            WHERE user_id = @id AND revoked_at IS NULL;
            """,
            new { hash, now, id = owner.Id },
            dbTransaction,
            cancellationToken: cancellationToken));

        await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(
            new AuditEntry("identity", "OWNER_EMERGENCY_RESET", "User", owner.Id, $"Usuario {username} · {owner.DisplayName}",
                $"Recuperación de emergencia del Propietario {owner.DisplayName} desde el servidor (contraseña temporal, sesiones cerradas).",
                Severity: AuditSeverity.Critical),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OwnerResetResult(true, $"Contraseña temporal asignada a {owner.DisplayName}. Deberá cambiarla al entrar.", temporary);
    }
}
