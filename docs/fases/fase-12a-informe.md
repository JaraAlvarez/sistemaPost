# Fase 12-A · Servidor y portal web de licencias — Informe

> Estado: **IMPLEMENTADA — pendiente de tu validación** · 2026-09-29 · Rama `fase-12a-licencias`
> Documentos: [propuesta aprobada](fase-12a-propuesta.md) · [guía de despliegue](../despliegue-nube.md) · ADR
> [0037](../adr/0037-nube-separada-en-el-mismo-repositorio.md) · [0038](../adr/0038-token-ed25519-con-rotacion-por-kid.md) ·
> [0039](../adr/0039-modelo-por-edicion-y-licencia-por-nit.md) · notas de implementación en el [doc 09](../09-licenciamiento.md)

## 1. Resultado frente a los criterios de aceptación (§11)

| Criterio | Resultado | Evidencia |
|---|---|---|
| Portal con usuarios, roles y TOTP; clientes, empresas, suscripciones (Caja Única / Multicaja), claves, instalaciones y equipos | ✅ | Portal Blazor + MudBlazor probado a mano en el navegador contra PostgreSQL 18 (ingreso, enrolamiento con QR, cambio obligatorio de la temporal, clientes, empresas, suscripciones, claves, instalaciones, usuarios, auditoría, claves de firma); `PortalAccessTests`: contraseña + TOTP con enrolamiento, cambio de contraseña y salida; bloqueo por intentos; Soporte atiende pero no administra; el distribuidor no accede a la API interna |
| Activación, check-in y liberación de equipo por API con errores de código estable; límite de peticiones | ✅ | `PosApiTests`: token verificable con las claves publicadas, códigos `LICENSE.*` de la activación, check-in y liberación desde el POS; `RateLimitTests`: 429 por IP y por licencia (`LICENSE.TOO_MANY_REQUESTS`) |
| Token Ed25519 con `kid`, verificable con la clave pública; rotación probada | ✅ | `Pos.Licensing.Contracts.UnitTests` (firma, alteración, `kid` desconocido, 2 claves, versión futura); `SigningKeyRotationTests`: `STANDBY → ACTIVE → RETIRED` sin reinstalar el POS, clave revocada → `LICENSE.TOKEN_INVALID`, servidor con clave revocada → `503 LICENSE.SIGNING_UNAVAILABLE` |
| Suspender, renovar y reactivar desde el portal se reflejan en el siguiente check-in | ✅ | `SubscriptionLifecycleTests`: suspender, reactivar, renovar y cancelar llegan en el siguiente check-in; regenerar y revocar la clave también; liberar desde el portal permite activar el PC nuevo |
| Simulador de POS de punta a punta | ✅ | `tools/Pos.License.Simulator` (biblioteca + consola: `keys`, `activate`, `checkin`, `deactivate`, `status`); `SimulatorEndToEndTests`: activa, hace check-in, recibe la suspensión y libera; descarta un token que no es de su equipo o no está firmado por una clave de confianza |
| Contenedor desplegable con guía paso a paso; respaldo de la BD de la nube | ✅ (local) | `Dockerfile` multi-etapa no root, `deploy/cloud/` (Compose con `postgres:18`, `db-init`, `app`, Caddy), verificado con `docker-compose.local.yml` (`/health` y portal por Caddy); [despliegue-nube.md](../despliegue-nube.md) para Ubuntu 24.04; `backup.sh`/`restore.sh` con `pg_dump` + Data Protection cifrados con `age`. **VPS real pendiente** |
| Auditoría de todas las acciones del portal; `build.ps1` en verde; cobertura del dominio de licencias ≥ 90 % | ✅ | `AuditTests`: cada acción con su autor y la cadena verifica; alterar una fila se detecta; cobertura `Pos.Cloud.Licensing.Domain` 100 % (combinada), `Pos.Cloud.PortalIdentity.Domain` 100 %, `Pos.Licensing.Contracts` 100 % (umbrales de 90 % en `build.ps1`); `build.ps1` en verde con 1.019 pruebas |
| Docs 09 actualizado, ADRs (nube separada, token Ed25519 con rotación, modelo por edición) e informe | ✅ | ADR-0037 a 0039; notas de implementación en el doc 09; `http/fase-12a.http`; este informe |

## 2. Qué se construyó

```
src/Shared/Pos.Licensing.Contracts          Contrato con el POS (solo NSec): clave POS-XXXXX-… (Luhn mod 32), huella fp1 (2 de 3), token JWS
                                            EdDSA con kid y ver, anillo de claves, DTOs, rutas y códigos de error LICENSE.*
src/Cloud/Pos.Cloud.Abstractions            Roles, matriz de permisos del portal, IPortalUserContext, Outcome<T>, lectura de auditoría
src/Cloud/Pos.Cloud.Infrastructure          Contexto EF propio, transacción por comando, permiso en el pipeline, auditoría encadenada
                                            (AuditHasher/AuditSealer/AuditVerifier del POS), estado de la BD
src/Cloud/Pos.Cloud.Migrations              V001 línea base · V002 auditoría · V003 portal (usuarios, sesiones, usuario técnico) ·
                                            V004 licensing (cuentas, empresas, suscripciones y eventos, licencias, instalaciones, equipos,
                                            activaciones, check-ins, claves de firma) · A__system__privileges (pos_app sin DELETE)
src/Cloud/Modules/Licensing/*               Dominio (cuenta, empresa, suscripción, licencia, instalación, check-in, clave de firma),
                                            casos de uso del POS y del portal, persistencia, firma con PEM, límite por licencia, /v1 y /admin
src/Cloud/Modules/PortalIdentity/*          Usuarios del portal, sesiones por etapas, TOTP RFC 6238 propio, /admin/auth, /admin/users,
                                            /admin/sessions, /admin/audit
src/Cloud/Pos.Cloud.Host                    ASP.NET Core + portal Blazor/MudBlazor, cookie __Host- y Bearer, CSP/HSTS, limitadores, /health,
                                            tarea horaria de estados, consola (setup-database, migrate, status, create-superadmin,
                                            recover-user, generate-signing-key, register-standby-key, revoke-signing-key, verify-audit,
                                            healthcheck), Dockerfile
tools/Pos.License.Simulator                 Simulador de POS (biblioteca + consola)
deploy/cloud/                               docker-compose.yml, docker-compose.local.yml, Caddyfile(.local), .env.example, backup.sh, restore.sh
docs/despliegue-nube.md · http/fase-12a.http
```

Pantallas del portal: ingreso, segundo factor, enrolamiento con QR, tablero (vencen en 7/15/30 días, en gracia, suspendidas,
vencidas, sin check-in), clientes (lista, alta, ficha), empresas (lista y ficha con pestañas), suscripciones (filtros y acciones según
permiso), instalaciones (equipos, liberar), usuarios (solo superadmin), auditoría (paginada y verificación de la cadena), claves de
firma, mi cuenta y sin acceso.

Roles: **Superadministrador** hace todo (incluidos usuarios del portal y claves de firma); **Soporte** consulta, reactiva, extiende la
gracia, libera equipos y ve la auditoría; **Distribuidor** existe en el modelo sin permisos.

## 3. Pruebas y cobertura

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| Pos.Licensing.Contracts.UnitTests *(nuevo)* | 57 | Firma y verificación, alteración, `kid` desconocido, rotación con 2 claves, versión futura, PEM, formato y dígito de control de la clave, transposiciones, huella 2 de 3 |
| Pos.Cloud.Licensing.UnitTests *(nuevo)* | 84 | Cuenta y empresa (NIT con DV), suscripción (prueba/pagada, renovar, suspender, reactivar, cancelar, gracia, edición, transiciones por fecha y eventos), licencia, instalación (activación idempotente, 2 de 3, liberar, mover a la licencia regenerada), check-in, clave de firma, reglas de edición |
| Pos.Cloud.PortalIdentity.UnitTests *(nuevo)* | 58 | Usuario (roles, bloqueo por intentos, temporal), sesión por etapas, TOTP RFC 6238 (±1 paso, anti-repetición), Base32 y `otpauth://` |
| Pos.Cloud.Database.Tests *(nuevo)* | 26 | Migraciones desde cero e idempotentes, dueño `pos_owner`, FK con índice, restricciones con nombre, conformidad del modelo EF, NIT único, hash único, una licencia y una suscripción vigentes por empresa, activación única por equipo e instalación, huella solo con hashes, FK diferida al regenerar, una clave de firma activa, `pos_app` sin `DELETE`/`TRUNCATE`, solo inserción (eventos, check-ins, auditoría) incluso para el dueño, usuario técnico |
| Pos.Cloud.IntegrationTests *(nuevo)* | 24 | API del POS, límites 429, portal con TOTP, permisos por rol, auditoría y cadena, ciclo de la suscripción en el check-in, rotación de la clave de firma, simulador de punta a punta y reglas de arquitectura de la nube |
| Demás proyectos (POS) | 770 | Sin cambios |
| **Total** | **1.019** | Fase 7: 770 |

Cobertura de líneas (combinada entre proyectos):

| Ensamblado | Cobertura | Mínimo |
|---|---|---|
| Pos.Cloud.Licensing.Domain | 99,2 % | 90 % |
| Pos.Cloud.PortalIdentity.Domain | 100 % | 90 % |
| Pos.Licensing.Contracts | 100 % | 90 % |

## 4. Decisiones y desviaciones respecto de la propuesta (y por qué)

| # | Propuesta | Implementado | Motivo |
|---|---|---|---|
| 1 | Claves de firma `ACTIVE` / `RETIRED` | Se agregaron **`STANDBY`** (reserva publicada, no firma) y **`REVOKED`** (comprometida, deja de ser de confianza) | Publicar la reserva desde el día 1 y distinguir una rotación normal de una filtración (ADR-0038) |
| 2 | Distribuidor que "solo ve sus clientes" | **RESELLER sin permisos ni pantallas**; la prueba "el distribuidor solo ve sus clientes" se reemplaza por **"el distribuidor no accede a la API interna"** | Resolución de la propuesta (solo el equipo del propietario entra al portal); filtrar por cuenta queda para cuando haya distribuidores |
| 3 | Dominio que solo depende del SharedKernel | `Pos.Cloud.Licensing.Domain` depende de `Pos.Licensing.Contracts` (huella y clave), permitido explícitamente en las reglas de arquitectura | La nube y el POS deben interpretar igual la misma huella y la misma clave (ADR-0037) |
| 4 | Pantallas con el contexto del circuito | `PortalOperations`: cada acción de una pantalla interactiva corre en un **ámbito nuevo** | No reutilizar un `DbContext` durante toda la vida del circuito de Blazor |
| 5 | Contraseña temporal | Mientras sea temporal, el portal **obliga a ir a `/mi-cuenta`** (`MustChangePasswordMiddleware` + `Routes.razor`); la API no se redirige | Nadie opera el portal con una contraseña que conoce otra persona |
| 6 | Claves de firma solo por consola | Permiso nuevo **`licensing.signing_key.manage`** (solo superadministrador) para publicar una reserva y revocar desde el portal | Operar la rotación sin entrar al VPS; queda auditado como crítico |
| 7 | Cancelar la suscripción | Cancelar **revoca la licencia vigente** (el siguiente check-in recibe `LICENSE.REVOKED`) | Una empresa cancelada no debe seguir renovando tokens; reanudar exige suscripción y clave nuevas |
| 8 | `GET /v1/licenses/{id}/status` (doc 09) | No existe: el estado viaja en el token de cada check-in; se agregó `GET /v1/public-keys` | El POS solo confía en lo firmado |
| 9 | Imagen base mínima | Se instala **`libgssapi-krb5-2`** en la imagen de ejecución | Npgsql/.NET la buscan al abrir conexiones y, sin ella, registran un error en cada arranque |
| 10 | `VOLUME` para las llaves de Data Protection | **Sin `VOLUME`** en el Dockerfile; el volumen con nombre lo declara el compose | Evitar volúmenes anónimos en `db-init` y en cada comando de consola |
| 11 | Registros por defecto | En el compose, `Microsoft.AspNetCore` y `Microsoft.EntityFrameworkCore` a **Warning** | Registros legibles en producción (sin una línea por petición o consulta) |
| 12 | Ejecutar desde `bin/` | Para probar con `dotnet run` usar **`ASPNETCORE_ENVIRONMENT=Development`** | `wwwroot`, `_content/` y `_framework/` solo se sirven completos al publicar; en `Production` desde `bin/` salen vacíos |
| 13 | Opciones leídas al registrar | `Licensing` y `Portal` se leen **al resolver** los servicios | Se leían antes de aplicar la configuración final del host y el servidor arrancaba sin clave de firma (corregido) |
| 14 | Índices de las FK | 10 FK sin índice en V003/V004 se corrigieron **editando los scripts en su sitio** | Nunca se habían aplicado en ningún ambiente; las pruebas de BD exigen índice en toda FK |

## 5. Limitaciones conocidas y pendiente

1. **Fase 12-B**: el POS todavía no consume la licencia (claves públicas embebidas, activación en el asistente, check-in diario,
   estados `VALID`/`GRACE`/`RESTRICTED`/`DEMO`, reloj atrasado). Hoy lo hace el simulador.
2. **Distribuidores (RESELLER)**: sin permisos ni pantallas; "solo ve sus clientes" queda para una fase posterior.
3. **Respaldo**: `backup.sh` y `restore.sh` solo se validaron con `bash -n` y a mano; `age` no se probó en Windows. Falta probarlos
   en el VPS real (respaldo diario, copia fuera del VPS y una restauración completa).
4. **VPS real**: el despliegue se verificó en local (`docker-compose.local.yml`); falta el DNS, Let's Encrypt y la guía completa en el
   servidor definitivo.
5. **Revocación por consola**: tarda hasta 1 minuto en aplicarse en el servidor en marcha (caché `CachedTrustedSigningKeys`); desde el
   portal es inmediata.
6. `CloudHostSetup` todavía lee `CloudOptions` al registrar (las opciones de los módulos ya se leen al resolver).
7. En Windows, con `Cloud:DataProtectionKeysPath` vacío, las llaves de Data Protection se guardan en el perfil del usuario (el
   comentario del código dice "en memoria"); en el contenedor siempre se usa el volumen.

## 6. Cómo probarlo

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1          # compila, pruebas (Testcontainers con postgres:18) y cobertura → BUILD OK
```

**Despliegue local** (Docker Desktop; detalle en [despliegue-nube.md](../despliegue-nube.md) §13):

```bash
cd deploy/cloud
mkdir -p secrets
dotnet run --project ../../src/Cloud/Pos.Cloud.Host -- generate-signing-key --out "$PWD/secrets/signing-key.pem"
# crear .env.local como indica la guía §13
docker compose -p pos-cloud-local -f docker-compose.yml -f docker-compose.local.yml --env-file .env.local up -d --build --wait
curl -s http://127.0.0.1:8089/health
docker compose -p pos-cloud-local -f docker-compose.yml -f docker-compose.local.yml --env-file .env.local \
  exec app dotnet /app/Pos.Cloud.Host.dll create-superadmin --email admin@example.com --name "Administrador"
```

- **Portal**: `https://licencias.localhost:8443/cuenta/ingresar` (Caddy con certificado interno; la cookie `__Host-` exige HTTPS).
  Entre con la temporal, cámbiela en `/mi-cuenta` y enrole el autenticador con el QR.
- **API**: ejecute `http/fase-12a.http` en orden contra `http://127.0.0.1:8089` (ingreso + TOTP, cuenta, empresa, suscripción, clave,
  activación, check-in, suspender/renovar/reactivar y su reflejo en el check-in, liberar y reactivar, usuarios, auditoría).
- **Simulador**:
  `dotnet run --project tools/Pos.License.Simulator -- activate --server http://127.0.0.1:8089 --key <clave> --nit 900123456-8`,
  luego `checkin`, `status` y `deactivate`.
- **Producción**: siga [despliegue-nube.md](../despliegue-nube.md) (Ubuntu 24.04, DNS, clave activa y reserva, primer
  superadministrador, respaldo diario, rotación y recuperación de emergencia).
