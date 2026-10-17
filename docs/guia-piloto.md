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
   y los parámetros. En **Funciones del negocio** confirme el tipo de negocio y deje encendido solo lo que la tienda usa (sección 11).
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
   "Key" o "ID", no pasa nada: se guarda solo el código). Elija el **ambiente**: **Producción** para cobrar de verdad (viene así) o
   **Pruebas** con las llaves de Staging.
3. Oprima **Conectar**. BusinessPost guarda las llaves cifradas, **prueba la conexión con Sistecrédito** y solo si responde bien activa
   la forma de pago. Si dice que las llaves **funcionan en el otro ambiente**, cambie el ambiente y vuelva a pulsar Conectar.
4. Queda **"Conectado"** y el medio **Sistecrédito** aparece en la pantalla de cobro de la caja. Ahí mismo están **Volver a probar**,
   **Cambiar llaves** y **Desconectar** (las llaves se conservan).

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

## 11. Funciones del negocio

En **Administración → Configuración → Funciones del negocio** el dueño ve el **tipo de negocio** que eligió al instalar y un
interruptor por cada función. El tipo de negocio **no es un plan de precios**: todas las funciones vienen en Caja Única y en Multicaja;
solo ordena los menús y los formularios para que su negocio vea lo que usa.

| Función | Qué hace | Si se apaga |
|---|---|---|
| Báscula y PLU | Lee el peso de la báscula en la caja; PLU, tara y etiquetas de báscula en el producto | La caja no lee la báscula; el producto no muestra PLU ni tara; el botón Báscula de Cajas e impresoras desaparece |
| Lotes y vencimientos | Lote y fecha de vencimiento en los productos que lo necesitan | El producto no muestra "Maneja lotes" ni "Maneja fecha de vencimiento" |
| Venta por metro o fracción | Cantidades con decimales en productos que van por unidad (1,5 m de cable) | El producto no muestra "Permite cantidades con decimales" (los productos por peso o volumen siguen con decimales) |
| Precio abierto | El cajero escribe el precio al vender | El producto no muestra "Permite precio abierto" |
| Promociones | Descuentos y ofertas automáticas | "Promociones" sale del menú. Las promociones activas **siguen aplicándose**: desactívelas antes si no las quiere |
| Compras y proveedores | Compras, proveedores y cuentas por pagar | "Compras", "Proveedores" y "Cuentas por pagar" salen del menú |
| Tallas y colores | Un producto en varias tallas y colores | Aún no existe: aparece deshabilitada, "Disponible en una próxima versión" |

- **Apagar una función nunca borra datos.** Si un producto ya tiene datos de una función apagada (por ejemplo, un PLU), el campo se
  sigue viendo con el aviso *"Esta función está apagada; los datos se conservan"*. Las pantallas de compras y promociones se pueden
  abrir desde el enlace "Ver las compras guardadas" / "Ver las promociones guardadas" del interruptor.
- **Cambiar el tipo de negocio** pide confirmación y deja los interruptores en los valores recomendados de ese tipo; después se puede
  ajustar cada uno.
- Lo cambia quien tiene el permiso **"Modificar los datos de la empresa"** (el propietario lo tiene). Cada cambio queda en la
  **Auditoría** ("Tipo de negocio o funciones del negocio cambiados") con lo que se encendió y lo que se apagó.
- Los menús se ajustan enseguida para quien hizo el cambio; los demás usuarios lo ven al abrir el Tablero o al volver a ingresar. La
  caja deja de leer (o vuelve a leer) la báscula la próxima vez que se abre la pantalla de venta.
- El portal de BusinessPost muestra el tipo de negocio de cada empresa (lo envía la tienda en su verificación diaria de la licencia).

## 12. Reabastecimiento: mínimos, máximos y pedido sugerido

Para no quedarse sin mercancía, cada producto puede tener en cada bodega un **mínimo** (de ahí para abajo hay que pedir), un **máximo**
(hasta dónde llenar) y, si quiere, un **punto de pedido** (pedir antes de llegar al mínimo). Con eso BusinessPost arma el pedido y crea
las órdenes de compra.

**Cómo poner los mínimos y máximos** (permiso "Definir mínimos y máximos de existencias"; lo tienen el propietario, el administrador y los
roles Inventario y Compras: el rol Compras lo recibe a propósito, porque quien compra es quien suele fijar mínimos y máximos. Al actualizar,
todo rol creado por la empresa que podía hacer ajustes de inventario también lo recibe, para que nadie pierda lo que ya podía hacer)
- **Uno por uno**: en la ficha del producto, pestaña **Existencias** → "Mínimos y máximos": una fila por bodega con mínimo, punto de
  pedido, máximo y cantidad a pedir; escriba los números y pulse **Guardar** en esa fila. El máximo puede quedar vacío. Si usa punto de
  pedido, también hay que poner el máximo o la cantidad a pedir (lo exige el sistema, también al importar).
- **Desde Excel**: Inventario → Importar desde Excel → "Mínimos y máximos". Descargue la plantilla (columnas `sku` o `codigo_barras`,
  `minimo`, y opcionales `maximo`, `punto_pedido` y `bodega` con el código de la bodega). Si el archivo no trae la bodega, se usa la
  que elija en la pantalla. Al subir el archivo se ve la **vista previa** fila por fila (nuevo, cambia, sin cambios o error con el
  motivo: producto que no existe o descontinuado, máximo menor que el mínimo, producto repetido, decimales en un producto por unidad…;
  un producto inactivo se acepta con un aviso). Nada se guarda
  hasta pulsar **Aplicar**, y solo si ninguna fila tiene errores. Una celda vacía de máximo o punto de pedido **conserva** lo que ya
  tenía.
- **Calculados con la venta**: Inventario → Reabastecimiento → pestaña **"Calcular mínimos sugeridos"**. Elija la bodega, los días de
  venta a mirar (p. ej. 30), los días que tarda el proveedor en entregar (p. ej. 3) y los días de cobertura (p. ej. 7):
  - venta diaria = lo vendido en esos días ÷ días (ventas menos anulaciones y devoluciones);
  - mínimo = venta diaria × días de entrega; máximo = venta diaria × (días de entrega + días de cobertura), redondeados hacia arriba.
  - Ejemplo: 90 unidades en 30 días = 3 diarias; entrega en 4 días → mínimo 12; con 7 días de cobertura → máximo 33.
  Revise la tabla (puede cambiar cualquier número y desmarcar productos) y pulse **Guardar los marcados**. El punto de pedido que ya
  tuviera se conserva.

**Pedido sugerido** (Inventario → Reabastecimiento → pestaña "Pedido sugerido"; se ve con el permiso de consultar compras y la función
"Compras y proveedores" encendida)
- Muestra los productos con la existencia **bajo el mínimo** o en el punto de pedido, agrupados por **proveedor** (el preferido del
  producto o, si no hay, el de la compra más reciente). Se puede filtrar por bodega, categoría y proveedor.
- **Pedir** = máximo − (existencia + lo ya pedido). Sin máximo: la cantidad a pedir, si la tiene; si no, hasta el punto de pedido (si es
  mayor que el mínimo) o hasta el mínimo. Solo aparecen bodegas activas. Lo "ya pedido"
  son las órdenes de compra en borrador, aprobadas, enviadas o recibidas en parte que aún no llegan: así no se pide dos veces.
- Si el proveedor vende por **caja** (la presentación del producto en la ficha del proveedor), la cantidad se redondea a **cajas
  completas**: faltan 30 unidades y la caja es de 24 → 2 cajas (48 unidades).
- Puede cambiar la cantidad, marcar o desmarcar productos (o un proveedor completo). Los productos **sin proveedor** quedan en el grupo
  "Sin proveedor": elija uno por producto o para todos los marcados.
- **Crear órdenes de compra** (permiso "Crear, enviar y cerrar órdenes de compra") crea **una orden en borrador por proveedor y bodega**
  con el último costo del proveedor, y muestra los enlaces a cada orden (el valor solo lo ve quien puede ver costos). Si una falla (por
  ejemplo, el proveedor está bloqueado o la bodega se inactivó), no se crea ninguna y el aviso dice cuál falló.
- **No se pide dos veces**: un doble clic o un reintento después de un corte devuelve las mismas órdenes. Si otra persona pidió alguno
  de los productos marcados mientras usted revisaba, el sistema lo avisa (con el SKU y el nombre), vuelve a cargar la lista **conservando
  sus cambios** en los demás productos y resalta y desmarca los que cambiaron para que los revise. Las ventas que ocurran mientras tanto
  no bloquean: se pide la cantidad que usted eligió.
- Si hay más de 5.000 productos para pedir, se muestran los primeros 5.000 con un aviso: filtre por categoría o proveedor. Después, en Compras → Órdenes, revise, apruebe y envíe cada una como siempre.
- Todo queda en la **Auditoría**: cada cambio de mínimo y máximo ("Mínimo y máximo · creado/modificado"), cada importación
  ("Mínimos y máximos importados de Excel"), los mínimos calculados guardados y las órdenes creadas desde el pedido sugerido.
## 13. Etiquetas de góndola

En **Inventario → Etiquetas** se imprimen las etiquetas de precio de la góndola (habladores) y los adhesivos con código de barras. Lo
puede usar quien consulta productos; generar códigos internos exige poder modificar productos.

**1. Arme la lista** (se pueden combinar varias formas; si un producto ya estaba, se toma la cantidad nueva):
- **Buscar productos** uno por uno (nombre, SKU o código).
- **Una categoría**: todos sus productos activos, incluidas las subcategorías.
- **Cambios de precio desde una fecha**: los productos cuyo precio nuevo ya rige y empezó desde ese día (para cambiar las etiquetas
  después de subir o bajar precios).
- **Productos de una compra** (si Compras está encendida y tiene el permiso de consultar compras): escriba el **número interno** de la
  compra de esta sucursal (no el de la factura del proveedor, que se repite entre proveedores). La cantidad de cada producto es lo que
  llegó en unidades (2 cajas × 12 = 24 etiquetas). Lo que se vende por peso lleva una sola. También está el botón **Imprimir etiquetas**
  en la compra confirmada.
- **Productos sin código de barras**.

Los productos **descontinuados** no salen (se avisa cuántos se omitieron); los **inactivos** salen marcados para que decida. Si la empresa
tiene varias sucursales, elija arriba la **sucursal**: los precios y las promociones dependen de ella (solo las sucursales en las que su
usuario puede consultar productos). La lista de precios general siempre admite promociones, como en la caja.

**2. Revise**: precio al público, calculado **igual que en la caja** (con IVA e impuestos fijos como la bolsa o el IBUA, también si la lista
de precios no incluye impuestos; los centavos se suben al peso para que la góndola nunca diga menos de lo que se cobra); oferta vigente
hoy en la sucursal (precio especial o porcentaje con el precio normal tachado y "hasta dd/mm" si la promoción tiene fecha final;
"Lleve 3 pague 2" o "Desde 3 und" como texto); precio por kilo o por litro cuando el producto tiene contenido neto (500 g, 750 ml) y la
cantidad de copias (0 = no se imprime). Las promociones que solo rigen ciertos días u horas **no** se imprimen en la góndola.

Si un producto **no tiene código de barras**, la etiqueta lleva el SKU (la caja también lo lee). Con el botón **Generar código interno**
el sistema le crea un código EAN-13 que empieza por 29 (si ese número ya lo tiene otro producto, toma el siguiente); pide confirmación,
queda guardado en el producto y en la **Auditoría** ("Códigos internos generados para etiquetas"). Si algún producto no se pudo (ya tenía
código, está descontinuado), se informa sin detener los demás.

**3. Imprima**:
- **Hoja A4 · 24 etiquetas (66 × 35 mm)** u **Hoja A4 · 14 etiquetas grandes (100 × 40 mm)**: imprima al **100 %** (sin "ajustar a la
  página") en papel adhesivo o normal y recorte por las líneas grises.
- **Adhesivo 40 × 25 mm** o **50 × 30 mm**: el PDF viene al tamaño exacto del rollo, una etiqueta por página. En la impresora de
  etiquetas (Zebra, Xprinter, TSC…) elija ese tamaño de papel en Windows y la opción de etiquetas con separación (gap).
- **Imprimir en la impresora de tiquetes** (solo en un computador de caja): nombre, precio y código de barras CODE128 una tras otra en
  el papel de 80 mm, para recortar a mano. Sirve para salir del paso; para la góndola es mejor el PDF.

Las barras nunca se imprimen más delgadas de 0,25 mm (2 puntos de una impresora de 203 ppp); los EAN buscan su tamaño recomendado
(0,264 mm) y, si en el adhesivo de 40 × 25 mm no alcanza, la vista previa lo avisa. Un código largo (un SKU de muchas
letras) usa todo el ancho de la etiqueta; si aun así no cabe, el sistema lo dice: use la hoja de 14 etiquetas o genere el código interno.
En la impresora de tiquetes los EAN-13 salen como EAN y el ancho de las barras se ajusta al papel (58 mm = 384 puntos; 80 mm = 512); si un código no cabe,
se imprime su texto.

Máximo 2.000 etiquetas por archivo, 200 en la impresora de tiquetes y 500 copias por producto: si es más, imprima por partes.
