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
| [0037](0037-nube-separada-en-el-mismo-repositorio.md) | Nube separada del POS en el mismo repositorio (`src/Cloud`, BD propia) | Aceptada | 2026-09-29 |
| [0038](0038-token-ed25519-con-rotacion-por-kid.md) | Token de licencia JWS EdDSA/Ed25519 con rotación por `kid` | Aceptada | 2026-09-29 |
| [0039](0039-modelo-por-edicion-y-licencia-por-nit.md) | Modelo de licencias por edición y una licencia por NIT | Aceptada | 2026-09-29 |
| [0040](0040-modulo-customers-y-rol-con-la-clave-del-tercero.md) | Módulo Customers y rol de cliente con la clave del tercero | Aceptada | 2026-09-29 |
| [0041](0041-precio-por-cliente-y-promociones.md) | Precio por cliente (listas fijas y derivadas) y promociones | Aceptada | 2026-09-29 |
| [0042](0042-autorizacion-de-datos-y-derechos-del-titular.md) | Autorización de tratamiento de datos y derechos del titular | Aceptada | 2026-09-29 |
| [0043](0043-credito-y-puntos-reservados.md) | Crédito (fiado) y puntos reservados para la Fase 8-B | Aceptada | 2026-09-29 |
| [0044](0044-reportes-de-solo-lectura-sobre-vistas.md) | Módulo Reporting de solo lectura y vistas `reporting.*` como contrato | Aceptada | 2026-09-29 |
| [0045](0045-catalogo-de-reportes-y-exportacion.md) | Catálogo de reportes en el código, endpoint genérico y exportación auditada | Aceptada | 2026-09-29 |
| [0046](0046-ventas-netas-y-fecha-de-negocio-en-reportes.md) | Ventas netas, utilidad y fecha de negocio en los reportes | Aceptada | 2026-09-29 |
| [0047](0047-catalogo-de-acciones-de-auditoria.md) | Catálogo de acciones de auditoría en el código y en la BD | Aceptada | 2026-09-29 |
| [0048](0048-verificacion-programada-e-incidentes-de-integridad.md) | Verificación programada e incidentes de integridad | Aceptada | 2026-09-29 |
| [0049](0049-datos-personales-y-retencion-de-la-bitacora.md) | Datos personales enmascarados y retención de la bitácora | Aceptada | 2026-09-29 |
| [0050](0050-paquete-de-backup-cifrado-y-codigo-de-recuperacion.md) | Paquete de backup cifrado (.posbak) y código de recuperación | Aceptada | 2026-09-29 |
| [0051](0051-destinos-programacion-y-retencion-de-backups.md) | Destinos, programación, retención y verificación de los backups | Aceptada | 2026-09-29 |
| [0052](0052-restauracion-desde-la-consola.md) | Restauración solo desde la consola del servidor | Aceptada | 2026-09-29 |
| [0053](0053-restricciones-de-licencia-por-lista-de-permitidos.md) | Restricciones de la licencia en el backend por lista de permitidos | Aceptada | 2026-09-29 |
| [0054](0054-licencia-local-token-en-la-bd-y-reloj-confiable.md) | Licencia local: token en la BD, estado calculado, claves embebidas y reloj confiable | Aceptada | 2026-09-29 |
| [0055](0055-instalador-inno-setup-y-postgresql-empaquetado.md) | Instalador con Inno Setup y PostgreSQL empaquetado | Aceptada | 2026-09-30 |
| [0056](0056-versiones-lado-a-lado-y-actualizador-firmado.md) | Versiones lado a lado y actualizador con manifiesto firmado | Aceptada | 2026-09-30 |
| [0057](0057-subida-a-la-nube-por-cursores-con-acuse.md) | Subida tienda → nube por cursores con acuse, autenticada con la licencia | Aceptada | 2026-09-30 |
| [0058](0058-paquete-possync-y-portal-del-cliente.md) | Paquete `.possync` cifrado para la nube y portal del cliente | Aceptada | 2026-09-30 |
| [0059](0059-modo-de-emision-y-factura-electronica-por-venta-con-factus.md) | Modo de emisión (OFF / ON_REQUEST / EVERY_SALE) y factura electrónica por venta con Factus | Aceptada | 2026-09-30 |
| [0060](0060-emision-asincrona-idempotente-y-contingencia.md) | Emisión asíncrona, idempotencia por `reference_code` y contingencia sin Internet | Aceptada | 2026-09-30 |
| [0061](0061-mapeo-fiscal-desde-la-venta-guardada.md) | Mapeo fiscal desde la venta guardada (modelo neutro, pesables, notas y documento soporte) | Aceptada | 2026-09-30 |
| [0062](0062-ingreso-con-google-y-doble-factor-opcional.md) | Ingreso al portal con Google y doble factor (TOTP) opcional | Aceptada | 2026-09-30 |
