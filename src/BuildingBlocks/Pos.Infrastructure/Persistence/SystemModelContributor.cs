using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Auditing;

namespace Pos.Infrastructure.Persistence;

/// <summary>Mapeo de las tablas técnicas (system y audit). Los nombres de columna siguen la convención snake_case.</summary>
internal sealed class SystemModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InstallationRecord>(b =>
        {
            b.ToTable("installation", "system");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<DocumentSeriesRecord>(b =>
        {
            b.ToTable("document_series", "system");
            b.HasKey(x => x.Id);
            b.HasControlColumns();
        });

        modelBuilder.Entity<SettingRecord>(b =>
        {
            b.ToTable("settings", "system");
            b.HasKey(x => x.Id);
            b.Property(x => x.Value).HasColumnType("jsonb");
            b.HasControlColumns();
        });

        modelBuilder.Entity<OutboxMessageRecord>(b =>
        {
            b.ToTable("outbox_messages", "system");
            b.HasKey(x => x.Id);
            b.Property(x => x.NodeSeq).ValueGeneratedOnAdd();
            b.Property(x => x.Payload).HasColumnType("jsonb");
        });

        modelBuilder.Entity<InboxMessageRecord>(b =>
        {
            b.ToTable("inbox_messages", "system");
            b.HasKey(x => x.MessageId);
        });

        modelBuilder.Entity<AuditLogRecord>(b =>
        {
            b.ToTable("audit_log", "audit");
            b.HasKey(x => new { x.OccurredAt, x.Id });
            b.Property(x => x.Seq).ValueGeneratedNever();
            b.Property(x => x.OldValues).HasColumnType("jsonb");
            b.Property(x => x.NewValues).HasColumnType("jsonb");
            b.Property(x => x.IpAddress).HasColumnType("inet");
            b.Property(x => x.RowHash).HasColumnType("char(64)");
        });
    }
}
