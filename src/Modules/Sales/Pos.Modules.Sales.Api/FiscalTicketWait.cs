using Microsoft.Extensions.Logging;
using Pos.Modules.Sales.Application;
using Pos.Modules.Sales.Contracts;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Sales.Api;

/// <summary>
/// Espera del tiquete (D11B-03) DESPUÉS de que el despachador confirmó la transacción de la venta, la anulación o el reintegro: la cola
/// de facturación solo ve lo confirmado. La venta nunca falla por la facturación: si la espera o el nuevo armado fallan, se devuelve el
/// tiquete que ya se tenía (con la leyenda "en proceso").
/// </summary>
internal sealed partial class FiscalTicketWait(SaleReceipts receipts, ILogger<FiscalTicketWait> logger)
{
    public async Task<Result<SaleReceiptDto>> SaleAsync(Result<SaleReceiptDto> result, CancellationToken cancellationToken)
    {
        if (result.IsFailure)
        {
            return result;
        }

        try
        {
            return await receipts.AwaitFiscalDataAsync(result.Value, cancellationToken);
        }
#pragma warning disable CA1031 // La venta ya quedó confirmada: la facturación no puede convertirla en un error.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogWaitFailed(logger, ex, result.Value.Sale.Number);
            return result;
        }
    }

    public async Task<Result<RefundReceiptDto>> RefundAsync(Result<RefundReceiptDto> result, CancellationToken cancellationToken)
    {
        if (result.IsFailure)
        {
            return result;
        }

        try
        {
            return await receipts.AwaitFiscalDataAsync(result.Value, cancellationToken);
        }
#pragma warning disable CA1031 // El reintegro ya quedó confirmado.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogWaitFailed(logger, ex, result.Value.Refund.Number);
            return result;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudieron obtener los datos fiscales para el tiquete {Number}; se imprime \"en proceso\"")]
    private static partial void LogWaitFailed(ILogger logger, Exception exception, string? number);
}
