# Fase 12-B · Licencia dentro del POS — Informe de implementación

- **Estado:** Implementada — pendiente de tu validación · 2026-09-29
- **Propuesta:** [fase-12b-propuesta.md](fase-12b-propuesta.md) (aprobada con las recomendaciones de la §13)
- **ADR:** [0053](../adr/0053-restricciones-de-licencia-por-lista-de-permitidos.md) · [0054](../adr/0054-licencia-local-token-en-la-bd-y-reloj-confiable.md)
- **Pruebas manuales:** [http/fase-12b.http](../../http/fase-12b.http) · claves de producción: [despliegue-nube.md §15](../despliegue-nube.md#15-claves-públicas-embebidas-en-el-pos-fase-12-b)

## 1. Qué se entregó

| Bloque | Entregado |
|---|---|
| 12B.1 Estado y reglas | `LicenseEvaluator`: función pura con los estados `DEMO` (30 días), `VALID`, `VALID_OFFLINE`, `GRACE`, `RESTRICTED` y `REACTIVATION_REQUIRED`, días restantes y avisos (los del servidor + los propios) |
| 12B.2 Activación, check-in y liberación | Módulo `Licensing`: `POST /license/activate` (solo propietario), `/license/check`, `/license/deactivate`, `GET /license` y `/license/checkins`. Cliente HTTP con el contrato de 12-A; huella `fp1` (placa, disco del sistema y `MachineGuid` por WMI y registro); proceso en segundo plano que recalcula cada minuto y verifica cada 24 h ± 2 h, al arrancar y con reintentos (1 min → 1 h) |
| 12B.3 Restricciones y avisos | Filtro del *pipeline* por lista de permitidos (`IAllowedWhenRestricted`, 57 comandos, prueba R10). `/auth/me` trae `license` (estado, días, avisos) para todos los usuarios. Tiquete con "*** DEMOSTRACIÓN ***" mientras no se active |
| 12B.4 Reloj, restauración y auditoría | Hora confiable del token (`iat`) y mayor hora observada: atrasar el reloj más de 24 h restringe hasta el siguiente check-in (`LICENSE_CLOCK_ROLLBACK`). Backup restaurado en otro PC → `REACTIVATION_REQUIRED`. Sello de auditoría en cada check-in (ancla externa del ADR-0048). Acciones nuevas: `LICENSE_DEACTIVATED`, `LICENSE_CHECKIN_FAILED`, `LICENSE_CLOCK_ROLLBACK` |

### Base de datos y configuración
- POS: `V2026.10.030__licensing__licensing.sql` (esquema `licensing`: `license_state` y `checkins` de solo inserción).
- Nube: `V2026.10.005__licensing__checkin_audit_seal.sql` (sello de auditoría en `checkins`) y una columna nueva en la pestaña
  "Check-ins" del portal. El contrato `CheckinRequest` tiene el campo opcional `AuditSeal`: los POS anteriores siguen funcionando.
- Configuración del POS: `Pos:Licensing:ServerUrl` (dirección de su servidor de licencias) y `Pos:Licensing:CheckinHours` (24).
  Solo en desarrollo: `DevelopmentTrustedKeys` y `DevelopmentFingerprint`.
- 3 permisos nuevos (93 en total); `licensing.license.manage` solo Propietario.
- Se retiró `IFeatureGate`/`AllowAllFeatureGate` (stub de la Fase 1): lo reemplaza `ILicenseGate`.

## 2. Validación hecha (corta, según tu forma de trabajo)

| Prueba | Resultado |
|---|---|
| Compilación de toda la solución | ✅ sin errores ni advertencias |
| `LicenseEvaluatorTests` (reloj simulado: demo día 29/31, vigente, sin conexión, gracia día 7 y un segundo después, suspendida, cancelada, vencida, en mora, cambio de disco 2 de 3, otro equipo, otra instalación, clave desconocida, reloj atrasado 23 h/25 h y corregido) y `LicenseRestrictionBehaviorTests` | ✅ 11/11 |
| `LicenseApiTests` (punta a punta con una nube en memoria que habla el contrato real por HTTP): demostración con tiquete marcado → clave mal escrita, inexistente y cajero sin permiso → activación → jornada abierta, la nube suspende → `RESTRICTED`: se termina la venta y se consulta el reporte, crear una categoría da `LICENSE.RESTRICTED` → la nube reactiva → reloj atrasado 3 días → `RESTRICTED` → verificación → `VALID`; sello de auditoría recibido; acciones auditadas | ✅ |
| Arquitectura (R1–R10), conformidad y catálogo de permisos, esquemas (74), nube: unitarias (84 + 57), BD (26), API del POS y simulador | ✅ |

**Cambio frente a la propuesta:** la prueba de punta a punta usa una nube en memoria con el contrato real (rutas, DTOs, token Ed25519 y
códigos `LICENSE.*`) en lugar de levantar el servidor de 12-A en la misma prueba (los dos *hosts* chocan en el mismo proyecto). El
servidor real ya se prueba con el simulador en la Fase 12-A; la prueba conjunta con tu nube es tu validación (§3).

## 3. Qué te toca validar

1. Con tu nube local o el VPS (`docs/despliegue-nube.md`), crea en el portal un cliente, su empresa con **el mismo NIT** de la tienda,
   la suscripción y la clave.
2. En el POS de desarrollo configura `Pos:Licensing:ServerUrl` y, **solo en desarrollo**, la clave pública de tu nube en
   `Pos:Licensing:DevelopmentTrustedKeys:0` (la imprime `keys` en el servidor). Sigue `http/fase-12b.http`: estado en demostración,
   activar, verificar, suspender en el portal y verificar, reactivar, liberar.
3. En el portal, pestaña "Check-ins": debe aparecer el **sello de auditoría** de la tienda.
4. Antes de vender: copia las claves públicas `ACTIVE` y `STANDBY` de producción a
   `src/Modules/Licensing/Pos.Modules.Licensing.Infrastructure/trusted-keys.json` y compila la versión (despliegue-nube §15).

## 4. Pendientes y notas

- Un intento de activación **rechazado** no queda en el historial local (se revierte con su transacción); la nube sí lo registra.
- Cambio de equipo: lo hace soporte desde el portal (12-A); el POS muestra `REACTIVATION_REQUIRED` y pide la clave.
- La huella se lee por WMI: el servicio de Windows (cuenta `LocalSystem` o de servicio) tiene acceso. Si no se puede leer, activar
  responde `LICENSE.FINGERPRINT_INVALID` con un mensaje claro.
- Pantallas de licencia: Fase 15.
