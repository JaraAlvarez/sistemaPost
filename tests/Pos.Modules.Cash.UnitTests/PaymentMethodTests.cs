using Pos.Modules.Cash.Application;
using Pos.Modules.Cash.Domain;

namespace Pos.Modules.Cash.UnitTests;

public class PaymentMethodTests
{
    private static readonly Guid Company = Guid.NewGuid();

    [Fact]
    public void Solo_el_efectivo_afecta_el_cajon_y_no_pide_referencia()
    {
        var cash = PaymentMethod.Create(Guid.NewGuid(), Company, "efectivo", "Efectivo", PaymentMethodKind.Cash, "10", requiresReference: true, 10, isSystem: true).Value;
        cash.Code.ShouldBe(PaymentMethod.CashCode);
        cash.AffectsCashDrawer.ShouldBeTrue();
        cash.RequiresReference.ShouldBeFalse();
        cash.AuditLabel.ShouldBe("Medio de pago EFECTIVO · Efectivo");

        var nequi = PaymentMethod.Create(Guid.NewGuid(), Company, "NEQUI", " Nequi ", PaymentMethodKind.Wallet, " 47 ", true, 50).Value;
        nequi.AffectsCashDrawer.ShouldBeFalse();
        nequi.RequiresReference.ShouldBeTrue();
        nequi.Name.ShouldBe("Nequi");
        nequi.DianCode.ShouldBe("47");
    }

    [Fact]
    public void El_efectivo_del_sistema_no_se_inactiva_y_validaciones()
    {
        var cash = PaymentMethod.Create(Guid.NewGuid(), Company, "EFECTIVO", "Efectivo", PaymentMethodKind.Cash, "10", false, 10, isSystem: true).Value;
        cash.Update("Efectivo", "10", false, 10, isActive: false).Error.ShouldBe(CashErrors.CashMethodRequired);

        var bono = PaymentMethod.Create(Guid.NewGuid(), Company, "BONO", "Bono", PaymentMethodKind.Voucher, null, true, 70).Value;
        bono.Update("Bono regalo", "71", true, 80, isActive: false).IsSuccess.ShouldBeTrue();
        bono.Status.ShouldBe(MasterStatus.Inactive);
        bono.SortOrder.ShouldBe(80);

        PaymentMethod.Create(Guid.NewGuid(), Company, "X", "Uno", PaymentMethodKind.Other, null, false, 0).Error.ShouldBe(CashErrors.InvalidPaymentMethod);
        PaymentMethod.Create(Guid.NewGuid(), Company, "CON ESPACIO", "Uno", PaymentMethodKind.Other, null, false, 0).Error.ShouldBe(CashErrors.InvalidPaymentMethod);
        PaymentMethod.Create(Guid.NewGuid(), Company, "OTRO", " ", PaymentMethodKind.Other, null, false, 0).Error.ShouldBe(CashErrors.InvalidPaymentMethod);
        PaymentMethod.Create(Guid.NewGuid(), Company, "OTRO", "Otro", PaymentMethodKind.Other, "123456", false, 0).Error.ShouldBe(CashErrors.InvalidPaymentMethod);
        PaymentMethod.Create(Guid.NewGuid(), Company, "OTRO", "Otro", PaymentMethodKind.Other, null, false, 1000).Error.ShouldBe(CashErrors.InvalidPaymentMethod);
    }

    [Fact]
    public void Nombres_de_los_tipos_en_la_BD()
    {
        PaymentMethodMapping.Db(PaymentMethodKind.DebitCard).ShouldBe("DEBIT_CARD");
        PaymentMethodMapping.Db(PaymentMethodKind.CreditCard).ShouldBe("CREDIT_CARD");
        PaymentMethodMapping.Db(MasterStatus.Active).ShouldBe("ACTIVE");
        new CashPermissionCatalog().GetPermissions().Count().ShouldBe(7);
    }
}
