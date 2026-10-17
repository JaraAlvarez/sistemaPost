# Manual del cajero de BusinessPost

Este manual explica cómo vender con BusinessPost en la caja: entrar, abrir el turno, registrar productos, cobrar con cualquier medio de
pago, corregir errores y cerrar el turno con el reporte Z. Está escrito para el cajero y para quien lo capacita. Las imágenes son de una
tienda de ejemplo (*Supermercado La Economía*, sucursal Centro, caja C01) con datos ficticios.

> **Consejo:** al final del manual está la **hoja de atajos**: una página para imprimir y pegar junto a la caja.

## Contenido

1. La pantalla de la caja
2. Entrar a la caja
3. Abrir el turno
4. Vender: escanear, cantidades y búsqueda
5. Cliente de la venta (F3)
6. Descuentos y autorización del supervisor (F6)
7. Cobrar: efectivo, tarjeta, pago mixto y Sistecrédito (F10)
8. Anular una línea (Supr)
9. Suspender y recuperar una venta (F8 y F9)
10. Cancelar la venta (F7)
11. Cerrar el turno y el reporte Z
12. Lo que cambia según el tipo de negocio
13. Atajos de teclado
14. Problemas comunes y soluciones
15. Hoja de atajos para imprimir

## 1. La pantalla de la caja

La caja se maneja casi toda con el teclado y el lector de códigos de barras. La pantalla tiene cuatro zonas:

- **Barra superior:** la caja (C01), el número del turno, el nombre del cajero, el estado de la conexión (**En línea** o **Sin conexión**),
  la hora y los botones **Reimprimir** (aparece después de la primera venta), **Cerrar turno** y **Salir**. Si hay ventas suspendidas,
  aparece también el aviso **"1 suspendida"** (al pulsarlo se abren).
- **Escáner** (arriba a la izquierda): el campo "Escanee el código o escriba el nombre del producto". Siempre tiene el cursor: lo que
  lea el lector entra ahí, aunque usted haya hecho clic en otra parte.
- **Lista de productos** (centro): cada línea con su número, nombre, código interno, cantidad, precio, descuento y subtotal. La línea
  seleccionada se ve resaltada; las etiquetas debajo del nombre indican si tiene promoción, descuento manual, si se pesó o si está anulada.
- **Panel derecho:** el **Total a pagar** con el número de artículos, el subtotal, los impuestos, el **Ahorro del cliente** (promociones y
  descuentos), el redondeo si lo hay, el cliente de la venta, el botón verde **Cobrar F10** y los botones de acciones, cada uno con su
  tecla: Cliente **F3**, Cantidad **F4**, Descuento **F6**, Anular línea **Supr**, Suspender **F8**, Recuperar **F9** y Cancelar venta **F7**.

La barra inferior recuerda las dos teclas más útiles: `F5` consultar precio y `F1` todos los atajos.

![La caja lista para vender, con el turno abierto.](imagenes/uso/caja-06-venta-vacia.png)

## 2. Entrar a la caja

1. Abra BusinessPost en el computador de la caja. Aparece la pantalla de ingreso.
2. Elija la pestaña **Caja**.
3. Escriba su **Código de cajero** y su **PIN** (los números que le entregó el administrador) y pulse **Entrar a la caja** o Enter.

![Ingreso a la caja: pestaña Caja, código de cajero y PIN.](imagenes/uso/caja-01-ingreso.png)

![El código y el PIN se escriben ocultos.](imagenes/uso/caja-02-ingreso-codigo-y-pin.png)

> **Importante:** el código y el PIN son personales. Cada venta, cada turno y cada conteo de la caja quedan a su nombre. No los preste ni
> los anote en la caja.

Si alguien entró por la pestaña **Administración** y pulsa **Ir a la caja**, BusinessPost muestra "Para vender, entre como cajero": pulse
**Entrar con código de caja** para salir y volver a entrar con el código y el PIN de cajero.

![Con una sesión de administración la caja pide entrar con código de cajero.](imagenes/uso/adm-08-caja-desde-la-administracion.png)

## 3. Abrir el turno

La primera vez que entra en el turno, la caja pide contar la **base del cajón** (el efectivo inicial).

1. Cuente el dinero del cajón.
2. En cada valor de billete o moneda escriba **cuánto dinero hay de ese valor** (no cuántos billetes). Por ejemplo, si tiene dos
   billetes de $ 50.000, escriba 100.000 en el de $ 50.000. Debajo de cada valor la caja le confirma "= 2 billetes".
3. Deje vacío lo que no tenga.
4. Revise el total **Base del cajón** y pulse **Abrir turno**.

![Abrir turno: conteo de la base del cajón por valor.](imagenes/uso/caja-04-abrir-turno.png)

![Base de $ 300.000 contada; la caja muestra cuántos billetes son de cada valor.](imagenes/uso/caja-05-abrir-turno-conteo.png)

Aparece el aviso "Turno … abierto." y la pantalla queda en **Lista para vender**. El número del turno queda en la barra superior.

## 4. Vender: escanear, cantidades y búsqueda

### Escanear un producto

Pase el código de barras por el lector. El lector escribe el código y da Enter solo: el producto se agrega a la lista y queda
seleccionado. Si no tiene lector, escriba el código de barras, el código interno (SKU) o el PLU y pulse **Enter**.

Si el mismo producto se registra otra vez, la caja suma la cantidad en la misma línea cuando corresponde.

Las **promociones** se aplican solas: debajo del nombre aparece la etiqueta de la promoción (por ejemplo "Lleve 3 pague 2") y en el panel
derecho el **Ahorro del cliente**. El cajero no tiene que hacer nada.

![Venta con cinco productos: cantidades con 2* y 3* y promociones aplicadas solas.](imagenes/uso/caja-07-venta-con-productos-y-promociones.png)

### Varias unidades: 3*código

Para registrar varias unidades del mismo producto escriba la cantidad, un asterisco y luego escanee o escriba el código:

- `3*` y pase el producto por el lector → 3 unidades.
- `3*7702011000123` y Enter → 3 unidades de ese código.
- `2*arroz` → busca "arroz" y agrega 2 unidades del producto que elija.

### Buscar por nombre

Si el producto no tiene código o no lee, escriba parte del nombre en el escáner (por ejemplo "leche"). Debajo aparece la lista de
productos que coinciden, con su código y su precio:

- `↑` y `↓` eligen el producto.
- **Enter** lo agrega (también puede hacer clic en él).
- **Esc** cierra la lista.

Si solo hay un producto que coincide, o escribió el código interno exacto, Enter lo agrega de una vez.

![Búsqueda por nombre: la lista aparece bajo el escáner al escribir «leche».](imagenes/uso/caja-08-busqueda-por-nombre.png)

### Elegir una línea y cambiar la cantidad (F4)

- Con la lista de sugerencias cerrada, `↑` y `↓` mueven la **línea seleccionada** de la venta. También puede hacer clic en la línea.
- **F4** abre **Cambiar cantidad** para la línea seleccionada: escriba la nueva cantidad y pulse **Enter** (o **Aplicar**).

![Cambiar cantidad de la línea seleccionada.](imagenes/uso/caja-16-cambiar-cantidad.png)

### Consultar un precio sin vender (F5)

Cuando un cliente pregunta un precio, pulse **F5**. Escanee el producto o escriba su nombre y elija con `↑` `↓` y Enter. La caja muestra
el precio en grande, si es por unidad o por kilo, el IVA y, si su usuario puede verlas, las existencias. **No se agrega a la venta** y
puede consultar uno tras otro. **Esc** cierra la consulta.

![Consultar precio: lista al escribir el nombre.](imagenes/uso/caja-12-consulta-de-precio-lista.png)

![Resultado de la consulta: precio, IVA y existencias.](imagenes/uso/caja-13-consulta-de-precio.png)

## 5. Cliente de la venta (F3)

Por defecto la venta es a **Consumidor final**. Asigne un cliente cuando pida factura a su nombre o cuando tenga precios especiales (por
ejemplo, empleados o mayoristas): la venta usa sus precios.

1. Pulse **F3**.
2. Escriba la cédula, el NIT, el nombre o el celular (al menos 2 letras o números).
3. Haga clic en el cliente. Aparece "Cliente: …" y su nombre queda en el panel derecho.

Para volver a consumidor final, pulse F3 y elija **Consumidor final**. Si el cliente no existe, la caja dice "No encontramos clientes
con ese dato": los clientes nuevos se crean en la administración (Clientes).

![Cliente de la venta: búsqueda por cédula, NIT, nombre o celular.](imagenes/uso/caja-14-cliente-buscar.png)

![Venta con el cliente asignado en el panel derecho.](imagenes/uso/caja-15-cliente-asignado.png)

## 6. Descuentos y autorización del supervisor (F6)

1. Seleccione la línea (si el descuento es para un producto) y pulse **F6**.
2. Elija **A la línea seleccionada** o **A toda la venta**.
3. Elija **Porcentaje (%)** o **Valor en pesos ($)** y escriba el valor.
4. Escriba el **Motivo** (es obligatorio) y pulse **Aplicar descuento**.

![Descuento del 10 % a la línea seleccionada, con su motivo.](imagenes/uso/caja-17-descuento.png)

Si su usuario no tiene permiso para dar ese descuento, aparece **Autorización de supervisor** con el aviso de la acción que se va a
autorizar. El supervisor escribe su **código** y su **PIN** (quedan ocultos), opcionalmente un motivo, y pulsa **Autorizar** (o Enter).
La acción se completa sola y queda registrada con el nombre de quien autorizó.

![La cajera no tiene el permiso: la caja pide la autorización de un supervisor.](imagenes/uso/caja-18-autorizacion-de-supervisor.png)

![El supervisor escribe su código y su PIN.](imagenes/uso/caja-19-autorizacion-de-supervisor-datos.png)

![Descuento aplicado: la línea muestra «Descuento manual».](imagenes/uso/caja-20-venta-con-cliente-y-descuento.png)

La misma ventana de autorización aparece en cualquier acción que su usuario no pueda hacer solo: cancelar una venta, anular una línea,
vender un lote vencido o digitar un peso, según los permisos que le haya dado el administrador. Si el PIN es incorrecto, la caja lo
avisa y borra el PIN para escribirlo de nuevo. **Cancelar** (o Esc) cierra la ventana sin hacer la acción.

## 7. Cobrar (F10)

Pulse **F10** (o el botón **Cobrar**). El panel derecho cambia a **Cobrar**: arriba el **Total a pagar**, el **Recibido** y lo que
**Falta** (en naranja) o el **Cambio** (en verde); en el centro los botones de los medios de pago; abajo el valor recibido y el botón
**Confirmar cobro F10**. **Esc** (o **Volver**) regresa a la venta sin cobrar.

![Panel de cobro: efectivo elegido por defecto y valores rápidos.](imagenes/uso/caja-21-cobro.png)

### Efectivo

1. **Efectivo** viene elegido y el valor recibido ya tiene el total exacto.
2. Escriba lo que le entregó el cliente (por ejemplo 100000) o pulse uno de los **valores rápidos** (el exacto y los billetes que cubren
   el total).
3. Pulse **Enter** o **F10**. La caja muestra el **Cambio** en grande y queda lista para el siguiente cliente. El tiquete se imprime solo.

![Efectivo: recibido $ 100.000 y el cambio a entregar.](imagenes/uso/caja-22-cobro-efectivo-con-cambio.png)

![Venta cobrada: total, recibido, cambio y estado de la factura electrónica.](imagenes/uso/caja-23-venta-cobrada.png)

Si la tienda tiene facturación electrónica, debajo del cambio aparece el estado del documento: "validada por la DIAN", "en proceso" (se
envía en segundo plano), "en contingencia" (se enviará cuando haya conexión) o "con problema" (la administración debe revisarla). En
todos los casos la venta ya quedó cobrada: entregue el tiquete y siga vendiendo.

Cuando hay productos por peso, el total puede tener centavos; al cobrar en efectivo se redondea y la diferencia aparece como
**Redondeo** en el panel y como ajuste en el tiquete.

### Tarjeta débito o crédito (datáfono)

1. En el cobro pulse **Tarjeta débito** o **Tarjeta crédito**.
2. Pase la tarjeta por el datáfono por el valor del total.
3. Escriba en **Referencia (aprobación del datáfono)** el número de aprobación del voucher.
4. Pulse **F10**. El cambio queda en $ 0.

![Tarjeta débito: la caja pide la referencia de aprobación del datáfono.](imagenes/uso/caja-26-cobro-tarjeta-con-referencia.png)

![Venta cobrada con tarjeta.](imagenes/uso/caja-27-venta-cobrada-con-tarjeta.png)

Los medios que piden referencia (datáfono, y según la configuración de la tienda también transferencias o billeteras como Nequi) no se
agregan sin ella: la caja avisa "Escriba la referencia del pago". Un medio distinto del efectivo no puede pasar del total (no da cambio).

### Pago mixto (dos o más medios)

Cuando el cliente paga una parte con un medio y el resto con otro:

1. Pulse el primer medio (por ejemplo **Nequi**), escriba el valor que paga con él y, si la pide, la referencia.
2. Pulse **otra vez el mismo botón** para agregarlo. Queda en la lista de pagos y la caja muestra lo que **Falta**.
3. Pulse el siguiente medio (por ejemplo **Efectivo**): el valor ya trae lo que falta. Cámbielo si le entregan más (para el cambio) y
   pulse **otra vez ese botón** para agregarlo.
4. Cuando lo recibido cubre el total, pulse **F10** para confirmar.

> **Importante:** en el pago mixto cada medio se agrega pulsando su botón dos veces (una para elegirlo y otra para agregarlo). Si
> escribe el último valor y pulsa F10 sin agregarlo, la caja no cobra porque todavía "falta".

Para quitar un pago mal agregado, pulse la **X** al lado de ese pago.

![Pago mixto: $ 15.000 por Nequi agregados; la caja muestra lo que falta.](imagenes/uso/caja-24-pago-mixto-primer-medio.png)

![Pago mixto completo: Nequi más efectivo, con el cambio.](imagenes/uso/caja-25-pago-mixto-completo.png)

### Sistecrédito

Con Sistecrédito el cliente paga **a cuotas** con su crédito. Solo aparece si la tienda conectó su cuenta de aliado (lo hace el
administrador). Se necesita **Internet en el momento del cobro**; si no hay, cobre con otro medio.

1. En el cobro pulse **Sistecrédito**. El valor trae lo que falta; puede cobrar todo o una parte (pago mixto).
2. Elija el tipo de documento (**CC**, CE, NIT, PA o TI) y escriba el **número de documento del cliente**. Si la venta tiene cliente
   asignado, el documento ya aparece.
3. Pulse **Generar cobro de $ …**. Aparece un **código QR** con el mensaje "Escanee con su celular, elija las cuotas y confirme con el
   código que le llega".
4. El cliente escanea el QR con su celular y confirma. La caja se actualiza sola cada pocos segundos.
5. Cuando Sistecrédito **aprueba**, aparece "Sistecrédito aprobó el crédito…" y la venta se completa sola (o, en pago mixto, la caja deja
   listo el valor que falta para cobrarlo con otro medio).
6. Si Sistecrédito **no aprueba** o el cobro vence, la caja muestra el motivo: pulse **Intentar de nuevo** o elija otro medio.

Para dejar de esperar, pulse **Cancelar** debajo del QR. Si el cliente igual aprueba después, el sistema lo detecta y la administración lo
revisa.

![Sistecrédito: tipo y número de documento del cliente y el botón Generar cobro.](imagenes/uso/caja-28-cobro-sistecredito.png)

### Reimprimir el tiquete

El botón **Reimprimir** de la barra superior vuelve a imprimir el tiquete de la **última venta** (sale marcado como copia). Según los
permisos de su usuario, puede pedir la autorización del supervisor.

## 8. Anular una línea (Supr)

Si registró un producto por error o el cliente ya no lo quiere:

1. Seleccione la línea con `↑` `↓` o con un clic.
2. Pulse **Supr** (o el botón **Anular línea**). Según los permisos, la caja puede pedir la autorización del supervisor.

La línea no desaparece: queda en la lista con la etiqueta **Anulada** y en $ 0, para que quede registro.

![Línea anulada: queda en la lista marcada «Anulada» y en $ 0.](imagenes/uso/caja-29-linea-anulada.png)

## 9. Suspender y recuperar una venta (F8 y F9)

Cuando un cliente olvidó algo y va a buscarlo, suspenda su venta para atender al siguiente:

1. Pulse **F8**. Aparece "Venta suspendida. Recupérela con F9." y la caja queda vacía. En la barra superior aparece el aviso
   **"1 suspendida"**.
2. Atienda al siguiente cliente normalmente.
3. Cuando el primer cliente vuelva, pulse **F9** (o el aviso de la barra). Aparece la lista de **Ventas suspendidas** con la hora, la
   cantidad de artículos y el total.
4. Haga clic en la venta: vuelve con todas sus líneas y puede seguir registrando o cobrar.

![Venta suspendida: la caja queda vacía y la barra avisa «1 suspendida».](imagenes/uso/caja-30-venta-suspendida.png)

![F9: lista de ventas suspendidas para recuperar.](imagenes/uso/caja-31-ventas-suspendidas.png)

![La venta recuperada vuelve con sus líneas.](imagenes/uso/caja-32-venta-recuperada.png)

> **Importante:** no cierre el turno con ventas suspendidas olvidadas: recupérelas y cóbrelas o cancélelas.

## 10. Cancelar la venta (F7)

Si el cliente desiste de toda la compra:

1. Pulse **F7** (o **Cancelar venta**).
2. La caja pregunta "¿Cancelar la venta?" con la cantidad de productos y el valor. Pulse **Sí, cancelar venta**, o **No, seguir vendiendo**
   (o Esc) para volver.
3. Según los permisos, la caja pide la autorización del supervisor. Aparece "Venta cancelada." y queda registrada en la auditoría.

![Confirmación para cancelar la venta.](imagenes/uso/caja-33-cancelar-venta.png)

Cancelar una venta **en curso** es distinto de anular una venta ya cobrada: una venta cobrada no se anula desde la caja.

## 11. Cerrar el turno y el reporte Z

Al terminar su turno:

1. Cobre, suspenda o cancele la venta en curso (si hay una, la caja avisa "Cobre, suspenda o cancele la venta en curso antes de cerrar").
2. Pulse **Cerrar turno** en la barra superior.
3. Cuente el efectivo del cajón y escriba **cuánto dinero hay de cada valor**, igual que al abrir. El cierre es **ciego**: la caja no le
   muestra cuánto debería haber; compara cuando usted confirma.
4. Escriba el total de cada uno de los **demás medios** (tarjetas, Nequi, Sistecrédito…): súmelos con los vouchers del datáfono y los
   comprobantes del día.
5. Si sabe de una diferencia, explíquela en **Nota sobre la diferencia** (por ejemplo, "faltaron $ 2.000 en monedas").
6. Pulse **Confirmar cierre**. Si quiere seguir vendiendo, pulse **Volver a vender**.

![Cerrar turno: conteo ciego del efectivo por valor.](imagenes/uso/caja-39-cerrar-turno.png)

![Efectivo contado y los demás medios de pago.](imagenes/uso/caja-40-cerrar-turno-conteo.png)

![Nota sobre la diferencia y los botones Volver a vender y Confirmar cierre.](imagenes/uso/caja-41-cerrar-turno-conteo-continuacion.png)

Al confirmar aparece **Turno cerrado · Reporte Z (cierre)** en pantalla y el reporte Z se imprime solo ("Reporte Z enviado a la
impresora."). Guarde el reporte impreso con el efectivo. Si la impresora falló, pulse **Reimprimir Z**. Pulse **Terminar** para salir.

![Reporte Z en pantalla al cerrar el turno.](imagenes/uso/caja-42-reporte-z.png)

![Final del reporte Z con el código de control y los botones Reimprimir Z y Terminar.](imagenes/uso/caja-43-reporte-z-continuacion.png)

Si un turno quedó abierto (por ejemplo, el cajero se fue sin cerrar), un supervisor lo puede cerrar desde la administración.

## 12. Lo que cambia según el tipo de negocio

BusinessPost sirve para cualquier comercio. Al instalar, el dueño elige el **tipo de negocio** (supermercado, fruver o carnicería,
droguería, ropa y calzado, ferretería u otro) y en la administración enciende o apaga funciones como la báscula, los lotes y
vencimientos, la venta por fracción o el precio abierto. Todo lo explicado hasta aquí funciona igual en todos los tipos; esta sección
explica solo lo que cambia en la caja.

### Supermercado o minimercado

Funciona todo lo anterior. Si la tienda tiene sección de fruver o carnes, mire también la báscula (abajo); si vende productos con fecha
de vencimiento, mire la parte de lotes (droguería).

### Fruver o carnicería: báscula

Hay dos formas de vender por peso:

- **Etiqueta de báscula:** la báscula etiquetadora imprime una etiqueta con código de barras (empieza por 20 a 28) que ya trae el peso o el
  precio. Se escanea como cualquier producto; la línea queda con la etiqueta **Etiqueta**.
- **Báscula conectada a la caja:** la báscula de mostrador está conectada al computador de la caja y BusinessPost lee el peso solo.

**Cómo se pesa con la báscula conectada.** No hay botón "Pesar":

1. Ponga el producto en la báscula.
2. Escanee o escriba el código del producto (o búsquelo por nombre) y pulse **Enter**.
3. BusinessPost lee el peso en ese momento y agrega la línea con el peso, el precio por kilo y la etiqueta **Báscula**.

Mientras lee, se abre una ventana con el nombre del producto, el peso en vivo y un aviso:

- **"Ponga el producto en la báscula"**: la báscula está en cero. La caja espera unos segundos; si no llega peso, dice "No llegó peso a la
  báscula: no se agregó nada".
- **"Esperando peso estable…"**: el producto se está moviendo. En cuanto el peso se queda quieto, la línea se agrega sola. Si no se queda
  quieto, dice "El peso no se quedó quieto: no se agregó. Vuelva a registrar el producto".
- **"Retire el producto de la báscula"**: después de cada pesada **la báscula debe volver a cero** antes de pesar el siguiente producto.
  Retire el producto anterior y la lectura sigue sola. Si la báscula no queda vacía, dice "La báscula no quedó vacía después de la pesada
  anterior: no se agregó. Retire el producto y vuelva a registrarlo".
- **Sobrepeso o peso negativo**: aviso claro; no se agrega nada.

**Esc** cancela la lectura sin agregar nada.

![Producto de báscula registrado con la báscula en cero: «Ponga el producto en la báscula».](imagenes/uso/caja-09-bascula-ponga-el-producto.png)

![El peso se está moviendo: «Esperando peso estable…».](imagenes/uso/caja-10-bascula-esperando-peso-estable.png)

![La línea se agregó sola con 1,256 kg y la etiqueta «Báscula».](imagenes/uso/caja-11-venta-con-producto-pesado.png)

**Tara.** Si el producto se empaca siempre en la misma bolsa o bandeja, el administrador le pone su **tara** y la caja la descuenta sola
(la ventana muestra "tara … g"). Para una bolsa o bandeja distinta, póngala vacía en la báscula y pulse **Ins**: aparece "Tara tomada:
… kg." y ese peso se descuenta de la siguiente pesada, en lugar de la tara del producto. Esa tara manual se quita sola al agregar la
línea o al cancelar con Esc. Si la báscula no responde, Ins avisa el problema.

**Corregir un producto pesado.** La cantidad de un producto pesado no se cambia con F4: anule la línea con **Supr** y vuelva a pesarlo.
Con los productos de báscula no escriba cantidad con `3*`: deje que la báscula pese.

**Peso digitado.** Si la báscula no responde (apagada, desconectada o con el cable suelto) y la caja lo permite, se abre **Digitar el
peso** con el mensaje del problema. Escriba el peso **en kilos** (por ejemplo 0,700) y pulse **Enter** o **Agregar**. Digitar el peso
necesita el permiso "Digitar el peso" o la autorización del supervisor; la línea queda marcada **Peso digitado** en la caja y en el
tiquete, y queda en la auditoría. Si la caja no permite digitar el peso, el mensaje dice "Avise al administrador".

![La báscula no responde: la caja permite digitar el peso con autorización.](imagenes/uso/caja-35-peso-digitado.png)

![La línea queda marcada «Peso digitado».](imagenes/uso/caja-36-venta-1366x768.png)

### Droguería: lotes y vencimientos

En los productos que manejan lotes y fecha de vencimiento, **el cajero no elige el lote**: la caja descuenta primero el lote que vence
antes. Se vende igual que cualquier producto.

Si el producto tiene existencias de un **lote vencido**, la caja avisa "El producto tiene existencias de un lote vencido: venderlo
requiere autorización… Retire los vencidos de la góndola." y abre la **Autorización de supervisor**. El supervisor decide si autoriza la
venta; si no, pulse Cancelar y no lo venda. En cualquier caso, avise para que retiren los vencidos de la góndola.

### Ferretería, papelería o miscelánea: fracción y precio abierto

**Venta por metro o fracción.** En los productos que el administrador marcó con "Permite cantidades con decimales" (cable, manguera,
cadena, tela…) la cantidad puede llevar decimales:

- `1,5*` y el código → 1,5 metros.
- O registre el producto y cambie la cantidad con **F4** (por ejemplo 2,25).

En los productos que no lo permiten, la caja rechaza la cantidad con decimales ("Cantidad inválida: mayor que cero y entera si el
producto no se vende por fracciones").

**Precio abierto.** Hay productos sin precio fijo que el administrador marca con "Permite precio abierto en la caja". Si un producto así
no tiene precio, al registrarlo entra en $ 0 y el panel derecho muestra el aviso "…: fije el precio (precio abierto) antes de cobrar".
**En esta versión la pantalla de la caja todavía no tiene el campo para escribir el precio**, y la venta no se puede cobrar con un
producto en $ 0 ("Hay productos sin precio"). Mientras tanto: anule esa línea con **Supr**, cobre el resto y pida al administrador que le
ponga precio al producto en **Productos y precios**.

### Ropa, calzado y deportes

Las **tallas y colores** (variantes) todavía no existen en BusinessPost: la función aparece como "Disponible en una próxima versión".
Hoy la caja vende la ropa y el calzado igual que cualquier producto, con su código de barras.

### Otro tipo de negocio

La caja funciona con lo básico de este manual: escanear o buscar, cantidades, cliente, descuentos, cobro y cierre.

## 13. Atajos de teclado

En cualquier momento, **F1** muestra la lista de atajos en pantalla.

![F1: atajos de la caja (con báscula activa aparecen Enter para pesar e Ins).](imagenes/uso/caja-34-ayuda-atajos.png)

| Tecla | Qué hace | Cuándo funciona |
|---|---|---|
| `Enter` | Agrega el código escaneado o escrito (código de barras, código interno o PLU). Con letras, busca por nombre | En el escáner |
| `3*código` o `3*nombre` y Enter | Registra varias unidades (también con decimales: `1,5*código`) | En el escáner |
| `↑` `↓` | Eligen el producto en la lista de sugerencias; sin lista, mueven la línea seleccionada | En el escáner |
| `Enter` (con la lista abierta) | Agrega el producto elegido | Lista de sugerencias |
| `Esc` | Cierra la lista de sugerencias, la ventana abierta o vuelve del panel (cobro, cliente, suspendidas); cancela la lectura de la báscula | Siempre (no cierra la pantalla de cierre de turno) |
| `F1` | Abre o cierra la ayuda de atajos | Sin ventana abierta (también sin turno) |
| `F2` | Vuelve al escáner | Sin ventana abierta |
| `F3` | Cliente de la venta | Con turno abierto |
| `F4` | Cambiar la cantidad de la línea seleccionada (Enter aplica) | Con una línea seleccionada |
| `F5` | Consultar precio sin vender | Sin ventana abierta (también sin turno) |
| `F6` | Descuento a la línea o a toda la venta | Con productos en la venta |
| `F7` | Cancelar la venta | Con una venta en curso |
| `F8` | Suspender la venta | Con productos en la venta |
| `F9` | Recuperar una venta suspendida | Con turno abierto |
| `F10` | Abre el cobro; dentro del cobro, confirma el cobro | Con productos en la venta / en el cobro |
| `Enter` | Confirma el cobro | En el campo "Recibido en …" |
| `Supr` | Anular la línea seleccionada | Con una línea seleccionada |
| `Ins` | Tara manual de la báscula (bolsa o bandeja) | Solo con báscula conectada |
| `Enter` | Aplicar la cantidad, agregar el peso digitado o autorizar | Ventanas Cambiar cantidad, Digitar el peso y Autorización de supervisor |

Si empieza a escribir o a escanear con el cursor en otra parte, la caja devuelve el cursor al escáner sola y no se pierde la lectura.

## 14. Problemas comunes y soluciones

| Lo que pasa | Qué hacer |
|---|---|
| La barra dice **Sin conexión** y aparece "Sin conexión con el servidor de la tienda" | La venta en curso no se pierde. La caja reintenta cada 15 segundos y avisa "Se recuperó la conexión". Revise el cable de red o el wifi; si sigue igual, avise al administrador. |
| El producto no se agrega al escanear | Escriba el código a mano y pulse Enter, o búsquelo por nombre. Si aparece "No encontramos productos con …", el producto no está creado: aparte el producto y avise al administrador. |
| "No hay existencias suficientes en la bodega de la caja" | El sistema no tiene existencias registradas de ese producto. Un supervisor puede hacer un ajuste rápido en la administración. |
| Aviso de **lote vencido** | Pida la autorización del supervisor o no venda el producto. Avise para que retiren los vencidos (sección 12, droguería). |
| "Escriba el motivo del descuento" | El motivo es obligatorio en todo descuento. |
| "Escriba la referencia del pago (número de aprobación del datáfono)" | Escriba el número de aprobación del voucher en **Referencia** antes de agregar el pago. |
| El botón **Confirmar cobro** no se ve completo (pantallas pequeñas) | Pulse **F10**: confirma el cobro igual. |
| Después de pesar un producto, **F10 no abre el cobro** o aparece "Ocurrió un error inesperado en la pantalla" | Pulse **Recargar** en el aviso o la tecla **Ctrl+F5**. La venta en curso no se pierde. Si se repite, avise al administrador. |
| "No se pudo leer la báscula (COM…)" (a veces con un texto técnico en inglés, como "Could not find file 'COM7'") | La báscula está apagada, desconectada o en otro puerto. Revise que esté encendida y el cable. Mientras tanto puede digitar el peso con autorización (sección 12). |
| "La báscula de esta caja no está vinculada" | La báscula se cambió o es nueva: el administrador debe guardarla de nuevo en el computador de la caja (Configuración → Cajas e impresoras → Báscula). Mientras tanto, digite el peso si la caja lo permite. |
| La lectura de la báscula se queda en "Retire el producto de la báscula" | Quite todo lo que haya sobre la báscula hasta que marque cero. Si registró mal la última pesada, anúlela con Supr y vuelva a pesar. |
| "Hay productos sin precio" al cobrar | Hay una línea en $ 0 (precio abierto). Anúlela con Supr y avise al administrador (sección 12, ferretería). |
| El tiquete no se imprimió | La venta sí quedó cobrada. Revise papel, tapa y cable de la impresora y pulse **Reimprimir**. Si aparece un aviso de la impresora, avise al administrador. |
| El reporte Z no se imprimió | Pulse **Reimprimir Z** antes de pulsar Terminar. El administrador también puede imprimirlo desde **Turnos de caja**. |
| "Su usuario no puede buscar clientes" | Pida ayuda al supervisor. |
| Sistecrédito no genera el cobro | Sistecrédito necesita Internet en el momento del cobro. Cobre con otro medio. |
| "Cobre, suspenda o cancele la venta en curso antes de cerrar" | Termine la venta que está en pantalla antes de cerrar el turno. |
| "No hay un medio de pago en efectivo activo" al abrir el turno | El administrador debe activar el medio Efectivo en la configuración. |
| El supervisor escribió mal el PIN | La caja avisa y borra el PIN: escríbalo de nuevo. |
| Aviso "La licencia está restringida" | Algunas funciones pueden no estar disponibles. Avise al administrador o al dueño. |
| Diferencia de dinero al cerrar | Escriba la explicación en **Nota sobre la diferencia**. El administrador revisa las diferencias en **Turnos de caja**. |

## 15. Hoja de atajos para imprimir

Recorte esta página y péguela junto a la caja.

| Tecla | Para qué |
|---|---|
| `Enter` | Agregar el producto escaneado, escrito o elegido en la lista |
| `3*` + código | Varias unidades (decimales: `1,5*` + código) |
| Nombre + `↑` `↓` + `Enter` | Buscar por nombre y agregar |
| `↑` `↓` | Elegir la línea de la venta |
| `F1` | Ver todos los atajos |
| `F2` | Volver al escáner |
| `F3` | Cliente de la venta |
| `F4` | Cambiar cantidad |
| `F5` | Consultar precio (sin vender) |
| `F6` | Descuento (con motivo) |
| `F7` | Cancelar la venta |
| `F8` | Suspender la venta |
| `F9` | Recuperar una venta suspendida |
| `F10` | Cobrar / confirmar el cobro |
| `Supr` | Anular la línea seleccionada |
| `Ins` | Tara manual de la báscula |
| `Esc` | Cerrar, volver o cancelar la lectura de la báscula |

**Cobro rápido:** `F10` → escriba lo recibido → `Enter`. **Pago mixto:** medio → valor → mismo medio otra vez → siguiente medio → valor → ese medio otra vez → `F10`.

**Báscula:** producto en la báscula → código → `Enter`. Antes del siguiente, la báscula debe volver a cero.

**Cierre:** Cerrar turno → contar el efectivo por valor → demás medios → Confirmar cierre → guardar el reporte Z con el efectivo.
