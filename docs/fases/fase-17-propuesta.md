# Fase 17 · Backoffice completo y preparación del piloto — Propuesta

> Estado: **APROBADA POR ANTICIPADO** · 2026-09-30 (pediste ejecutar 15, 16 y 17 sin confirmación; se aplican estas recomendaciones).
> Base: Fase 15 (interfaz, D15-01…), API de las fases 2–11 y 16.

## 0. Objetivo y alcance

Después de esta fase **todo lo que un supermercado hace en el día a día se hace en pantalla**, sin usar la API a mano, y hay una guía
para montar el **piloto** en una tienda real.

| Incluido | Excluido (fase) |
|---|---|
| **Proveedores**: lista, crear, editar, activar/inactivar, estado de cuenta | Cuentas bancarias y retenciones del proveedor en pantalla (API; v1.1) |
| **Compras**: facturas de compra (crear con líneas, lotes y vencimientos, contabilizar, anular) y órdenes de compra (crear, aprobar, enviar, cerrar) | Devoluciones a proveedor en pantalla (API; v1.1) |
| **Cuentas por pagar**: saldos, vencidas por edades, registrar y anular pagos | |
| **Clientes**: buscar, crear, editar, ficha (resumen e historial), bloquear, grupos | Solicitudes de datos personales y políticas de privacidad en pantalla (API; v1.1) |
| **Promociones**: lista, crear, editar, activar, pausar, terminar, simular | Reporte de promociones (ya está en Reportes) |
| **Inventario**: conteos (crear, iniciar, registrar, revisar, aprobar), traslados (crear, despachar, recibir), ajustes (crear, aprobar, aplicar) | |
| **Gastos**: registrar, anular, categorías | |
| **Jornadas de caja**: lista, detalle con reportes X/Z, revisión, cierre por supervisor | |
| **Configuración**: empresa, sucursales, bodegas, medios de pago, parámetros | Editor de roles (la API lo tiene; los roles de fábrica bastan en el piloto) |
| **Cajas e impresoras**: cajas, impresora de tiquetes, equipos emparejados (revocar) y **código de emparejamiento** | |
| **Guía del piloto** y lista de chequeo (`docs/guia-piloto.md`) | |

## 1. Decisiones

| # | Decisión | Recomendación y motivo |
|---|---|---|
| D17-01 | Forma de las pantallas | La misma de la Fase 15: página con lista y acciones, formularios en panel, JSON de la API sin copiar reglas de negocio. Lo que la API rechaza se muestra con su mensaje en español |
| D17-02 | Permisos | Cada menú y botón aparece solo con su permiso (el backend los exige igual) |
| D17-03 | Alcance v1 | Las operaciones de todos los días en pantalla; lo ocasional y avanzado (bancos y retenciones de proveedores, devoluciones, privacidad, roles a medida) sigue por la API y entra en v1.1 según lo que pida el piloto |
| D17-04 | Menú | Agrupado: Ventas y caja · Inventario · Compras y proveedores · Clientes y promociones · Administración |
| D17-05 | Piloto | Guía paso a paso (instalar, configurar, cargar productos, capacitar, primer día, cierre, qué vigilar la primera semana) con lista de chequeo imprimible y criterios de salida |

## 2. Validación
Compilación; la interfaz se sirve en las rutas nuevas; recorrido corto en el navegador de compras → existencias y de un conteo. Tú: el
recorrido completo con datos reales antes del piloto (guía §5).
