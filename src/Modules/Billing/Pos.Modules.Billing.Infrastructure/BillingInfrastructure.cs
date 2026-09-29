using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Outbox;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Billing.Application;
using Pos.Modules.Billing.Domain;

namespace Pos.Modules.Billing.Infrastructure;

internal sealed class BillingModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FiscalDocument>(b =>
        {
            b.ToTable("fiscal_documents", "billing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Source).HasUpperSnakeConversion();
            b.Property(x => x.DocumentType).HasUpperSnakeConversion();
            b.Property(x => x.Status).HasUpperSnakeConversion();
            b.Ignore(x => x.IsElectronic);
            b.HasMany(x => x.Events).WithOne().HasForeignKey("FiscalDocumentId").IsRequired().OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Events).HasField("_events");
            b.HasControlColumns();
            b.HasXminConcurrency();
        });

        modelBuilder.Entity<FiscalDocumentEvent>(b =>
        {
            b.ToTable("fiscal_document_events", "billing");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}

internal sealed class FiscalDocumentStore(PosDbContext context) : IFiscalDocumentStore
{
    public void Add(FiscalDocument document) => context.Add(document);

    public Task<FiscalDocument?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.Set<FiscalDocument>().Include(d => d.Events).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<FiscalDocument?> GetBySourceAsync(Guid sourceId, string source, CancellationToken cancellationToken)
    {
        var kind = BillingMapping.Source(source);

        // Primero lo que el caso de uso ya agregó y no se ha guardado (la emisión y la anulación ocurren en la misma transacción).
        return context.ChangeTracker.Entries<FiscalDocument>().Select(e => e.Entity).FirstOrDefault(d => d.SourceId == sourceId && d.Source == kind)
            ?? await context.Set<FiscalDocument>().Include(d => d.Events).SingleOrDefaultAsync(d => d.SourceId == sourceId && d.Source == kind, cancellationToken);
    }

    public async Task<IReadOnlyList<FiscalDocument>> ListAsync(DocumentFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return await context.Set<FiscalDocument>().Include(d => d.Events).AsSplitQuery()
            .Where(d => d.BranchId == filter.BranchId)
            .Where(d => filter.From == null || d.BusinessDate >= filter.From)
            .Where(d => filter.To == null || d.BusinessDate <= filter.To)
            .Where(d => filter.Status == null || d.Status == filter.Status)
            .OrderByDescending(d => d.IssuedAt)
            .Take(filter.Limit)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Trabajador del outbox: envía los documentos pendientes al proveedor (D7-12).</summary>
internal sealed class FiscalDocumentPendingHandler(FiscalSubmissionService submissions) : IOutboxMessageHandler
{
    public string MessageType => BillingService.PendingMessage;

    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
        submissions.SubmitAsync(FiscalSubmissionService.DocumentIdOf(payload), cancellationToken);
}

public static class BillingInfrastructureRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, BillingModelContributor>();
        services.AddScoped<IFiscalDocumentStore, FiscalDocumentStore>();
        services.AddScoped<IOutboxMessageHandler, FiscalDocumentPendingHandler>();
    }
}
