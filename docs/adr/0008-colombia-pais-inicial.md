# ADR-0008 · Colombia como país inicial

- **Estado:** Aceptada · 2026-09-28 (aprobada por el propietario del producto)

## Decisión
El primer mercado es **Colombia**. Valores por defecto:

| Aspecto | Valor |
|---|---|
| Moneda | COP, 2 decimales (ISO 4217 / DIAN); efectivo redondeado a $50 |
| Zona horaria | `America/Bogota` (UTC-5, sin horario de verano) |
| Identificación | NIT con dígito de verificación, CC, CE, pasaporte… (tabla parametrizable) |
| Impuestos | IVA (19 %, 5 %, exento, excluido), INC, impuestos saludables, impuesto a la bolsa (parametrizables) |
| Documento fiscal | Documento equivalente electrónico POS y factura electrónica DIAN (Fase 11-B) |
| Datos personales | Ley 1581 de 2012 |

Todo lo específico del país se modela como **datos y configuración** (tablas de impuestos, tipos de identificación, adaptadores fiscales), nunca como condicionales en el código, para poder abrir otros países sin reescribir el sistema.

## Consecuencias
- ⚠️ Riesgo R-01: la normativa DIAN del POS electrónico se verificará con un contador antes de la Fase 7.
