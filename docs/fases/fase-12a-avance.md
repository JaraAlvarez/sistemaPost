# Fase 12-A · Servidor y portal web de licencias — Avance (trabajo en progreso)

> Estado: **EN PROGRESO — interrumpido por cambio de equipo** · 2026-09-29
> Propuesta aprobada: [fase-12a-propuesta.md](fase-12a-propuesta.md) (se copió a esta rama tal como estaba en el checkout principal).
> Rama: `fase-12a-licencias`. La solución completa (`Pos.slnx`) **compila en Release con 0 advertencias y 0 errores**; las pruebas
> nuevas del contrato pasan (53/53). Aún **no** se ejecutó `build.ps1` completo con cobertura.

## 1. Qué está hecho

| Bloque | Estado | Dónde |
|---|---|---|
| Contrato compartido (lo usará el POS en 12-B) | ✅ Hecho y probado | `src/Shared/Pos.Licensing.Contracts`: clave `POS-XXXXX-XXXXX-XXXXX-XXXXX` (alfabeto sin 0/O/1/I, dígito de control Luhn mod 32), huella con tolerancia 2 de 3 (`fp1.<placa>.<disco>.<máquina>`, solo hashes), token JWS compacto EdDSA/Ed25519 con `kid` y `ver`, verificación con varias claves (rotación), DTOs y códigos de error estables de la API. Solo depende de NSec |
| Pruebas del contrato | ✅ 53 pruebas | `tests/Pos.Licensing.Contracts.UnitTests` (firma/verificación, alteración, kid desconocido, rotación con 2 claves, versión futura, PEM, formato y dígito de control, transposiciones, huella) |
| Núcleo de la nube | ✅ Compila | `src/Cloud/Pos.Cloud.Abstractions` (roles, matriz de permisos, `IPortalUserContext`, `Outcome<T>`, lectura de auditoría), `src/Cloud/Pos.Cloud.Infrastructure` (contexto EF propio, transacción por comando, permiso del portal en el pipeline, auditoría encadenada reutilizando `AuditHasher`/`AuditSealer`/`AuditVerifier` del POS, estado de la BD) |
| Migraciones de la BD de la nube | ✅ Escritas (sin probar contra PostgreSQL) | `src/Cloud/Pos.Cloud.Migrations`: V001 línea base + nodo, V002 auditoría (misma estructura que la del POS), V003 `portal.portal_users/portal_sessions` + usuario técnico, V004 esquema `licensing` completo (cuentas, empresas con NIT único, suscripciones, eventos solo inserción, licencias con hash + prefijo y una vigente por empresa, instalaciones, equipos, activaciones con índices únicos parciales, check-ins solo inserción, claves de firma), `A__system__privileges` (sin DELETE para `pos_app`). Se aplican con el migrador SQL-first del POS (`Pos.Server.Migrations`) |
| Dominio de licencias | ✅ Compila (sin pruebas aún) | `Pos.Cloud.Licensing.Domain`: cuenta, empresa (NIT con DV), suscripción (prueba/pagada, renovar, suspender, reactivar, cancelar, extender gracia, cambiar edición, transiciones por fecha con eventos), licencia (hash en tiempo constante, máximo de instalaciones), instalación (activar idempotente en el mismo equipo, 2 de 3, liberar, mover a la licencia regenerada), check-in, clave de firma (STANDBY/ACTIVE/RETIRED/REVOKED), reglas de edición |
| Dominio de usuarios del portal | ✅ Compila (sin pruebas aún) | `Pos.Cloud.PortalIdentity.Domain`: usuario (roles SUPERADMIN/SUPPORT/RESELLER, bloqueo por intentos), sesión revocable con etapas (pendiente de TOTP / enrolamiento / activa), TOTP RFC 6238 propio (HMAC-SHA1, ±1 paso, anti-repetición), Base32 y enlace `otpauth://` |
| Casos de uso | ✅ Compilan | API del POS (activar, check-in con rechazo registrado, liberar, claves públicas) y portal (clientes, empresas, suscripciones, claves, instalaciones, tablero, claves de firma); acceso con contraseña Argon2id + TOTP obligatorio, enrolamiento con QR, cambio de contraseña, usuarios (solo superadmin), sesiones, primer superadmin y recuperación de emergencia por consola |
| Infraestructura | ✅ Compila | Mapeo EF, lecturas del portal, firma con la clave privada de un archivo PEM (`Licensing:Signing:PrivateKeyPath`), claves de confianza en caché, límite por licencia, secreto TOTP cifrado con Data Protection |
| Endpoints | ✅ Compilan | `/v1/activations`, `/v1/checkins`, `/v1/deactivations`, `/v1/public-keys` (límite por IP + por licencia; 429/503 con código); `/admin/...` (mismos permisos que el portal, Bearer) y `/admin/auth/...` (login + TOTP) |
| Host | ✅ Compila (sin ejecutar aún) | `src/Cloud/Pos.Cloud.Host`: arranque de la BD con reintentos, sellado de auditoría, actualización horaria de estados, `/health`, cabeceras de seguridad (CSP, HSTS, nosniff, sin marcos), encabezados reenviados (Caddy), autenticación cookie `__Host-` (HttpOnly, SameSite=Strict) y Bearer con sesión en BD, políticas por permiso, limitadores, consola (`setup-database`, `migrate`, `status`, `create-superadmin`, `recover-user`, `generate-signing-key`, `register-standby-key`, `revoke-signing-key`, `verify-audit`, `healthcheck`) |
| Portal Blazor + MudBlazor | ✅ Hecho (probado a mano contra PostgreSQL 18) | App/Routes/diseños, ingreso, segundo factor, enrolamiento con QR, tablero, clientes (lista, alta, ficha, empresas), empresas (lista y ficha con pestañas), suscripciones (filtros por estado y vencimiento, acciones según permiso con `SubscriptionActions`), instalaciones (filtro, equipos, liberar), usuarios (solo superadmin: alta con temporal, rol, bloquear, desbloquear, restablecer contraseña/TOTP, sesiones), auditoría (paginada + verificación de la cadena), claves de firma (estados; publicar reserva y revocar con el permiso nuevo `licensing.signing_key.manage`), mi cuenta (cambio de contraseña; `MustChangePasswordMiddleware` + `Routes.razor` redirigen allí mientras sea temporal) y sin acceso |

## 2. Qué falta (frente a los criterios de aceptación §11)

| Criterio §11 | Estado | Pendiente |
|---|---|---|
| Portal con usuarios, roles y TOTP; clientes, empresas, suscripciones, claves, instalaciones y equipos | 🟡 | Páginas completas y probadas en el navegador (ingreso + enrolamiento TOTP, cambio obligatorio de la temporal, suspender, alta de usuario, restablecer contraseña, auditoría paginada y verificada, publicar/revocar clave de reserva). Falta automatizarlo en `tests/Pos.Cloud.IntegrationTests`. Nota: con `ASPNETCORE_ENVIRONMENT=Production` desde `bin/` (sin publicar) los recursos `_content/` y `_framework/` salen vacíos; usar `Development` o `dotnet publish` |
| Activación, check-in y liberación por API con códigos estables; límite de peticiones | 🟡 | Código hecho; faltan las **pruebas de integración** (`tests/Pos.Cloud.IntegrationTests` con WebApplicationFactory + Testcontainers) |
| Token Ed25519 con `kid`; rotación probada | 🟡 | Probado en el contrato; falta probar la rotación de punta a punta en el servidor (STANDBY → ACTIVE → RETIRED) |
| Suspender, renovar y reactivar se reflejan en el siguiente check-in | ⬜ | Prueba de integración |
| Simulador de POS de punta a punta | ⬜ | `tools/Pos.License.Simulator` (biblioteca + CLI: activar, check-in, recibir suspensión/renovación, verificar cada token) y su prueba contra el host de pruebas |
| Contenedor desplegable con guía; respaldo de la BD | ⬜ | `src/Cloud/Pos.Cloud.Host/Dockerfile` (multi-etapa, no root), `deploy/cloud/` (docker-compose con app + postgres:18 + caddy, `db-init` con `setup-database`, Caddyfile `licencias.<dominio>`, `.env.example`, script de `pg_dump` cifrado fuera del VPS), verificación local con puertos libres (no usar 5432, 5433, 5435, 5488, 6379), `docs/despliegue-nube.md` (Ubuntu 24.04) |
| Auditoría de todas las acciones; `build.ps1` en verde; cobertura del dominio ≥ 90 % | 🟡 | Auditoría implementada; faltan pruebas: unitarias de `Pos.Cloud.Licensing.Domain` y `Pos.Cloud.PortalIdentity.Domain`, BD real (`tests/Pos.Cloud.Database.Tests`: hash único, activación única por equipo, solo inserción, privilegios, modelo EF contra la BD) e integración; umbrales en `build.ps1` (`Pos.Cloud.Licensing.Domain` 90, `Pos.Licensing.Contracts` 90) — **no se agregaron todavía** para no romper `build.ps1` |
| Docs 09, ADRs e informe | ⬜ | ADR 0037 (nube separada en el mismo repositorio), 0038 (token Ed25519 con rotación por kid), 0039 (modelo por edición y licencia por NIT); notas de implementación en doc 09; `http/fase-12a.http`; `docs/fases/fase-12a-informe.md`; fila 12-A de `docs/README.md` |

## 3. Problemas conocidos y decisiones a revisar

- **Nada se ha ejecutado contra PostgreSQL** todavía: las migraciones, el mapeo EF (conversiones de enumeraciones, `smallint` de la gracia,
  navegaciones con campos) y el orden de guardado al regenerar la clave (FK diferida `replaced_by` + `FlushAsync`) deben validarse con las
  pruebas de BD.
- El dominio de licencias depende del contrato compartido (`Pos.Licensing.Contracts`, para la huella y la clave): desviación consciente de
  la regla "Domain solo depende del SharedKernel"; documentarla en el ADR 0037 y en la prueba de arquitectura de la nube.
- Estados de clave de firma: se agregaron `STANDBY` y `REVOKED` a los `ACTIVE`/`RETIRED` de la propuesta (reserva publicada y clave
  comprometida). Documentar en el ADR 0038 / informe.
- El rol distribuidor (RESELLER) queda sin permisos ni pantallas (resolución de la propuesta); la prueba "el distribuidor solo ve sus
  clientes" se reemplaza por "el distribuidor no accede a la API interna" (anotar como desviación).
- Las pantallas interactivas ejecutan cada acción en un ámbito nuevo (`PortalOperations`) para no reutilizar un contexto EF durante toda la
  vida del circuito de Blazor.

## 4. Cómo retomar

```powershell
git fetch origin
git switch fase-12a-licencias
dotnet build Pos.slnx -c Release            # debe terminar sin advertencias
dotnet test --project tests/Pos.Licensing.Contracts.UnitTests
```

Siguientes pasos, en orden:
1. Páginas faltantes del portal (§2) y prueba manual en el navegador (`dotnet run --project src/Cloud/Pos.Cloud.Host` con
   `Cloud__Database__ConnectionString`, `Cloud__Database__MigratorConnectionString`, `Cloud__Database__MigrateOnStartup=true`,
   `Licensing__Signing__PrivateKeyPath` apuntando a un PEM creado con `dotnet run --project src/Cloud/Pos.Cloud.Host -- generate-signing-key --out <ruta>`;
   luego `create-superadmin`).
2. `tests/Pos.Cloud.Licensing.UnitTests` y `tests/Pos.Cloud.PortalIdentity.UnitTests` (dominio ≥ 90 %).
3. `tests/Pos.Cloud.Database.Tests` (Testcontainers, `postgres:18`).
4. `tools/Pos.License.Simulator` + `tests/Pos.Cloud.IntegrationTests` (API, portal con TOTP, permisos, auditoría, límite, rotación, simulador
   de punta a punta y reglas de arquitectura de la nube).
5. Despliegue (`Dockerfile`, `deploy/cloud/`, verificación local de `docker compose` y `/health`) y `docs/despliegue-nube.md`.
6. Umbrales en `build.ps1`, ADRs 0037–0039, doc 09, `http/fase-12a.http`, informe y fila de `docs/README.md`; `build.ps1` completo en verde.

## 5. Archivos compartidos tocados (para revisar la fusión con la Fase 7)

- `Pos.slnx`: carpetas nuevas `/src/Cloud/` y `/src/Shared/` y el proyecto de pruebas `tests/Pos.Licensing.Contracts.UnitTests`.
- `Directory.Packages.props`: `MudBlazor` 9.11.0 y `QRCoder` 1.8.0 (MIT).
- `docs/licencias-terceros.md`: filas de MudBlazor y QRCoder.
- `docs/fases/fase-12a-propuesta.md`: copiado sin cambios desde el checkout principal (no estaba confirmado en git).
- No se modificó nada bajo `src/Modules`, `src/Server` ni `src/BuildingBlocks` (solo se reutilizan).
