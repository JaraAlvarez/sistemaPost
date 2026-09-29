# ADR-0012 · Auditoría con sellado por lotes, por nodo, con horizonte seguro y anclas externas

- **Estado:** Aceptada · 2026-09-28 · Fase 2 · Reemplaza la cadena de hash fila a fila del doc 04

## Contexto
Una cadena de hash fila a fila obliga a bloquear el "último hash" en cada transacción: serializaría las ventas de todas las cajas. Además, los huecos de una secuencia por *rollback* y los *commits* fuera de orden producen falsos positivos, y cada nodo (tienda, caja autónoma, nube) necesita su propia cadena. Detalle: [revisión arquitectónica §4](../fases/fase-02-revision-arquitectonica.md).

## Decisión
- Cada fila guarda `row_hash` = SHA-256 del JSON canónico **RFC 8785** de una lista fija de campos (`hash_version`), con instantes truncados a microsegundos y valores como texto. El `seq` se reserva dentro de la transacción de la fila.
- `pos_app` tiene `transaction_timeout = 30s`. El sellador toma el último `seq` entregado (S₀) y, pasados 45 s, sella todas las filas del nodo hasta S₀: `rows_digest` + `seal_hash` encadenado con el anterior (`seal_no` consecutivo por nodo).
- `pos_app` solo tiene `SELECT/INSERT` en `audit`; triggers rechazan `UPDATE/DELETE` incluso a roles con privilegios.
- El verificador (`verify-audit`, `POST /audit/verify`) detecta filas alteradas, borradas o insertadas en rangos sellados y sellos alterados.
- Anclas externas del último sello: reporte Z impreso (Fase 6), manifiesto de backup (Fase 11), nube y servidor de licencias.

## Consecuencias
- ✅ Sin contención entre cajas; sin falsos positivos por *rollbacks*.
- ✅ Una manipulación deja de pasar inadvertida en cuanto existe un ancla posterior.
- ⚠️ Límite honesto: el administrador del equipo puede reescribir la BD; la protección es detectarlo, no impedirlo.
- ⚠️ Los procesos masivos deben trabajar en transacciones cortas (menos de 30 s).
