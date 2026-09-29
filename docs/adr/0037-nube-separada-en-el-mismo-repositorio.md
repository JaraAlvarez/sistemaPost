# ADR-0037 · Nube separada del POS en el mismo repositorio

- **Estado:** Aceptada · 2026-09-29 · Fase 12-A · Decisiones L-01 y L-02 de la [propuesta](../fases/fase-12a-propuesta.md)

## Contexto
El servidor de licencias vive en internet y lo usan el propietario y su equipo (portal) y cada POS instalado (API de activación y
check-in). Será además el núcleo de la futura plataforma en la nube (sincronización y portal del cliente). El POS, en cambio, es
*local-first* (ADR-0001) y nunca depende de la nube para vender. Ambos comparten el contrato del token y las convenciones de código,
migraciones, auditoría y pruebas.

## Decisión
- **Mismo repositorio, aplicación aparte**: todo lo de la nube está bajo `src/Cloud` y se despliega por separado (contenedor, ver
  [despliegue-nube.md](../despliegue-nube.md)):
  - `Pos.Cloud.Host`: ASP.NET Core con la API del POS (`/v1`), la API interna (`/admin`) y el portal Blazor + MudBlazor en un solo
    proceso; consola de operación en el mismo ejecutable.
  - `Pos.Cloud.Abstractions`, `Pos.Cloud.Infrastructure`: roles y permisos del portal, contexto EF propio, transacción por comando,
    permiso en el pipeline, auditoría.
  - `Modules/Licensing/*` y `Modules/PortalIdentity/*`: módulos con las mismas cuatro capas del POS (Domain, Application,
    Infrastructure, Api).
  - `Pos.Cloud.Migrations`: scripts propios (`V2026.10.001` a `004` y `A__system__privileges`).
- **Base de datos propia** (`pos_cloud`, PostgreSQL 18), con los mismos roles que el POS (`pos_owner`, `pos_migrator`, `pos_app` sin
  DDL ni `DELETE`, `pos_backup`). Ninguna tabla es compartida con el POS.
- **Se reutilizan bloques del POS** sin copiarlos: el despachador y los contratos de `src/BuildingBlocks`, el hash de contraseñas
  Argon2id (ADR-0017), la **auditoría encadenada** (`AuditHasher`, `AuditSealer`, `AuditVerifier`, ADR-0012) con la misma estructura
  de `audit_log`/`audit_seals`, y el **migrador SQL-first** de `Pos.Server.Migrations` (ADR-0010) aplicado a los scripts de la nube.
- **Contrato compartido** `src/Shared/Pos.Licensing.Contracts` (solo depende de NSec): clave de licencia, huella del equipo, token,
  claves de firma, DTOs, rutas y códigos de error de la API. Lo usan la nube y, en la Fase 12-B, el POS.
- Nada de `src/Modules`, `src/Server` (salvo el migrador) ni del POS de escritorio entra en la imagen de la nube.

### Desviación consciente
La regla del POS "el dominio solo depende del SharedKernel" tiene una excepción: **`Pos.Cloud.Licensing.Domain` depende de
`Pos.Licensing.Contracts`** para la huella (`DeviceFingerprint`, tolerancia 2 de 3) y la clave (`LicenseKey`, normalización y hash).
Son tipos de valor puros del contrato; duplicarlos en el dominio haría posible que la nube y el POS interpreten distinto la misma
huella o la misma clave. Las reglas de arquitectura de la nube (`tests/Pos.Cloud.IntegrationTests/CloudArchitectureTests.cs`)
permiten esa dependencia de forma explícita solo al dominio de `Licensing` (ningún otro dominio de la nube puede depender de algo
distinto del SharedKernel), y verifican además que la nube solo reutilice los bloques permitidos del POS, que ni el POS ni el
contrato referencien la nube y que el contrato solo dependa de .NET y NSec.

## Consecuencias
- ✅ Un solo lugar para lo que vive en internet; el POS no la necesita para vender.
- ✅ Contratos y convenciones compartidos (token, auditoría, migraciones, pruebas) sin paquetes publicados ni versiones cruzadas.
- ✅ La sincronización y el portal del cliente se agregarán como módulos nuevos de `src/Cloud`.
- ⚠️ Un cambio en `BuildingBlocks` o en el migrador afecta a las dos aplicaciones: la compilación de la solución completa lo detecta.
- ⚠️ El contrato compartido es de cambio muy difícil una vez haya POS instalados (ver ADR-0038, campo `ver`).
