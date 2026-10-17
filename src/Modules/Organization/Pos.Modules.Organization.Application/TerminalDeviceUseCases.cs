using Pos.Application.Abstractions.Messaging;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Application;

internal static class TerminalDeviceMapping
{
    public static ReceiptPrinterDto ToDto(this TerminalDevice d, bool configured) => new(
        d.PosTerminalId, configured, d.Connection.ToString().ToUpperInvariant() switch { "WINDOWSSPOOLER" => "WINDOWS_SPOOLER", var c => c }, d.Address,
        d.PaperWidthMm, d.CodePage.ToString().ToUpperInvariant(), d.AutoCut, d.DrawerConnected, d.DrawerPin.ToString().ToUpperInvariant());
}

/// <summary>Impresora de tiquetes de una caja (la lee la interfaz de caja y se la entrega al agente, D7-14).</summary>
public sealed record GetReceiptPrinterQuery(Guid PosTerminalId) : IQuery<ReceiptPrinterDto>;

internal sealed class GetReceiptPrinterHandler(IOrganizationStore store) : IQueryHandler<GetReceiptPrinterQuery, ReceiptPrinterDto>
{
    public async Task<Result<ReceiptPrinterDto>> Handle(GetReceiptPrinterQuery request, CancellationToken cancellationToken)
    {
        var terminal = await store.GetTerminalAsync(request.PosTerminalId, cancellationToken);
        if (terminal is null)
        {
            return OrganizationErrors.TerminalNotFound;
        }

        var device = await store.GetTerminalDeviceAsync(terminal.Id, TerminalDevice.ReceiptPrinterKind, cancellationToken);
        return device is null ? TerminalDevice.Defaults(Guid.Empty, terminal.CompanyId, terminal.Id).ToDto(configured: false) : device.ToDto(configured: true);
    }
}

public sealed record SetReceiptPrinterCommand(
    Guid PosTerminalId, PrinterConnection Connection, string? Address, int PaperWidthMm, PrinterCodePageSetting CodePage, bool AutoCut, bool DrawerConnected,
    DrawerPinSetting DrawerPin) : ICommand<ReceiptPrinterDto>;

internal sealed class SetReceiptPrinterHandler(IOrganizationStore store, IIdGenerator ids) : ICommandHandler<SetReceiptPrinterCommand, ReceiptPrinterDto>
{
    public async Task<Result<ReceiptPrinterDto>> Handle(SetReceiptPrinterCommand request, CancellationToken cancellationToken)
    {
        var terminal = await store.GetTerminalAsync(request.PosTerminalId, cancellationToken);
        if (terminal is null)
        {
            return OrganizationErrors.TerminalNotFound;
        }

        var device = await store.GetTerminalDeviceAsync(terminal.Id, TerminalDevice.ReceiptPrinterKind, cancellationToken);
        var isNew = device is null;
        device ??= TerminalDevice.Defaults(ids.NewId(), terminal.CompanyId, terminal.Id);
        var configured = device.Configure(
            request.Connection, request.Address, request.PaperWidthMm, request.CodePage, request.AutoCut, request.DrawerConnected, request.DrawerPin);
        if (configured.IsFailure)
        {
            return configured.Error;
        }

        if (isNew)
        {
            store.Add(device);
        }

        return device.ToDto(configured: true);
    }
}

internal static class TerminalScaleMapping
{
    public static TerminalScaleDto ToDto(this TerminalScale s, bool configured) => new(
        s.PosTerminalId, configured, s.Enabled, s.Driver, s.Port, s.BaudRate, s.DataBits, s.Parity, s.StopBits, s.ReadMode, s.RequestCommand, s.TimeoutMs,
        s.Decimals, s.Brand, s.Model, s.CatalogCode, s.StableWaitSeconds, s.EmptyWaitSeconds, s.AllowManualWeight, MinWeightGrams: s.MinWeightGrams,
        ReadingKeyId: s.ReadingKeyId, ReadingKeySetAt: s.ReadingKeySetAt);
}

/// <summary>Báscula de la caja (Fase 7-C). Sin fila: <c>Configured</c> = false y la caja trabaja sin báscula.</summary>
public sealed record GetTerminalScaleQuery(Guid PosTerminalId) : IQuery<TerminalScaleDto>;

internal sealed class GetTerminalScaleHandler(IOrganizationStore store) : IQueryHandler<GetTerminalScaleQuery, TerminalScaleDto>
{
    public async Task<Result<TerminalScaleDto>> Handle(GetTerminalScaleQuery request, CancellationToken cancellationToken)
    {
        var terminal = await store.GetTerminalAsync(request.PosTerminalId, cancellationToken);
        if (terminal is null)
        {
            return OrganizationErrors.TerminalNotFound;
        }

        var scale = await store.GetTerminalScaleAsync(terminal.Id, cancellationToken);
        var dto = scale is null ? TerminalScale.Defaults(Guid.Empty, terminal.CompanyId, terminal.Id).ToDto(configured: false) : scale.ToDto(configured: true);

        // Fase 7-C parte A: con "Báscula y PLU" apagada la caja no lee la báscula; la configuración guardada no se toca.
        var company = await store.GetCompanyAsync(terminal.CompanyId, cancellationToken);
        return company is null || company.Has(BusinessFeature.Scale) ? dto : dto with { FeatureEnabled = false };
    }
}

/// <summary>
/// Vincula la báscula de la caja (D3): registra la clave pública que el agente de ESA caja acaba de crear. Exige la báscula configurada.
/// Volver a vincular reemplaza la clave (rotación): las lecturas firmadas con la anterior dejan de valer.
/// </summary>
public sealed record SetTerminalScaleKeyCommand(Guid PosTerminalId, Guid KeyId, string PublicKey) : ICommand<TerminalScaleDto>;

internal sealed class SetTerminalScaleKeyHandler(
    IOrganizationStore store,
    Pos.SharedKernel.Time.IClock clock,
    Pos.Application.Abstractions.Security.ICurrentUser current,
    Pos.Application.Abstractions.Security.IClientContext client,
    Pos.Application.Abstractions.Security.IAuthorizationScope authorization,
    Pos.Application.Abstractions.Auditing.IAuditWriter audit) : ICommandHandler<SetTerminalScaleKeyCommand, TerminalScaleDto>
{
    public async Task<Result<TerminalScaleDto>> Handle(SetTerminalScaleKeyCommand request, CancellationToken cancellationToken)
    {
        var terminal = await store.GetTerminalAsync(request.PosTerminalId, cancellationToken);
        if (terminal is null)
        {
            return OrganizationErrors.TerminalNotFound;
        }

        // BB2: el agente no tiene una credencial propia que el servidor pueda verificar (la del equipo vive en el navegador de la caja).
        // Por eso solo se vincula desde ESA caja: una sesión de caja (PIN) abierta en el equipo emparejado de esa caja (o en el propio
        // servidor si es Caja Única). Un administrador desde otro equipo, o con un token robado de la administración, no puede registrar
        // su propia clave.
        var fromThisTerminal = current.IsTerminalSession && current.PosTerminalId == terminal.Id
            && (client.PosTerminalId == terminal.Id || (client.IsLocal && client.DeviceId is null));
        if (!fromThisTerminal)
        {
            return Error.Forbidden(
                "ORGANIZATION.SCALE_LINK_TERMINAL",
                "La báscula se vincula desde la pantalla de venta de esa misma caja (F1 → Vincular báscula), con la autorización de un supervisor.");
        }

        if (await store.GetTerminalScaleAsync(terminal.Id, cancellationToken) is not { } scale)
        {
            return Error.BusinessRule("ORGANIZATION.SCALE_NOT_CONFIGURED", "Guarde primero la báscula de la caja y luego vincúlela.");
        }

        var changed = scale.ReadingKeyId is not null;
        var set = scale.SetReadingKey(request.KeyId, request.PublicKey, clock.UtcNow);
        if (set.IsFailure)
        {
            return set.Error;
        }

        await audit.WriteAsync(
            new Pos.Application.Abstractions.Auditing.AuditEntry(
                "organization", "SCALE_KEY_LINKED", nameof(TerminalScale), scale.Id, $"Báscula de la caja {terminal.Code}",
                changed
                    ? $"La clave de la báscula de la caja {terminal.Code} cambió (se volvió a vincular): las lecturas firmadas con la anterior ya no valen."
                    : $"Báscula de la caja {terminal.Code} vinculada con su agente.",
                AuthorizedBy: authorization.Current?.AuthorizedBy, Severity: Pos.Application.Abstractions.Auditing.AuditSeverity.Warning),
            cancellationToken);
        return scale.ToDto(configured: true);
    }
}

/// <summary>
/// Alertas de báscula del Tablero: clave vinculada o cambiada en los últimos 7 días (BB2, además de la auditoría) y básculas activas sin
/// vincular (BK3). Dos consultas (cajas y básculas), sin una por caja (BK6).
/// </summary>
public sealed record ListScaleKeyChangesQuery : IQuery<IReadOnlyList<ScaleKeyChangeDto>>;

internal sealed class ListScaleKeyChangesHandler(IOrganizationStore store, Pos.SharedKernel.Time.IClock clock)
    : IQueryHandler<ListScaleKeyChangesQuery, IReadOnlyList<ScaleKeyChangeDto>>
{
    public async Task<Result<IReadOnlyList<ScaleKeyChangeDto>>> Handle(ListScaleKeyChangesQuery request, CancellationToken cancellationToken)
    {
        var since = clock.UtcNow.AddDays(-7);
        var scales = (await store.ListTerminalScalesAsync(cancellationToken)).ToDictionary(s => s.PosTerminalId);
        var changes = new List<ScaleKeyChangeDto>();
        foreach (var terminal in await store.ListTerminalsAsync(null, cancellationToken))
        {
            if (!scales.TryGetValue(terminal.Id, out var scale))
            {
                continue;
            }

            if (scale.ReadingKeySetAt is { } at && at >= since)
            {
                changes.Add(new ScaleKeyChangeDto(terminal.Id, terminal.Code, at));
            }
            else if (scale.Enabled && scale.ReadingKeyId is null && terminal.Status == "ACTIVE")
            {
                changes.Add(new ScaleKeyChangeDto(terminal.Id, terminal.Code, null, Linked: false));
            }
        }

        return changes;
    }
}

public sealed record SetTerminalScaleCommand(Guid PosTerminalId, ScaleConfiguration Configuration) : ICommand<TerminalScaleDto>;

internal sealed class SetTerminalScaleHandler(IOrganizationStore store, IIdGenerator ids) : ICommandHandler<SetTerminalScaleCommand, TerminalScaleDto>
{
    public async Task<Result<TerminalScaleDto>> Handle(SetTerminalScaleCommand request, CancellationToken cancellationToken)
    {
        var terminal = await store.GetTerminalAsync(request.PosTerminalId, cancellationToken);
        if (terminal is null)
        {
            return OrganizationErrors.TerminalNotFound;
        }

        var scale = await store.GetTerminalScaleAsync(terminal.Id, cancellationToken);
        var isNew = scale is null;
        scale ??= TerminalScale.Defaults(ids.NewId(), terminal.CompanyId, terminal.Id);
        var configured = scale.Configure(request.Configuration);
        if (configured.IsFailure)
        {
            return configured.Error;
        }

        if (isNew)
        {
            store.Add(scale);
        }

        return scale.ToDto(configured: true);
    }
}
