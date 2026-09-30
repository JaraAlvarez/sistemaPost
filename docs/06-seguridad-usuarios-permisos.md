# 06 · Usuarios, permisos (K), seguridad y auditoría

> Estado: **PROPUESTA — pendiente de aprobación**

## K. Sistema de usuarios y permisos

### Modelo: RBAC con alcance + excepciones + autorización delegada

```
Permiso efectivo(usuario, permiso, sucursal) =
     (∃ rol del usuario con ese permiso, en esa sucursal o en todas)
  ∪  GRANT explícito del usuario
  −  DENY explícito del usuario            ← DENY siempre gana
  ∩  feature habilitada por la licencia     ← si el plan no lo incluye, nadie lo tiene
```

- **Permisos** son un catálogo fijo definido en el código (`module.resource.action`) y sembrado en la BD en cada migración. Los usuarios no inventan permisos; combinan permisos en **roles**.
- **Roles** son editables por el administrador (los de sistema se pueden clonar, no borrar).
- **Alcance por sucursal**: "Supervisor en Sucursal Norte" no es supervisor en Sucursal Sur.
- **Excepciones por usuario** para casos puntuales, auditadas.
- **Autorización de supervisor (override)**: cuando un cajero intenta una acción sensible sin permiso, la API responde `403` con `authorization_required: <permiso>`. La UI pide credencial/PIN del supervisor, la API valida que ese supervisor sí tenga el permiso, crea un `authorization_grant` de **un solo uso** ligado a la acción concreta, y la operación se reintenta con ese grant. Todo queda auditado con ambos usuarios.

### Catálogo de permisos (extracto — el catálogo completo se define en la Fase 3)

| Módulo | Permisos |
|---|---|
| organization | `organization.company.manage`, `organization.branch.manage`, `organization.terminal.manage`, `organization.devices.manage` |
| identity | `identity.user.view`, `identity.user.manage`, `identity.role.manage`, `identity.session.revoke` |
| catalog | `catalog.product.view`, `catalog.product.manage`, `catalog.price.update`, `catalog.cost.view`, `catalog.tax.manage`, `catalog.import` |
| inventory | `inventory.stock.view`, `inventory.kardex.view`, `inventory.adjustment.create`, `inventory.adjustment.approve`, `inventory.count.manage`, `inventory.count.approve`, `inventory.transfer.send`, `inventory.transfer.receive` |
| purchasing | `purchasing.supplier.manage`, `purchasing.order.create`, `purchasing.order.approve`, `purchasing.purchase.post`, `purchasing.purchase.void`, `purchasing.payable.pay`, `purchasing.return.post` |
| sales | `sales.sale.create`, `sales.line.void`, `sales.discount.apply`, `sales.discount.above_limit`, `sales.price.override`, `sales.sale.cancel`, `sales.sale.void`, `sales.sale.reprint`, `sales.return.create`, `sales.return.without_receipt` |
| cash | `cash.session.open`, `cash.session.close`, `cash.session.review`, `cash.movement.cash_in`, `cash.movement.withdrawal`, `cash.drawer.open_no_sale`, `cash.session.view_expected` (ver esperado en arqueo ciego) |
| expenses | `expenses.expense.create`, `expenses.expense.void` |
| billing | `billing.document.view`, `billing.document.retry`, `billing.range.manage` |
| reporting | `reporting.sales.basic`, `reporting.sales.advanced`, `reporting.profit.view` (sensible), `reporting.taxes.view`, `reporting.inventory.view`, `reporting.purchases.view`, `reporting.cash.view`, `reporting.antifraud.view` (sensible), `reporting.report.export` (sensible; el formato de permisos exige tres partes) — Fase 9, roles en docs/fases/fase-09-propuesta.md §8 |
| audit | `audit.log.view` |
| settings | `settings.manage` |
| backup | `backup.backup.run`, `backup.backup.view`, `backup.destination.configure` (sensible), `backup.recovery.manage` (sensible, solo propietario) — Fase 11; restaurar: consola del servidor |
| licensing | `licensing.view`, `licensing.activate` |

`is_sensitive = true` → candidatos a autorización de supervisor y aparecen en el reporte antifraude.

### Roles predeterminados (sembrados, clonables)

| Rol | Resumen |
|---|---|
| **Propietario** (`OWNER`) | Todo. No puede quedar sin al menos un usuario (RN-SEC-04). |
| **Administrador** | Todo excepto licencia y restauración de backups. |
| **Supervisor de caja** | Ventas + anulaciones, descuentos sobre límite, retiros, revisión de cierres, devoluciones. |
| **Cajero** | Abrir/cerrar su caja, vender, descuento hasta límite, eliminar líneas, suspender/recuperar. |
| **Inventario / Bodega** | Productos (sin precios), kardex, ajustes (sin aprobar), conteos, traslados, recepción de compras. |
| **Compras** | Proveedores, órdenes, compras, devoluciones a proveedor, costos. |
| **Contador** | Solo lectura: reportes, impuestos, CxP, auditoría. |

### Autenticación y sesiones

- **Backoffice**: usuario + contraseña → token de sesión opaco (256 bits aleatorios); en BD solo se guarda su **hash SHA-256**. Expiración deslizante.
- **Caja**: la caja está emparejada y se autentica con su secreto de equipo (`X-Device-Id` / `X-Device-Secret`). Sobre esa caja, el cajero entra con **código de cajero + PIN** (rápido) — el PIN solo es válido desde cajas emparejadas (`AUTH.PIN_REQUIRES_TERMINAL`).
- **Backoffice**: solo desde el propio servidor o desde un equipo administrativo emparejado ([ADR-0018](adr/0018-emparejamiento-y-https-en-la-lan.md)).
- **Propietario**: se crea en el asistente inicial (`/setup/owner`, solo desde el servidor). Si pierde su contraseña, se recupera **solo en el servidor** con `Pos.Server.Migrator reset-owner --username …` (contraseña temporal, cierra sesiones, auditoría CRÍTICA).
- Tokens opacos (no JWT) para usuarios: revocables al instante (desactivar usuario, cierre remoto, revocar equipo). JWT firmado solo para la licencia ([ADR-0016](adr/0016-sesiones-con-tokens-opacos.md)).
- Autorización de supervisor: el supervisor ingresa su código + PIN en la caja (`POST /auth/authorizations`) y la caja reenvía la acción con `X-Authorization-Grant`; la auditoría registra a ambos.
- Con cambio de contraseña pendiente, una sesión de backoffice solo puede consultar su perfil, cambiar la contraseña y salir (`AUTH.PASSWORD_CHANGE_REQUIRED`).
- Bloqueo de pantalla por inactividad sin perder la venta en curso.
- Cambio de cajero en la misma caja = cierre de jornada (o transferencia de jornada con autorización ⚙️).

---

## Seguridad (punto 15)

| Amenaza / requisito | Control |
|---|---|
| Contraseñas | **Argon2id** con NSec/libsodium (memoria 64 MiB, 3 iteraciones, paralelismo 1 — ajustado al hardware de caja), sal única, formato PHC para poder subir parámetros en el futuro (rehash al iniciar sesión). PIN también con Argon2id (19 MiB, 2 iteraciones). [ADR-0017](adr/0017-argon2id-con-nsec.md). |
| SQL Injection | EF Core/Dapper **siempre parametrizados**; prohibido concatenar SQL (regla de análisis estático + revisión). El rol de BD de la app no es superusuario. |
| Validación | Validación en 3 niveles: DTO (FluentValidation: formato), dominio (invariantes/reglas), BD (CHECK/FK/UNIQUE). |
| Control de acceso | Autorización por **permiso** en cada endpoint (política declarativa, verificada antes de leer el cuerpo de la petición) + alcance por sucursal. Permisos efectivos = roles ∪ GRANT − DENY. Denegado por defecto: un endpoint sin política hace fallar la prueba automática. |
| Acceso a la BD | PostgreSQL escucha **solo en 127.0.0.1**, puerto no estándar, usuario de app con privilegios mínimos (sin DDL en runtime; migraciones con usuario separado). Contraseña de BD generada al instalar, guardada con **DPAPI** (máquina). |
| Tráfico LAN | Solo Multicaja: HTTPS en el puerto 5443 con certificado ECDSA autogenerado por instalación; los equipos lo fijan (*pinning*) al emparejarse. El HTTP (5480) solo escucha en localhost. |
| Emparejamiento de equipos | Una caja o un equipo administrativo se une a la tienda con un **código de 6 dígitos, un solo uso, 10 min**, generado por un administrador. Un equipo no emparejado solo puede llamar a `/devices/pair`. |
| Datos sensibles | Nunca se guarda número completo de tarjeta ni CVV (solo marca y últimos 4). Claves técnicas de facturación y credenciales de backup cifradas (AES-256-GCM, clave protegida por DPAPI). |
| Datos personales | Cumplimiento de ley de protección de datos (Colombia: Ley 1581/2012): finalidad, consentimiento para marketing, derecho de consulta/rectificación, exportación. |
| Auditoría | Ver abajo. |
| Integridad de binarios | Ejecutables firmados (Authenticode); actualizaciones con manifiesto firmado Ed25519. |
| Backups | Cifrados; ver doc 10. |
| Logs | Sin datos sensibles (contraseñas, tokens, PIN) — filtros en Serilog. |
| Fuerza bruta | Bloqueo progresivo por usuario y por IP; registro en `login_attempts`. |
| Soporte remoto | Usuario de soporte deshabilitado por defecto; se habilita temporalmente por el propietario; todo auditado. |

---

## Auditoría (punto 14)

### Qué se registra

| Tipo | Ejemplos | Mecanismo |
|---|---|---|
| Cambios de datos maestros | Precio, costo, impuesto, datos del producto, usuarios, roles, permisos, configuración, medios de pago, resoluciones | Interceptor automático EF Core (`[Audited]`) — antes/después solo de campos cambiados |
| Eventos de negocio sensibles | Anular venta/línea, cancelar venta, descuento sobre límite, precio abierto, retiro de caja, apertura de cajón sin venta, reimpresión, devolución, ajuste, cierre con diferencia | `IAuditWriter` explícito en el caso de uso |
| Seguridad | Login correcto/fallido, bloqueo, cambio de contraseña, revocación de sesión, autorización de supervisor | Módulo Identity |
| Sistema | Backup/restauración, actualización, migración, cambio de estado de licencia, cambio de hora detectado | Servicios de infraestructura |

### Ejemplo — "Juan modificó el precio del producto X"

```json
{
  "occurred_at": "2026-10-05T14:32:10Z",
  "user_id": "0192…", "user_display_name": "Juan Pérez",
  "session_id": "0192…", "device_id": "0192…", "pos_terminal_id": null,
  "ip_address": "192.168.1.20",
  "module": "catalog", "action": "PRODUCT_PRICE_CHANGED",
  "entity_type": "ProductPrice", "entity_id": "0192…",
  "entity_label": "Arroz Diana 500 g (SKU 000123) — Lista General",
  "old_values": { "price": 4500.00 },
  "new_values": { "price": 4800.00 },
  "summary": "Juan Pérez cambió el precio de Arroz Diana 500 g de $4.500 a $4.800",
  "authorized_by": null, "correlation_id": "c8f1…", "severity": "INFO"
}
```

Responde: **quién** (usuario + sesión + equipo + IP), **cuándo** (UTC, mostrado en hora local), **qué** (módulo, acción, entidad), **valor anterior** y **valor nuevo**.

### Garantías

- Se escribe en la **misma transacción** que el cambio: si el cambio se guarda, la auditoría también; si falla, ninguno.
- Append-only por permisos de BD + triggers, con **sellado por lotes por nodo** y anclas externas (ADR-0012, reemplaza la cadena fila a fila): detecta filas alteradas, borradas o insertadas en rangos sellados.
- Consultas: historial de una entidad, actividad de un usuario, acciones por tipo y fecha, reporte antifraude.

### Implementación de la Fase 10 (ADR-0047 a 0049)

- **Catálogo de acciones** (211) con nombre en español y severidad: código en `AuditActions` = tabla `audit.action_types`; una prueba de
  arquitectura exige que todo código escrito esté en el catálogo.
- **Eventos agregados**: ingreso fallido (`LOGIN_FAILED`), restablecimiento de contraseña y PIN por un administrador, línea eliminada de
  una venta, arranque del servidor, actualización de la BD, reloj atrasado, verificación e incidentes de integridad, constancia emitida.
  Los de backups y licencia quedan en el catálogo y se emiten en las Fases 11 y 12.
- **Consultas**: `/audit/logs` (con severidad, caja, autorizador y texto), `/audit/entities/{tipo}/{id}/history`,
  `/audit/users/{id}/activity`, `/audit/actions`; cada fila trae los cambios campo a campo en español.
- **Verificación automática** diaria incremental (03:00) y completa los domingos; historial en `/audit/verifications`. Un hallazgo nuevo
  abre un **incidente de integridad** CRÍTICO visible en `/auth/me` (`openIntegrityIncidents`) y en el tablero, hasta que el
  **propietario** lo reconoce con una nota (`audit.incident.acknowledge`, excluido del Administrador). Nunca bloquea la venta.
- **Constancia de integridad** en PDF (`/audit/integrity-certificate`): ancla externa manual además del sello del Z.
- **Datos personales enmascarados** en la bitácora (`[PersonalData]`): correo `j***@dominio`, teléfono `***1234`, dirección, notas y
  detalle de solicitudes como `(registrado)`.
- **Reportes de auditoría** (grupo Auditoría del catálogo de reportes, permiso `audit.log.view`): cambios de precios/costos/impuestos,
  seguridad, eventos sensibles, actividad por usuario, fuera de horario, exportaciones e integridad.
