# Inventario de licencias de terceros

Política (ADR-0006): solo licencias **MIT, Apache-2.0, BSD, ISC o PostgreSQL** (ISC agregada el 2026-09-28 por decisión del propietario, Fase 3: es equivalente a MIT/BSD-2). Toda dependencia nueva se registra aquí **antes** de agregarse a `Directory.Packages.props`.

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
| Npgsql | 10.0.3 | PostgreSQL License | Conector de PostgreSQL |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL License | Proveedor de EF Core |
| Microsoft.EntityFrameworkCore.Relational | 10.0.12 | MIT | Persistencia (EF Core) |
| EFCore.NamingConventions | 10.0.1 | Apache-2.0 | Nombres `snake_case` en el mapeo |
| Dapper | 2.1.89 | Apache-2.0 | Lecturas y SQL directo (numeración, sellado, reportes) |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT | Secretos de la instalación con DPAPI |
| System.IO.Ports *(desde la Fase 7)* | 10.0.12 | MIT | Agente de caja: impresora por puerto serie / USB virtual (`COMx`) |
| System.Management *(desde la Fase 12-B)* | 10.0.12 | MIT | Huella del equipo para la licencia (WMI: placa y disco del sistema) |
| System.ServiceProcess.ServiceController *(desde la Fase 13)* | 10.0.12 | MIT | Instalador y actualizador: arrancar y detener los servicios |
| PostgreSQL 18 (binarios para Windows) *(desde la Fase 13)* | 18.x | PostgreSQL License (permisiva) | Se distribuye dentro del instalador, sin modificar |
| Inno Setup 6 *(herramienta, desde la Fase 13)* | 6.x | Licencia de Inno Setup (gratuita, también para uso comercial; confirmar la vigente) | Solo compila el instalador; no se distribuye |
| NSec.Cryptography *(desde la Fase 3)* | 26.4.0 | MIT | Argon2id para contraseñas y PIN |
| libsodium (binario nativo incluido en NSec) | 1.0.22 | ISC | Implementación criptográfica de Argon2id |
| MiniExcel *(desde la Fase 4)* | 1.46.0 | Apache-2.0 | Leer archivos Excel (.xlsx) en las importaciones y exportar reportes (Fase 9) |
| PostgreSQL 18 · `pg_dump` / `pg_restore` *(desde la Fase 11)* | 18 | PostgreSQL | Volcado y restauración de los backups (binarios que instala el instalador, Fase 13) |
| PDFsharp-MigraDoc *(desde la Fase 9)* | 6.2.4 | MIT | Exportar reportes a PDF (tabla con encabezado de la empresa); QuestPDF se descartó por su licencia comercial |
| MudBlazor *(desde la Fase 12-A, solo la nube)* | 9.11.0 | MIT | Componentes del portal web de licencias (Blazor) |
| QRCoder *(desde la Fase 12-A, solo la nube)* | 1.8.0 | MIT | Código QR del enrolamiento del doble factor (TOTP) |
| Datos DIVIPOLA (DANE, datos.gov.co) | 2026-09-28 | Datos abiertos del Gobierno de Colombia | Catálogo de departamentos y municipios |

## Solo desarrollo y pruebas (no se distribuyen)

| Componente | Versión | Licencia |
|---|---|---|
| xunit.v3 | 4.0.1 | Apache-2.0 |
| Shouldly | 4.3.0 | BSD-3-Clause |
| coverlet.MTP | 10.1.0 | MIT |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | MIT |
| Testcontainers.PostgreSql | 4.15.0 | MIT |
| Imagen Docker `postgres:18` (desarrollo y pruebas) | 18 | PostgreSQL License |

## Descartadas por licencia

| Componente | Motivo |
|---|---|
| MediatR 13+, AutoMapper 15+ | Licencia comercial de pago |
| FluentAssertions 8+ | Licencia comercial de pago |
| MassTransit 9+ | Licencia comercial de pago |
| Microsoft.Testing.Extensions.CodeCoverage | Licencia propietaria de Microsoft |
