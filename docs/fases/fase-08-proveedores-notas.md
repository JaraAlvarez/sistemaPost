# Fase 8 · Bloque 8.4 — Mejoras de proveedores (notas de implementación)

Notas para integrar en el informe de la Fase 8. Alcance: D8-14, §4.5, §5.5, RN-PUR-09, RN-PUR-10, permiso
`purchasing.supplier.bank_manage` (§7) y los endpoints de compras de la §9 de `fase-08-propuesta.md`.

## 1. Qué se construyó

### Base de datos

| Script | Contenido |
|---|---|
| `V2026.10.025__purchasing__supplier_improvements.sql` | `ref.banks`; `suppliers` + `minimum_order_amount`, `order_cutoff_note`; `supplier_schedules`; `supplier_bank_accounts`; `supplier_withholding_defaults`; índice `ix_accounts_payable__due` (vencimientos por empresa) |
| `repeatable/R__ref__banks.sql` (nuevo) | 28 bancos y billeteras de Colombia con su código ACH (Bancolombia 1007, Davivienda 1051, Nequi 1507, Daviplata 1551…) |
| `repeatable/R__identity__permissions_catalog.sql` | Fila `purchasing.supplier.bank_manage` (sensible) y su código en la lista `NOT IN` |

Reglas en la BD:

- **Cuentas bancarias**: único (proveedor, banco, número); número solo dígitos (5–20); `VERIFIED` ⇔ `verified_at`;
  `verified_by <> changed_by` (`ck_supplier_bank_accounts__verifier`: RN-PUR-09 también en la BD); una inactiva no es principal;
  **una sola principal por proveedor** con una restricción de exclusión **diferida** (`ex_supplier_bank_accounts__primary`):
  cambiar la principal es un solo guardado sin importar el orden de los `UPDATE`.
- **Agenda**: día ISO 1–7, tipo `VISIT`/`ORDER`/`DELIVERY`, sucursal opcional (FK compuesta con la empresa); único
  (proveedor, tipo, día, sucursal) con `NULLS NOT DISTINCT` entre las filas vivas (borrado lógico).
- **Retenciones sugeridas**: una por tipo (`RETEFUENTE`, `RETEIVA`, `RETEICA`), tarifa > 0 y ≤ 100.
- `ref.banks` queda de solo lectura para `pos_app` (lo cubre `A__system__privileges.sql`: `REVOKE ALL` + `GRANT SELECT` en `ref`);
  lo prueba `SupplierImprovementsSchemaTests`.
- Toda FK tiene índice; las tablas nuevas son maestros sincronizables de la empresa (`company_id`, `row_version`, columnas de control).

### Dominio (`Pos.Modules.Purchasing.Domain/SupplierImprovements.cs`, `Suppliers.cs`)

- `Supplier.SetOrderingTerms` (pedido mínimo ≥ 0 con 2 decimales, nota de corte ≤ 200).
- `SupplierSchedule` (validación de la agenda completa, coincidencia por tipo/día/sucursal, próxima fecha `NextOn`).
- `SupplierWithholdingDefault` y `WithholdingSuggestion`: bases sugeridas → retefuente y reteICA sobre subtotal − descuentos,
  reteIVA sobre el IVA de la compra; los cargos (fletes) no entran; las que darían 0 no se sugieren.
- `SupplierBankAccount`: nueva, modificada o reactivada ⇒ `PENDING_VERIFICATION` y `ChangedBy` = responsable del cambio;
  `Verify` rechaza al mismo usuario (`PURCHASING.BANK_ACCOUNT_SAME_USER`) y a una cuenta que no está pendiente; inactivar quita
  la marca de principal; número normalizado (sin espacios, guiones ni puntos) y enmascarado (`****1234`).

### Aplicación y API (`SupplierImprovementUseCases.cs`, `PurchasingModule.MapSupplierImprovements`)

| Método y ruta | Permiso | Qué hace |
|---|---|---|
| `GET /purchasing/suppliers/{id}/summary?from&to` | `purchasing.payable.view` | Comprado en el período (compras contabilizadas por fecha de factura; por defecto 90 días), # compras, última compra (fecha y total), devoluciones (contabilizadas o liquidadas), saldo, vencido (valor y # cuentas), productos activos, plazo, pedido mínimo y estado de la cuenta principal |
| `GET /purchasing/products/{productId}/suppliers` | `purchasing.purchase.view` | Quién lo vende, código del proveedor, último costo (**solo con** `inventory.cost.view`), días de entrega, preferido; preferido primero y luego el menor costo |
| `GET /purchasing/products/{productId}/cost-history?supplierId&from&to&limit` | `inventory.cost.view` | Costo neto por unidad base en cada compra contabilizada, con proveedor y factura |
| `GET/PUT /purchasing/suppliers/{id}/schedule` | ver: `purchase.view` · cambiar: `supplier.manage` | Agenda (reemplazo completo, conserva las filas que no cambian) + pedido mínimo + nota de corte; cada entrada trae su próxima fecha |
| `GET /purchasing/suppliers/{id}/bank-accounts` | `purchasing.payable.view` | Cuentas con su estado |
| `POST /purchasing/suppliers/{id}/bank-accounts` | `purchasing.supplier.bank_manage` (admite autorización) | Registra: queda por verificar; la primera cuenta activa es la principal; marcar otra como principal le quita la marca a la anterior |
| `PUT /purchasing/suppliers/{id}/bank-accounts/{accountId}` | ídem | Modifica, cambia la principal, inactiva (`isActive=false`) o reactiva |
| `POST /purchasing/suppliers/{id}/bank-accounts/{accountId}/verify` | ídem | Verifica (otro usuario) |
| `GET/PUT /purchasing/suppliers/{id}/withholdings` | ver: `purchase.view` · cambiar: `supplier.manage` | Retenciones sugeridas (reemplazo completo, una por tipo) |
| `GET /purchasing/payables/due?days=7` | `purchasing.payable.view` | Vencidas y por vencer en los próximos días (0–90) con totales, para el tablero |
| `GET /purchasing/banks` | `purchasing.purchase.view` | Catálogo de bancos |

- **Auditoría crítica** (`SUPPLIER_BANK_ACCOUNT_CREATED`, `_CHANGED`, `_VERIFIED`) con valores antes/después y el número
  **enmascarado** (la bitácora nunca guarda el número completo); registra `authorized_by` cuando hubo autorización de supervisor.
- **Pago con advertencia**: `POST /purchasing/payments` ahora devuelve `warnings` (lista, vacía si no hay). Si el medio no es
  efectivo y la cuenta principal activa del proveedor está `PENDING_VERIFICATION`, incluye `PURCHASING.BANK_ACCOUNT_UNVERIFIED`;
  el pago **no se bloquea**.
- **Pre-llenado de retenciones (RN-PUR-10)**: `POST /purchasing/purchases` **sin** la propiedad `withholdings` (o con `null`)
  calcula las sugeridas del proveedor sobre las líneas; con una lista (aunque sea vacía) respeta lo digitado. La compra queda en
  borrador y editable; lo que se contabiliza es lo que el usuario confirmó al contabilizar (D5-11 sigue vigente). La edición
  (`PUT`) nunca pre-llena.
- Errores nuevos con código estable: `PURCHASING.INVALID_ORDERING_TERMS`, `INVALID_SCHEDULE`, `INVALID_WITHHOLDING_DEFAULT`,
  `INVALID_BANK_ACCOUNT`, `BANK_NOT_FOUND`, `BANK_ACCOUNT_NOT_FOUND`, `BANK_ACCOUNT_DUPLICATED`, `BANK_ACCOUNT_NOT_PENDING`,
  `BANK_ACCOUNT_SAME_USER`, `PRODUCT_NOT_FOUND`, `INVALID_PERIOD` (y la traducción de las restricciones nuevas de la BD).

### Permiso

`purchasing.supplier.bank_manage` (sensible, admite autorización de supervisor): en `PurchasingPermissions` y en el catálogo SQL.
OWNER y ADMIN lo reciben automáticamente (`SystemRoles` les da todo el catálogo vigente); **ningún otro rol** lo tiene
(PURCHASING no). `SystemRoles.cs` no se tocó.

## 2. Decisiones tomadas al implementar

1. **Quién es el "otro usuario" (RN-PUR-09)**: la cuenta guarda `changed_by` = responsable del último cambio sensible (quien
   autorizó, si hubo autorización de supervisor; si no, el usuario). Verificar exige que ni el verificador (autorizador o usuario)
   ni quien hace la petición sean ese responsable. La BD lo refuerza con `ck_supplier_bank_accounts__verifier`.
2. **Qué deja la cuenta por verificar**: cambiar banco, tipo, número, titular o su identificación, y reactivarla. Cambiar solo
   la marca de principal hacia una cuenta ya verificada **no** la invalida (la cuenta en sí ya fue confirmada), pero se audita
   como crítico igual.
3. **La advertencia de pago** solo aplica a medios distintos del efectivo (el efectivo no va a una cuenta) y mira la cuenta
   **principal activa**. Un proveedor sin cuentas no genera advertencia (el pago en efectivo o por consignación sigue siendo común).
4. **Bases de las retenciones sugeridas**: retefuente/reteICA sobre subtotal − descuentos y reteIVA sobre el IVA. La tarifa
   se guarda en % (reteICA típica 0,414 % = 4,14 ‰). El redondeo es el del dinero (2 decimales), igual que en la compra.
5. **Resumen**: el período filtra compras por fecha de factura y devoluciones por fecha del documento (POSTED y SETTLED); saldo y
   vencido son a hoy (independientes del período). Toda la empresa (no por sucursal), igual que la cartera.
6. **Permisos de lectura**: el resumen y las cuentas bancarias exigen `purchasing.payable.view` (muestran cartera y datos
   bancarios, información sensible); la agenda, las retenciones y "quién lo vende" solo `purchasing.purchase.view`.
7. **Agenda como reemplazo completo** (`PUT`): diferencia por (tipo, día, sucursal): conserva las filas iguales (solo actualiza la
   nota), borra lógicamente las que ya no están y crea las nuevas; así la sincronización por campo no ve cambios falsos.

## 3. Desviaciones frente a la propuesta

| Propuesta | Implementado | Motivo |
|---|---|---|
| `ref.banks` creada en la migración 021 y sembrada en `R__ref__seed_colombia.sql` (§4 y §10) | Creada en la **025** y sembrada en el repetible nuevo **`R__ref__banks.sql`** | División del trabajo en paralelo: la 021 y `R__ref__seed_colombia.sql` son del bloque 8.1 (otro responsable); así no hay conflictos al integrar |
| `ref.banks.country_code` con FK a `ref.countries` (convención de `ref`) | Sin FK (CHECK de formato) | Los repetibles corren en orden alfabético: `R__ref__banks` se ejecuta antes que `R__ref__seed_colombia` (que carga los países) en una BD nueva |
| Endpoints de §9 | + `GET /purchasing/banks` | La UI necesita el catálogo para registrar cuentas; se dejó en Compras para no tocar el módulo Reference |
| — | El pedido mínimo se edita con la agenda (`PUT /schedule`) y no en `PUT /suppliers/{id}` | No cambia el contrato de `SupplierDto`/`SupplierInput` de la Fase 5; el sugerido de pedido (9/10) lo leerá de ahí |
| — | La orden de compra no advierte todavía si queda por debajo del pedido mínimo | Fuera del alcance de 8.4; queda para el sugerido de pedido (fases 9/10) |
| ~40 pruebas nuevas (§15) | 35 | Ver §4; la cobertura del dominio de compras quedó en 97,8 % |

## 4. Pruebas

| Proyecto | Nuevas | Qué cubren |
|---|---|---|
| `Pos.Modules.Purchasing.UnitTests` (`SupplierImprovementsTests.cs`) | 27 | Pedido mínimo, agenda (validación, coincidencia, próxima fecha), retenciones sugeridas (bases, redondeo, sin IVA, en la compra), cuenta bancaria (normalización, máscara, verificación por otro usuario, cambio ⇒ por verificar, inactivar/reactivar, principal) |
| `Pos.Database.Tests` (`SupplierImprovementsSchemaTests.cs`) | 5 | Bancos cargados y de solo lectura para `pos_app`; verificador ≠ quien cambió; único por banco+número; principal única diferida; agenda sin repetidos con borrado lógico; retenciones una por tipo; pedido mínimo ≥ 0 |
| `Pos.Server.IntegrationTests` (`Phase8/SupplierImprovementsTests.cs`) | 3 | (1) cuenta nueva por verificar → pago por transferencia con advertencia (efectivo sin ella) → el mismo usuario no la verifica → otro ADMIN la verifica → pago sin advertencia; PURCHASING recibe 403 al registrar y verificar; cambio del número vuelve a pendiente; principal única; 5 auditorías CRÍTICAS sin el número completo. (2) Resumen, "quién lo vende", historial de costos, 403 de la cajera, vencimientos a 7 y 30 días. (3) Agenda y pedido mínimo, retenciones sugeridas que pre-llenan la compra y se confirman al contabilizar |

Resultados (Release): Purchasing unitarias 53/53; Database 69/69; integración `SupplierImprovementsTests` 3/3 y
`PurchasingApiTests` + `ConformityTests` + `EndpointProtectionTests` + `CashApiTests` 22/22. Cobertura de líneas de
`Pos.Modules.Purchasing.Domain` (solo pruebas unitarias): 97,8 %. `EndpointProtectionTests` recorre las rutas nuevas
automáticamente (401 sin sesión y 403 sin el permiso); no hizo falta modificarla.

Archivos de pruebas existentes modificados: `PurchasingDomainTests.cs` (el catálogo de permisos de compras pasa de 10 a 11).

## 5. Pendiente para el informe de la fase

- Mencionar en el doc 06 (permisos) `purchasing.supplier.bank_manage` y en el doc 04 las tablas nuevas y `ref.banks`.
- Verificar los códigos ACH de `R__ref__banks.sql` contra el listado vigente de ACH Colombia antes de la versión de producción.
- La UI de pagos debe mostrar `warnings` de forma destacada.
