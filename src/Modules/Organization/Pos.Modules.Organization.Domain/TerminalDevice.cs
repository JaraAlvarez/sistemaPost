using Pos.SharedKernel.Domain;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Domain;

/// <summary>Cómo se conecta la impresora de tiquetes al agente de la caja (Fase 7, D7-14).</summary>
public enum PrinterConnection
{
    /// <summary>Escribe los bytes ESC/POS en una carpeta (pruebas y cajas sin impresora).</summary>
    File,

    /// <summary>Impresora de red, puerto 9100 (RAW).</summary>
    Network,

    /// <summary>Impresora instalada en Windows con su controlador, por nombre (envío RAW por el spooler).</summary>
    WindowsSpooler,

    /// <summary>Puerto serie o USB virtual (COMx).</summary>
    Serial,
}

public enum PrinterCodePageSetting
{
    Pc850,
    Ascii,
}

public enum DrawerPinSetting
{
    Pin2,
    Pin5,
}

/// <summary>
/// Impresora de tiquetes de una caja (tabla <c>org.terminal_devices</c>, tipo RECEIPT_PRINTER). Sin fila, la caja usa la
/// configuración por defecto (<see cref="Defaults"/>): archivo, 80 mm, PC850, corte automático y cajón en el pin 2.
/// </summary>
[Audited("organization")]
public sealed class TerminalDevice : AggregateRoot<Guid>, ICompanyOwned, IHasAuditLabel
{
    public const string ReceiptPrinterKind = "RECEIPT_PRINTER";

    private TerminalDevice(Guid id, Guid companyId, Guid posTerminalId)
        : base(id)
    {
        CompanyId = companyId;
        PosTerminalId = posTerminalId;
    }

    public Guid CompanyId { get; private set; }

    public Guid PosTerminalId { get; private set; }

    public string Kind { get; private set; } = ReceiptPrinterKind;

    public PrinterConnection Connection { get; private set; }

    /// <summary>IP:puerto, nombre de la impresora de Windows, COMx o carpeta, según la conexión.</summary>
    public string? Address { get; private set; }

    public int PaperWidthMm { get; private set; } = 80;

    public PrinterCodePageSetting CodePage { get; private set; }

    public bool AutoCut { get; private set; } = true;

    public bool DrawerConnected { get; private set; } = true;

    public DrawerPinSetting DrawerPin { get; private set; }

    public string AuditLabel => $"Impresora de la caja {PosTerminalId}";

    public static TerminalDevice Defaults(Guid id, Guid companyId, Guid posTerminalId) => new(id, companyId, posTerminalId);

    public Result Configure(
        PrinterConnection connection, string? address, int paperWidthMm, PrinterCodePageSetting codePage, bool autoCut, bool drawerConnected, DrawerPinSetting drawerPin)
    {
        var trimmed = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        if (!Enum.IsDefined(connection) || paperWidthMm is not (58 or 80) || trimmed is { Length: > 200 }
            || (connection is PrinterConnection.Network or PrinterConnection.WindowsSpooler or PrinterConnection.Serial && trimmed is null))
        {
            return Error.Validation(
                "ORGANIZATION.INVALID_PRINTER",
                "Impresora inválida: papel de 58 u 80 mm y dirección obligatoria para red (IP:puerto), spooler (nombre) o puerto serie (COMx).");
        }

        Connection = connection;
        Address = trimmed;
        PaperWidthMm = paperWidthMm;
        CodePage = codePage;
        AutoCut = autoCut;
        DrawerConnected = drawerConnected;
        DrawerPin = drawerPin;
        return Result.Success();
    }
}
