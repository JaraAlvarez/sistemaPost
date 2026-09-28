using System.Globalization;

namespace Pos.SharedKernel.Finance;

/// <summary>
/// Importe monetario exacto (<see cref="decimal"/>) con moneda. Nunca se usa double/float para dinero.
/// Las operaciones conservan la precisión; el redondeo es siempre explícito con <see cref="RoundingPolicy"/>.
/// </summary>
public sealed record Money : IComparable<Money>
{
    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public static Money Of(decimal amount, Currency currency) => new(amount, Guard.NotNull(currency));

    public static Money Zero(Currency currency) => new(0m, Guard.NotNull(currency));

    public static Money operator +(Money left, Money right) => Add(left, right);

    public static Money operator -(Money left, Money right) => Subtract(left, right);

    public static Money operator -(Money value) => Negate(value);

    public static Money operator *(Money left, decimal right) => Multiply(left, right);

    public static bool operator <(Money left, Money right) => Compare(left, right) < 0;

    public static bool operator >(Money left, Money right) => Compare(left, right) > 0;

    public static bool operator <=(Money left, Money right) => Compare(left, right) <= 0;

    public static bool operator >=(Money left, Money right) => Compare(left, right) >= 0;

    public static Money Add(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money Subtract(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money Negate(Money value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Money(-value.Amount, value.Currency);
    }

    public static Money Multiply(Money left, decimal right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return new Money(left.Amount * right, left.Currency);
    }

    public static Money Sum(IEnumerable<Money> values, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Aggregate(Zero(currency), Add);
    }

    public Money Round(RoundingPolicy policy) => new(Guard.NotNull(policy).RoundMoney(Amount), Currency);

    public Money RoundToCash(RoundingPolicy policy) => new(Guard.NotNull(policy).RoundToCash(Amount), Currency);

    /// <summary>
    /// Reparte el importe (redondeado) proporcionalmente a los pesos, sin perder ni crear centavos
    /// (método del mayor residuo). Se usa, por ejemplo, para prorratear un descuento global entre líneas.
    /// </summary>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<decimal> weights, RoundingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(policy);
        if (weights.Count == 0)
        {
            throw new ArgumentException("Se requiere al menos un peso.", nameof(weights));
        }

        if (weights.Any(w => w < 0m))
        {
            throw new ArgumentException("Los pesos no pueden ser negativos.", nameof(weights));
        }

        var totalWeight = weights.Sum();
        if (totalWeight == 0m)
        {
            throw new ArgumentException("La suma de los pesos debe ser mayor que cero.", nameof(weights));
        }

        var total = policy.RoundMoney(Math.Abs(Amount));
        var unit = 1m / Pow10(policy.MoneyDecimals);

        var shares = new decimal[weights.Count];
        var remainders = new decimal[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            var exact = total * weights[i] / totalWeight;
            shares[i] = decimal.Round(exact, policy.MoneyDecimals, MidpointRounding.ToZero);
            remainders[i] = exact - shares[i];
        }

        var pendingUnits = (int)((total - shares.Sum()) / unit);
        foreach (var index in Enumerable.Range(0, weights.Count)
                     .OrderByDescending(i => remainders[i])
                     .ThenBy(i => i)
                     .Take(pendingUnits))
        {
            shares[index] += unit;
        }

        var sign = Amount < 0m ? -1m : 1m;
        return shares.Select(s => new Money(s * sign, Currency)).ToArray();
    }

    public int CompareTo(Money? other)
    {
        if (other is null)
        {
            return 1;
        }

        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Currency.Code} {Amount}");

    private static int Compare(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException(
                $"No se pueden operar importes de monedas distintas ({left.Currency.Code} y {right.Currency.Code}).");
        }
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
