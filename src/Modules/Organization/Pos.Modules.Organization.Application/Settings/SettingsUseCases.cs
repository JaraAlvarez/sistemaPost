using System.Text.Json;
using FluentValidation;
using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Messaging;
using Pos.Application.Abstractions.Security;
using Pos.Application.Abstractions.Settings;
using Pos.Modules.Organization.Contracts;
using Pos.Modules.Organization.Domain;
using Pos.SharedKernel.Finance;
using Pos.SharedKernel.Results;

namespace Pos.Modules.Organization.Application.Settings;

/// <summary>
/// Configuraciones generales de la Fase 2 (docs/fases/fase-02-propuesta.md §13). Cada módulo agrega las suyas en su
/// fase. REGLA: el valor por defecto de una clave publicada no se cambia (revisión §5.3).
/// </summary>
public static class GeneralSettings
{
    private static readonly int[] AllowedCashIncrements = [1, 10, 50, 100, 200, 500, 1000];

    /// <summary>Decimales de los importes (RoundingPolicy). Solo por empresa: todas las sucursales facturan igual.</summary>
    public static readonly SettingDefinition<int> MoneyDecimals = new(
        "finance.money_decimals",
        RoundingPolicy.Colombia.MoneyDecimals,
        SettingScope.Company,
        "Decimales de los importes (totales, impuestos).",
        v => v is < 0 or > 4 ? "Debe estar entre 0 y 4." : null);

    /// <summary>Múltiplo de redondeo del efectivo. Empresa o sucursal (p. ej. una sucursal rural sin monedas de $50).</summary>
    public static readonly SettingDefinition<int> CashIncrement = new(
        "finance.cash_increment",
        (int)RoundingPolicy.Colombia.CashIncrement,
        SettingScope.Company | SettingScope.Branch,
        "Múltiplo al que se redondea el cobro en efectivo.",
        v => AllowedCashIncrements.Contains(v) ? null : $"Valores permitidos: {string.Join(", ", AllowedCashIncrements)}.");

    /// <summary>Mensaje al pie del tiquete. Empresa, sucursal o caja.</summary>
    public static readonly SettingDefinition<string> ReceiptFooter = new(
        "documents.receipt_footer_message",
        "¡Gracias por su compra!",
        SettingScope.Company | SettingScope.Branch | SettingScope.Terminal,
        "Mensaje impreso al pie del tiquete.",
        v => v.Length > 200 ? "Máximo 200 caracteres." : null);

    public static IEnumerable<SettingDefinition> All => [MoneyDecimals, CashIncrement, ReceiptFooter];
}

public sealed class GeneralSettingsProvider : ISettingDefinitionProvider
{
    public IEnumerable<SettingDefinition> GetDefinitions() => GeneralSettings.All;
}

/// <summary>Permisos declarados por el módulo Organization (incluida la configuración general).</summary>
public sealed class OrganizationPermissionCatalog : IPermissionCatalogProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() => OrganizationPermissions.All.Concat(SettingsPermissions.All);
}

/// <summary>Valores efectivos para la empresa, una sucursal o una caja (con su origen).</summary>
public sealed record GetSettingsQuery(Guid? BranchId = null, Guid? PosTerminalId = null) : IQuery<IReadOnlyList<EffectiveSetting>>;

internal sealed class GetSettingsHandler(IInstallationContext installation, IOrganizationStore store, ISettingsWriter settings)
    : IQueryHandler<GetSettingsQuery, IReadOnlyList<EffectiveSetting>>
{
    public async Task<Result<IReadOnlyList<EffectiveSetting>>> Handle(GetSettingsQuery request, CancellationToken cancellationToken)
    {
        var context = await SettingsScopeResolver.ResolveContextAsync(installation, store, request.BranchId, request.PosTerminalId, cancellationToken);
        return context.IsSuccess
            ? Result.Success(await settings.GetEffectiveAsync(context.Value, cancellationToken))
            : context.Error;
    }
}

/// <summary>Guarda una excepción de configuración en el nivel indicado (sin <c>ScopeId</c> en el nivel empresa).</summary>
public sealed record SetSettingCommand(string Key, SettingScope Scope, Guid? ScopeId, JsonElement Value) : ICommand;

internal sealed class SetSettingValidator : AbstractValidator<SetSettingCommand>
{
    public SetSettingValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ScopeId).NotNull().When(x => x.Scope != SettingScope.Company)
            .WithMessage("Indique la sucursal o caja (scopeId).");
    }
}

internal sealed class SetSettingHandler(IInstallationContext installation, ISettingsWriter settings) : ICommandHandler<SetSettingCommand>
{
    public async Task<Result> Handle(SetSettingCommand request, CancellationToken cancellationToken)
    {
        var target = SettingsScopeResolver.Target(installation, request.Scope, request.ScopeId);
        return target.IsSuccess ? await settings.SetAsync(target.Value, request.Key, request.Value, cancellationToken) : target.Error;
    }
}

/// <summary>Elimina la excepción: ese nivel vuelve a heredar del superior.</summary>
public sealed record RemoveSettingCommand(string Key, SettingScope Scope, Guid? ScopeId) : ICommand;

internal sealed class RemoveSettingHandler(IInstallationContext installation, ISettingsWriter settings) : ICommandHandler<RemoveSettingCommand>
{
    public async Task<Result> Handle(RemoveSettingCommand request, CancellationToken cancellationToken)
    {
        var target = SettingsScopeResolver.Target(installation, request.Scope, request.ScopeId);
        return target.IsSuccess ? await settings.RemoveAsync(target.Value, request.Key, cancellationToken) : target.Error;
    }
}

internal static class SettingsScopeResolver
{
    public static Result<SettingTarget> Target(IInstallationContext installation, SettingScope scope, Guid? scopeId)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return OrganizationErrors.SetupRequired;
        }

        return scope == SettingScope.Company
            ? new SettingTarget(companyId, scope, companyId)
            : scopeId is { } id
                ? new SettingTarget(companyId, scope, id)
                : Error.Validation("SETTINGS.SCOPE_ID_REQUIRED", "Indique la sucursal o caja (scopeId).");
    }

    /// <summary>Con caja se deduce su sucursal: la cadena de herencia es siempre caja → su sucursal → empresa.</summary>
    public static async Task<Result<SettingContext>> ResolveContextAsync(
        IInstallationContext installation, IOrganizationStore store, Guid? branchId, Guid? terminalId, CancellationToken cancellationToken)
    {
        if (installation.CompanyId is not { } companyId)
        {
            return OrganizationErrors.SetupRequired;
        }

        if (terminalId is { } t)
        {
            var terminal = await store.GetTerminalAsync(t, cancellationToken);
            return terminal is null ? OrganizationErrors.TerminalNotFound : new SettingContext(companyId, terminal.BranchId, terminal.Id);
        }

        if (branchId is { } b)
        {
            return await store.GetBranchAsync(b, cancellationToken) is null
                ? OrganizationErrors.BranchNotFound
                : new SettingContext(companyId, b);
        }

        return new SettingContext(companyId);
    }
}
