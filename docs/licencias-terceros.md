# Inventario de licencias de terceros

Política (ADR-0006): solo licencias **MIT, Apache-2.0, BSD o PostgreSQL**. Toda dependencia nueva se registra aquí **antes** de agregarse a `Directory.Packages.props`.

## Se distribuyen con el producto

| Componente | Versión | Licencia | Uso |
|---|---|---|---|
| .NET Runtime / ASP.NET Core | 10.0 | MIT | Plataforma |
| Microsoft.Extensions.* | 10.0.12 | MIT | DI, logging, hosting, servicio de Windows |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | MIT | Documento OpenAPI (solo desarrollo) |
| FluentValidation (+ DependencyInjectionExtensions) | 12.1.1 | Apache-2.0 | Validación de comandos |
| Serilog.AspNetCore (incluye Sinks.File, Sinks.Console, Formatting.Compact, Settings.Configuration) | 10.0.0 | Apache-2.0 | Logs |
| Scalar.AspNetCore | 2.17.10 | MIT | Interfaz de la documentación de la API (solo desarrollo) |
| PostgreSQL *(desde la Fase 2)* | 18 | PostgreSQL License | Base de datos |

## Solo desarrollo y pruebas (no se distribuyen)

| Componente | Versión | Licencia |
|---|---|---|
| xunit.v3 | 4.0.1 | Apache-2.0 |
| Shouldly | 4.3.0 | BSD-3-Clause |
| coverlet.MTP | 10.1.0 | MIT |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | MIT |

## Descartadas por licencia

| Componente | Motivo |
|---|---|
| MediatR 13+, AutoMapper 15+ | Licencia comercial de pago |
| FluentAssertions 8+ | Licencia comercial de pago |
| MassTransit 9+ | Licencia comercial de pago |
| Microsoft.Testing.Extensions.CodeCoverage | Licencia propietaria de Microsoft |
