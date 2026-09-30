# Fase 11-B · Facturación electrónica con Factus — Propuesta

> Estado: **APROBADA** · 2026-09-30 — con todas las recomendaciones de la §14 (construir y dejar apagada; Factus; espera de 3 s; factura en cada venta al encender; documento soporte incluido; prueba en el sandbox con tus credenciales). La migración es la V2026.10.032 (la 030 y la 031 ya existían).
> Requisitos previos: Fases 7 (Billing con comprobante interno y proveedor nulo, ADR-0035), 8 (datos fiscales del cliente en la venta),
> 10 (auditoría) y 11 (backups) implementadas.
> Base: plan [12 §S, fase 11-B](../12-plan-riesgos-decisiones.md), doc [08 facturación](../08-pos-caja-facturacion.md), ADR-0013 (número
> interno ≠ número fiscal: lo asigna Factus), ADR-0035, [revisión arquitectónica de la Fase 2 §10.3](fase-02-revision-arquitectonica.md),
> tus decisiones: **proveedor Factus**; "por ahora no se requiere facturación electrónica, solo el comprobante impreso".

**Convenciones:** 🔒 = decisión difícil de cambiar después (matriz en la §3). ✅ = regla que se cumple. ⚙️ = parámetro configurable.
El signo **§** significa "sección" de un documento.

## 0. Hallazgos que debes conocer antes de decidir (verificados hoy)

| # | Hallazgo | Fuente | Consecuencia |
|---|---|---|---|
| H1 | Desde 2024 el **documento equivalente electrónico POS (DEE POS)** es **obligatorio para toda venta con sistema POS, sin importar el valor** (el tope de 5 UVT ya no aplica). Un tiquete interno impreso **no es válido** como soporte fiscal | DIAN, Resolución 000165 de 2023 y su Abecé; guías 2026 de proveedores | El comprobante interno actual (Fase 7) sirve solo mientras tu contador confirme que la tienda aún no está obligada o para pruebas. **Es el riesgo R-01 que ya estaba anotado** |
| H2 | **Factus no emite el DEE POS**: su respuesta oficial es que para sistemas POS usa la **factura electrónica** (válida ante la DIAN), emitida a **consumidor final** o al cliente identificado | Preguntas frecuentes de developers.factus.com.co (API v2) | Cumplir con Factus = **una factura electrónica por cada venta** |
| H3 | Factus cobra por **paquetes anuales** de documentos (cada factura, nota crédito o documento soporte consume uno; no acumulables) + certificado digital (incluido en el paquete individual o $130.000/año por NIT en bolsas) | Idem | Una tienda con ~2.000 ventas diarias emite **~60.000 facturas al mes (~720.000 al año)**: hay que pedir cotización por volumen **antes** de activar |
| H4 | Factus **asigna el número y el CUFE en línea** (≈2,5 s por factura), **idempotencia por `reference_code`**, token OAuth2 de 60 min con renovación, **80 peticiones por minuto por NIT** (ampliable), **sin webhooks**, sandbox gratuito, activación en ~1–3 días hábiles con Cámara de Comercio, RUT, cédula del representante y rango DIAN | Idem | La venta **nunca espera** a Factus (ADR-0035): la factura se envía en segundo plano; sin Internet queda pendiente y sale al volver la conexión |
| H5 | Varias sucursales bajo un NIT: un **rango de numeración por sucursal** (o por caja) y el objeto `establishment` en cada factura | Idem | Rangos por sucursal/caja en `billing.fiscal_numbering_ranges` |
| H6 | Impuestos múltiples: Factus calcula **cada impuesto por separado con redondeo bancario**; la bolsa plástica va **como un ítem** con su valor | Idem | Hay que conciliar nuestros valores con los de Factus en el sandbox (riesgo de diferencias de $1) |

**Conclusión:** técnicamente la integración está lista para construirse sin tocar la venta, pero **encenderla en producción es una
decisión de negocio y legal** (tu contador + cotización de Factus). Por eso esta fase se construye y se prueba **en el sandbox de
Factus** y queda **apagada** hasta que decidas (pregunta 1).

## 1. Objetivo y alcance

Después de esta fase, cuando la enciendas, **cada venta genera su factura electrónica** (consumidor final o cliente identificado), cada
anulación y cada cambio o garantía su **nota crédito**, todo validado por la DIAN a través de Factus, **sin que la caja espere ni se
detenga sin Internet**; el tiquete muestra el número fiscal, el CUFE y el QR cuando llegan a tiempo, y el cliente recibe la factura por
correo.

Entregable verificable del plan: **documentos aceptados en el ambiente de pruebas** (sandbox de Factus / habilitación DIAN).

| Incluido | Excluido (fase) |
|---|---|
| **Adaptador Factus** (`IFiscalProvider`): autenticación OAuth2, factura electrónica, nota crédito, consulta de estado, descarga PDF/XML | DEE POS con otro proveedor (solo si decides cambiar de proveedor — pregunta 2) |
| **Emisión en segundo plano** desde el outbox con reintentos y **contingencia** sin Internet; idempotencia por `reference_code` | Recepción de facturas de proveedores y eventos RADIAN (posterior) |
| **Rangos de numeración** sincronizados desde Factus y asignados por sucursal/caja; alertas de rango por agotarse o vencer | Nómina electrónica |
| **Mapeo fiscal**: cliente (datos fiscales de la Fase 8) o consumidor final, ítems, impuestos (IVA, INC, bolsa), medios de pago, redondeo del efectivo | Facturación desde la nube (la tienda envía directamente; con la sincronización podrá enrutarse por la nube) |
| **Notas crédito** por anulación, cambio y reintegro por garantía | Personalización visual del PDF (usa la representación de Factus) |
| **Tiquete** con número fiscal, CUFE y QR (si llegan en ⚙️ 3 s) o leyenda de "en proceso" y reimpresión con los datos fiscales | |
| **Documento soporte** para compras a proveedores no obligados a facturar (campo que ya existe en compras — pregunta 5) | |
| **Conciliación**: reporte ventas vs. documentos fiscales (pendientes, rechazados, contingencia), reintento, corrección de datos del cliente | |
| Credenciales cifradas, ambientes sandbox/producción, auditoría de todo | |

Se entrega en **cinco bloques** con una sola aprobación: **11B.1 Adaptador y credenciales**, **11B.2 Rangos y mapeo fiscal**,
**11B.3 Emisión, contingencia y tiquete**, **11B.4 Notas crédito y documento soporte**, **11B.5 Conciliación, alertas y pruebas en
sandbox**.

---

## 2. Actores

| Actor | Qué hace |
|---|---|
| Propietario | Contrata Factus, entrega documentos, **enciende** la facturación, configura credenciales y rangos |
| Contador | Valida el modo (consumidor final por venta), los impuestos y el tratamiento de contingencia |
| Cajero | Nada nuevo: vende igual; si el cliente pide factura a su nombre, lo identifica (Fase 8) |
| Administrador / supervisor | Revisa pendientes y rechazos, corrige datos y reintenta |
| Sistema | Envía, reintenta, consulta estados, emite notas crédito, alerta |

## 3. Decisiones principales

| # | Decisión | Por qué | |
|---|---|---|---|
| D11B-01 | **Modo de emisión** ⚙️ `billing.mode`: `OFF` (hoy, comprobante interno) · `ON_REQUEST` (factura solo si el cliente la pide; el resto comprobante interno — **solo transición**, no cumple H1) · `EVERY_SALE` (factura electrónica en **cada** venta; consumidor final si no hay cliente) — recomendado al encender | Cumplir la norma (H1/H2) sin cambiar la venta; permite empezar gradualmente | 🔒 |
| D11B-02 | **La venta nunca espera a Factus** (se mantiene ADR-0035): el documento fiscal nace `PENDING` en la transacción de la venta y un proceso en segundo plano lo envía; `reference_code` = id de la venta (reintentar nunca duplica) | Una caja no puede depender de Internet ni de un tercero | 🔒 |
| D11B-03 | **Tiquete con datos fiscales si llegan a tiempo:** la caja espera ⚙️ hasta 3 s la validación; si llega, imprime número, CUFE y QR; si no, imprime "Factura electrónica en proceso — se enviará a su correo / reimpresión en caja" y el número interno | Equilibrio entre la fila de la caja y entregar la representación fiscal | |
| D11B-04 | **Contingencia sin Internet:** los documentos quedan `PENDING`/`CONTINGENCY` y se envían en orden al volver la conexión; alerta si hay pendientes de más de ⚙️ 24 h (la DIAN exige transmitir lo antes posible; plazo a validar con el contador) | Operar offline es requisito del producto | 🔒 |
| D11B-05 | **Rangos por sucursal (y opcionalmente por caja)** sincronizados desde Factus (`GET` de rangos) en `billing.fiscal_numbering_ranges`, con vigencia y consecutivo actual; alerta al ⚙️ 90 % de uso o ⚙️ 30 días del vencimiento | Multisucursal bajo un NIT (H5); un rango agotado detiene la facturación | |
| D11B-06 | **Mapeo fiscal desde la venta guardada** (no desde el catálogo actual): ítems con código, nombre, cantidad, precio, descuentos, impuestos por tarifa (IVA 0/5/19, exento vs. excluido según la responsabilidad del emisor), INC y **bolsa como ítem**; medios de pago con los códigos DIAN; el **redondeo del efectivo** como ajuste documentado | La factura refleja exactamente lo cobrado (inmutable desde la venta, D7-01) | 🔒 |
| D11B-07 | **Notas crédito** automáticas: anulación de venta (total, concepto "anulación"), cambio de mercancía y reintegro por garantía (parcial, "devolución"), referenciando la factura aceptada; si la factura aún no fue aceptada, la anulación la cancela antes de enviarla | Cada ajuste queda soportado ante la DIAN | |
| D11B-08 | **Credenciales de Factus** (usuario, clave, client id/secret) cifradas con DPAPI en el servidor; token en memoria renovado con el refresh token; ambiente ⚙️ `SANDBOX`/`PRODUCTION` por empresa; nunca en la BD en claro | Seguridad (mismo criterio que los secretos de los backups) | |
| D11B-09 | **Límite de ritmo** propio: máximo ⚙️ 60 envíos por minuto por NIT (Factus permite 80), envío en paralelo controlado, reintentos con espera creciente | No ser bloqueados en hora pico (H4) | |
| D11B-10 | **Rechazos**: un rechazo DIAN (regla FAK…) deja el documento `REJECTED` con el mensaje; el supervisor corrige los datos del cliente o reintenta; alerta CRÍTICA si hay rechazos sin atender | Un rechazo no se puede quedar callado | |
| D11B-11 | **Permisos**: `billing.document.view` (existe), `billing.document.manage` (existe: reintentar, corregir), nuevo `billing.settings.manage` (sensible: credenciales, modo, rangos) | La activación y las credenciales son del propietario/administrador | |

## 4. Revisión arquitectónica de las decisiones difíciles de cambiar

Impacto en licenciamiento: **ninguno** (facturación electrónica en ambas ediciones; el costo de Factus lo paga cada empresa).

| ID | Decisión | Motivo | Alternativas | Consecuencia positiva | Riesgo | Impacto POS local | Impacto multisucursal | Impacto sincronización | Dificultad de cambiar | Recomendación |
|---|---|---|---|---|---|---|---|---|---|---|
| D11B-01 | Modo `EVERY_SALE` con factura a consumidor final | Factus no tiene DEE POS (H2) y el POS electrónico es obligatorio (H1) | Cambiar a un proveedor con DEE POS; solo facturas a pedido | Cumplimiento con el proveedor elegido | **Costo por volumen** (H3) | Ninguno en la venta | Un rango por sucursal | La nube verá los documentos al sincronizar | Media (el adaptador es intercambiable) | **Adoptar**, activando tras cotización y visto bueno del contador |
| D11B-02 | Emisión asíncrona con `reference_code` | Venta offline y sin esperas | Emitir en línea en la caja | La caja nunca se detiene; sin duplicados | Documentos pendientes si no hay Internet | Cola en el servidor de la tienda | Cada tienda envía lo suyo | En el futuro se puede enviar desde la nube | Alta | **Adoptar** |
| D11B-04 | Contingencia con envío diferido | Sin Internet no hay número ni CUFE (Fase 2 §10.3) | Talonario de contingencia DIAN en papel | Operación continua | **Tratamiento legal a validar con el contador** | Tiquete "en proceso" | — | — | Alta | **Adoptar** y validar |
| D11B-06 | Mapeo desde la venta guardada | Inmutabilidad | Recalcular desde el catálogo | Factura = lo cobrado | Diferencias de redondeo con Factus (H6) | — | — | — | Alta | **Adoptar** y conciliar en sandbox |

## 5. Modelo de datos

| Script | Contenido |
|---|---|
| `V2026.10.030__billing__electronic.sql` | `fiscal_documents`: + `document_type 'SUPPORT_DOCUMENT'`, fuente `PURCHASE`, `reference_code`, `numbering_range_id`, `provider_document_id`, `provider_status`, `rejection_message`, `pdf_url`/`xml` guardados o descargables, `validated_at`. `fiscal_numbering_ranges`: rango de Factus (id, prefijo, desde/hasta, consecutivo actual, resolución, vigencia, tipo de documento) + asignación a sucursal/caja. `provider_settings` (ambiente, modo, credenciales cifradas como bytes, última sincronización). Índices de la cola de envío |
| `R__identity__permissions_catalog.sql` | `billing.settings.manage` |

## 6. Flujos

1. **Venta** (sin cambios en la caja): al completarse, `IBillingService` crea el documento `PENDING` (o `NOT_REQUIRED` en modo `OFF`).
2. **Envío** (segundo plano, orden de llegada, límite de ritmo): arma la factura, la envía a Factus, guarda número, CUFE, QR y estado;
   si falla la red → reintento con espera; si la DIAN rechaza → `REJECTED` + alerta.
3. **Tiquete**: la caja consulta el documento hasta ⚙️ 3 s; imprime con o sin datos fiscales (D11B-03); la reimpresión posterior los trae.
4. **Anulación / cambio / garantía**: nota crédito automática que referencia la factura aceptada (o cancela la pendiente).
5. **Compra a proveedor no obligado** (si apruebas el documento soporte): al contabilizarla se crea el documento soporte pendiente.
6. **Conciliación diaria**: ventas del día vs. documentos aceptados, pendientes y rechazados (reporte del catálogo de la Fase 9).

## 7. Reglas

| Regla | Implementación |
|---|---|
| RN-FE-01 La venta nunca espera al proveedor | Documento `PENDING` + envío en segundo plano |
| RN-FE-02 Sin duplicados | `reference_code` = id del documento de origen (idempotencia de Factus) |
| RN-FE-03 Factura = lo cobrado | Mapeo desde la venta guardada; conciliación de totales antes de enviar |
| RN-FE-04 Todo ajuste con nota crédito | Anulación, cambio y garantía |
| RN-FE-05 Rango vigente | Sin rango vigente para la sucursal → documento `PENDING` + alerta CRÍTICA (la venta sigue) |
| RN-FE-06 Credenciales protegidas | DPAPI; nunca en la BD en claro ni en los registros |
| RN-FE-07 Todo auditado | Activación, cambio de modo/credenciales/rangos, rechazos, reintentos manuales |

## 8. Permisos y roles

| Permiso | Roles de sistema |
|---|---|
| `billing.document.view` (existe) | CASH_SUPERVISOR, ACCOUNTANT, OWNER, ADMIN |
| `billing.document.manage` (existe) | CASH_SUPERVISOR, OWNER, ADMIN |
| `billing.settings.manage` — nuevo, sensible | OWNER, ADMIN |

## 9. Impacto en la sincronización

Ninguno inmediato: cada tienda envía sus documentos desde su servidor. Los documentos fiscales ya llevan tienda y origen: cuando exista
la sincronización, la nube tendrá la vista consolidada y, si se decide, podrá enviar en nombre de tiendas sin Internet.

## 10. Validación de la fase (según tu forma de trabajo)

Compilación sin advertencias y coherencia: pruebas del mapeo (venta → JSON de Factus, con IVA, exento, excluido, INC, bolsa, descuentos,
redondeo del efectivo y varios medios de pago) contra un servidor Factus simulado; prueba corta de la cola (reintento, idempotencia,
rechazo, contingencia) y de las notas crédito. **La prueba en el sandbox real de Factus la hacemos juntos** con tus credenciales de
sandbox (gratuitas): es el entregable del plan.

## 11. Riesgos

| Riesgo | Mitigación |
|---|---|
| **Costo por volumen** (H3) | Cotización antes de activar; el modo `ON_REQUEST` como transición temporal si el contador lo acepta |
| Norma (H1): operar con comprobante interno | Decisión documentada con el contador; la fase deja lista la activación |
| Contingencia prolongada sin Internet | Alerta; envío ordenado al volver; guía de contingencia con el contador |
| Diferencias de redondeo con Factus (H6) | Conciliación en sandbox; ajuste del mapeo |
| Rango agotado o vencido | Alertas al 90 % y a 30 días; la venta sigue y el documento queda pendiente |
| Factus caído | Reintentos; la venta sigue; alerta por pendientes |
| Límite de 80/min en hora pico | Ritmo propio de 60/min; solicitar ampliación a Factus si hace falta |

## 12. Estructura de código

```
src/Modules/Billing/*          Adaptador Factus, rangos, mapeo, cola con ritmo y reintentos, notas crédito, documento soporte,
                               configuración cifrada, conciliación, API
src/Modules/Sales              Tiquete con datos fiscales (espera ⚙️ 3 s) y reimpresión
src/Modules/Purchasing         Documento soporte (si se aprueba)
src/Modules/Reporting          Reporte de conciliación fiscal
src/Server/Pos.Server.Migrations  V030, permisos
http/fase-11b.http · docs/guia-factus.md (activación paso a paso)
```

## 13. Criterios de aceptación

- [ ] Modo `OFF` / `ON_REQUEST` / `EVERY_SALE`; en `OFF` todo sigue como hoy.
- [ ] Factura y nota crédito **aceptadas en el sandbox de Factus** desde ventas, anulaciones, cambios y garantías reales del escenario.
- [ ] Sin Internet: la venta se completa y el documento se envía al volver la conexión, sin duplicados.
- [ ] Rangos sincronizados, asignados por sucursal/caja y con alertas.
- [ ] Tiquete con número, CUFE y QR cuando llegan a tiempo; reimpresión con los datos fiscales.
- [ ] Rechazos visibles, corregibles y auditados; reporte de conciliación.
- [ ] `dotnet build` sin advertencias; docs 08 actualizado; guía de activación; ADRs (modo de emisión; contingencia; mapeo fiscal) e
      informe.

## 14. Preguntas para ti (la opción recomendada va primero)

1. **¿Construimos ahora y dejamos apagado?** Recomendado: **sí** — se construye y se prueba en el sandbox de Factus, queda en `OFF`, y
   lo enciendes cuando tengas la cotización de Factus y el visto bueno de tu contador. ¿O prefieres posponer toda la fase?
2. **Proveedor:** ¿seguimos con **Factus** emitiendo factura electrónica en cada venta (**recomendado**: ya lo elegiste, la API es clara y
   el adaptador queda intercambiable), o quieres que evalúe un proveedor que emita el **documento equivalente POS**?
3. **Tiquete:** ¿la caja espera **hasta 3 s** para imprimir el número fiscal, el CUFE y el QR (**recomendado**), o imprime siempre de
   inmediato con "factura en proceso"?
4. **Modo al encender:** ¿**factura en cada venta** (consumidor final si no hay cliente, **recomendado**, cumple la norma) o solo cuando el
   cliente la pida (transición, **no** cumple la obligación del POS electrónico)?
5. **Documento soporte:** ¿lo incluimos para las compras a proveedores no obligados a facturar (campesinos, informales) (**recomendado**:
   el campo ya existe en compras y Factus lo soporta), o lo dejamos para después?
6. **Credenciales de sandbox:** ¿las solicitas tú a Factus (gratis, con tu correo) para la prueba conjunta del final de la fase
   (**recomendado**), o uso el sandbox público de Factus (sus datos son visibles para otros integradores)?
