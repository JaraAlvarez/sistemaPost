using Pos.Cloud.Licensing.Domain;
using Pos.Licensing.Contracts;
using Pos.SharedKernel.Identifiers;

namespace Pos.Cloud.Licensing.UnitTests;

internal sealed class TestIds : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}

internal static class TestSupport
{
    public const string ValidNit = "900123456";
    public const string ValidDv = "8";

    public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly Guid Actor = Guid.CreateVersion7();

    public static readonly DeviceFingerprint PcA = DeviceFingerprint.FromHardware("placa-a", "disco-a", "maquina-a");

    /// <summary>El mismo equipo con otro disco (2 de 3).</summary>
    public static readonly DeviceFingerprint PcANewDisk = DeviceFingerprint.FromHardware("placa-a", "disco-nuevo", "maquina-a");

    public static readonly DeviceFingerprint PcB = DeviceFingerprint.FromHardware("placa-b", "disco-b", "maquina-b");

    public static ChangeContext At(DateTimeOffset now) => new(Actor, now, new TestIds());

    public static ChangeContext Now => At(Start);

    public static Subscription Trial(int days = 15, int grace = 7, LicenseEdition edition = LicenseEdition.MultiTerminal) =>
        Subscription.StartTrial(Guid.CreateVersion7(), Guid.CreateVersion7(), edition, BillingPeriod.Monthly, days, grace, Now).Value;

    public static Subscription Paid(BillingPeriod period = BillingPeriod.Monthly, int periods = 1, int grace = 7) =>
        Subscription.StartPaid(Guid.CreateVersion7(), Guid.CreateVersion7(), LicenseEdition.MultiTerminal, period, "PAGO-001", periods, grace, Now).Value;

    public static License Issue(Subscription subscription, int? max = null) =>
        License.Issue(Guid.CreateVersion7(), subscription, LicenseKey.Generate(), max, Start).Value;

    public static ActivationData Data(DeviceFingerprint fingerprint, string version = "1.0.0", string? branch = "Centro") =>
        new(fingerprint, DeviceRole.StoreServer, version, branch, "SERVIDOR", "Windows 11");
}
