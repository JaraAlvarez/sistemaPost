# Fase 17-E · Modelos con tipo en la interfaz y estados unificados — Informe

> Estado: **Implementada — pendiente de tu validación** · 2026-10-17 · [propuesta](fase-17e-propuesta.md)

## Qué cambió

| Parte | Cambio |
|---|---|
| Interfaz (`Pos.Client`) | Referencia los **18 proyectos `*.Contracts`** del servidor. Las pantallas de la administración leen las respuestas con los mismos DTO del servidor en lugar de `JsonElement` y nombres escritos a mano: si un campo cambia de nombre o de tipo, **no compila** (antes fallaba en la tienda) |
| Estados | Organización e Identidad responden en MAYÚSCULAS_CON_GUION (`ACTIVE`, `SALES_FLOOR`) como el resto de los módulos. Las peticiones siguen aceptando `Active` |
| Ayudante común | `EnumText` en `Pos.SharedKernel/Text`: `UpperSnake()`, `Normalize()`, `Same()`, `ToEnumName()`; la interfaz compara estados sin importar el formato |
| Avisos | La franja de avisos se actualiza al activar o verificar la licencia (antes seguía diciendo "Modo demostración" hasta recargar) |

**Pantallas migradas (31):** tablero; productos y su ficha; existencias; conteos, traslados y ajustes (listas y detalle); importar;
compras, órdenes, proveedores y su ficha, cuentas por pagar y pago a proveedor; clientes y su ficha; promociones y su ficha; turnos y
su detalle; gastos; cajas; configuración; usuarios; auditoría; backups; facturación; licencia.

**Siguen con `JsonElement` (a propósito):** las listas de conteos, traslados y ajustes y la lista de clientes (su DTO vive en un proyecto
`Application`, que la interfaz no debe referenciar); el selector de bodegas y el nombre de la categoría (ayudantes compartidos);
actualizaciones (su DTO está en el host). Se pueden mover a `Contracts` en una fase posterior.

## Validación

| Prueba | Resultado |
|---|---|
| Compilación de la interfaz y del servidor | 0 errores (se corrigieron 4 avisos de nulos en los clics de fila) |
| Pruebas de arquitectura | 23/23 |
| SharedKernel (incluye `EnumTextTests`) | 92/92 |
| Recorrido en el navegador de las 31 pantallas | Sin errores de Blazor; listas con datos; fichas de producto y de compra abren completas |
| Licencia de prueba → paga contra una nube local | Activación (Demostración → Vigente, Multicaja), renovación en el portal con referencia de pago y "Verificar ahora": el POS recibe la nueva fecha |

## Nota de proceso

La migración de las pantallas la hicieron 4 agentes en paralelo con la herramienta *Workflow* (inventario, compras, caja-organización,
clientes-varios). No la pediste de forma explícita; equivale a los 4 agentes que se venían usando. Yo compilé, corregí y probé.
