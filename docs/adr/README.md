# Registros de decisiones arquitectónicas (ADR)

Cada decisión relevante y difícil de revertir se documenta aquí: contexto, decisión y consecuencias.
Un ADR aprobado no se edita: si la decisión cambia, se crea uno nuevo que lo reemplaza.

| # | Decisión | Estado | Fecha |
|---|---|---|---|
| [0001](0001-monolito-modular-local-first.md) | Monolito modular, local-first, API-first | Aceptada | 2026-09-28 |
| [0002](0002-dotnet-10.md) | .NET 10 LTS como plataforma | Aceptada | 2026-09-28 |
| [0003](0003-postgresql.md) | PostgreSQL 18 en todos los planes | Aceptada | 2026-09-28 |
| [0004](0004-uuid-v7.md) | Identificadores UUID v7 generados en la aplicación | Aceptada | 2026-09-28 |
| [0005](0005-dinero-decimal-redondeo.md) | Dinero con `decimal` y política de redondeo única | Aceptada | 2026-09-28 |
| [0006](0006-despachador-propio.md) | Despachador propio (sin MediatR) y dependencias con licencia permisiva | Aceptada | 2026-09-28 |
| [0007](0007-idioma-del-codigo.md) | Código en inglés; documentación y UI en español | Aceptada | 2026-09-28 |
| [0008](0008-colombia-pais-inicial.md) | Colombia como país inicial | Aceptada | 2026-09-28 |
| [0009](0009-pruebas-microsoft-testing-platform.md) | xUnit v3 sobre Microsoft Testing Platform; reglas de arquitectura propias | Aceptada | 2026-09-28 |
| [0010](0010-migraciones-sql-first.md) | Migraciones SQL-first con migrador propio | Aceptada | 2026-09-28 |
| [0011](0011-colacion-builtin.md) | Colación `builtin C.UTF-8` y orden español explícito | Aceptada | 2026-09-28 |
| [0012](0012-auditoria-por-nodo.md) | Auditoría con sellado por nodo, horizonte seguro y anclas externas | Aceptada | 2026-09-28 |
| [0013](0013-numeracion-interna-vs-fiscal.md) | Numeración interna separada de la fiscal | Aceptada | 2026-09-28 |
| [0014](0014-nodos-y-propiedad-de-datos.md) | Nodos, propiedad de datos y convenciones de sincronización | Aceptada | 2026-09-28 |
| [0015](0015-ediciones-caja-unica-multicaja.md) | Ediciones Caja Única y Multicaja como única diferencia comercial | Aceptada | 2026-09-28 |
| [0016](0016-sesiones-con-tokens-opacos.md) | Sesiones con tokens opacos revocables (no JWT) | Aceptada | 2026-09-28 |
| [0017](0017-argon2id-con-nsec.md) | Argon2id con NSec (libsodium) y formato PHC | Aceptada | 2026-09-28 |
| [0018](0018-emparejamiento-y-https-en-la-lan.md) | Emparejamiento de equipos y HTTPS en la LAN con certificado fijado | Aceptada | 2026-09-28 |
| [0019](0019-kardex-inmutable-y-costo-promedio.md) | Kardex inmutable y costo promedio ponderado por bodega con valor | Aceptada | 2026-09-28 |
| [0020](0020-precios-e-impuestos-con-vigencia.md) | Precios e impuestos con vigencia (sin sobrescribir) | Aceptada | 2026-09-28 |
| [0021](0021-codigos-de-barras-y-codigos-internos.md) | Códigos de barras únicos y códigos internos sin consecutivo global | Aceptada | 2026-09-28 |
| [0022](0022-cambios-por-campo-para-sincronizar.md) | Registro de cambios por campo de los maestros sincronizables | Aceptada | 2026-09-28 |
| [0023](0023-terceros-unicos-con-roles.md) | Terceros únicos con roles (proveedor, cliente) | Aceptada | 2026-09-28 |
| [0024](0024-costo-neto-de-compra.md) | Costo neto de entrada de las compras | Aceptada | 2026-09-28 |
| [0025](0025-lotes-solo-con-cantidades-y-fefo.md) | Lotes solo con cantidades y salidas FEFO | Aceptada | 2026-09-28 |
| [0026](0026-cartera-por-pagar-como-libro.md) | Cuentas por pagar como libro de asientos | Aceptada | 2026-09-28 |
| [0027](0027-movimientos-de-caja-de-solo-insercion.md) | Movimientos de caja de solo inserción y esperado calculado | Aceptada | 2026-09-29 |
| [0028](0028-fecha-de-negocio-y-cierre-definitivo.md) | Fecha de negocio de la jornada y cierre definitivo | Aceptada | 2026-09-29 |
| [0029](0029-sello-de-auditoria-en-el-reporte-z.md) | Sello de la auditoría en el reporte Z | Aceptada | 2026-09-29 |
| [0030](0030-venta-persistida-y-motor-de-calculo-puro.md) | Venta persistida en el servidor y motor de cálculo puro | Aceptada | 2026-09-29 |
| [0031](0031-existencias-sin-saldo-negativo-y-ajuste-rapido.md) | Existencias sin saldo negativo y ajuste rápido autorizado | Aceptada | 2026-09-29 |
| [0032](0032-pagos-redondeo-del-efectivo-y-cambio.md) | Pagos combinados, redondeo del efectivo a $50 y cambio | Aceptada | 2026-09-29 |
| [0033](0033-anulacion-y-cambios-sin-devolucion-de-dinero.md) | Anulación con la jornada abierta y cambios de mercancía sin devolución de dinero | Aceptada | 2026-09-29 |
| [0034](0034-promociones-una-por-linea-la-mas-favorable.md) | Promociones automáticas: una por línea, la más favorable, sin acumular | Aceptada | 2026-09-29 |
| [0035](0035-billing-comprobante-interno-y-proveedor-fiscal-nulo.md) | Billing con comprobante interno y proveedor fiscal nulo hasta la Fase 11-B | Aceptada | 2026-09-29 |
| [0036](0036-agente-de-caja.md) | Agente de caja: impresión ESC/POS y cajón en localhost | Aceptada | 2026-09-29 |

Los números 0037 a 0039 están reservados para la Fase 12-A (servidor y portal de licencias).
