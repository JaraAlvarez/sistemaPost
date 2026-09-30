# ADR-0059 · Modo de emisión y factura electrónica por venta con Factus

- **Estado:** Aceptada · 2026-09-30 · Fase 11-B · Decisiones D11B-01, D11B-08 y D11B-11 de la [propuesta](../fases/fase-11b-propuesta.md)
  (preguntas 1, 2 y 4 del propietario: construir y dejar apagada; Factus; factura en cada venta al encender)
- **Complementa:** [ADR-0035](0035-billing-comprobante-interno-y-proveedor-fiscal-nulo.md) (comprobante interno y proveedor nulo) y
  [ADR-0013](0013-numeracion-interna-vs-fiscal.md) (el número fiscal lo asigna el proveedor)

## Contexto
- Desde 2024 el **documento equivalente electrónico POS (DEE POS)** es obligatorio para toda venta con sistema POS (Resolución DIAN
  000165 de 2023): el tiquete interno impreso no es soporte fiscal válido (riesgo R-01).
- El proveedor elegido, **Factus**, **no emite el DEE POS**: para sistemas POS emite la **factura electrónica de venta**, a consumidor final
  o al cliente identificado. Cobra por paquetes anuales de documentos: una tienda con ~2.000 ventas diarias emite ~60.000 facturas al mes.
- El propietario aún no está obligado a facturar según su criterio actual; encender la facturación es una decisión de negocio y legal
  (cotización de Factus y visto bueno del contador).

## Decisión
1. **Modo de emisión por empresa** (`billing.provider_settings.mode`, migración V2026.10.032):
   - `OFF` (por defecto): todo sigue como en la Fase 7: comprobante interno `NOT_REQUIRED` y tiquete "No es factura".
   - `ON_REQUEST`: factura electrónica solo en las ventas marcadas "pide factura" (`PUT /sales/{id}/customer` con `invoiceRequested`);
     el resto, comprobante interno. **Solo transición**: no cumple la obligación del POS electrónico.
   - `EVERY_SALE` (recomendado al encender): factura electrónica en **cada** venta; sin cliente identificado va a **consumidor final**.
   La antigua clave `billing.electronic_enabled` solo cuenta mientras no exista la configuración del proveedor (equivale a `EVERY_SALE`).
2. **Documento por venta = factura electrónica** (`INVOICE_ELECTRONIC`); las anulaciones, cambios y reintegros por garantía emiten **nota
   crédito** (`CREDIT_NOTE`) que referencia la factura aceptada. El tipo `POS_ELECTRONIC` queda reservado para un adaptador que emita
   DEE POS; cambiar de proveedor es escribir otro adaptador de `IFiscalProvider`.
3. **Adaptador elegido por configuración del servidor** (`Pos:Billing:Provider`): `NONE` por defecto (proveedor nulo), `FACTUS` (API v2) o
   `FAKE` (simulado, rechazado en producción). El modo lo cambia el propietario desde la pantalla; el adaptador lo fija la instalación.
4. **Ambiente por empresa** `SANDBOX` / `PRODUCTION` y **credenciales** (usuario, contraseña, client id y client secret) cifradas con
   **DPAPI** del equipo servidor como bytes en `provider_settings.credentials`; nunca se devuelven por la API, no entran en la auditoría ni
   en los registros (`ToString` las oculta). Encender (modo ≠ `OFF`) exige credenciales (regla en el dominio y CHECK en la BD); borrarlas
   apaga la facturación. Restaurar la BD en otro equipo obliga a volver a escribirlas.
5. **Permisos:** `billing.settings.manage` (nuevo, sensible: modo, ambiente, credenciales, rangos) para OWNER y ADMIN;
   `billing.document.view` y `billing.document.manage` (reintentar, corregir el comprador, enviar la cola) también para CASH_SUPERVISOR.
6. La pantalla **Administración → Facturación** (`/admin/facturacion`) exige confirmar "tengo la cotización de Factus por volumen y el
   visto bueno de mi contador" antes de guardar `EVERY_SALE`, y advierte que `ON_REQUEST` no cumple la norma.

## Consecuencias
- ✅ Encender o apagar la facturación no toca la venta, la caja ni el inventario; en `OFF` el comportamiento es idéntico al de la Fase 7.
- ✅ El adaptador es intercambiable (modelo fiscal neutro, [ADR-0061](0061-mapeo-fiscal-desde-la-venta-guardada.md)).
- ⚠️ **Costo por volumen**: cada venta consume un documento del paquete de Factus; hay que cotizar antes de activar `EVERY_SALE`.
- ⚠️ Factura electrónica ≠ DEE POS: el tratamiento (consumidor final por venta) debe validarlo el contador.
- ⚠️ La facturación queda **construida y apagada** hasta la prueba conjunta en el sandbox real de Factus con las credenciales del dueño.
