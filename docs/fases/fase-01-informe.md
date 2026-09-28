# Fase 1 · Arquitectura general — Informe

> Estado: **IMPLEMENTADA — pendiente de validación** · 2026-09-28 · Propuesta: [fase-01-propuesta.md](fase-01-propuesta.md)

## 1. Resultado

| Criterio de aceptación | Resultado | Evidencia |
|---|---|---|
| `build.ps1` compila sin advertencias y todas las pruebas pasan | ✅ | 0 advertencias (tratadas como errores), **144 pruebas en verde** |
| El servidor arranca y responde `/health/ready` y `/api/v1/system/info` | ✅ | Pruebas de integración + ejecución manual y del binario publicado |
| El mismo ejecutable corre como Servicio de Windows | ⚠️ **Pendiente de tu ejecución** | La sesión de desarrollo no tiene permisos de administrador. Script listo: `tools/scripts/service-smoke-test.ps1` (ver §5) |
| Un error provocado devuelve ProblemDetails con código y Correlation-Id y queda en el log | ✅ | `ErrorHandlingTests` (incluye lectura del archivo de log) |
| Las pruebas de arquitectura fallan si se viola una frontera (caso negativo) | ✅ | `ArchitectureRulesNegativeTests`: cada regla R1–R6 tiene su caso negativo |
| Documentación OpenAPI/Scalar accesible en desarrollo (oculta en producción) | ✅ | `OpenAPI_documenta_la_API_en_desarrollo` + verificación del binario en Producción (404) |
| ADRs e informe escritos; primer commit en git | ✅ | `docs/adr/0001–0009`, este informe, commit inicial |

### Cobertura de líneas

| Ensamblado | Cobertura | Mínimo |
|---|---|---|
| Pos.SharedKernel | **99,6 %** | 95 % ✅ |
| Pos.Api.Abstractions | 100 % | — |
| Pos.Server.Host | 95,3 % | — |
| Pos.Infrastructure | 94,2 % | — |
| Pos.Application.Abstractions | 0 % (solo interfaces y un record, sin lógica) | — |

### Pruebas por proyecto

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| Pos.SharedKernel.UnitTests | 70 | Dinero, redondeo colombiano, IVA incluido, prorrateo, cantidades, porcentajes, resultados, códigos de error, entidades, reloj de Bogotá |
| Pos.Infrastructure.UnitTests | 12 | Despachador, pipeline (orden, validación), UUID v7, reloj, feature gate |
| Pos.ArchitectureTests | 19 | Reglas R1–R6 sobre los ensamblados reales + casos negativos |
| Pos.Server.IntegrationTests | 43 | Health, system info, correlación, errores, validación HTTP, logs, enmascarado de datos sensibles, mapeo Result → HTTP |

## 2. Qué se construyó

```
Pos.slnx
├── src/BuildingBlocks
│   ├── Pos.SharedKernel               Money, Currency, RoundingPolicy, Percentage, Quantity,
│   │                                  Result/Error, Entity, AggregateRoot, DomainEvent, DomainException,
│   │                                  IClock (+ zona Bogotá), IIdGenerator, Guard
│   ├── Pos.Application.Abstractions   ICommand/IQuery/handlers, IDispatcher, IPipelineBehavior,
│   │                                  IUnitOfWork, ICurrentUser, IPermissionChecker, IFeatureGate, IAuditWriter
│   ├── Pos.Api.Abstractions           IModule, traducción Result → HTTP (ProblemDetails)
│   └── Pos.Infrastructure             Dispatcher, LoggingBehavior, ValidationBehavior, SystemClock,
│                                      UuidV7IdGenerator, AllowAllFeatureGate, registro de handlers
├── src/Server/Pos.Server.Host         Servicio de Windows/consola, configuración en capas, Serilog,
│                                      Correlation-Id, manejo global de errores, health checks,
│                                      /api/v1/system/info, OpenAPI + Scalar (dev), diagnóstico (dev)
└── tests/ (4 proyectos)
```

### Comportamientos clave verificados

- **Redondeo colombiano:** precio de $4.800 con IVA 19 % incluido → base $4.033,61 + IVA $766,39 = $4.800,00 exactos. Efectivo redondeado a múltiplos de $50.
- **Prorrateo sin perder centavos:** $100 entre 3 líneas → 33,34 + 33,33 + 33,33.
- **Errores con código estable:** formato `MODULO.DESCRIPCION` validado en tiempo de ejecución. La API responde RFC 9457 con `code`, `correlationId` y `errors` por campo.
- **En producción:** no se exponen trazas internas, la documentación y los endpoints de diagnóstico no existen, y el servidor solo escucha en `localhost`.
- **Logs:** archivo JSON diario en `{DataRoot}\logs`, retención de 30 días y enmascarado de propiedades sensibles (contraseña, PIN, token…).

## 3. Desviaciones respecto de la propuesta (y por qué)

| Propuesta | Implementado | Motivo |
|---|---|---|
| — | Proyecto adicional **`Pos.Api.Abstractions`** | `IModule` y la traducción `Result → HTTP` dependen de ASP.NET Core; así no contaminan `Application.Abstractions` y los módulos no dependen del host. |
| — | Proyecto adicional **`Pos.Infrastructure.UnitTests`** | El despachador y el reloj merecen pruebas propias, separadas del SharedKernel. |
| ArchUnitNET para pruebas de arquitectura | Reglas propias por reflexión | Sin dependencia extra y con casos negativos probables. Ver ADR-0009. |
| Registro de módulos "por convención" | **Lista explícita** `ModuleCatalog` | Visible y ordenada; no carga código por reflexión. Ver ADR-0001. |
| Pipeline "validación, transacción, auditoría, logging" | Logging + validación hoy | Transacción y auditoría requieren la BD: se agregan en la Fase 2 (las interfaces ya existen). |
| xUnit con VSTest | xUnit v3 sobre **Microsoft Testing Platform** + coverlet.MTP | El SDK de .NET 10 ya no admite VSTest con xUnit v3 4.x. Ver ADR-0009. |
| Validación con reglas `FluentValidation` | ✅ igual | Se añadió al inventario de licencias (Apache-2.0). |

## 4. Observaciones para fases siguientes

1. **Tamaño de la publicación:** 109 MB autocontenida (369 archivos). Se optimizará en la Fase 13 (recorte, ReadyToRun, compresión del instalador).
2. **Versión informativa:** cuando haya commits, .NET añade el hash de git a la versión (`0.1.0+abc123`), útil para soporte.
3. **Docker:** la Fase 2 necesita Docker Desktop encendido para PostgreSQL y Testcontainers.
4. **CI remoto:** `build.ps1` está listo para GitHub Actions o Azure DevOps cuando exista un repositorio remoto.

## 5. Prueba pendiente: Servicio de Windows

Abre **PowerShell como administrador** en la carpeta del proyecto y ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\scripts\service-smoke-test.ps1
```

El script publica el servidor e instala un servicio temporal (`PosSupermercado-Server-SmokeTest`, puerto 5481). Luego verifica que:
- detecta que corre como servicio;
- está en entorno de Producción;
- el health check responde;
- se genera el log.

Al terminar desinstala el servicio y borra sus datos, incluso si la verificación falla. Debe terminar con `PRUEBA DEL SERVICIO: OK`.
