# ADR-0057 · Subida tienda → nube por cursores con acuse, autenticada con la licencia

- **Estado:** Aceptada · 2026-09-30 · Fase 16 · Decisiones D16-01, D16-02, D16-03, D16-05 y D16-07 de la [propuesta](../fases/fase-16-propuesta.md)

## Decisión
1. **Fuentes con cursor** (módulo `Sync` de la tienda): ventas completadas y anuladas (con líneas y pagos), cierres de caja, saldos de
   inventario que cambiaron y productos con su precio. Cada tipo se lee de sus tablas ordenado por (marca de tiempo del último cambio, id),
   con un **margen de ⚙️ 60 s** (`Pos:Sync:SafetyLagSeconds`) para no saltar filas de transacciones que aún no confirman. El JSON lo arma
   PostgreSQL. No se agregan eventos a los módulos: los documentos ya son inmutables (ADR-0014).
2. **El cursor en línea avanza solo con el acuse** de la nube (`sync.cursors.acked_*`). Un envío fallido queda en `sync.batches` como
   `FAILED` y se reintenta en el siguiente ciclo (⚙️ cada 60 s, lotes de 500).
3. **Autenticación:** `Authorization: License <token>` + `X-Pos-Fingerprint`. La nube verifica el token con sus claves, que la licencia y la
   instalación estén activas y que la huella corresponda al equipo activado (misma tolerancia de 2 de 3 componentes). No hay credenciales
   nuevas; una instalación liberada deja de poder subir.
4. **Nube:** esquema `sync`, `documents` (instalación, tipo, id) con JSONB y su versión; el lote se aplica con un solo `INSERT … ON CONFLICT
   … WHERE version < EXCLUDED.version` (gana el más reciente) y `sync.batches` guarda el id de cada lote: **repetir un lote no lo reaplica**
   (`duplicate`). Máximo 5.000 datos por lote.
5. **Existencias:** instantánea de los saldos, solo para consultar; la verdad sigue en la tienda (ADR-0014).

## Consecuencias
- Nada se pierde si falla un envío; algo puede llegar dos veces y la nube lo ignora.
- La edición de maestros en el portal que baja a las tiendas (bidireccional, conflictos) queda para la Fase 18.
- Si el volumen crece, las consultas del portal se pasan a tablas proyectadas sin cambiar el contrato.
