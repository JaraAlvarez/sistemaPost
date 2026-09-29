# ADR-0021 · Códigos de barras únicos y códigos internos sin consecutivo global

- **Estado:** Aceptada · 2026-09-28 · Fase 4 · Decisiones D4-06, D4-07 y D4-12

## Contexto
El escaneo en caja no puede ser ambiguo. Los lectores envían UPC-A con o sin el 0 inicial. Hay productos sin código de
fábrica y tiendas que trabajan sin conexión y crean productos a la vez. Las básculas etiquetan con formatos distintos.

## Decisión
- Un código identifica **una sola cosa** (producto o presentación) en la empresa: índice único sobre `normalized_code`.
- Se valida el **dígito de control** de EAN-13, EAN-8 y UPC-A; UPC-A se normaliza a EAN-13 anteponiendo 0.
- Códigos internos **sin consecutivo global**: EAN-13 `29` + número del nodo (3) + consecutivo del nodo (7) + dígito de
  control; SKU generado `NNN-000000`. El número del nodo (1–999, `org.nodes.number`) lo asigna la autoridad de la empresa
  (el asistente al primer nodo; la nube a los siguientes).
- Reglas de báscula configurables por empresa (prefijos 20–28; el 29 queda para los internos): PLU y peso o precio en
  posiciones fijas. Productos de báscula: por peso, en kilogramos, con PLU único.

## Consecuencias
- ✅ Escaneo determinista; etiquetas de cualquier báscula sin cambiar código.
- ✅ Dos tiendas sin conexión nunca generan el mismo código interno.
- ⚠️ Dos tiendas pueden registrar sin conexión el mismo código de fábrica en productos distintos: la sincronización lo
  detectará y lo pondrá en la bandeja de conflictos para fusionarlos (revisión de la Fase 2, §10.1).
