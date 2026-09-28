# Fase 1 · Arquitectura general — Propuesta

> Estado: **PROPUESTA — pendiente de aprobación** · 2026-09-28

## 1. Decisiones ya aprobadas

| # | Decisión | Consecuencia inmediata |
|---|---|---|
| D1 | **País: Colombia** | Moneda COP, zona horaria `America/Bogota`, NIT con DV, impuestos IVA/INC/saludables, documento equivalente electrónico POS (DIAN) en la fase 11-B |
| D2 | **.NET 10 LTS** | SDK fijado en `global.json` |
| D3 | **PostgreSQL** en todos los planes | Versión propuesta: **PostgreSQL 18** (UUID v7 nativo, madura a la fecha). Se usa desde la Fase 2 |

### Decisiones que asumo con mi recomendación (confírmalas al aprobar)

| # | Supuesto | Afecta a la Fase 1 |
|---|---|---|
| D4 | Caja autónoma (sin servidor) diseñada pero **no** en v1 | No |
| D5 | Código y BD en **inglés**; documentación, UI y mensajes en **español** | **Sí** (nombres de proyectos y clases) |
| D6 | Licencia: 7 días de gracia → modo restringido | No |
| D7 | Costo promedio ponderado por bodega | No |
| D12 | Repositorio **git** local desde hoy; remoto privado (GitHub/Azure DevOps) cuando decidas | **Sí** |

## 2. Objetivo de la fase

Dejar la **base técnica sobre la que se construirán todos los módulos**. Esta fase no incluye lógica de supermercado. Al terminar:

- el repositorio y la solución están creados, con convenciones y calidad forzadas por herramientas;
- existe el `SharedKernel` con los tipos que protegen el dinero, las cantidades y los errores en todo el sistema;
- el servidor arranca como aplicación de consola **y** como Servicio de Windows, con logs, health checks, OpenAPI y manejo de errores;
- unas pruebas de arquitectura impiden romper las fronteras entre módulos desde el primer día.

## 3. Alcance

### Incluido

**3.1 Repositorio y calidad**
- `git init`, `.gitignore`, `.gitattributes` (fin de línea), `.editorconfig`.
- `global.json` con .NET SDK 10 fijado.
- `Directory.Build.props`: `Nullable` activado, **advertencias como errores**, analizadores de .NET, `InvariantGlobalization` desactivado (cultura es-CO) y versión del producto centralizada.
- `Directory.Packages.props` con la gestión central de versiones NuGet.
- Script `build.ps1` que compila, prueba y reporta la cobertura. Es la misma entrada que usará el CI cuando exista un repositorio remoto.

**3.2 Solución (`Pos.slnx`) — proyectos de la Fase 1**
```
src/BuildingBlocks/Pos.SharedKernel
src/BuildingBlocks/Pos.Application.Abstractions
src/BuildingBlocks/Pos.Infrastructure
src/Server/Pos.Server.Host
tests/Pos.SharedKernel.UnitTests
tests/Pos.ArchitectureTests
tests/Pos.Server.IntegrationTests
docs/adr/
http/
```
Los módulos de negocio (`Pos.Modules.*`) **no** se crean ahora. Cada uno nace en su fase con la plantilla de 5 proyectos definida en el doc 11.

**3.3 `Pos.SharedKernel`** (dominio puro, sin dependencias externas)

| Tipo | Responsabilidad | Reglas que garantiza |
|---|---|---|
| `Money` | Importe `decimal` + moneda | No suma monedas distintas; sin `double`; redondeo explícito |
| `Currency` | Código ISO y decimales (COP) | — |
| `RoundingPolicy` | Redondeo centralizado (`MidpointRounding.AwayFromZero`, decimales configurables, redondeo de efectivo a múltiplos, p. ej. $50) | RN-GEN-07 |
| `Quantity` | Cantidad `decimal` + unidad; conversión por factor | Cantidades > 0 donde aplique; precisión de 4 decimales |
| `Percentage` | Tasas/porcentajes (19 % = 19.0000) | Rango válido |
| `Result` / `Result<T>` / `Error` | Resultados de operación con **código de error estable** (`SALES.INSUFFICIENT_STOCK`) y tipo (Validación, NoEncontrado, Conflicto, Prohibido, ReglaDeNegocio) | Sin excepciones para flujos de negocio |
| `Entity<TId>`, `AggregateRoot`, `IDomainEvent` | Base de entidades y eventos de dominio | — |
| `IClock` | Hora UTC inyectable y fecha de negocio en zona Bogotá | RN-GEN-08; permite probar vencimientos de licencia |
| `IdGenerator` | UUID v7 (`Guid.CreateVersion7`) | IDs ordenados, aptos para offline |
| `Guard` | Validaciones de invariantes | — |

**3.4 `Pos.Application.Abstractions`**
- `ICommand`, `IQuery`, sus handlers y un **despachador propio y ligero** con *pipeline* de comportamientos (validación, transacción, auditoría y logging). La razón está en la sección 5.
- Interfaces que implementarán fases posteriores: `IUnitOfWork`, `ICurrentUser`, `IAuditWriter`, `IPermissionChecker` y `IFeatureGate` (stub que permite todo hasta la Fase 12).

**3.5 `Pos.Server.Host`**
- ASP.NET Core Minimal APIs, con prefijo `/api/v1`.
- Ejecución dual: consola (desarrollo) y **Servicio de Windows** (`UseWindowsService`).
- Configuración en capas: `appsettings.json` → `C:\ProgramData\PosSupermercado\config\server.json` → variables de entorno.
- **Serilog**: consola + archivo rotativo diario en `ProgramData\...\logs` (retención de 30 días), en formato estructurado y con filtro de datos sensibles.
- Middleware de **Correlation-Id** (se devuelve en la cabecera y se registra en todos los logs de la petición).
- Manejo global de errores → `ProblemDetails` (RFC 9457) con `code` de negocio. Las trazas internas nunca se exponen fuera de desarrollo.
- `GET /health/live`, `GET /health/ready` y `GET /api/v1/system/info` (versión, entorno, hora del servidor UTC/Bogotá, uptime).
- **OpenAPI** nativo de .NET 10 + interfaz **Scalar** (solo en desarrollo) para probar la API sin UI.
- Registro de módulos por convención (`IModule.Register/MapEndpoints`), para que cada fase agregue su módulo sin tocar el host.
- Kestrel en un puerto configurable (propuesto: **5480**), escuchando solo en localhost hasta la fase de instalador (HTTPS en LAN en la Fase 13).

**3.6 Pruebas**
- **Unitarias** del `SharedKernel`: dinero, redondeo (casos colombianos: IVA 19 % sobre precio con IVA incluido, redondeo a $50), cantidades y resultados. Objetivo: **≥ 95 % de cobertura** del SharedKernel.
- **Arquitectura** (ArchUnitNET), reglas activas desde hoy:
  1. `SharedKernel` no depende de nada del proyecto ni de frameworks de infraestructura.
  2. `*.Domain` solo depende de `SharedKernel`.
  3. Un módulo solo referencia `*.Contracts` de otros módulos.
  4. `Api` no referencia `Infrastructure` de otro módulo.
  5. Prohibido `double`/`float` en propiedades públicas de `Domain` y `Contracts`, para proteger el dinero.
- **Integración** del host (`WebApplicationFactory`): arranque, health checks, formato de `ProblemDetails` y Correlation-Id.

**3.7 Documentación**
- ADRs iniciales en `docs/adr/`:
  - 0001 Monolito modular local-first
  - 0002 .NET 10
  - 0003 PostgreSQL 18
  - 0004 UUID v7
  - 0005 Dinero con `decimal` y política de redondeo
  - 0006 Despachador propio (sin MediatR)
  - 0007 Idioma del código
  - 0008 Colombia como país inicial
- `README.md` raíz: requisitos, cómo compilar, probar y ejecutar.
- `http/fase-01.http`: peticiones de prueba.
- Informe de la fase: `docs/fases/fase-01-informe.md`.

### Excluido (llega en fases posteriores)
Base de datos y EF Core (Fase 2), auditoría (Fase 2), autenticación (Fase 3), módulos de negocio, Terminal Agent (Fase 7), instalador (Fase 13) y CI remoto (cuando exista un repositorio remoto).

## 4. Preparación del entorno (necesita tu autorización)

| Herramienta | Para qué | Cómo |
|---|---|---|
| **.NET 10 SDK** | Imprescindible desde la Fase 1 | `winget install Microsoft.DotNet.SDK.10` (lo puedo ejecutar yo si me autorizas) |
| **Docker Desktop encendido** | PostgreSQL de desarrollo y pruebas de integración con BD real (Testcontainers) **desde la Fase 2** | Ya está instalado; solo hay que iniciarlo. En la Fase 1 no es obligatorio |
| PostgreSQL local | No hace falta instalarlo en tu PC: en desarrollo corre en Docker, y en los clientes lo instalará nuestro instalador | — |

## 5. Advertencias arquitectónicas (antes de implementar)

1. **Licencias de librerías de terceros.** Varias librerías populares de .NET pasaron a licencia **comercial de pago** (MediatR, AutoMapper, FluentAssertions v8, MassTransit v9). Como el producto se venderá, propongo **no usarlas**:
   - despachador propio de comandos (unas 150 líneas);
   - mapeos explícitos en lugar de AutoMapper;
   - **Shouldly** en lugar de FluentAssertions.

   Solo se admitirán dependencias con licencia MIT, Apache 2.0, BSD o PostgreSQL. Llevaré un inventario de licencias (`docs/licencias-terceros.md`).
2. **Nombre comercial del producto.** Uso el prefijo neutro `Pos.*` en el código y `PosSupermercado` para carpetas y servicios. El nombre comercial se configura en un solo lugar (`Directory.Build.props`), así que no hace falta decidirlo ahora.
3. **Cultura y formato.** La API trabaja siempre con cultura invariante (números con punto, fechas ISO-8601 en UTC). El formato colombiano (`$ 4.800`, `28/09/2026`) es responsabilidad de la UI y de los tiquetes. Así se evitan errores de decimales entre cajas con distinta configuración regional de Windows.

## 6. Criterios de aceptación

- [ ] `./build.ps1` compila sin advertencias y todas las pruebas pasan.
- [ ] `dotnet run` levanta el servidor, y `/health/ready` y `/api/v1/system/info` responden.
- [ ] El mismo ejecutable se instala y arranca como Servicio de Windows (prueba manual documentada con `sc.exe`).
- [ ] Un error provocado devuelve `ProblemDetails` con código y Correlation-Id, y queda en el archivo de log.
- [ ] Las pruebas de arquitectura fallan si alguien viola una frontera (se demuestra con un caso negativo).
- [ ] La documentación de Scalar/OpenAPI es accesible en desarrollo.
- [ ] ADRs e informe de fase escritos; primer commit en git.
