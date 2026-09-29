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
| reporting | `reporting.sales.basic`, `reporting.sales.advanced`, `reporting.profit.view`, `reporting.inventory.view`, `reporting.antifraud.view`, `reporting.export` |
| audit | `audit.log.view` |
| settings | `settings.manage` |
| backup | `backup.run`, `backup.restore`, `backup.configure` |
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
- **Caja**: la caja (terminal) está registrada y autenticada por su propio certificado/secreto de dispositivo. Sobre esa caja, el cajero entra con **usuario + PIN** (rápido) — el PIN solo es válido desde terminales registrados.
- Tokens opacos (no JWT) para usuarios: revocables al instante (desactivar usuario, cierre remoto). JWT firmado solo para la licencia.
- Bloqueo de pantalla por inactividad sin perder la venta en curso.
- Cambio de cajero en la misma caja = cierre de jornada (o transferencia de jornada con autorización ⚙️).

---

## Seguridad (punto 15)

| Amenaza / requisito | Control |
|---|---|
| Contraseñas | **Argon2id** (memoria 64 MB, 3 iteraciones, paralelismo 1 — ajustado al hardware de caja), sal única, formato PHC para poder subir parámetros en el futuro (rehash al iniciar sesión). PIN también con Argon2id. |
| SQL Injection | EF Core/Dapper **siempre parametrizados**; prohibido concatenar SQL (regla de análisis estático + revisión). El rol de BD de la app no es superusuario. |
| Validación | Validación en 3 niveles: DTO (FluentValidation: formato), dominio (invariantes/reglas), BD (CHECK/FK/UNIQUE). |
| Control de acceso | Autorización por **permiso** en cada endpoint (política declarativa) + verificación de alcance (sucursal) + feature de licencia. Denegado por defecto: un endpoint sin política no compila la prueba de arquitectura. |
| Acceso a la BD | PostgreSQL escucha **solo en 127.0.0.1**, puerto no estándar, usuario de app con privilegios mínimos (sin DDL en runtime; migraciones con usuario separado). Contraseña de BD generada al instalar, guardada con **DPAPI** (máquina). |
| Tráfico LAN | HTTPS con certificado autogenerado por instalación; los terminales lo fijan (*pinning*) al emparejarse. |
| Emparejamiento de cajas | Una caja nueva se une a la tienda con un **código de emparejamiento temporal** generado por un administrador en el servidor. |
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
