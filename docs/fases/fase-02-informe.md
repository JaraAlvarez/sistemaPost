# Fase 2 · Arquitectura de datos y núcleo organizacional — Informe

> Estado: **IMPLEMENTADA — pendiente de tu validación** · 2026-09-28
> Documentos: [revisión arquitectónica](fase-02-revision-arquitectonica.md) · [propuesta v2](fase-02-propuesta.md)

## 1. Resultado frente a los criterios de aceptación (§22)

| Criterio | Resultado | Evidencia |
|---|---|---|
| `migrate` crea desde cero la BD completa; `verify` confirma checksums | ✅ | `MigrationTests` · `Pos.Server.Migrator migrate / status / verify` contra la BD de desarrollo |
| Script aplicado y modificado bloquea el migrador; script con error deja la BD intacta | ✅ | `MIGRATION.CHECKSUM_MISMATCH`, rollback verificado, BD más nueva rechazada (`MIGRATION.DATABASE_NEWER`) |
| `POST /api/v1/setup` crea todo en una transacción auditada; segundo intento `409 SETUP.ALREADY_COMPLETED` | ✅ | Empresa, sucursal, 3 bodegas, caja, 18 series, 7 roles, usuario `system`, nodo y 16 filas de auditoría. Un NIT con DV incorrecto no deja nada guardado |
| Caja Única rechaza una segunda caja (`409 LICENSE.EDITION_SINGLE_TERMINAL`); Multicaja sin límite | ✅ | `OrganizationApiTests` con las dos ediciones |
| CRUD de sucursales, bodegas y cajas con reglas y errores legibles (ninguna violación llega como 500) | ✅ | 10 creaciones simultáneas del mismo código: 1 × 201 y 9 × `409 ORGANIZATION.BRANCH_CODE_DUPLICATED` (la restricción única decide) |
| Configuración caja → sucursal → empresa → defecto, auditada | ✅ | Ejemplos exactos de la revisión §5.2 (10 → 5 → 15 y eliminación en cascada); alcance no permitido, clave desconocida y valor inválido rechazados |
| Numeración: 1.000 asignaciones concurrentes sin repetidos ni huecos; rollback no consume | ✅ | 20 tareas × 50 números sobre la misma serie = 1…1000 exactos. Recrear una caja con el mismo código continúa su serie |
| Auditoría antes/después; `pos_app` no puede modificar ni borrar; `verify-audit` detecta alteraciones | ✅ | Detecta fila alterada, fila alterada con hash recalculado, fila borrada, fila insertada en un rango sellado y sello alterado; **cero falsos positivos** por transacciones revertidas; el sellador respeta el horizonte seguro |
| Todas las FK indexadas; toda entidad de negocio con `company_id`; todo endpoint con seguridad declarada | ✅ | Pruebas sobre `pg_constraint`, el modelo de EF y los endpoints reales |
| Conformidad modelo EF ↔ esquema real | ✅ | Tabla, columna, familia de tipo y nulabilidad de cada propiedad mapeada |
| `/health/ready` informa la BD y la versión del esquema | ✅ | Estado `Ready` / `SchemaOutdated` / `Unavailable`; negocio responde `503` si no está lista |
| `build.ps1` en verde con cobertura: dominio ≥ 90 %, persistencia ≥ 85 % | ✅ | Ver §3 |
| Docs 04/05/06 actualizados, ADRs nuevos e informe | ✅ | ADR-0010 a 0015; notas de actualización en los docs 04, 05, 06, 08, 09, 10 y 12 |

## 2. Qué se construyó

```
src/Server/Pos.Server.Migrations   Motor SQL-first + scripts incrustados (V2026.10.001–005, 3 repetibles, privilegios)
src/Server/Pos.Server.Migrator     Consola: create-database | migrate | status | verify | verify-audit
src/BuildingBlocks/Pos.Infrastructure
  Persistence/  PosDbContext único, contribuidores de modelo, convenciones (control, borrado lógico, row_version,
                filtro por empresa), interceptor, TransactionBehavior, traducción de restricciones, instalación,
                estado de la BD, enumeraciones ↔ varchar
  Auditing/     JSON canónico RFC 8785, hashes, AuditWriter, AuditSealer (horizonte seguro), AuditVerifier
  Outbox/       Outbox LOCAL/SYNC, OutboxProcessor, InboxStore
  Numbering/    DocumentNumberAllocator, DocumentSeriesProvisioner
  Settings/     Catálogo, caché invalidada tras el commit, lector y escritor auditado
src/Modules/Reference     Catálogos de Colombia (solo lectura, búsqueda sin tildes)
src/Modules/Organization  Asistente inicial, empresa (DV del NIT), sucursales, bodegas, cajas, nodo, configuración
src/Modules/Identity      Estructura RBAC, usuario system, 7 roles de sistema, catálogo de permisos
src/Modules/Audit         Consulta de la bitácora y verificación
docker-compose.dev.yml · tools/scripts/dev-db.ps1 · http/fase-02.http
```

Datos de referencia: **33 departamentos y 1.122 municipios** DIVIPOLA (fuente DANE en datos.gov.co, copia en `tools/seed-data/ref`), 12 tipos de identificación con su código DIAN, responsabilidades fiscales y regímenes.

## 3. Pruebas y cobertura

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| Pos.ArchitectureTests | 20 | Reglas R1–R7 (nueva R7: los contratos no exponen capas internas) sobre 24 ensamblados |
| Pos.Database.Tests | 45 | PostgreSQL 18 real: migraciones, restricciones, privilegios, numeración concurrente, auditoría, configuración, outbox, convenciones del interceptor, concurrencia optimista |
| Pos.Infrastructure.UnitTests | 12 | Despachador, reloj, IDs |
| Pos.Modules.Organization.UnitTests | 31 | DV del NIT con NIT públicos (DIAN, Bancolombia, Éxito), reglas de empresa, sucursal, bodega, caja, nodo, usuario y roles |
| Pos.Server.IntegrationTests | 58 | Las 43 de la Fase 1 (ahora contra BD real) + API de la Fase 2 y conformidad |
| Pos.SharedKernel.UnitTests | 70 | Sin cambios |
| **Total** | **236** | Fase 1: 144 |

Cobertura de líneas (combinada entre proyectos; ver desviación 7):

| Ensamblado | Cobertura | Mínimo |
|---|---|---|
| Pos.Modules.Organization.Domain | 100 % | 90 % ✅ |
| Pos.Modules.Identity.Domain | 100 % | 90 % ✅ |
| Pos.Infrastructure | 95,2 % | 85 % ✅ |
| Pos.Server.Migrations | 93,7 % | 85 % ✅ |
| Pos.SharedKernel | 99,6 % | 95 % ✅ |
| Pos.Server.Host | 89,2 % | — |
| Módulos (Api/Infrastructure) | 87–100 % | — |
| Pos.Modules.Organization.Application | 71,9 % | — (casos de error menos frecuentes; se amplía en la Fase 3) |

## 4. Desviaciones respecto de la propuesta v2 (y por qué)

| # | Propuesta | Implementado | Motivo |
|---|---|---|---|
| 1 | Modo `JOIN_COMPANY` → `501` | `422 SETUP.MODE_NOT_AVAILABLE` | El modelo de errores no tiene "no implementado"; el código estable es lo que importa |
| 2 | `system.document_types.default_prefix` | Eliminado | El prefijo se genera con los códigos de sucursal y caja (P3) |
| 3 | `identity.permissions.requires_feature` | Eliminado | Sin planes por módulos (ADR-0015) |
| 4 | Módulo de configuración propio | Endpoints en Organization, servicios en la infraestructura | Evita un módulo con una sola tabla técnica |
| 5 | Reference y Audit con 5 proyectos | 4 (sin capa Domain) | No tienen reglas de dominio: son lectura |
| 6 | — | Servidor en pie si la BD no responde (reintento cada 10 s, `503 SYSTEM.DATABASE_UNAVAILABLE`) | Un Servicio de Windows no debe caerse en bucle si PostgreSQL arranca después |
| 7 | Cobertura "mejor valor por proyecto" (Fase 1) | Cobertura **combinada** entre proyectos | La persistencia la cubren juntas las pruebas de BD y las de integración; medir por separado subestimaba |
| 8 | — | Clave de caché del modelo de EF por contribuidores | Sin ella, dos composiciones en el mismo proceso compartirían el primer modelo |
| 9 | — | Guardar fuera de una transacción abre una propia | El `seq` de auditoría siempre se reserva dentro de una transacción (horizonte seguro) |

## 5. Pendiente para fases siguientes

1. **Permisos permisivos** hasta la Fase 3 (riesgo aceptado: solo escucha en `localhost`). Todos los endpoints ya declaran su permiso.
2. **Roles de sistema**: al agregar permisos en fases futuras hay que concedérselos a los roles de sistema existentes (sincronización al arrancar, Fase 3).
3. **Dispositivos**: la tabla existe; el emparejamiento de cajas llega en las Fases 3/13.
4. **DPAPI**: el servidor ya lee secretos `dpapi:`; el instalador los escribe en la Fase 13.
5. **Anclas externas** del sello: reporte Z (Fase 6), manifiesto de backup (Fase 11), nube y licencias.
6. **Factus**: validar documento equivalente POS, costo por volumen y venta sin Internet antes de la Fase 7.

## 6. Cómo validarlo

Con Docker Desktop encendido:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1          # compila, 236 pruebas y cobertura → BUILD OK
powershell -ExecutionPolicy Bypass -File .\tools\scripts\dev-db.ps1   # PostgreSQL de desarrollo + migraciones
dotnet run --project src\Server\Pos.Server.Host              # servidor en http://localhost:5480
```

Luego ejecuta en orden las peticiones de `http/fase-02.http` (o usa http://localhost:5480/scalar/v1) y, al final:

```powershell
dotnet run --project src\Server\Pos.Server.Migrator -- verify-audit --connection "Host=127.0.0.1;Port=5488;Database=pos;Username=pos_app;Password=pos-dev-app-password"
```

Sigue pendiente la validación de la Fase 1 como **Servicio de Windows** (`tools/scripts/service-smoke-test.ps1`, como administrador).
