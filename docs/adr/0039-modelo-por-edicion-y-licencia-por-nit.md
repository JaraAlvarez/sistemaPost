# ADR-0039 · Modelo de licencias por edición y una licencia por NIT

- **Estado:** Aceptada · 2026-09-29 · Fase 12-A · Decisiones L-03, L-06, L-07 y L-08 de la [propuesta](../fases/fase-12a-propuesta.md)
  (reemplaza, para la nube, el modelo de planes y *features* del doc 09 original; ver ADR-0015)

## Contexto
ADR-0015 dejó la **edición** (Caja Única / Multicaja) como única diferencia comercial, sin planes por módulos ni límite de cajas. El
propietario decidió licenciar la **razón social**: una empresa con varias sucursales paga una sola licencia, y renovar o suspender
debe afectar a todas sus sucursales a la vez.

## Decisión
- **Jerarquía** (esquema `licensing`):
  `cuenta (accounts: DIRECT/RESELLER) → empresas (organizations: NIT con DV, único) → suscripción (subscriptions) → licencia
  (licenses) → instalaciones (installations, una por sucursal) → equipos (devices) → activaciones (activations) → check-ins`.
- **Suscripción**: edición `SINGLE`/`MULTI`, periodicidad `MONTHLY`/`ANNUAL`, prueba (30 días ⚙️) o pagada, gracia (7 días ⚙️),
  estados `TRIAL`, `ACTIVE`, `PAST_DUE`, `SUSPENDED`, `CANCELLED`, `EXPIRED` con transiciones por fecha (tarea horaria y en cada
  activación/check-in). Una sola suscripción vigente por empresa (índice único parcial). Cada cambio queda en
  `subscription_events` (solo inserción): creada, renovada (con referencia del pago), edición cambiada, suspendida, reactivada,
  cancelada, gracia extendida.
- **Una licencia vigente por empresa** (`ux_licenses__organization_active`). La clave `POS-XXXXX-XXXXX-XXXXX-XXXXX` (alfabeto sin
  0/O/1/I, 95 bits aleatorios y dígito de control Luhn mod 32) se muestra una sola vez; en la BD solo quedan su hash SHA-256 y el
  prefijo visible. Regenerar revoca la anterior y las instalaciones pasan a la nueva cuando se activan con ella; máximo de
  instalaciones ⚙️ por licencia (por defecto sin límite). **Cancelar la suscripción revoca su licencia vigente**: el siguiente
  check-in recibe `LICENSE.REVOKED` y la empresa necesita una suscripción y una clave nuevas.
- **Edición y rol del equipo**: Caja Única solo admite `ALL_IN_ONE`; Multicaja admite `STORE_SERVER` o `ALL_IN_ONE` (sucursal
  pequeña). Las cajas adicionales no se activan en la nube: se emparejan con el servidor de la tienda (ADR-0018).
- **Huella con tolerancia 2 de 3** (`fp1.<placa>.<disco>.<máquina>`, solo hashes de 32 hexadecimales): un cambio de disco no obliga a
  reactivar; una copia a otro PC sí (`LICENSE.REACTIVATION_REQUIRED`). Un equipo activo por instalación; activar de nuevo en el mismo
  equipo es idempotente; para cambiar de PC se libera el anterior (portal o `POST /v1/deactivations`).
- **Activación** valida, en este orden: formato de la clave, huella, rol, clave (comparación en tiempo constante con todas las
  candidatas del prefijo; clave inexistente y revocada responden igual), límite por licencia, suscripción que admite activación,
  NIT igual al de la empresa, edición compatible, máximo de instalaciones.
- **Roles del portal**: `SUPERADMIN` (todo) y `SUPPORT` (consulta, reactivar, extender la gracia, liberar equipos, auditoría). El rol
  **`RESELLER` existe en el modelo** (usuario con cuenta de distribuidor) **sin permisos ni pantallas en 12-A**: no accede al portal
  ni a la API interna.

## Consecuencias
- ✅ Simple y alineado al negocio: el POS solo mira la edición y el estado; suspender o renovar la empresa llega a todas sus
  sucursales en el siguiente check-in.
- ✅ Una fuga de la BD no revela claves; los rechazos de check-in quedan registrados.
- ⚠️ Si vuelven los planes por módulos o el cobro por sucursal, se agregan tablas y se usa el máximo de instalaciones.
- ⚠️ Una empresa que cambia de NIT (otra razón social) es otra empresa con otra licencia.
- ⚠️ El distribuidor ("solo ve sus clientes") queda para una fase posterior: requiere filtrar cada lectura y caso de uso por cuenta.
