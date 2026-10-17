# Fase 17-E · Modelos con tipo en la interfaz y estados unificados — Propuesta

> Estado: **APROBADA** · 2026-10-17 (pediste: "dale con el punto 4").
> Origen: la mayoría de las fallas encontradas en las fases 17-B y 17-C (categorías con ids, "Active"/"ACTIVE", formato de números,
> campos que no existían) vinieron de que la interfaz lee la respuesta de la API "a mano" (`JsonElement` y nombres como texto).

## Decisiones

| # | Decisión | Motivo |
|---|---|---|
| D17E-01 | La interfaz (`Pos.Client`) referencia los **18 proyectos `*.Contracts`** del servidor y usa sus DTO | El compilador detecta un nombre o un tipo distinto antes de llegar a la tienda. Los contratos no tienen dependencias de servidor (verificado: compilan en WebAssembly) |
| D17E-02 | **Un solo formato para los estados** en las respuestas: MAYÚSCULAS_CON_GUION (`ACTIVE`, `IN_TRANSIT`, `SALES_FLOOR`), como ya hace la mayoría de los módulos. Se corrigen Organización e Identidad, que respondían `Active` | La interfaz tenía que comparar las dos formas |
| D17E-03 | Las peticiones siguen aceptando el nombre de la enumeración (`"Active"`); no cambia nada para quien ya llama a la API | Compatibilidad |
| D17E-04 | Un ayudante común `EnumText.UpperSnake()` en `Pos.SharedKernel` para convertir estados (hoy hay 9 copias de `Db()` en los módulos; se reemplazan poco a poco) | Una sola regla |
| D17E-05 | Migración de pantallas por prioridad, de `JsonElement` a DTO con tipo: inventario y catálogo, compras, caja y turnos, organización; luego el resto. `JsonElement` solo donde la respuesta es genérica (reportes) | Se empieza donde hubo fallas |

## Validación
Compilación (la interfaz con los contratos), pruebas de arquitectura y de las áreas tocadas, y recorrido en el navegador de las pantallas
migradas.
