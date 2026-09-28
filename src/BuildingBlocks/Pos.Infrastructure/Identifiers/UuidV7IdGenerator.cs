using Pos.SharedKernel.Identifiers;
using Pos.SharedKernel.Time;

namespace Pos.Infrastructure.Identifiers;

/// <summary>UUID v7 con la marca de tiempo del <see cref="IClock"/> del sistema.</summary>
public sealed class UuidV7IdGenerator(IClock clock) : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7(clock.UtcNow);
}
