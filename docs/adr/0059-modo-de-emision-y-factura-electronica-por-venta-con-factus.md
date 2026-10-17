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

## Adenda A · Factus como adaptador por defecto y salvaguardas de la cola (2026-10, rama `pantallas-admin`)

- **Estado:** Propuesto — pendiente de aprobación del usuario. No se fusiona a `main` sin esa aprobación.
- **Cambia:** el punto 3 de la decisión. **Detalle:** [pantallas-faltantes-propuesta.md §2](../fases/pantallas-faltantes-propuesta.md).
- **Mientras tanto (ronda 3, PB2):** el código mantiene `NONE` por defecto (`BillingOptions.DefaultProvider`); aprobar esta adenda es
  cambiar esa sola línea a `FactusFiscalProvider.ProviderName`. La pantalla ya funciona habilitando `FACTUS` en `server.json`, y todas las
  salvaguardas de abajo ya están activas con cualquier valor.

### Contexto
Encender Factus exigía editar `server.json` (`Pos:Billing:Provider = FACTUS`) en el servidor de cada tienda. La pantalla nueva
"Conectar Factus" permite hacerlo desde la interfaz, pero solo sirve si el adaptador real está presente.

### Decisión
1. **Punto 3 (nuevo texto):** adaptador por configuración del servidor con `FACTUS` **por defecto**; `NONE` prohíbe la facturación
   electrónica en una instalación; `FAKE` solo fuera de producción. Lo que la enciende o apaga es el **modo de la empresa** (OFF por
   defecto), que solo pasa a `ON_REQUEST`/`EVERY_SALE` tras **verificar** las credenciales y el ambiente contra Factus
   (`POST /billing/settings/verify`, que solo pide un token).
2. **Salvaguardas (PA1)**, necesarias porque el adaptador real queda siempre presente:
   - La cola envía **solo con modo ≠ OFF y la conexión verificada**. Con OFF no sale nada, ni lo que ya estaba en cola.
   - La migración V2026.10.060 **apaga** las configuraciones encendidas sin verificación (instalaciones con llaves y modo ≠ OFF de cuando
     el adaptador era `NONE`) y el tablero avisa hasta que se reconecte.
   - Los documentos electrónicos emitidos **antes de la primera verificación correcta** no se envían solos: la administración elige
     **Enviar ahora** o **No enviar** (estado `NOT_SENT`, sin reintentos), con permiso `billing.settings.manage` y auditoría.
3. Un fallo transitorio de la verificación no quita una verificación correcta; como máximo una verificación cada 10 s por juego de llaves;
   "Volver a probar" valida la clave de verdad en un espacio de token aparte del de la cola; "Cambiar llaves" prueba las nuevas antes de
   guardarlas y no apaga la facturación si pasan. Las llamadas a Factus van fuera de la transacción de BD.
4. **Ambiente por documento (PB1):** cada documento electrónico guarda el ambiente con que se emitió; la cola solo envía los del ambiente
   vigente. Verificar en otro ambiente o con otras llaves mueve el corte del atraso a ese momento. Lo de otro ambiente queda retenido y
   visible, solo con "No enviar".
5. "Enviar ahora" emite con la fecha del día del envío (Factus no recibe fecha de emisión). Con la licencia restringida se permiten
   "Enviar ahora", "Verificar" y "Reintentar" (obligación ante la DIAN); "No enviar" y "Cambiar llaves", no.

### Consecuencias
- ✅ La tienda conecta Factus sin tocar archivos; las instalaciones actuales en OFF no cambian.
- ✅ Ningún documento sale a la DIAN sin una conexión verificada y el modo encendido; el atraso viejo exige una decisión explícita.
- ⚠️ Instalaciones que tenían la facturación encendida con `NONE` quedan apagadas al actualizar: hay que reconectarlas desde la pantalla.
- ⚠️ Si se rechaza esta adenda, basta con volver el valor por defecto a `NONE`: las salvaguardas y la pantalla siguen siendo válidas con
  `Pos:Billing:Provider = FACTUS` explícito.
