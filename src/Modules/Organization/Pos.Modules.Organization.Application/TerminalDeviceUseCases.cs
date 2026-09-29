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
