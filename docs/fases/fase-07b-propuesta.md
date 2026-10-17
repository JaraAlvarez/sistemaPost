# Fase 7-B · Cobrar con Sistecrédito en la caja — Propuesta

> Estado: **APROBADA** · 2026-10-17 ("lo de Sistecrédito sí me gustaría implementarlo de una vez").
> Origen: en un comercio (Toto) el cajero cobró con Sistecrédito: con la cédula del cliente generó un cobro, el cliente eligió las
> cuotas y confirmó. Base técnica: la integración de tutiendanueva (`backend/src/services/sistecredito.service.js`, API
> `https://api.credinet.co/pay`, guías de Sistecrédito G-ALI-12 y G-ALI-09), ya probada en su tienda en línea.
> Addi y la sincronización con tutiendanueva quedan como **plan** (§6).

## 1. Cómo se ve en la caja

1. El cajero elige el medio **Sistecrédito** e ingresa el **tipo y número de documento** del cliente.
2. BusinessPost crea el cobro en Sistecrédito por el valor (o la parte del pago mixto) y muestra un **código QR** y el enlace.
3. El cliente lo abre en su celular, **elige el número de cuotas** y confirma con el código que Sistecrédito le envía.
4. La caja consulta el estado cada pocos segundos: **Aprobado** → la venta se completa (referencia = id de la transacción de
   Sistecrédito, cuotas si Sistecrédito las informa); **Rechazado / Vencido / Cancelado** → aviso claro y se elige otro medio.
5. Botón **Cancelar** mientras espera; si el cliente ya pagó en ese momento, la revisión automática lo detecta (§2, D7B-05).

## 2. Decisiones

| # | Decisión | Motivo |
|---|---|---|
| D7B-01 | Se usa la **misma API** que tutiendanueva (`/pay/create`, `/pay/GetTransactionResponse`, cabeceras `SCOrigen`, `Ocp-Apim-Subscription-Key`, `ApplicationKey`, `ApplicationToken`, método de pago 2) | Probada; el cliente completa en su celular (cuotas + OTP) |
| D7B-02 | **Credenciales por tienda** (las de su convenio con Sistecrédito) en Administración → Configuración → Medios de pago, **cifradas** en la BD local con el mismo mecanismo de los secretos de Factus; ambiente Pruebas/Producción; botón **"Verificar conexión"** (misma lógica de tutiendanueva: detecta llaves del otro ambiente) | El dinero es de la tienda; nunca pasa por BusinessPost |
| D7B-03 | **Sin webhooks**: el POS está en el local (sin dirección pública); el servidor de la tienda **consulta** el estado (polling) — la `urlConfirmation` exigida por la API apunta a una página informativa (desde 2026-10-17 la nube publica `/sistecredito/respuesta` y `/sistecredito/confirmacion`: 200 sin guardar nada; ver ADR-0064) | Arquitectura local-first |
| D7B-04 | Requiere internet **solo en ese momento**; sin conexión el medio aparece deshabilitado con "Sistecrédito necesita internet" | La caja nunca se bloquea |
| D7B-05 | Cada intento queda guardado (`PENDING/APPROVED/REJECTED/EXPIRED/CANCELLED`); un proceso de fondo revisa los pendientes; un pago aprobado sin venta completada queda **"Requiere revisión"** con alerta en la administración (no se pierde la plata del cliente) | Evitar cobros sin venta |
| D7B-06 | En el cierre de turno, Sistecrédito es un medio propio (no efectivo): se cuenta por referencias, como el datáfono | Cuadre por medio |
| D7B-07 | Documento del cliente: CC, CE, NIT, PA, TI; se puede tomar del cliente asignado a la venta | Rápido en la fila |
| D7B-08 | Mensajes de error traducidos (685/690/691/694 credenciales, 615 valor mínimo, 739 método no disponible para el cliente, 738 factura repetida) | Lenguaje simple |

## 3. Pruebas

Unitarias con un Sistecrédito simulado (crear, esperar URL, aprobado, rechazado, vencido, credenciales malas, sin internet);
integración de la venta completa con pago Sistecrédito y pago mixto; la prueba con llaves reales de **Staging** la haces tú con las
credenciales de tu convenio.

## 4. Lo que tienes que hacer tú

Pedir a Sistecrédito el convenio de **aliado** y sus llaves de **Staging** (pruebas) y Producción para cada tienda. Preguntarles si
tienen además un canal **presencial** propio (código de pago desde su portal de aliado, como viste en Toto): si lo hay y tiene API,
se agrega como segunda opción.

## 5. Fuera de alcance

Devoluciones de Sistecrédito desde el POS (se hacen en su portal y se registran aquí como nota crédito normal).

## 6. Plan (sin implementar)

### 6.1 Addi en la caja
Addi tiene un flujo parecido (enlace/QR, el cliente aprueba en su celular). Mismo diseño de §1–§2 con su API (tutiendanueva ya la usa
en `addi.service.js`); se agrega como otro proveedor del mismo módulo cuando haya convenio.

### 6.2 Sincronizar productos con tutiendanueva
- **Qué:** catálogo (nombre, SKU, código de barras, precio, oferta, categoría, existencias) del POS → tienda en línea de
  tutiendanueva; y los **pedidos en línea** → BusinessPost como ventas/reservas que descuentan inventario.
- **Cómo:** a través de la nube de BusinessPost (que ya recibe productos y existencias de cada tienda, Fase 16) y la **API pública con
  llaves** de tutiendanueva (`/api/public/products`, con alcance por tienda). El POS nunca habla directo con tutiendanueva.
- **Quién manda:** precio y existencias los manda el POS; descripciones, fotos y SEO se editan en tutiendanueva.
- **Imágenes:** hoy el POS no guarda fotos. Opción recomendada: las fotos viven en tutiendanueva (ya tiene almacenamiento, compresión
  e IA para fondos); el POS solo guarda la referencia y la muestra en la consulta de precio (F5) si hay internet. Alternativa: subir la
  foto desde la administración del POS a la nube de BusinessPost (almacenamiento S3/MinIO) y de ahí a tutiendanueva.
- **Conflictos:** cada producto se empareja por SKU/código de barras; los no emparejados quedan en una bandeja para revisar.
- Encaja con la Fase 18 (edición de maestros en el portal), que ya prevé el sentido nube → tienda.

### 6.3 Desinstalación premium (se suma a la Fase 13-B)
Desinstalador con la marca: confirma qué se borra, ofrece **hacer un backup final** y muestra dónde quedó, deja los datos por
defecto (casilla "Borrar también los datos de la tienda" con doble confirmación y el código de recuperación), libera el equipo en la
licencia para poder activarlo en otro, y pregunta (opcional) el motivo.
