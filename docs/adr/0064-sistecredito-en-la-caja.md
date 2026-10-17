# ADR-0064 · Sistecrédito en la caja

- **Estado:** Aceptada · 2026-10-17 · POS (módulo Cash y Sales) · Fase 7-B ([propuesta](../fases/fase-07b-propuesta.md), D7B-01..08).
  Amplía [ADR-0032](0032-pagos-redondeo-del-efectivo-y-cambio.md) (pagos y redondeo) y reutiliza el cifrado de [ADR-0059](0059-modo-de-emision-y-factura-electronica-por-venta-con-factus.md).

## Contexto
Muchos clientes pagan a cuotas con su crédito Sistecrédito. En la caja, el cajero ingresa la cédula, el cliente abre un enlace en su
celular, elige las cuotas y confirma con un código que le llega. tutiendanueva ya usa la API de pagos de Sistecrédito en su tienda en
línea (`sistecredito.service.js`, guías G-ALI-12 y G-ALI-09). El POS es local (sin dirección pública): no puede recibir avisos de
Sistecrédito, y la caja nunca se debe bloquear por falta de internet.

## Decisión
- **Mismo protocolo que tutiendanueva** (D7B-01) detrás de `ISistecreditoGateway` (Cash.Application). Adaptador real
  `SistecreditoHttpGateway` (HttpClient tipado): base `https://api.credinet.co/pay`, `POST /create` y
  `GET /GetTransactionResponse?transactionId=`; cabeceras `SCLocation: 0,0`, `SCOrigen: Staging|Production`, `country: co`,
  `Ocp-Apim-Subscription-Key`, `ApplicationKey`, `ApplicationToken`; método de pago 2, `COP`, valor en **pesos enteros**,
  `sandbox.isActive = false`, `methodConfirmation: POST` y `client {docType, document}`. El id de la transacción es `data._id`; la
  URL de pago llega en `paymentMethodResponse.paymentRedirectUrl` (se consulta hasta que aparece). Estados de fallo: `Rejected`,
  `Cancelled`, `Expired`, `Abandoned`, `Failed`; `Approved` = éxito. Errores traducidos (602, 613–629, 685, 690, 691, 694, 708, 738,
  739). Las pruebas usan un `HttpMessageHandler` simulado y un Sistecrédito simulado: **nunca** la API real.
- **Medio de pago propio**: tipo `SISTECREDITO` (nuevo valor de `PaymentMethodKind` y del CHECK `ck_payment_methods__kind`) y medio
  del sistema `SISTECREDITO` (código DIAN `ZZZ`, pide referencia, no afecta el cajón). Nace **inactivo** y se activa/inactiva con la
  configuración. No se crean otros medios de ese tipo (`CASH.SISTECREDITO_RESERVED`).
- **Llaves por tienda** (D7B-02) en `cash.sistecredito_settings`: cifradas con DPAPI de la máquina (propósito propio, mismo mecanismo
  que Factus), nunca devueltas por la API ni escritas en la bitácora; solo se muestran los últimos 4 de la llave de suscripción. Al
  guardar se conserva **la última palabra** de cada llave (quita la etiqueta que se arrastra al copiar el correo). "Verificar
  conexión" crea una transacción de verificación (vence sola) y detecta llaves del **otro ambiente**. A diferencia de tutiendanueva,
  si no hay internet la verificación **no** dice "correcta": dice que no se pudo conectar.
- **Cobro** (`cash.sistecredito_attempts`, UUID v7): factura `BP-<id>` (única: evita el error 738), valor, documento (tipo CC, CE,
  NIT, PA o TI), transacción, estado `PENDING / APPROVED / REJECTED / EXPIRED / CANCELLED / REVIEW_REQUIRED`, URL, cuotas (si
  Sistecrédito las informa: se buscan los nombres usuales del campo), fechas y venta completada. El documento completo solo se guarda
  en esa tabla (Sistecrédito lo necesita); bitácora, registros y API muestran los **últimos 4**.
- **Sin webhooks** (D7B-03): la caja consulta el estado **cada 3 s** mientras espera; la `urlConfirmation` obligatoria apunta a una
  página informativa (⚙️ `Pos:Sistecredito:ConfirmationUrl`). Internet solo en ese momento (D7B-04): sin conexión el cobro responde
  `CASH.SISTECREDITO_OFFLINE` ("Sistecrédito necesita internet…") y la caja elige otro medio; consultar un cobro pendiente sin
  conexión no falla (aviso y sigue intentando). Tiempo máximo por petición ⚙️ 20 s; al crear se consulta la URL hasta 3 veces cada
  1,5 s ⚙️ y, si no llega, la caja la sigue pidiendo.
- **Completar la venta** (D7B-05): `CompleteSaleCommand` exige, para cada pago `SISTECREDITO`, un cobro **APROBADO de esa venta, por
  ese mismo valor y sin usar** (`ISistecreditoPayments.UseApprovedAsync`, contrato de Cash usado por Sales); queda usado en la misma
  transacción de la venta (si la venta falla, se revierte). La BD lo refuerza (`ck_sistecredito_attempts__used_approved`). Pago
  mixto: el cobro puede ser una parte; el resto con otros medios. Si la venta ya tiene un cobro aprobado sin usar por el mismo valor,
  "Generar cobro" lo **reutiliza** (nunca se cobra dos veces); los pendientes anteriores de la venta se cancelan.
- **Cancelar** en la caja: Sistecrédito no tiene cómo anular; antes se consulta (si ya aprobó, se usa). Un aprobado que la caja
  cancela queda `REVIEW_REQUIRED`.
- **Revisión automática** (`SistecreditoReconcileWorker`, ⚙️ 60 s): consulta los pendientes (más de 1 min) y los cancelados en la
  caja en las últimas 24 h (el cliente pudo aprobar después) y marca `REVIEW_REQUIRED` los aprobados sin venta tras ⚙️ 15 min. La
  administración ve el aviso en la franja de avisos (`MeDto.SistecreditoReviews`, para quien tiene `cash.payment_method.manage`) y
  en la tarjeta de Sistecrédito, y lo cierra con una nota ("Marcar revisado"): completar la venta o reversar en el portal de
  Sistecrédito.
- **Licencia restringida** (ADR-0053): generar, consultar y cancelar el cobro son vender → `IAllowedWhenRestricted` (prueba R10).
  Configurar, verificar y revisar no.
- **Permisos**: la configuración y la revisión usan `cash.payment_method.manage`; el cobro, `cash.session.operate`. Sin permisos nuevos.
- **Auditoría** (`cash`): `SISTECREDITO_SETTINGS_CHANGED`, `SISTECREDITO_DISABLED`, `SISTECREDITO_CONNECTION_VERIFIED`,
  `SISTECREDITO_PAYMENT_STARTED`, `SISTECREDITO_PAYMENT_APPROVED`, `SISTECREDITO_PAYMENT_CANCELLED`, `SISTECREDITO_REVIEW_REQUIRED`,
  `SISTECREDITO_REVIEW_RESOLVED`.
- **Cierre de caja** (D7B-06): sin cambios en el cierre: la venta registra el movimiento por medio, así que Sistecrédito aparece como
  medio propio no efectivo en el arqueo, el X y el Z.

## Consecuencias
- Migración `V2026.10.034__cash__sistecredito.sql`; `pos_app` no puede borrar cobros ni la configuración.
- Restaurar la BD en otro equipo deja las llaves ilegibles (DPAPI de la máquina): hay que volver a ingresarlas (mismo caso que Factus).
- La factura `BP-<id>` y la URL de confirmación quedan registradas en Sistecrédito; las devoluciones de Sistecrédito se hacen en su
  portal (fuera de alcance) y aquí como nota crédito normal.
- El nombre del campo de cuotas no está documentado: si Sistecrédito no lo envía, el cobro queda sin cuotas (no afecta la venta).
- Addi (plan §6.1 de la propuesta) sería otro tipo de medio y otro adaptador con el mismo diseño.
