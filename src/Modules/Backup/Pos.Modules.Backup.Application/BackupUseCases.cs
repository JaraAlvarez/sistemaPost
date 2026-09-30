using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Licensing;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Backup.Contracts;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Backup.Application;

/// <summary>Configuración de los backups (D11-05, D11-08, D11-10). REGLA: el valor por defecto de una clave publicada no se cambia.</summary>
public static class BackupSettings
{
    public static readonly SettingDefinition<int> IntervalHours = new(
        "backup.interval_hours", 4, SettingScope.Company, "Horas entre backups programados dentro del horario de la tienda.",
        v => v is >= 1 and <= 24 ? null : "Entre 1 y 24.");

    public static readonly SettingDefinition<int> WindowStartHour = new(
        "backup.window_start_hour", 8, SettingScope.Company, "Hora local desde la que corren los backups programados.", Hour);

    public static readonly SettingDefinition<int> WindowEndHour = new(
        "backup.window_end_hour", 22, SettingScope.Company, "Hora local hasta la que corren los backups programados (sin incluirla).", Hour);

    public static readonly SettingDefinition<int> NightlyHour = new(
        "backup.nightly_hour", 23, SettingScope.Company, "Hora local del backup nocturno.", Hour);

    public static readonly SettingDefinition<int> NightlyMinute = new(
        "backup.nightly_minute", 30, SettingScope.Company, "Minuto del backup nocturno.", v => v is >= 0 and <= 59 ? null : "Entre 0 y 59.");

    public static readonly SettingDefinition<int> ClosingMinMinutes = new(
        "backup.closing_min_minutes", 30, SettingScope.Company, "Minutos mínimos entre dos backups disparados por cierres de caja.",
        v => v is >= 5 and <= 240 ? null : "Entre 5 y 240.");

    public static readonly SettingDefinition<int> MaxAgeHours = new(
        "backup.max_age_hours", 26, SettingScope.Company, "Alerta si el último backup verificado tiene más de estas horas.",
        v => v is >= 4 and <= 168 ? null : "Entre 4 y 168.");

    public static readonly SettingDefinition<int> RestoreTestWeekday = new(
        "backup.restore_test_weekday", 0, SettingScope.Company, "Día de la restauración de prueba semanal (0 = domingo … 6 = sábado).",
        v => v is >= 0 and <= 6 ? null : "Entre 0 y 6.");

    public static readonly SettingDefinition<int> RestoreTestHour = new(
        "backup.restore_test_hour", 4, SettingScope.Company, "Hora local desde la que corre la restauración de prueba semanal.", Hour);

    public static IEnumerable<SettingDefinition> All =>
        [IntervalHours, WindowStartHour, WindowEndHour, NightlyHour, NightlyMinute, ClosingMinMinutes, MaxAgeHours, RestoreTestWeekday, RestoreTestHour];

    private static string? Hour(int v) => v is >= 0 and <= 23 ? null : "Entre 0 y 23.";
}

public sealed class BackupSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => BackupSettings.All;
}

public sealed class BackupPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => BackupPermissions.All;
}

public static class BackupErrors
{
    public static readonly Error SetupRequired = Error.BusinessRule("SETUP.REQUIRED", "Complete primero la configuración inicial (POST /api/v1/setup).");
    public static readonly Error RunNotFound = Error.NotFound("BACKUP.NOT_FOUND", "El backup no existe.");
    public static readonly Error FileMissing = Error.BusinessRule("BACKUP.FILE_MISSING", "El archivo del backup ya no está en este equipo (retención o borrado manual).");
    public static readonly Error DestinationNotFound = Error.NotFound("BACKUP.DESTINATION_NOT_FOUND", "El destino no existe.");
    public static readonly Error LocalIsFixed = Error.BusinessRule("BACKUP.LOCAL_DESTINATION_FIXED", "El destino LOCAL ya existe: solo se cambian su retención y cuándo se usa.");
    public static readonly Error RecoveryCodeMismatch = Error.Validation("BACKUP.RECOVERY_CODE_MISMATCH", "El código escrito no corresponde al código de recuperación vigente.");
    public static readonly Error NoRecoveryCode = Error.BusinessRule("BACKUP.NO_RECOVERY_CODE", "Aún no se ha generado un código de recuperación.");
    public static readonly Error BackupToolsMissing = Error.BusinessRule(
        "BACKUP.NOT_CONFIGURED", "Los backups no están configurados en este equipo (falta la conexión de backup o las herramientas de PostgreSQL).");
}

// ------------------------------------------------------------------------------------------------ Puertos

public sealed record DestinationInput(
    string Kind,
    string Name,
    string? Path,
    string? VolumeLabel,
    string? Endpoint,
    string? Region,
    string? Bucket,
    string? Prefix,
    string? AccessKey,
    string? SecretKey,
    bool OnScheduled,
    bool OnNightly,
    bool OnClosing,
    bool OnManual,
    int KeepDaily,
    int KeepWeekly,
    int KeepMonthly,
    bool IsActive);

public sealed record RecoveryKeyState(int Version, string KeyId, string WrappedKeyJson, bool Confirmed);

public interface IBackupStore
{
    Task<IReadOnlyList<BackupRunDto>> ListRunsAsync(int limit, CancellationToken cancellationToken);

    Task<BackupRunDto?> GetRunAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<BackupDestinationDto>> ListDestinationsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<BackupDestinationDto>> ListDestinationsAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>Sin <paramref name="actorId"/> se registra a nombre del usuario técnico <c>system</c> de la empresa.</summary>
    Task SaveDestinationAsync(Guid id, Guid companyId, DestinationInput input, byte[]? protectedSecret, bool isNew, Guid? actorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RestoreTestDto>> ListRestoreTestsAsync(int limit, CancellationToken cancellationToken);

    Task<RecoveryKeyState?> LatestRecoveryKeyAsync(CancellationToken cancellationToken);

    Task SaveRecoveryKeyAsync(int version, string keyId, string wrappedKeyJson, Guid userId, DateTimeOffset at, CancellationToken cancellationToken);

    Task ConfirmRecoveryKeyAsync(int version, Guid userId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Operaciones que necesitan el motor de backups (Infraestructura: cifrado, archivos, destinos).</summary>
public interface IBackupOperations
{
    bool IsConfigured { get; }

    /// <summary>Pone en cola un backup manual (lo ejecuta el proceso en segundo plano).</summary>
    void EnqueueManual(Guid? requestedBy);

    Task<BackupVerificationDto> VerifyAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Ruta del archivo en el destino LOCAL, si todavía existe.</summary>
    Task<string?> LocateAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Nuevo código de recuperación: el código (se muestra una vez), el id de la clave y la clave envuelta (JSON).</summary>
    (string Code, string KeyId, string WrappedKeyJson) NewRecoveryKey(int version);

    bool RecoveryCodeMatches(string wrappedKeyJson, string code);

    byte[] ProtectSecret(string secret);

    /// <summary>Escribe, lee y borra un archivo de prueba en el destino. Devuelve el error o <c>null</c>.</summary>
    Task<string?> TestDestinationAsync(Guid destinationId, CancellationToken cancellationToken);
}

/// <summary>Destino LOCAL de cada empresa (D11-06): la carpeta del servidor. Idempotente.</summary>
public sealed class BackupInitializer(IBackupStore store, IIdGenerator ids) : ICompanyInitializer
{
    public int Order => 90;

    public async Task InitializeAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if ((await store.ListDestinationsAsync(companyId, cancellationToken)).Any(d => d.Kind == DestinationKinds.Local))
        {
            return;
        }

        await store.SaveDestinationAsync(
            ids.NewId(), companyId,
            new DestinationInput(DestinationKinds.Local, "Carpeta del servidor", null, null, null, null, null, null, null, null,
                OnScheduled: true, OnNightly: true, OnClosing: true, OnManual: true, KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 12, IsActive: true),
            null, isNew: true, actorId: null, cancellationToken);
    }
}

// ------------------------------------------------------------------------------------------------ Consultas

public sealed record ListBackupsQuery(int Limit = 50) : IQuery<IReadOnlyList<BackupRunDto>>;

internal sealed class ListBackupsHandler(IBackupStore store) : IQueryHandler<ListBackupsQuery, IReadOnlyList<BackupRunDto>>
{
    public async Task<Result<IReadOnlyList<BackupRunDto>>> Handle(ListBackupsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.ListRunsAsync(Math.Clamp(request.Limit, 1, 500), cancellationToken));
}

public sealed record ListDestinationsQuery : IQuery<IReadOnlyList<BackupDestinationDto>>;

internal sealed class ListDestinationsHandler(IBackupStore store) : IQueryHandler<ListDestinationsQuery, IReadOnlyList<BackupDestinationDto>>
{
    public async Task<Result<IReadOnlyList<BackupDestinationDto>>> Handle(ListDestinationsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.ListDestinationsAsync(cancellationToken));
}

public sealed record ListRestoreTestsQuery(int Limit = 20) : IQuery<IReadOnlyList<RestoreTestDto>>;

internal sealed class ListRestoreTestsHandler(IBackupStore store) : IQueryHandler<ListRestoreTestsQuery, IReadOnlyList<RestoreTestDto>>
{
    public async Task<Result<IReadOnlyList<RestoreTestDto>>> Handle(ListRestoreTestsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await store.ListRestoreTestsAsync(Math.Clamp(request.Limit, 1, 200), cancellationToken));
}

public sealed record GetBackupAlertsQuery : IQuery<BackupAlertsDto>;

internal sealed class GetBackupAlertsHandler(IBackupStatus status) : IQueryHandler<GetBackupAlertsQuery, BackupAlertsDto>
{
    public async Task<Result<BackupAlertsDto>> Handle(GetBackupAlertsQuery request, CancellationToken cancellationToken) =>
        await status.GetAlertsAsync(cancellationToken);
}

/// <summary>Verifica un backup del destino LOCAL: descifra, huellas y volcado legible. No usa transacción (puede tardar).</summary>
public sealed record VerifyBackupQuery(Guid RunId) : IQuery<BackupVerificationDto>;

internal sealed class VerifyBackupHandler(IBackupStore store, IBackupOperations operations) : IQueryHandler<VerifyBackupQuery, BackupVerificationDto>
{
    public async Task<Result<BackupVerificationDto>> Handle(VerifyBackupQuery request, CancellationToken cancellationToken)
    {
        if (await store.GetRunAsync(request.RunId, cancellationToken) is not { Succeeded: true })
        {
            return BackupErrors.RunNotFound;
        }

        return await operations.VerifyAsync(request.RunId, cancellationToken);
    }
}

/// <summary>Prueba un destino: escribe, lee y borra un archivo pequeño.</summary>
public sealed record TestDestinationQuery(Guid DestinationId) : IQuery<string>;

internal sealed class TestDestinationHandler(IBackupOperations operations) : IQueryHandler<TestDestinationQuery, string>
{
    public async Task<Result<string>> Handle(TestDestinationQuery request, CancellationToken cancellationToken) =>
        await operations.TestDestinationAsync(request.DestinationId, cancellationToken) is { } error
            ? Error.BusinessRule("BACKUP.DESTINATION_FAILED", error)
            : "El destino respondió correctamente.";
}

// ------------------------------------------------------------------------------------------------ Comandos

/// <summary>Respaldar ahora (D11-05): se pone en cola; el resultado aparece en el historial.</summary>
public sealed record RunBackupNowCommand : ICommand<string>, IAllowedWhenRestricted;

internal sealed class RunBackupNowHandler(IBackupOperations operations, ICurrentUser user) : ICommandHandler<RunBackupNowCommand, string>
{
    public Task<Result<string>> Handle(RunBackupNowCommand request, CancellationToken cancellationToken)
    {
        if (!operations.IsConfigured)
        {
            return Task.FromResult<Result<string>>(BackupErrors.BackupToolsMissing);
        }

        operations.EnqueueManual(user.UserId);
        return Task.FromResult(Result.Success("Backup en cola: el resultado aparecerá en el historial en unos minutos."));
    }
}

/// <summary>Descarga de un backup (queda en la auditoría): devuelve la ruta del archivo local.</summary>
public sealed record DownloadBackupCommand(Guid RunId) : ICommand<string>, IAllowedWhenRestricted;

internal sealed class DownloadBackupHandler(IBackupStore store, IBackupOperations operations, IAuditWriter audit) : ICommandHandler<DownloadBackupCommand, string>
{
    public async Task<Result<string>> Handle(DownloadBackupCommand request, CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(request.RunId, cancellationToken);
        if (run is not { Succeeded: true })
        {
            return BackupErrors.RunNotFound;
        }

        var path = await operations.LocateAsync(run.Id, cancellationToken);
        if (path is null)
        {
            return BackupErrors.FileMissing;
        }

        await audit.WriteAsync(
            new AuditEntry("backup", "BACKUP_DOWNLOADED", "Backup", run.Id, run.FileName, $"Descargó el backup {run.FileName} (cifrado).", Severity: AuditSeverity.Warning),
            cancellationToken);
        return path;
    }
}

public sealed record SaveDestinationCommand(Guid? Id, DestinationInput Input) : ICommand<BackupDestinationDto>, IAllowedWhenRestricted;

internal sealed class SaveDestinationValidator : AbstractValidator<SaveDestinationCommand>
{
    public SaveDestinationValidator()
    {
        RuleFor(x => x.Input.Kind).Must(k => k is DestinationKinds.Local or DestinationKinds.External or DestinationKinds.Network or DestinationKinds.S3)
            .WithMessage("Tipo: LOCAL, EXTERNAL, NETWORK o S3.");
        RuleFor(x => x.Input.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Input.Path).NotEmpty().MaximumLength(400).When(x => x.Input.Kind is DestinationKinds.External or DestinationKinds.Network)
            .WithMessage("Indique la carpeta (p. ej. E:\\Backups o \\\\NAS\\backups).");
        RuleFor(x => x.Input.Endpoint).NotEmpty().Must(e => Uri.TryCreate(e, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            .When(x => x.Input.Kind == DestinationKinds.S3).WithMessage("Indique la URL del servicio S3 (p. ej. https://backups.midominio.com).");
        RuleFor(x => x.Input.Bucket).NotEmpty().MaximumLength(100).When(x => x.Input.Kind == DestinationKinds.S3);
        RuleFor(x => x.Input.AccessKey).NotEmpty().MaximumLength(200).When(x => x.Input.Kind == DestinationKinds.S3);
        RuleFor(x => x.Input.KeepDaily).InclusiveBetween(1, 60);
        RuleFor(x => x.Input.KeepWeekly).InclusiveBetween(0, 52);
        RuleFor(x => x.Input.KeepMonthly).InclusiveBetween(0, 120);
    }
}

internal sealed class SaveDestinationHandler(
    IBackupStore store, IBackupOperations operations, IInstallationContext installation, ICurrentUser user, IAuditWriter audit, IIdGenerator ids)
    : ICommandHandler<SaveDestinationCommand, BackupDestinationDto>
{
    public async Task<Result<BackupDestinationDto>> Handle(SaveDestinationCommand request, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return BackupErrors.SetupRequired;
        }

        var existing = await store.ListDestinationsAsync(cancellationToken);
        BackupDestinationDto? current = null;
        if (request.Id is { } id)
        {
            current = existing.FirstOrDefault(d => d.Id == id);
            if (current is null)
            {
                return BackupErrors.DestinationNotFound;
            }

            if (current.Kind != request.Input.Kind)
            {
                return Error.Validation("BACKUP.KIND_CHANGE", "No se puede cambiar el tipo de un destino: cree uno nuevo.");
            }
        }
        else if (request.Input.Kind == DestinationKinds.Local)
        {
            return BackupErrors.LocalIsFixed;
        }

        if (request.Input.Kind == DestinationKinds.S3 && string.IsNullOrWhiteSpace(request.Input.SecretKey) && current is not { HasSecret: true })
        {
            return Error.Validation("BACKUP.SECRET_REQUIRED", "Indique la clave secreta del almacenamiento S3.");
        }

        var secret = string.IsNullOrWhiteSpace(request.Input.SecretKey) ? null : operations.ProtectSecret(request.Input.SecretKey);
        var destinationId = request.Id ?? ids.NewId();
        await store.SaveDestinationAsync(destinationId, companyId, request.Input, secret, request.Id is null, user.UserId!.Value, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("backup", "BACKUP_DESTINATION_CHANGED", "BackupDestination", destinationId, request.Input.Name,
                $"{(request.Id is null ? "Creó" : "Modificó")} el destino de backups {request.Input.Name} ({request.Input.Kind})"
                + (request.Input.IsActive ? "." : " (inactivo)."),
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return (await store.ListDestinationsAsync(cancellationToken)).First(d => d.Id == destinationId);
    }
}

/// <summary>Genera (o regenera) el código de recuperación (D11-04). Se muestra una sola vez.</summary>
public sealed record GenerateRecoveryCodeCommand : ICommand<RecoveryCodeDto>, IAllowedWhenRestricted;

internal sealed class GenerateRecoveryCodeHandler(IBackupStore store, IBackupOperations operations, ICurrentUser user, IAuditWriter audit, IClock clock)
    : ICommandHandler<GenerateRecoveryCodeCommand, RecoveryCodeDto>
{
    public async Task<Result<RecoveryCodeDto>> Handle(GenerateRecoveryCodeCommand request, CancellationToken cancellationToken)
    {
        var version = ((await store.LatestRecoveryKeyAsync(cancellationToken))?.Version ?? 0) + 1;
        var (code, keyId, wrapped) = operations.NewRecoveryKey(version);
        await store.SaveRecoveryKeyAsync(version, keyId, wrapped, user.UserId!.Value, clock.UtcNow, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("backup", "RECOVERY_CODE_GENERATED", "RecoveryKey", null, $"Versión {version}",
                version == 1 ? "Generó el código de recuperación de los backups." : $"Regeneró el código de recuperación (versión {version}); los backups nuevos lo usan.",
                Severity: AuditSeverity.Warning),
            cancellationToken);
        return new RecoveryCodeDto(version, code,
            "Imprima o escriba este código y guárdelo fuera del equipo. Se muestra UNA sola vez. Sin él no se puede restaurar un backup en otro "
            + "computador. Confírmelo escribiéndolo de nuevo en POST /backups/recovery-code/confirm.");
    }
}

/// <summary>El propietario escribe de nuevo el código para confirmar que lo guardó (se comprueba abriendo la clave envuelta).</summary>
public sealed record ConfirmRecoveryCodeCommand(string Code) : ICommand, IAllowedWhenRestricted;

internal sealed class ConfirmRecoveryCodeHandler(IBackupStore store, IBackupOperations operations, ICurrentUser user, IAuditWriter audit, IClock clock)
    : ICommandHandler<ConfirmRecoveryCodeCommand>
{
    public async Task<Result> Handle(ConfirmRecoveryCodeCommand request, CancellationToken cancellationToken)
    {
        var latest = await store.LatestRecoveryKeyAsync(cancellationToken);
        if (latest is null)
        {
            return BackupErrors.NoRecoveryCode;
        }

        if (!operations.RecoveryCodeMatches(latest.WrappedKeyJson, request.Code ?? string.Empty))
        {
            return BackupErrors.RecoveryCodeMismatch;
        }

        await store.ConfirmRecoveryKeyAsync(latest.Version, user.UserId!.Value, clock.UtcNow, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry("backup", "RECOVERY_CODE_CONFIRMED", "RecoveryKey", null, $"Versión {latest.Version}", "Confirmó que guardó el código de recuperación."),
            cancellationToken);
        return Result.Success();
    }
}
