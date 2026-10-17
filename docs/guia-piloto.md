# Guía del piloto en un supermercado real (Fase 17)

> Para el propietario del producto y quien acompaña el piloto. Instalación: [guia-instalacion.md](guia-instalacion.md) · nube:
> [despliegue-nube.md](despliegue-nube.md) · recuperación: [guia-recuperacion.md](guia-recuperacion.md).

El piloto es la primera tienda que **vende de verdad** con el sistema. Su objetivo es encontrar lo que falta antes de ofrecerlo a más
clientes, sin poner en riesgo la operación de la tienda.

## 1. Antes de ir a la tienda (1–2 semanas antes)

| ✓ | Tarea | Quién |
|---|---|---|
| ☐ | Nube desplegada con clave de firma, clave de sincronización y respaldo diario fuera del VPS (despliegue §5, §8, §17) | Propietario |
| ☐ | Versión compilada con las **claves públicas de producción** embebidas: licencias (`trusted-keys.json`), actualizaciones (`update-keys.json`) y sincronización (`sync-key.json`) | Propietario |
| ☐ | Instalador probado en un Windows limpio (guía de instalación §7) | Propietario |
| ☐ | En el portal: cuenta, empresa (NIT), suscripción de prueba y **clave de licencia**; usuario **Cliente** para el dueño de la tienda | Propietario |
| ☐ | Recorrido completo con datos de prueba en su PC: compra → existencias → venta → cierre Z → sincronización → reporte en el portal | Propietario |
| ☐ | Pedir a la tienda: lista de productos (código de barras, nombre, precio, IVA), existencias por producto con su costo, proveedores, usuarios y cajas | Tienda |
| ☐ | Revisar el hardware: equipo servidor (Windows 10/11 64 bits, 8 GB, SSD), cajas, impresoras térmicas (80 mm, ESC/POS), lectores y cajón | Ambos |
| ☐ | Definir el **plan de respaldo**: si el sistema falla, la tienda vuelve a su método anterior (caja registradora o cuaderno) ese día | Ambos |
| ☐ | Acordar el día de arranque: **no** un fin de semana ni una quincena; mejor un martes o miércoles | Ambos |

## 2. Instalación en la tienda (el día anterior al arranque)

1. Instale el servidor (o Todo en uno) y complete el asistente: empresa, propietario, **clave de licencia** y **código de recuperación**
   (imprímalo; el dueño lo guarda fuera del local).
2. **Administración → Configuración:** revise la empresa, la sucursal, la bodega principal, los medios de pago (datáfono, transferencias)
   y los parámetros.
3. **Administración → Cajas e impresoras:** cree cada caja, configure su impresora de tiquetes y empareje cada equipo con su código.
   Imprima un tiquete de prueba desde la caja.
4. **Inventario → Importar desde Excel:** cargue los productos y precios con la plantilla (revise la vista previa: ninguna fila con
   error), luego el **saldo inicial** de existencias y publíquelo en **Ajustes**.
5. **Administración → Usuarios:** cree los cajeros (código y PIN), el supervisor y el administrador. Nadie usa la cuenta del propietario
   para el día a día.
6. **Backups:** confirme que el primer backup se hizo y se verificó; configure la copia en la nube o en un disco externo.
7. **Sincronización:** "Sincronizar ahora" y compruebe en el portal (Datos de las tiendas) que llegaron los productos.

## 3. Capacitación (1–2 horas, con la tienda cerrada o en baja)

- **Cajeros:** ingresar con código y PIN, abrir la jornada contando la base, escanear, cantidades (`3*código`), buscar (F2), suspender y
  recuperar (F8/F9), cobrar con varios medios (F10), anular una línea (pide supervisor), reimprimir, cerrar la jornada a ciegas.
- **Supervisor:** autorizaciones en pantalla, descuentos, anulaciones, lotes vencidos, revisar jornadas y cerrar una jornada abandonada.
- **Administrador:** compras (factura con lotes y vencimientos), proveedores y cuentas por pagar, conteos y ajustes, traslados, gastos,
  promociones, clientes, reportes.
- **Dueño de la tienda:** cómo pagar la mensualidad en línea desde la administración (§8).
- Practique **una venta de cada tipo** antes de abrir: efectivo con cambio, datáfono, mixta, con cliente, con descuento autorizado.

## 4. Primer día

| Hora | Qué vigilar |
|---|---|
| Apertura | Cada caja abre su jornada con la base contada; las impresoras imprimen |
| Primeras 2 horas | Acompañe a cada cajero; anote productos sin código o con precio equivocado y corríjalos en **Productos y precios** |
| Mediodía | Revise el **Tablero** y **Existencias** (bajo el mínimo); confirme que la sincronización tiene 0 pendientes |
| Cierre | Cierre ciego de cada caja, reporte Z con su sello; revise las diferencias en **Jornadas de caja** |
| Después del cierre | Backup verificado; reporte de ventas del día comparado con el efectivo y los vouchers del datáfono |

## 5. Primera semana

- **Diario:** diferencias de caja, backups verificados, sincronización al día, avisos de licencia, incidentes de auditoría.
- **Día 3:** conteo de una categoría (Inventario → Conteos) para comprobar que las existencias cuadran.
- **Día 5:** una compra real con lotes y vencimientos y su pago en cuentas por pagar.
- **Día 7:** reunión con el dueño: qué falta, qué sobra, qué es lento. Todo va a una lista priorizada para la versión 1.1.
- Si hay un error: Menú Inicio → **Paquete de soporte** y envíelo; anote la hora y qué se estaba haciendo.

## 6. Criterios para dar el piloto por exitoso (2–4 semanas)

| Criterio | Meta |
|---|---|
| Días vendiendo solo con el sistema | ≥ 10 días seguidos, sin volver al método anterior |
| Tiempo de venta en caja | Igual o mejor que antes (p95 del escaneo < 300 ms; Fase 14) |
| Diferencias de caja sin explicar | $0 al final de la semana 2 |
| Existencias | Conteo de una categoría con diferencia < 2 % |
| Datos | Ningún día sin backup verificado; sincronización sin pendientes al cierre |
| Incidentes | Ningún incidente de integridad de la auditoría sin explicación |
| Dueño de la tienda | Consulta sus ventas en el portal y quiere seguir |

## 7. Plan de reversa

Si un día el sistema no permite vender (servidor dañado, falla grave): la tienda usa su método anterior ese día y anota las ventas; se
recupera el servidor con el último backup (guía de recuperación, menos de una hora) y, al volver, las ventas anotadas se registran en el
sistema como una venta por cada día con la nota "contingencia". Nunca se editan datos directamente en la base de datos.

## 8. Cómo pagar la mensualidad en línea (para el dueño de la tienda)

Cuando faltan **15 días o menos** para el vencimiento, o la licencia está **en gracia** o **restringida**, el sistema ofrece pagar en
línea sin entrar al portal:

1. Arriba de la administración aparece el aviso de la licencia con el enlace **Pagar** (se abre en otra pestaña). También puede ir a
   **Administración → Licencia**: ahí está la tarjeta **"Pagar mensualidad"**.
2. Oprima **Pagar mensualidad** y pague con **Nequi, PSE, tarjeta o Bancolombia**. Si prefiere pagar desde el celular, escanee el
   **código QR** de la tarjeta con la cámara del teléfono.
3. Al aprobarse el pago, la licencia **se renueva sola**: el sistema la verifica una vez al día. Para verla al instante, oprima
   **"Ya pagué, verificar"** en la misma tarjeta. Si el pago ya se aplicó, la fecha "Vigente hasta" cambia y la tarjeta desaparece.
4. Con PSE o Bancolombia el banco puede tardar unos minutos (a veces más) en confirmar: si dice "Todavía no se ve el pago", espere y
   vuelva a oprimir **"Ya pagué, verificar"** más tarde. Nunca pague dos veces el mismo mes sin preguntar antes a soporte.

Notas: la caja no muestra pagos (solo quien puede ver la licencia en la administración). Se necesita Internet en el equipo para pagar
y verificar; mientras tanto la tienda sigue vendiendo con normalidad (en restringida, solo en las jornadas abiertas). Si la tarjeta no
aparece, la licencia está al día o el pago en línea todavía no está habilitado: pague como siempre y avise a soporte.

## 9. Cobrar con Sistecrédito

Con Sistecrédito el cliente paga **a cuotas** con su crédito. La plata la paga Sistecrédito a la tienda (nunca pasa por BusinessPost).
Hace falta el **convenio de aliado** de la tienda con Sistecrédito y sus llaves (unas de **pruebas / Staging** y otras de
**producción**). Se necesita internet **solo en el momento del cobro**: sin internet se cobra con otro medio y la caja sigue vendiendo.

**Configurarlo (administración, una vez):**

1. **Administración → Configuración → Medios de pago → tarjeta Sistecrédito.**
2. Pegue la **llave de suscripción**, la **ApplicationKey** y el **ApplicationToken** del correo de Sistecrédito (si se copia la etiqueta
   "Key" o "ID", no pasa nada: se guarda solo el código). Elija el **ambiente** (Pruebas o Producción), deje **Activo en la caja** y
   oprima **Guardar**. Las llaves quedan cifradas en el equipo y no se vuelven a mostrar.
3. Oprima **Verificar conexión**. Si dice que las llaves **sí funcionan en el otro ambiente**, cambie el ambiente y guarde de nuevo.
4. Desde ese momento el medio **Sistecrédito** aparece en la pantalla de cobro de la caja.

**Cobrar en la caja:**

1. F10 (Cobrar) → botón **Sistecrédito**. Escriba el valor (todo lo que falta o una parte, para pago mixto).
2. Elija el tipo de documento (**CC** por defecto; también CE, NIT, PA, TI) y escriba el número. Si la venta tiene cliente asignado, el
   documento ya aparece.
3. Oprima **Generar cobro**. Sale un **código QR**: "Escanee con su celular, elija las cuotas y confirme con el código que le llega".
4. La caja se actualiza sola cada pocos segundos. Cuando el cliente **aprueba**, el pago se agrega y la venta se completa (o, en pago
   mixto, cobre el resto con otro medio). Si Sistecrédito **rechaza** o el cobro **vence**, la caja muestra el motivo: elija otro medio.
5. **Cancelar** detiene la espera. Si el cliente igual aprueba después, el sistema lo detecta solo.

**Cobros por revisar:** si Sistecrédito aprobó un cobro y la venta no se completó (por ejemplo, se canceló la venta o se cerró la
pantalla), a los 15 minutos aparece el aviso **"cobro de Sistecrédito por revisar"** arriba de la administración. En la tarjeta de
Sistecrédito están esos cobros: complete la venta o **reverse el pago en el portal de Sistecrédito**, y oprima **Marcar revisado** con
una nota de lo que hizo. Las devoluciones de Sistecrédito se hacen en su portal; en el POS se registran como un cambio o reintegro normal.

**En el cierre de caja**, Sistecrédito sale como un medio aparte (no es efectivo), igual que el datáfono: cuadre con las transacciones
aprobadas del día.

**Primera prueba (con llaves de Staging):** configure el ambiente **Pruebas**, verifique la conexión, haga una venta pequeña con el
documento de prueba que dé Sistecrédito, apruébela en el celular y revise que la venta quede completada con la referencia de la
transacción. Luego pruebe un rechazo y un "Cancelar". Solo después cambie a **Producción** con las llaves de producción.

## 10. Báscula conectada

La báscula de mostrador va conectada al computador de la caja. Cuando el cajero registra un producto marcado **"Se vende por báscula"**
(escanea o escribe el código y da Enter), BusinessPost lee el peso solo y agrega la línea (0,700 kg × $3.000/kg = $2.100). No hay
botón "Pesar". Las etiquetas de la báscula etiquetadora (código que empieza por 20 a 28) siguen igual que antes.

**Qué comprar**
- Una báscula de mostrador con **salida serial RS-232** que hable **Toledo 8217** (la mayoría de Torrey, Moretti, CAS, Dibal lo traen
  como opción) o el formato **CAS de 22 bytes** (ST,GS,…kg). Pregunte al vendedor por "protocolo 8217" o "salida para PC/ECR".
- El **cable de datos** de esa báscula (a veces es especial: no sirve cualquier cable serial) y, si el computador no tiene puerto
  COM, un **adaptador USB a serial** con chip FTDI o Prolific (instale su controlador del fabricante; BusinessPost no instala
  controladores).

**Qué configurar en la báscula** (menú técnico del fabricante)
- Toledo 8217: protocolo 8217 / "ECR Toledo", **9600 baudios, 7 bits, paridad par, 1 bit de parada**, envío "a pedido".
- CAS: formato 22 bytes, **9600 baudios, 8 bits, sin paridad, 1 bit de parada**, envío continuo.
- Unidad en kilogramos.

**Qué configurar en BusinessPost** (en el computador de la caja, con la báscula conectada)
1. Windows → Administrador de dispositivos → Puertos (COM y LPT): anote el número del puerto (p. ej. COM3).
2. Configuración → Cajas e impresoras → botón **Báscula** de la caja: marque "Esta caja tiene báscula", elija la marca o
   protocolo y el puerto. Los valores del puerto serie se ponen solos; si la báscula usa otros, cámbielos en "Opciones avanzadas".
3. **Probar báscula**: ponga un peso conocido. Debe verse el peso en vivo, la palabra "Estable" cuando quede quieto, y la tara
   debe descontar la bandeja. Luego **Guardar**.
4. En cada producto pesado: marque **"Se vende por báscula"**, la unidad (Kilogramo, Gramo o **Libra (500 g)**) y, si se empaca en
   bolsa o bandeja, la **Tara (g)**.

**En la caja**
- Producto en movimiento: "Esperando peso estable…" (hasta 3 s); báscula vacía: "Ponga el producto en la báscula" (hasta 5 s);
  si no llega peso no se agrega nada. Sobrepeso o peso negativo: aviso claro. Tiempos configurables en la báscula de la caja.
- Si la báscula no responde, el cajero puede **digitar el peso** (si la caja lo permite): pide el permiso "Digitar el peso" o la
  autorización del supervisor, queda marcado "Peso digitado" en la línea y en el tiquete, y queda en la auditoría.
- **Ins** = tara manual (una bolsa que no está en el producto). Esc cancela la lectura.

**Si no lee**: revise que la báscula esté encendida, el cable, el número de puerto, que ningún otro programa use el puerto y que la
velocidad, bits y paridad coincidan con el menú de la báscula ("respondió, pero no en el formato…" = protocolo o velocidad distintos).
