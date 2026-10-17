namespace Pos.SharedKernel.Measurement;

/// <summary>
/// Evita cobrar el peso del producto anterior (Fase 7-C, hallazgo A2): después de agregar una línea pesada en la báscula de la caja, la
/// siguiente pesada solo vale si la báscula volvió a quedar vacía (por debajo del peso mínimo) o si marca un peso estable distinto
/// del último agregado en más de <see cref="ToleranceKg"/> ⚙️ (±5 g). Si no, la caja muestra "Retire el producto anterior de la báscula"
/// y espera, como con el peso inestable.
/// <para>Lo usa la caja (estado por caja, sobrevive entre ventas: la carne del cliente anterior puede seguir en la báscula) y el
/// servidor aplica la misma regla a dos líneas seguidas de la misma venta (<see cref="IsRepeat"/>).</para>
/// </summary>
public sealed class ScaleRepeatGuard
{
    /// <summary>Diferencia mínima con el último peso agregado para aceptar la pesada sin que la báscula pase por cero (5 g ⚙️).</summary>
    public const decimal ToleranceKg = 0.005m;

    /// <summary>Peso bruto de la última línea agregada con la báscula (nulo: todavía no se ha pesado nada en esta caja).</summary>
    public decimal? LastAddedKg { get; private set; }

    /// <summary>La báscula quedó vacía después de la última pesada agregada.</summary>
    public bool EmptiedSinceLast { get; private set; } = true;

    /// <summary>Misma pesada: los dos pesos difieren en <see cref="ToleranceKg"/> o menos.</summary>
    public static bool IsRepeat(decimal previousKg, decimal currentKg) => Math.Abs(currentKg - previousKg) <= ToleranceKg;

    /// <summary>¿Se puede agregar una línea con este peso bruto estable?</summary>
    public bool CanAdd(decimal grossKg) => LastAddedKg is not { } last || EmptiedSinceLast || !IsRepeat(last, grossKg);

    /// <summary>La báscula marcó vacía (estado ZERO): la siguiente pesada vale aunque pese lo mismo que la anterior.</summary>
    public void ObserveEmpty() => EmptiedSinceLast = true;

    /// <summary>Se agregó una línea con este peso bruto.</summary>
    public void Added(decimal grossKg)
    {
        LastAddedKg = grossKg;
        EmptiedSinceLast = false;
    }
}
