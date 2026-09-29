# Fase 3 · Autenticación, usuarios, permisos y equipos — Informe

> Estado: **IMPLEMENTADA — pendiente de tu validación** · 2026-09-28
> Documentos: [propuesta aprobada](fase-03-propuesta.md) · ADR [0016](../adr/0016-sesiones-con-tokens-opacos.md) ·
> [0017](../adr/0017-argon2id-con-nsec.md) · [0018](../adr/0018-emparejamiento-y-https-en-la-lan.md)

## 1. Resultado frente a los criterios de aceptación (§15)

| Criterio | Resultado | Evidencia |
|---|---|---|
| Ningún endpoint responde sin sesión (`401`) ni sin permiso (`403`) | ✅ | Pruebas que recorren **todos** los endpoints reales: `Todo_endpoint_protegido_responde_401_sin_sesion` y `…_403_a_un_usuario_sin_ese_permiso`. La verificación ocurre antes de leer el cuerpo (un JSON inválido no da `400` antes que el `401/403`) |
| Asistente con Propietario; `/setup/owner` solo desde el servidor y solo si falta | ✅ | `El_Propietario_se_crea_solo_desde_el_servidor_y_solo_si_falta`; en vivo contra la BD de desarrollo de la Fase 2: `200` y luego `409 SETUP.OWNER_ALREADY_EXISTS` |
| Login de backoffice y de caja; bloqueo al 5.º intento; desbloqueo por tiempo o administrador; mensajes que no revelan si el usuario existe | ✅ | `Cinco_intentos_fallidos_bloquean_y_el_administrador_desbloquea`, `Credenciales_incorrectas_no_revelan_si_el_usuario_existe` (mismo código y hash ficticio para igualar el tiempo), `UserTests` del dominio |
| Sesiones: inactividad, revocación inmediata al desactivar el usuario o revocar el equipo | ✅ | `Salir_y_desactivar_cierran_las_sesiones_de_inmediato`, `Emparejar_una_caja_entrar_con_PIN_y_revocar_el_equipo` |
| Emparejamiento: código de un solo uso con vencimiento; equipo no emparejado rechazado; PIN fuera de una caja rechazado | ✅ | `Un_codigo_de_emparejamiento_sirve_una_sola_vez`, `Desde_la_LAN_solo_equipos_emparejados_y_por_HTTPS`; en vivo: `AUTH.PIN_REQUIRES_TERMINAL` desde el servidor Multicaja |
| Multicaja por HTTPS en la LAN con el certificado de la instalación; Caja Única solo en localhost | ✅ | `Caja_Unica_entra_con_PIN_en_el_propio_equipo_y_no_atiende_la_LAN`; en vivo: `https://…:5443` responde y rechaza equipos sin emparejar; el `5480` no es alcanzable desde la red |
| RN-SEC-01 a RN-SEC-07 (salvo jornadas) | ✅ | Políticas de contraseña/PIN, historial, bloqueo, `ck_authorization_grants__not_self`, `IDENTITY.LAST_ADMINISTRATOR`, `IDENTITY.PRIVILEGE_ESCALATION`, `Nadie_se_modifica_a_si_mismo_ni_deja_la_empresa_sin_administrador` |
| Autorización de supervisor de un solo uso, con vencimiento, sin autorizarse a sí mismo y auditada con ambos usuarios | ✅ | `Autorizacion_de_supervisor_de_un_solo_uso` (la auditoría guarda `authorized_by`) |
| Auditoría con usuario, sesión, equipo e IP reales; `verify-audit` íntegro | ✅ | `La_auditoria_registra_usuario_sesion_y_quien_hizo_cada_cambio`; en vivo: `LOGIN_SUCCEEDED`, `USER_CREATED`, `OWNER_CREATED`, `SYSTEM_ROLE_UPDATED`, verificación íntegra con 2 sellos |
| Argon2id < 500 ms; 20 logins simultáneos sin error | ✅ | `Una_contrasena_se_verifica_en_menos_de_500_ms`, `Veinte_verificaciones_simultaneas_no_fallan`, `Veinte_ingresos_simultaneos_funcionan` |
| `reset-owner` funciona y queda auditado como crítico | ✅ | `La_recuperacion_de_emergencia_asigna_contrasena_temporal_cierra_sesiones_y_audita` |
| `build.ps1` en verde; cobertura de Identity.Domain ≥ 90 % e infraestructura ≥ 85 % | ✅ | Ver §3 |
| Docs 05/06, ADRs e informe | ✅ | ADR-0016 a 0018; docs 05, 06 y licencias actualizados; `http/fase-03.http` |

## 2. Qué se construyó

```
src/Server/Pos.Server.Migrations   V2026.10.006__identity__authentication.sql: empleados, credenciales de usuario
                                   (código de cajero, PIN, versión de seguridad), historial de contraseñas, credencial
                                   de equipos, códigos de emparejamiento, sesiones, intentos de ingreso, autorizaciones
                                   de supervisor; catálogo de permisos con 16 permisos
src/Server/Pos.Server.Migrator     + reset-owner --username (recuperación del Propietario, solo en el servidor)
src/BuildingBlocks
  Pos.SharedKernel                 SecureTokens (tokens de 256 bits, SHA-256, códigos numéricos); marcador [LocalOnly]
  Pos.Application.Abstractions     ISecretHasher, IClientContext, ISupervisorAuthorization, IAuthorizationScope
  Pos.Api.Abstractions             EndpointSecurity.AuthorizeAsync: 401/403 reales, autorización de supervisor
  Pos.Infrastructure               Argon2idSecretHasher (NSec), OwnerEmergencyReset; el interceptor registra sesión,
                                   caja y supervisor en la auditoría
src/Modules/Identity               Usuarios, roles (clonar, editar, borrar), excepciones GRANT/DENY por sucursal,
                                   empleados, login (contraseña y PIN), sesiones, cambio de contraseña/PIN,
                                   autorizaciones de supervisor, sincronización de roles de sistema al arrancar
src/Modules/Organization           Equipos: código de emparejamiento, emparejar, listar, revocar; el asistente crea
                                   el Propietario
src/Server/Pos.Server.Host         Middleware de autenticación (equipo + sesión + permiso), certificado TLS de la
                                   instalación, HTTPS 5443 en Multicaja, limitadores de /auth y /devices/pair
http/fase-03.http
```

## 3. Pruebas y cobertura

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| Pos.ArchitectureTests | 20 | Reglas R1–R7 |
| Pos.Database.Tests | 49 | + restricciones de la migración 006 y recuperación del Propietario |
| Pos.Infrastructure.UnitTests | 24 | + Argon2id (PHC, rehash, tiempo, concurrencia), tokens |
| Pos.Modules.Identity.UnitTests *(nuevo)* | 33 | Permisos efectivos, políticas de contraseña/PIN, bloqueo, roles, empleados |
| Pos.Modules.Organization.UnitTests | 31 | Sin cambios |
| Pos.Server.IntegrationTests | 76 | + 18 escenarios de seguridad de la Fase 3 (autenticación, protección de endpoints, equipos, supervisor, LAN simulada) |
| Pos.SharedKernel.UnitTests | 70 | Sin cambios |
| **Total** | **303** | Fase 2: 236 |

Cobertura de líneas (combinada entre proyectos):

| Ensamblado | Cobertura | Mínimo |
|---|---|---|
| Pos.Modules.Identity.Domain | 99,6 % | 90 % ✅ |
| Pos.Modules.Organization.Domain | 99,5 % | 90 % ✅ |
| Pos.Infrastructure (incluye Argon2id y la recuperación del Propietario) | 95,1 % | 85 % ✅ |
| Pos.Server.Migrations | 93,7 % | 85 % ✅ |
| Pos.SharedKernel | 99,6 % | 95 % ✅ |
| Pos.Api.Abstractions (verificación de permisos) | 100 % | — |
| Pos.Server.Host (middleware de autenticación, TLS) | 91,3 % | — |
| Pos.Modules.Identity.Api / Infrastructure | 93,9 % / 87,8 % | — |
| Pos.Modules.Identity.Application | 76,5 % | — (ramas de error poco frecuentes de la gestión de usuarios y roles) |

## 4. Desviaciones respecto de la propuesta (y por qué)

| # | Propuesta | Implementado | Motivo |
|---|---|---|---|
| 1 | Recuperación del Propietario en el módulo Identity | `OwnerEmergencyReset` en `Pos.Infrastructure`, con SQL directo | El migrador no carga los módulos; la recuperación debe funcionar aunque el servidor no arranque |
| 2 | Verificación de permisos como filtro del endpoint | En el middleware del host, **antes** de leer el cuerpo | Con filtro, un JSON inválido respondía `400` a un anónimo antes que el `401/403` |
| 3 | Cambio de contraseña pendiente bloquea toda sesión | Solo las sesiones de backoffice | Una cajera entra con PIN; el cambio de contraseña lo hace en el backoffice |
| 4 | — | Marcador `[LocalOnly]` en intentos fallidos, bloqueo temporal y último ingreso | Son estado del nodo: no suben `row_version` ni se sincronizarán |
| 5 | Bloqueo por usuario "en el nodo" | Contador y vencimiento locales, pero el estado `LOCKED` vive en `identity.users.status`, que sí se sincroniza | Limitación conocida: se resolverá al diseñar la sincronización (Fase de sincronización) moviendo el bloqueo a una tabla local o excluyéndolo del paquete |
| 6 | — | La autorización de supervisor se consume antes de ejecutar la acción | Si la acción falla después por una regla de negocio, el supervisor debe autorizar de nuevo (más seguro que reutilizarla) |
| 7 | — | Guardado del estado de autenticación con `ExecuteUpdate` | 20 ingresos simultáneos del mismo usuario chocaban por concurrencia optimista |

## 5. Pendiente para fases siguientes

1. **Pantallas** de login, caja y administración de usuarios (Fase 15). Hoy todo es API.
2. **Instalador** (Fase 13): escribir los secretos DPAPI, abrir el puerto 5443 solo en la red privada, emparejamiento asistido.
3. **Jornadas de caja** (Fase 6): completar RN-SEC-07 (no desactivar un usuario con caja abierta) y el bloqueo de pantalla.
4. **Sincronización**: resolver la desviación 5 y el login en el portal web.
5. **Factus**: validar documento equivalente POS, costo por volumen y contingencia sin Internet antes de la Fase 7.
6. **Servicio de Windows** de la Fase 1: sigue pendiente `tools/scripts/service-smoke-test.ps1` como administrador.

## 6. Cómo validarlo

Con Docker Desktop encendido:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1          # compila, 303 pruebas y cobertura → BUILD OK
powershell -ExecutionPolicy Bypass -File .\tools\scripts\dev-db.ps1   # aplica la migración 006
dotnet run --project src\Server\Pos.Server.Host              # http://localhost:5480 y https://…:5443 (Multicaja)
```

Luego ejecuta en orden las peticiones de `http/fase-03.http`. Si una instalación de la Fase 2 no tiene Propietario, el
sistema responde `SETUP.OWNER_REQUIRED` hasta que se cree con la petición 1. Recuperación de emergencia (en el servidor):

```powershell
dotnet run --project src\Server\Pos.Server.Migrator -- reset-owner --username dueno --connection "Host=127.0.0.1;Port=5488;Database=pos;Username=pos_migrator;Password=pos-dev-migrator"
```
