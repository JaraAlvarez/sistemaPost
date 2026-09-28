# ADR-0006 · Despachador propio y solo dependencias con licencia permisiva

- **Estado:** Aceptada · 2026-09-28

## Contexto
El producto se venderá. Varias librerías populares de .NET pasaron a licencia comercial de pago: MediatR, AutoMapper, FluentAssertions 8 y MassTransit 9. Usarlas crearía costos por cliente o riesgos legales.

## Decisión
- **Despachador propio** (`IDispatcher`, ~100 líneas) para comandos y consultas, con *pipeline* de comportamientos. Hoy: logging y validación; desde la Fase 2: transacción y auditoría.
- Validación con **FluentValidation** (Apache-2.0).
- Mapeos explícitos (sin AutoMapper). Aserciones de pruebas con **Shouldly** (BSD-3).
- Solo se admiten dependencias con licencia **MIT, Apache-2.0, BSD o PostgreSQL**, registradas en [licencias-terceros.md](../licencias-terceros.md) y con versiones centralizadas en `Directory.Packages.props`.

## Consecuencias
- ✅ Sin costos de licencia por cliente; control total del pipeline.
- ⚠️ Mantenemos ~100 líneas propias, cubiertas por `DispatcherTests`.
- ⚠️ Toda dependencia nueva requiere revisar su licencia antes de agregarla.
