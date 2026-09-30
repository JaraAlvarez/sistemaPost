# Fase 8 · Clientes y proveedores — Informe

> Estado: **IMPLEMENTADA — pendiente de tu validación** · 2026-09-29
> Documentos: [propuesta aprobada](fase-08-propuesta.md) · [notas del bloque 8.4 (proveedores)](fase-08-proveedores-notas.md) ·
> ADR [0040](../adr/0040-modulo-customers-y-rol-con-la-clave-del-tercero.md) · [0041](../adr/0041-precio-por-cliente-y-promociones.md) ·
> [0042](../adr/0042-autorizacion-de-datos-y-derechos-del-titular.md) · [0043](../adr/0043-credito-y-puntos-reservados.md) ·
> colecciones [`http/fase-08.http`](../../http/fase-08.http) y [`http/fase-08-proveedores.http`](../../http/fase-08-proveedores.http)

**Forma de validación de esta fase (pedido del propietario):** se construyó y se verificó la **coherencia** del código
(`dotnet build Pos.slnx -c Release` con 0 advertencias, migraciones aplicadas juntas, modelo EF contra la BD, pruebas nuevas
puntuales). Las pruebas funcionales las hace el propietario; al final hay una lista de qué conviene probar.

## 1. Criterios de aceptación (§14)

| Criterio | Estado | Cómo se comprobó |
|---|---|---|
| Venta con cliente identificado: búsqueda por cédula, nombre y celular; alta rápida con autorización y aviso en el tiquete | ✅ | Escenario de punta a punta `Phase8/CustomersApiTests` |
| Listas fijas y derivadas con la prioridad de D8-09; promociones según la lista; re-precio al cambiar el cliente | ✅ | Mismo escenario (Empleados −5 % con promoción; Mayorista sin promociones) |
| Snapshot fiscal completo y "pide factura" con validación; lista y origen del precio en cada línea | ✅ | Mismo escenario (`customer_fiscal` jsonb, `price_source`) |
| Historial y resumen correctos con anulaciones, cambios y garantías | ✅ | Mismo escenario (2 ventas + 1 cambio) |
| Autorizaciones de solo inserción; solicitudes con plazos; exportación y supresión por anonimización | ✅ | Escenario de privacidad + pruebas del dominio (calendario de festivos) |
| Proveedores: resumen, costos, agenda, cuentas bancarias con verificación, retenciones sugeridas, vencimientos | ✅ | `Phase8/SupplierImprovementsTests` (ver las notas del bloque 8.4) |
| Crédito y puntos reservados: tipos rechazados al crear, contratos nulos, ADR | ✅ | Escenario de listas + ADR-0043 |
| La cajera no modifica terceros ni asigna listas; permisos en todos los endpoints | ✅ | `403` en el escenario; `EndpointProtectionTests` recorre las rutas nuevas |
| Metas de rendimiento y `build.ps1` completo | ⏳ | **No se corrió** (validación por coherencia; ver §5) |
| Docs y ADRs | ✅ | ADR-0040 a 0043, este informe, colecciones `.http` |

## 2. Qué se construyó

```
src/Modules/Customers (nuevo)   Grupos, rol cliente (PK = tercero), búsqueda en caja, alta rápida, autorizaciones de solo inserción,
                                política versionada (plantilla pendiente de revisión), solicitudes con días hábiles (festivos de
                                Colombia calculados), exportación, supresión, historial y resumen; crédito y puntos nulos
src/Modules/Parties             IPartyRegistry: alta sin duplicados (bloqueo por identificación), completar vacíos, corrección,
                                anonimización y búsqueda por identificación/celular/nombre; rol del contacto
src/Modules/Catalog             Listas derivadas (% y redondeo), "admite promociones", precio por lista al escanear
src/Modules/Sales               Cliente con lista y re-precio, snapshot fiscal, "pide factura", origen del precio, historial del cliente
src/Modules/Purchasing          Bloque 8.4: resumen, costos, agenda, cuentas bancarias, retenciones sugeridas, vencimientos
src/Modules/Cash                Tipos CUSTOMER_CREDIT y LOYALTY_POINTS reservados
src/Modules/Identity            Roles: la cajera y el supervisor ya no editan terceros; permisos de clientes
Migraciones                     021 customers · 022 parties · 023 catalog · 024 sales · 025 purchasing · 026 cash · R__ref__banks
```

Permisos: 76 en total (7 de clientes y `purchasing.supplier.bank_manage`).

## 3. Pruebas nuevas

| Proyecto | Pruebas nuevas | Resultado |
|---|---|---|
| `Pos.Modules.Customers.UnitTests` (nuevo) | 13 | ✅ · cobertura del dominio de clientes 100 % |
| `Pos.Modules.Purchasing.UnitTests` | 27 | ✅ · cobertura del dominio de compras 97,8 % |
| `Pos.Database.Tests` | 5 | ✅ (69/69 con todas las migraciones) |
| `Pos.Server.IntegrationTests` Fase 8 | 6 (3 de clientes, 3 de proveedores) | ✅ |
| Ajustes a pruebas existentes | 2 | La cajera ya no crea terceros (Fase 5) y un vencimiento que dependía de la hora (Fase 7) |

## 4. Desviaciones respecto de la propuesta

| # | Propuesta | Implementado | Motivo |
|---|---|---|---|
| 1 | Festivos en la tabla `ref.holidays` | Calculados (Pascua + Ley Emiliani) en `ColombianCalendar` | Sin datos que mantener cada año |
| 2 | Alta rápida con cédula existente → `409` con el tercero | Responde `200` con el tercero existente (`created = false`) sin modificarlo | La caja lo usa sin reintentar; el `409` no puede llevar el tercero |
| 3 | Rol automático para los terceros que ya aparecen en ventas, al arrancar | El rol se crea en su siguiente compra (o al buscarlo y crearlo) | Evita escribir en masa sobre otro esquema al arrancar |
| 4 | Exportación en JSON y PDF | Solo JSON | El PDF queda para la interfaz (15) |
| 5 | `GET /catalog/price-check?customerId=` | No se agregó; el precio del cliente se ve al asignarlo en la venta | Sin interfaz aún |
| 6 | Bloque 8.4 | Ver [notas](fase-08-proveedores-notas.md): `ref.banks` en la 025 y `R__ref__banks.sql`, `GET /purchasing/banks`, pedido mínimo en la agenda, sin aviso en órdenes bajo el mínimo | Evitar conflictos entre bloques paralelos |

## 5. Qué no se probó y conviene que pruebes

1. `build.ps1` completo (todas las pruebas y la cobertura combinada) y las metas de rendimiento (búsqueda p95 < 50 ms con 100.000
   clientes).
2. En la caja: lista de Empleados con una promoción activa; cliente que pide factura sin correo; bloquear un cliente e intentar
   venderle; búsqueda por celular; cambio de mercancía de un cliente con lista.
3. Privacidad: activar la política, registrar y revocar marketing, solicitud del titular y su vencimiento, exportar y suprimir.
4. Proveedores: cuenta bancaria nueva → pago con advertencia → verificación por otro usuario; retenciones sugeridas en una compra.

## 6. Pendiente y avisos

- **Revisar con un abogado** el texto de la política (es una plantilla) y la autorización verbal en la caja; activarla desde
  `POST /customers/privacy-policies/{id}/activate`.
- Los **roles personalizados** clonados de cajero o supervisor conservan `parties.party.manage`: revísalos.
- **Crédito y puntos (8-B)**: definir con el contador antes de activarlos (ADR-0043).
