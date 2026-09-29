using System.Text.RegularExpressions;
using FluentValidation;
using Pos.Application.Abstractions.Auditing;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Identity.Contracts;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;
using Pos.SharedKernel.Security;
using Pos.SharedKernel.Time;

namespace Pos.Modules.Organization.Application.Devices;

/// <summary>Código de emparejamiento pendiente.</summary>
public sealed record PendingPairingCode(Guid Id, Guid CompanyId, DeviceKind Kind, Guid? PosTerminalId, Guid CreatedBy);

/// <summary>Puerto de persistencia de equipos y códigos de emparejamiento.</summary>
public interface IDeviceStore
{
    void Add(Device device);

    void AddPairingCode(Guid id, Guid companyId, Guid nodeId, string codeHash, DeviceKind kind, Guid? posTerminalId, Guid createdBy,
        DateTimeOffset createdAt, DateTimeOffset expiresAt);

    /// <summary>Bloquea un código vigente hasta el fin de la transacción (dos equipos no pueden usar el mismo).</summary>
    Task<PendingPairingCode?> LockPairingCodeAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkPairingCodeUsedAsync(Guid codeId, Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<Device?> GetAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken cancellationToken);
}

public static class DeviceErrors
{
    public static readonly Error PairingRequiresMulti = Error.Conflict(
        "DEVICE.PAIRING_REQUIRES_MULTI", "La edición Caja Única no admite otros equipos: instale la edición Multicaja.");

    public static readonly Error InvalidPairingCode =
        Error.Unauthorized("DEVICE.INVALID_PAIRING_CODE", "El código de emparejamiento no es válido, ya se usó o venció.");

    public static readonly Error DeviceNotFound = Error.NotFound("DEVICE.NOT_FOUND", "El equipo no existe.");
}

/// <summary>Resultado del emparejamiento: se confirma también cuando falla (queda auditado el intento).</summary>
public sealed record PairingOutcome(PairedDeviceDto? Value, Error? Error);

// ───────────────────────────── Código de emparejamiento ─────────────────────────────

/// <summary>Un administrador genera un código de 6 dígitos, de un solo uso, que vence en 10 minutos.</summary>
public sealed record CreatePairingCodeCommand(DeviceKind Kind, Guid? PosTerminalId) : ICommand<PairingCodeDto>;

internal sealed class CreatePairingCodeValidator : AbstractValidator<CreatePairingCodeCommand>
{
    public CreatePairingCodeValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.PosTerminalId).NotNull().When(x => x.Kind == DeviceKind.Terminal).WithMessage("Indique la caja que usará el equipo.");
        RuleFor(x => x.PosTerminalId).Null().When(x => x.Kind == DeviceKind.AdminWorkstation);
    }
}

internal sealed class CreatePairingCodeHandler(
    IInstallationContext installation, IDeviceStore devices, IOrganizationStore organization, IServerIdentity server, ICurrentUser current,
    IAuditWriter audit, IIdGenerator ids, IClock clock) : ICommandHandler<CreatePairingCodeCommand, PairingCodeDto>
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

    public async Task<Result<PairingCodeDto>> Handle(CreatePairingCodeCommand request, CancellationToken cancellationToken)
    {
        if (installation.NodeRole != NodeRole.StoreServer)
        {
            return DeviceErrors.PairingRequiresMulti;
        }

        if (request.PosTerminalId is { } terminalId
            && await organization.GetTerminalAsync(terminalId, cancellationToken) is not { Status: TerminalStatus.Active })
        {
            return OrganizationErrors.TerminalNotFound;
        }

        var code = SecureTokens.NumericCode();
        var now = clock.UtcNow;
        var id = ids.NewId();
        devices.AddPairingCode(id, installation.CompanyId!.Value, installation.NodeId, SecureTokens.Hash(code), request.Kind, request.PosTerminalId,
            current.UserId!.Value, now, now + Validity);
        await audit.WriteAsync(
            new AuditEntry("organization", "DEVICE_PAIRING_CODE_CREATED", "DevicePairingCode", id,
                Summary: $"{current.DisplayName} generó un código para emparejar un equipo ({request.Kind})."),
            cancellationToken);

        return new PairingCodeDto(code, now + Validity, request.Kind.ToString(), request.PosTerminalId, server.CertificateFingerprint);
    }
}

// ───────────────────────────── Emparejar ─────────────────────────────

/// <summary>El equipo nuevo presenta el código y recibe su credencial (que solo se muestra esta vez).</summary>
public sealed record PairDeviceCommand(string Code, string Hostname, string MachineFingerprintHash, string? OsVersion, string? AppVersion)
    : ICommand<PairingOutcome>;

internal sealed partial class PairDeviceValidator : AbstractValidator<PairDeviceCommand>
{
    public PairDeviceValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[0-9]{6}$");
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MachineFingerprintHash).NotEmpty().Must(h => FingerprintPattern().IsMatch(h ?? string.Empty))
            .WithMessage("La huella del equipo debe ser un SHA-256 en hexadecimal (64 caracteres en minúscula).");
        RuleFor(x => x.OsVersion).MaximumLength(60);
        RuleFor(x => x.AppVersion).MaximumLength(60);
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();
}

internal sealed class PairDeviceHandler(
    IInstallationContext installation, IDeviceStore devices, IOrganizationStore organization, ISessionRevoker sessions, IServerIdentity server,
    IActorContext actor, IAuditWriter audit, Pos.Application.Abstractions.Data.IUnitOfWork unitOfWork, IIdGenerator ids, IClock clock)
    : ICommandHandler<PairDeviceCommand, PairingOutcome>
{
    public async Task<Result<PairingOutcome>> Handle(PairDeviceCommand request, CancellationToken cancellationToken)
    {
        if (installation.NodeRole != NodeRole.StoreServer)
        {
            return new PairingOutcome(null, DeviceErrors.PairingRequiresMulti);
        }

        var now = clock.UtcNow;
        var deviceId = ids.NewId();
        var code = await devices.LockPairingCodeAsync(SecureTokens.Hash(request.Code), now, cancellationToken);
        if (code is null)
        {
            await audit.WriteAsync(
                new AuditEntry("organization", "DEVICE_PAIRING_FAILED", Summary: $"Intento de emparejar '{request.Hostname}' con un código inválido.",
                    Severity: AuditSeverity.Warning),
                cancellationToken);
            return new PairingOutcome(null, DeviceErrors.InvalidPairingCode);
        }

        actor.Use(code.CreatedBy, "Emparejamiento", code.CompanyId, installation.BranchId);
        var secret = SecureTokens.Create();
        var device = Device.Pair(
            deviceId, code.CompanyId, installation.NodeId, code.Kind == DeviceKind.Terminal ? DeviceType.Terminal : DeviceType.AdminWorkstation,
            request.Hostname, request.MachineFingerprintHash, request.OsVersion, request.AppVersion, SecureTokens.Hash(secret),
            server.CertificateFingerprint, code.CreatedBy, now);
        devices.Add(device);

        if (code.PosTerminalId is { } terminalId && await organization.GetTerminalAsync(terminalId, cancellationToken) is { } terminal)
        {
            // La caja pasa al equipo nuevo; el anterior (si lo había) queda revocado con sus sesiones.
            if (terminal.DeviceId is { } previous && await devices.GetAsync(previous, cancellationToken) is { } old)
            {
                old.Revoke(code.CreatedBy, now);
                await sessions.RevokeByDeviceAsync(old.Id, "DEVICE_REPLACED", cancellationToken);
            }

            terminal.AttachDevice(device.Id);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await devices.MarkPairingCodeUsedAsync(code.Id, device.Id, now, cancellationToken);

        return new PairingOutcome(
            new PairedDeviceDto(device.Id, secret, code.Kind.ToString(), code.PosTerminalId, server.CertificateFingerprint), null);
    }
}

// ───────────────────────────── Consulta y revocación ─────────────────────────────

public sealed record ListDevicesQuery : IQuery<IReadOnlyList<DeviceDto>>;

internal sealed class ListDevicesHandler(IDeviceStore devices) : IQueryHandler<ListDevicesQuery, IReadOnlyList<DeviceDto>>
{
    public async Task<Result<IReadOnlyList<DeviceDto>>> Handle(ListDevicesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await devices.ListAsync(cancellationToken));
}

/// <summary>Revoca un equipo: su credencial deja de valer y sus sesiones se cierran de inmediato.</summary>
public sealed record RevokeDeviceCommand(Guid DeviceId) : ICommand;

internal sealed class RevokeDeviceHandler(
    IDeviceStore devices, IOrganizationStore organization, ISessionRevoker sessions, ICurrentUser current, IClock clock) : ICommandHandler<RevokeDeviceCommand>
{
    public async Task<Result> Handle(RevokeDeviceCommand request, CancellationToken cancellationToken)
    {
        var device = await devices.GetAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return DeviceErrors.DeviceNotFound;
        }

        device.Revoke(current.UserId, clock.UtcNow);
        if (await organization.GetTerminalByDeviceAsync(device.Id, cancellationToken) is { } terminal)
        {
            terminal.DetachDevice();
        }

        await sessions.RevokeByDeviceAsync(device.Id, "DEVICE_REVOKED", cancellationToken);
        return Result.Success();
    }
}
