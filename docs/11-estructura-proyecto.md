# 11 · Estructura de carpetas y proyecto (R)

> Estado: **PROPUESTA — pendiente de aprobación** (asume stack .NET; si se elige TypeScript, la estructura modular se conserva)

He ajustado la estructura sugerida (`/core /auth /users ...`) a una organización **por módulos de negocio con capas internas**, porque:
- `auth`, `users` y `roles` son un mismo contexto (`Identity`) y separarlos crea dependencias circulares;
- `pos` y `sales` comparten el mismo modelo de venta (`Sales`);
- `database` no es un módulo de negocio: cada módulo es dueño de su esquema; las migraciones y semillas se orquestan en un proyecto aparte.

```
/                                   ← raíz del repositorio (git)
├── docs/                           ← esta documentación
│   ├── adr/                        ← registros de decisiones arquitectónicas (ADR-0001…)
│   └── ...
├── src/
│   ├── Server/
│   │   ├── Pos.Server.Host/        ← ASP.NET Core: composición, Windows Service, middlewares,
│   │   │                              autenticación, ProblemDetails, OpenAPI, health checks
│   │   └── Pos.Server.Migrator/    ← ejecutable de migraciones + semillas (usado por instalador y updater)
│   │
│   ├── BuildingBlocks/
│   │   ├── Pos.SharedKernel/       ← Money, Quantity, Percentage, Result, Error, IClock,
│   │   │                              IdGenerator (UUID v7), Entity/AggregateRoot, DomainEvent
│   │   ├── Pos.Application.Abstractions/ ← ICommand/IQuery, IUnitOfWork, ICurrentUser,
│   │   │                              IAuditWriter, IFeatureGate, IPermissionChecker
│   │   └── Pos.Infrastructure/     ← EF Core base, interceptores (auditoría, fechas), outbox,
│   │                                  idempotencia, secuencias, Serilog, DPAPI, cifrado
│   │
│   ├── Modules/
│   │   ├── Organization/
│   │   │   ├── Pos.Modules.Organization.Domain/
│   │   │   ├── Pos.Modules.Organization.Application/
│   │   │   ├── Pos.Modules.Organization.Infrastructure/
│   │   │   ├── Pos.Modules.Organization.Api/
│   │   │   └── Pos.Modules.Organization.Contracts/
│   │   ├── Identity/          (misma estructura de 5 proyectos)
│   │   ├── Parties/           (terceros, clientes)
│   │   ├── Catalog/
│   │   ├── Inventory/
│   │   ├── Purchasing/
│   │   ├── Sales/             (POS, devoluciones de clientes, medios de pago)
│   │   ├── Cash/
│   │   ├── Expenses/
│   │   ├── Billing/
│   │   │   └── Pos.Modules.Billing.Providers.*/   ← un proyecto por proveedor fiscal
│   │   ├── Reporting/
│   │   ├── Audit/
│   │   ├── Settings/
│   │   ├── Licensing/         (cliente local)
│   │   └── Backup/
│   │
│   ├── Terminal/
│   │   ├── Pos.Terminal.Agent/          ← Worker Service por caja (WebSocket localhost)
│   │   ├── Pos.Devices.Abstractions/    ← IReceiptPrinter, ICashDrawer, IScale, ICustomerDisplay…
│   │   ├── Pos.Devices.EscPos/          ← driver ESC/POS (USB, serial, red)
│   │   ├── Pos.Devices.WindowsPrinting/ ← spooler / impresora convencional / PDF
│   │   ├── Pos.Devices.Scales/          ← protocolos de básculas
│   │   └── Pos.Devices.Simulators/      ← periféricos simulados para desarrollo y pruebas
│   │
│   ├── Updater/
│   │   └── Pos.Updater.Service/
│   │
│   └── Client/                          ← (Fase 15) UI React + TypeScript y cáscara de escritorio
│
├── licensing-server/                    ← proyecto separado (puede vivir en otro repo)
│   ├── src/Pos.LicenseServer.Api/
│   ├── src/Pos.LicenseServer.Domain/
│   ├── src/Pos.LicenseServer.Infrastructure/
│   └── tests/
│
├── tests/
│   ├── Pos.ArchitectureTests/           ← fronteras entre módulos, capas, endpoints con permiso
│   ├── Pos.Modules.<X>.UnitTests/       ← dominio: reglas RN-xxx, cálculos, estados
│   ├── Pos.IntegrationTests/            ← API + PostgreSQL real (Testcontainers)
│   ├── Pos.ScenarioTests/               ← flujos completos: día de operación de un supermercado
│   └── Pos.PerformanceTests/            ← escaneo, completar venta, reportes con volumen
│
├── installer/
│   ├── wix/                             ← MSI + bootstrapper
│   └── postgres/                        ← script de empaquetado de binarios
│
├── tools/
│   ├── seed-data/                       ← catálogos del país, datos demo
│   └── scripts/                         ← build, firma, publicación
│
├── http/                                ← colecciones .http para probar la API sin UI
├── Directory.Build.props                ← versiones, analizadores, warnings como errores
├── Directory.Packages.props             ← versiones centralizadas de paquetes NuGet
├── global.json                          ← versión del SDK fijada
├── .editorconfig
└── Pos.sln
```

### Estructura interna de un módulo (ejemplo: Inventory)

```
Pos.Modules.Inventory.Domain/
  Movements/StockMovement.cs, MovementType.cs
  Balances/StockBalance.cs, AverageCostCalculator.cs
  Adjustments/InventoryAdjustment.cs, AdjustmentStatus.cs
  Counts/InventoryCount.cs
  Transfers/StockTransfer.cs
  InventoryErrors.cs                     ← errores con código estable (INVENTORY.INSUFFICIENT_STOCK)
Pos.Modules.Inventory.Application/
  Posting/InventoryPostingService.cs     ← implementa IInventoryPosting (Contracts)
  Adjustments/CreateAdjustment/…(Command, Handler, Validator)
  Kardex/GetKardex/…(Query, Handler)
Pos.Modules.Inventory.Infrastructure/
  InventoryDbContext.cs (schema "inventory"), Configurations/, Migrations/
Pos.Modules.Inventory.Api/
  InventoryEndpoints.cs                  ← mapea rutas + permisos + features
Pos.Modules.Inventory.Contracts/
  IInventoryPosting.cs, StockPostingRequest.cs, Events/
```

### Convenciones de desarrollo

- Git con ramas `main` (siempre desplegable) + ramas por funcionalidad; commits convencionales; PR con revisión.
- CI: compilación, analizadores, pruebas de arquitectura, unitarias e integración en cada cambio.
- Toda regla de negocio `RN-xxx` tiene al menos una prueba con su código en el nombre.
- Toda decisión arquitectónica relevante se registra como ADR en `docs/adr/`.
