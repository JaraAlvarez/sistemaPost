using Pos.Application.Abstractions.Installation;
using Pos.Application.Abstractions.Security;
using Pos.Modules.Catalog.Application;
using Pos.Modules.Organization.Contracts;

namespace Pos.Modules.Catalog.UnitTests;

/// <summary>Sucursal de las etiquetas: la pedida (existe y el usuario puede usarla), la del usuario o la del equipo; si no hay, se pide.</summary>
public class LabelBranchTests
{
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid North = Guid.NewGuid();

    [Fact]
    public async Task Sin_sucursal_pedida_usa_la_del_usuario_y_si_no_la_del_equipo()
    {
        (await Branch(userBranch: North, nodeBranch: Home).ResolveAsync(null, CancellationToken.None)).Value.ShouldBe(North);
        (await Branch(userBranch: null, nodeBranch: Home).ResolveAsync(null, CancellationToken.None)).Value.ShouldBe(Home);
    }

    [Fact]
    public async Task Sin_ninguna_sucursal_se_pide_elegirla() =>
        (await Branch(userBranch: null, nodeBranch: null).ResolveAsync(null, CancellationToken.None)).Error.ShouldBe(LabelErrors.BranchRequired);

    [Fact]
    public async Task La_sucursal_pedida_debe_existir_y_estar_permitida()
    {
        (await Branch(Home, Home, active: [Home, North], allowed: [North]).ResolveAsync(North, CancellationToken.None)).Value.ShouldBe(North);
        (await Branch(Home, Home, active: [Home]).ResolveAsync(North, CancellationToken.None)).Error.ShouldBe(LabelErrors.BranchNotFound);
        (await Branch(Home, Home, active: [Home, North], allowed: [Home]).ResolveAsync(North, CancellationToken.None)).Error
            .ShouldBe(LabelErrors.BranchForbidden);
    }

    private static LabelBranch Branch(Guid? userBranch, Guid? nodeBranch, Guid[]? active = null, Guid[]? allowed = null) =>
        new(new FakeUser(userBranch), new FakeInstallation(nodeBranch), new FakeDirectory(active ?? []), new FakePermissions(allowed ?? []));

    private sealed class FakeUser(Guid? branchId) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => Guid.Empty;

        public string? DisplayName => "Prueba";

        public Guid? CompanyId => Guid.Empty;

        public Guid? BranchId => branchId;

        public Guid? PosTerminalId => null;

        public Guid? SessionId => null;

        public bool IsTerminalSession => false;

        public bool MustChangePassword => false;
    }

    private sealed class FakeInstallation(Guid? branchId) : IInstallationContext
    {
        public Guid NodeId => Guid.Empty;

        public NodeRole NodeRole => NodeRole.AllInOne;

        public bool IsSetupCompleted => true;

        public Guid? CompanyId => Guid.Empty;

        public Guid? BranchId => branchId;

        public Guid? SystemUserId => null;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDirectory(Guid[] active) : IWarehouseDirectory
    {
        public Task<WarehouseInfo?> GetAsync(Guid warehouseId, CancellationToken cancellationToken = default) => Task.FromResult<WarehouseInfo?>(null);

        public Task<IReadOnlyList<WarehouseInfo>> ListByBranchAsync(Guid branchId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WarehouseInfo>>([]);

        public Task<Guid?> FindBranchIdByCodeAsync(string branchCode, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);

        public Task<bool> IsActiveBranchAsync(Guid branchId, CancellationToken cancellationToken = default) => Task.FromResult(active.Contains(branchId));

        public Task<short> GetLocalNodeNumberAsync(CancellationToken cancellationToken = default) => Task.FromResult((short)1);
    }

    private sealed class FakePermissions(Guid[] allowed) : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(string permissionCode, Guid? branchId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(branchId is { } b && allowed.Contains(b));
    }
}
