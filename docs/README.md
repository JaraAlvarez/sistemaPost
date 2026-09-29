# POS Supermercado — Documentación de arquitectura

> Fase 1 · Entregable "Primera tarea" (puntos A–T) · Estado: **PROPUESTA — pendiente de aprobación** · 2026-09-28

| Doc | Contenido | Puntos |
|---|---|---|
| [01-analisis-producto.md](01-analisis-producto.md) | Análisis del producto, funcionalidades por nivel, problemas habituales de los POS | A, 28 |
| [02-modulos.md](02-modulos.md) | Lista de módulos y funcionalidades de cada uno | B, C |
| [03-arquitectura.md](03-arquitectura.md) | Comparación de tecnologías, stack recomendado, arquitectura general, periféricos | D, E, 17 |
| [04-base-de-datos.md](04-base-de-datos.md) | Principios, modelo ER, tablas, relaciones, índices, estrategia de auditoría | F, G, H, I, 25 |
| [05-reglas-y-estados.md](05-reglas-y-estados.md) | Reglas de negocio codificadas (RN-xxx) y máquinas de estado | J, 23, 24 |
| [06-seguridad-usuarios-permisos.md](06-seguridad-usuarios-permisos.md) | Roles, permisos, autorizaciones, seguridad, auditoría | K, 14, 15 |
| [07-inventario-kardex.md](07-inventario-kardex.md) | Movimientos, costo promedio, conteos, traslados, lotes | L |
| [08-pos-caja-facturacion.md](08-pos-caja-facturacion.md) | Motor POS, pagos combinados, caja, devoluciones, facturación | M, 8, 9, 12 |
| [09-licenciamiento.md](09-licenciamiento.md) | Servidor de licencias, planes, token offline, estados | N, 19 |
| [10-offline-backups-actualizaciones.md](10-offline-backups-actualizaciones.md) | Offline, backups, actualizaciones, instalador | O, P, Q, 20 |
| [11-estructura-proyecto.md](11-estructura-proyecto.md) | Estructura de carpetas y proyectos | R |
| [12-plan-riesgos-decisiones.md](12-plan-riesgos-decisiones.md) | Plan por fases, decisiones pendientes y riesgos | S, T |

## Fases

| Fase | Documentos | Estado |
|---|---|---|
| 1 · Arquitectura general | [propuesta](fases/fase-01-propuesta.md) · [informe](fases/fase-01-informe.md) | Implementada — pendiente de validar el Servicio de Windows |
| 2 · Arquitectura de datos y núcleo organizacional | [revisión](fases/fase-02-revision-arquitectonica.md) · [propuesta v2](fases/fase-02-propuesta.md) · [informe](fases/fase-02-informe.md) | Implementada — pendiente de tu validación |
| 3 · Autenticación, usuarios, permisos y equipos | [propuesta](fases/fase-03-propuesta.md) · [informe](fases/fase-03-informe.md) | Implementada — pendiente de validación |
| 4 · Productos e inventario | [propuesta](fases/fase-04-propuesta.md) · [informe](fases/fase-04-informe.md) | Implementada — pendiente de validación |
| 5 · Terceros, proveedores y compras | [propuesta](fases/fase-05-propuesta.md) · [informe](fases/fase-05-informe.md) | Implementada — pendiente de validación |
| 6 · Caja: jornadas, arqueos y cierres | [propuesta](fases/fase-06-propuesta.md) · [informe](fases/fase-06-informe.md) | Implementada — pendiente de validación |
| 7 · POS y ventas | [propuesta](fases/fase-07-propuesta.md) · [informe](fases/fase-07-informe.md) | Implementada — pendiente de validación |
| 12-A · Servidor y portal web de licencias | [propuesta](fases/fase-12a-propuesta.md) · [informe](fases/fase-12a-informe.md) · [despliegue](despliegue-nube.md) | Implementada — pendiente de validación |

Decisiones arquitectónicas: [ADRs](adr/README.md) · Dependencias: [licencias de terceros](licencias-terceros.md)

## Decisiones clave propuestas (resumen)

- **Stack:** .NET 10 LTS (servidor como Servicio de Windows) + PostgreSQL local + UI web desacoplada (se define en Fase 15).
- **Estilo:** monolito modular, *local-first*, *API-first*. Un esquema de BD por módulo.
- **Topología:** servidor de tienda + cajas en LAN; instalación "todo en uno" para 1 caja.
- **Integridad:** documentos inmutables, snapshots, kardex append-only, `decimal` exacto, UUID v7, idempotencia, secuencias sin huecos.
- **Licencia:** token firmado Ed25519 verificable offline + periodo de gracia; nunca interrumpe una caja abierta.
- **Facturación:** venta ≠ documento fiscal; adaptadores por proveedor; cola asíncrona con contingencia.
