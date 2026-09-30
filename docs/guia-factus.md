# Guía de activación de la facturación electrónica con Factus

> Para el **dueño** de la tienda (y su contador). Fase 11-B · [propuesta](fases/fase-11b-propuesta.md) ·
> [informe](fases/fase-11b-informe.md) · ADR [0059](adr/0059-modo-de-emision-y-factura-electronica-por-venta-con-factus.md),
> [0060](adr/0060-emision-asincrona-idempotente-y-contingencia.md), [0061](adr/0061-mapeo-fiscal-desde-la-venta-guardada.md).

BusinessPost trae la facturación electrónica **construida y apagada** (modo `OFF`): mientras no la enciendas, cada venta imprime el
comprobante interno como hasta hoy. Esta guía explica cómo encenderla sin riesgos, en este orden:

| Paso | Qué | Quién | Tiempo |
|---|---|---|---|
| 1 | Contratar Factus y pedir la cotización por volumen | Dueño | 1–3 días hábiles |
| 2 | Entregar los documentos y obtener el rango DIAN | Dueño / Factus | 1–3 días hábiles |
| 3 | Pedir el sandbox gratuito y sus credenciales | Dueño | Mismo día |
| 4 | Configurar BusinessPost para Factus | Técnico / dueño | 10 min |
| 5 | Credenciales y ambiente SANDBOX en Administración → Facturación | Dueño | 5 min |
| 6 | Sincronizar y asignar los rangos | Dueño | 5 min |
| 7 | Probar en el SANDBOX (lista de la §7 y supuestos de la §9) | Dueño + soporte | 1–2 horas |
| 8 | Visto bueno del contador | Contador | — |
| 9 | Pasar a PRODUCTION y modo EVERY_SALE | Dueño | 5 min |

Después: qué hacer con los rechazos (§10), contingencia sin Internet (§11), conciliación diaria (§12) y rango por agotarse (§13).

---

## 1. Contratar Factus y pedir la cotización por volumen

- Factus **no emite el documento equivalente POS**: emite una **factura electrónica por cada venta** (a consumidor final si el cliente no se
  identifica). Cada factura, nota crédito, documento soporte y nota de ajuste **consume un documento** del paquete anual de Factus.
- Calcula tu volumen: ventas diarias × 30. Una tienda con 2.000 ventas diarias emite **~60.000 facturas al mes (~720.000 al año)**, más
  las notas crédito de anulaciones y cambios.
- Pide a Factus una **cotización por volumen** (paquete anual, no acumulable) e incluye el certificado digital. **No enciendas `EVERY_SALE`
  sin tener esa cotización aprobada.**

## 2. Documentos para Factus y rango de numeración DIAN

Factus activa la cuenta en ~1–3 días hábiles con:

- Certificado de Cámara de Comercio (persona jurídica) reciente.
- **RUT** actualizado con la responsabilidad de facturador electrónico.
- Cédula del representante legal.
- **Resolución de numeración de la DIAN** (prefijo, desde, hasta y vigencia) para facturas; Factus te guía para habilitarte como
  facturador y para crear los rangos de notas crédito, documento soporte y notas de ajuste.
- Si tienes varias sucursales: un rango por sucursal (o por caja) y los datos de cada establecimiento (nombre, dirección, teléfono, correo,
  municipio). En BusinessPost llena esos datos en **Administración → Configuración → Sucursales**: si falta alguno, la factura sale sin
  el bloque del establecimiento.

## 3. Sandbox gratuito y credenciales

- Solicita a Factus un **sandbox propio** (gratuito, con tu correo). No uses el sandbox público: sus datos los ven otros integradores.
- Factus te entrega cuatro datos: **usuario** (correo), **contraseña**, **Client ID** y **Client secret**. El sandbox y producción tienen
  credenciales distintas.
- Guárdalas en un lugar seguro (gestor de contraseñas). BusinessPost las guarda **cifradas** en el servidor de la tienda y **nunca las
  vuelve a mostrar**.

## 4. Configurar BusinessPost para Factus (una vez por servidor)

El adaptador del proveedor lo fija la instalación (por defecto `NONE`: sin proveedor). En el **servidor de la tienda**:

1. Abre `{DataRoot}\config\server.json` (por defecto bajo `C:\ProgramData\…\config\`) y agrega:
   ```json
   { "Pos": { "Billing": { "Provider": "FACTUS" } } }
   ```
   (si el archivo ya tiene una sección `Pos`, agrega solo `"Billing": { "Provider": "FACTUS" }` dentro de ella).
2. Reinicia el servicio del servidor de BusinessPost.
3. En **Administración → Facturación → Configuración** debe decir "adaptador activo: FACTUS".

## 5. Credenciales y ambiente SANDBOX

En **Administración → Facturación** (permiso *Configurar la facturación electrónica*: dueño y administrador):

1. Pestaña **Configuración** → *Credenciales de Factus*: escribe usuario, contraseña, Client ID y Client secret → **Guardar credenciales**.
   Para cambiarlas hay que escribir las cuatro de nuevo. Si restauras la base de datos en otro equipo, vuelve a escribirlas (el cifrado
   es del equipo).
2. *Modo de emisión y ambiente*: ambiente **Pruebas (sandbox)**. Deja el modo en **Apagada** hasta sincronizar los rangos.

## 6. Sincronizar y asignar los rangos

1. Pestaña **Rangos de numeración** → **Sincronizar desde Factus**. Aparecen los rangos de factura, nota crédito, documento soporte y nota
   de ajuste con prefijo, desde/hasta, actual, resolución y vigencia. (BusinessPost los vuelve a sincronizar solo cada 24 h.)
2. **Asigna** cada rango a una sucursal (y, si Factus te dio uno por caja, a esa caja). Necesitas uno vigente **por tipo de documento**
   en cada sucursal: factura y nota crédito siempre; documento soporte y nota de ajuste si compras a proveedores no obligados a facturar.
3. Sin rango asignado la venta **no se detiene**, pero el documento queda pendiente con una alerta crítica.

## 7. Probar en el SANDBOX

Con ambiente **sandbox**, pon el modo **Factura electrónica en cada venta** y haz, en una caja de prueba:

- [ ] Una venta a **consumidor final** en efectivo con redondeo (p. ej. $12.330 → $12.350).
- [ ] Una venta a un **cliente con NIT** (persona jurídica) y correo: debe llegar la factura al correo.
- [ ] Productos con **IVA 19 %, 5 %, exento, excluido**, uno con **INC**, y **bolsa plástica**.
- [ ] **Pesables**: 1,235 kg de un producto de fruver y 0,873 kg de carne.
- [ ] Una venta con **descuento** (promoción y manual) y otra con **dos medios de pago** (efectivo + tarjeta).
- [ ] Una venta **a crédito** (fiado).
- [ ] **Anular** una venta ya aceptada (nota crédito de anulación) y otra antes de que salga (la factura queda *cancelada*).
- [ ] Un **cambio de mercancía** y un **reintegro por garantía** (notas crédito parciales).
- [ ] Una **compra a un proveedor no obligado a facturar** (campesino con cédula, registrado como proveedor que **no factura**: la compra
      queda "requiere documento soporte"), contabilizarla y luego **anularla** (nota de ajuste).
- [ ] **Sin Internet**: desconecta el cable, vende 3 veces, reconecta: las 3 facturas salen en orden, sin duplicados.
- [ ] Un **rechazo** a propósito (cliente con NIT inválido) y su corrección (§10).
- [ ] El **tiquete**: con Internet debe decir "Factura electrónica de venta" con número, CUFE y QR; sin Internet, "en proceso", y la
      reimpresión posterior trae los datos.

En la pestaña **Documentos** revisa cada uno (estado *Aceptado*, número, CUFE) y compáralo con el panel del sandbox de Factus.
Anota **toda diferencia de valores**, aunque sea de $1.

## 8. Visto bueno del contador

Muéstrale al contador las facturas del sandbox (PDF de Factus) y el reporte de conciliación (§12) y pídele que confirme por escrito:

- que el modo **factura electrónica a consumidor final por cada venta** es el adecuado para tu tienda;
- los **impuestos** (IVA, INC, bolsa, exentos vs. excluidos) y el tratamiento de los **pesables facturados "por valor"** (§9);
- el tratamiento de la **contingencia** sin Internet y el plazo máximo para transmitir (§11);
- el **documento soporte** de las compras a no obligados.

## 9. Supuestos a verificar en el sandbox real

La integración se probó contra un **Factus simulado** construido con la documentación pública. Estos puntos son **supuestos** y deben
confirmarse en la prueba conjunta en tu sandbox:

| # | Supuesto | Cómo verificarlo |
|---|---|---|
| S1 | **Consumidor final** se acepta como cédula "13", número 222222222222, tributo "ZZ", responsabilidad R-99-PN, persona natural | Venta sin cliente aceptada |
| S2 | Unidades **KGM** (kilo) y **GRM** (gramo) aceptadas; los pesables van en gramos cuando el precio por gramo es exacto | Venta de 1,235 kg aceptada con 1235 GRM |
| S3 | Un ítem facturado **"por valor"** (cantidad 1, unidad 94, cantidad real en la **nota** del ítem) es aceptado y la nota aparece en el PDF | Pesable con precio no exacto |
| S4 | El campo `current` del rango es el **siguiente** número a usar (BusinessPost guarda el último usado) | Comparar "Actual" en la pantalla con el panel de Factus |
| S5 | Reenviar un `reference_code` ya validado responde **200 con el documento existente** (duplicado) y la creación responde **201** | Reintentar un documento aceptado desde la API |
| S6 | Un proveedor con **cédula** se acepta en el documento soporte como **NIT "31"** con el mismo número | Compra a campesino con cédula |
| S7 | Proveedor sin dirección: se acepta **"No informada"** | Idem |
| S8 | Impuestos que Factus no recibe como tarifa (valor fijo por unidad, otros códigos) **sumados a la base** del ítem no generan rechazo y el total cuadra | Producto con impuesto de valor fijo |
| S9 | Crédito (fiado) con forma de pago "2" y **vencimiento a 30 días** | Venta a crédito aceptada |
| S10 | En contingencia la **fecha de la factura es la del envío** (Factus no recibe fecha de emisión), no la de la venta | Venta sin Internet enviada al día siguiente: el contador decide si es aceptable |
| S11 | Un rechazo DIAN llega como **201 con `is_validated` falso** y el mensaje de rechazo, o como **422**; ambos se muestran como *Rechazado* | Cliente con NIT inválido |
| S12 | Las diferencias de centavos del IVA (redondeo bancario de Factus) caben en `cash_rounding_amount` junto con el redondeo del efectivo | Conciliación sin diferencias (§12) |

Si un supuesto falla, anota el mensaje exacto de Factus: el ajuste es en el adaptador (`FactusDraftMapper`), no en la venta.

## 10. Rechazos

Un rechazo (regla DIAN FAK…, o datos inválidos) deja el documento **Rechazado** con el mensaje, genera una **alerta crítica** y aparece
en la pestaña **Alertas y conciliación**. El supervisor de caja, el administrador o el dueño (permiso *Reintentar el envío de documentos
electrónicos*):

1. Abre el documento en la pestaña **Documentos** y lee el mensaje.
2. Si el problema son los datos del cliente (NIT, dígito de verificación, nombre, régimen, correo, municipio): **Corregir comprador** →
   **Guardar y reenviar**. La venta no cambia; la corrección queda en el documento y en la auditoría.
3. Si no: **Reintentar envío** (p. ej. después de arreglar un producto o un rango).
4. Al reenviar, BusinessPost **borra en Factus el documento rechazado con el mismo código** y lo envía de nuevo (Factus no deja emitir
   nada más mientras exista un rechazado).

> ⚠️ **Limitación:** si un documento rechazado se **cancela en el POS** (por ejemplo, se anuló la venta antes de corregirlo), BusinessPost
> ya no lo reenviará y **sigue bloqueando los envíos en Factus hasta que lo borres en el panel de Factus**. Verás pendientes que no salen
> con un mensaje de Factus sobre "un documento pendiente por enviar a la DIAN": entra al panel, elimina ese documento y usa
> **Reintentar envío** o espera al siguiente ciclo.

## 11. Contingencia sin Internet

- La caja **sigue vendiendo**. Los documentos quedan *Pendiente* o *En contingencia* y salen **en orden** al volver la conexión, sin
  duplicados; el tiquete imprime "Factura electrónica en proceso — se enviará a su correo / reimpresión en caja".
- Si hay pendientes de más de **24 h** aparece una alerta. Revisa el Internet del servidor de la tienda.
- El proceso en segundo plano revisa la cola cada 5 s; para un documento puntual usa **Reintentar envío** en la pestaña **Documentos**
  (soporte técnico puede forzar la cola completa con `POST /api/v1/billing/queue/process`, ver `http/fase-11b.http`).
- Si Factus pide esperar (límite de peticiones, HTTP 429), BusinessPost pausa toda la cola el tiempo indicado. El ritmo propio es de
  60 envíos por minuto (ajustable en el parámetro `billing.rate_per_minute`; Factus permite 80 y puede ampliarlo).
- La norma exige transmitir lo antes posible: el plazo y el tratamiento los define tu contador (§8).

## 12. Conciliación diaria

Cada día, al cierre:

1. **Reportes → Conciliación de facturación electrónica** (o la pestaña **Alertas y conciliación**): por día y sucursal, ventas contra
   facturas aceptadas, pendientes, en contingencia, rechazadas, sin documento, canceladas y notas crédito, con el **valor sin factura
   aceptada** y la **diferencia venta − factura**.
2. Objetivo: *Ventas sin factura aceptada* = 0 y *Diferencia* = $0 al día siguiente. Si no: revisar pendientes (Internet, rango) y
   rechazos (§10).
3. Una vez al mes, compara el total del reporte con el panel de Factus y con lo que consume tu paquete.

## 13. Rango por agotarse o por vencer

- La pestaña **Rangos de numeración** muestra el **uso** y los **disponibles**; hay alerta al **90 %** de uso o a **30 días** del
  vencimiento (parámetros `billing.range_alert_percent` y `billing.range_alert_days`).
- Pide a tiempo una **nueva resolución** a la DIAN (con apoyo de Factus), sincroniza y **asigna** el nuevo rango a la sucursal. Solo puede
  haber un rango activo asignado por sucursal/caja y tipo: desasigna el viejo cuando se agote.
- Si un rango se agota sin reemplazo, la venta sigue y las facturas quedan pendientes con alerta crítica hasta asignar uno nuevo.

## 14. Pasar a producción

Con el visto bueno del contador y la cotización aprobada:

1. **Configuración** → escribe las **credenciales de producción** de Factus → Guardar.
2. Ambiente **Producción** y modo **Factura electrónica en cada venta** (marca la confirmación de cotización y contador) → Guardar.
3. **Rangos** → Sincronizar → asignar los rangos de producción a cada sucursal/caja.
4. Haz una venta real pequeña y verifica que quede *Aceptada* y que el tiquete traiga número, CUFE y QR.
5. Los primeros días, revisa la conciliación (§12) cada cierre.

Para **apagar** (volver al comprobante interno): modo **Apagada**. Los documentos ya enviados no cambian.
