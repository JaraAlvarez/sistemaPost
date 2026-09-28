# PosSupermercado

Sistema POS comercial para supermercados y comercio minorista: local-first, instalable en Windows y licenciado por suscripción.

> Estado: **Fase 1 — Arquitectura general** completada (pendiente de validación). Ver [plan de fases](docs/12-plan-riesgos-decisiones.md).

## Requisitos de desarrollo

| Herramienta | Versión | Desde |
|---|---|---|
| .NET SDK | 10.0.401 (fijado en `global.json`) | Fase 1 |
| Git | 2.4x+ | Fase 1 |
| Docker Desktop (en ejecución) | reciente | Fase 2 (PostgreSQL de desarrollo y pruebas) |

## Comandos

Compilar, probar y verificar la cobertura (lo mismo que ejecutará el CI):

```bash
powershell -ExecutionPolicy Bypass -File ./build.ps1
```

Ejecutar el servidor en desarrollo (http://localhost:5480, documentación de la API en `/scalar/v1`):

```bash
dotnet run --project src/Server/Pos.Server.Host
```

Solo las pruebas:

```bash
dotnet test --solution Pos.slnx
```

Publicar el servidor autocontenido (`artifacts/publish/server`):

```bash
powershell -ExecutionPolicy Bypass -File ./tools/scripts/publish-server.ps1
```

Probar la instalación como Servicio de Windows (consola **como administrador**):

```bash
powershell -ExecutionPolicy Bypass -File ./tools/scripts/service-smoke-test.ps1
```

Las peticiones de ejemplo para probar la API sin interfaz están en [`http/`](http/).

## Estructura

```
docs/                 Arquitectura (01–12), ADRs, informes de fase, licencias de terceros
src/BuildingBlocks/   SharedKernel, Application.Abstractions, Api.Abstractions, Infrastructure
src/Server/           Pos.Server.Host — API local (consola o Servicio de Windows)
src/Modules/          Módulos de negocio (desde la Fase 2)
tests/                Unitarias, arquitectura e integración
tools/scripts/        Publicación y pruebas del servicio
http/                 Peticiones de prueba de la API
```

## Documentación

- [Índice de arquitectura](docs/README.md)
- [Decisiones arquitectónicas (ADR)](docs/adr/README.md)
- [Informe de la Fase 1](docs/fases/fase-01-informe.md)
- [Licencias de terceros](docs/licencias-terceros.md)
