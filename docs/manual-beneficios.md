# Beneficios y buenas prácticas de BusinessPost

Este manual le cuenta **qué gana su negocio** con cada parte de BusinessPost y **cómo sacarle el mayor provecho** en el día a día:
qué revisar cada día, cada semana y cada mes, cómo cuidar su información y cómo organizar a su equipo. Al final encontrará
recomendaciones según el tipo de negocio.

No necesita conocimientos técnicos. Si todavía no ha instalado el programa, empiece por el
[Manual de instalación](manual-instalacion.md).

> BusinessPost tiene dos ediciones: **Caja Única** (un solo computador hace todo) y **Multicaja** (un equipo principal y varias
> cajas en la red del local). **Las dos traen todas las funciones**: no hay planes por módulos ni funciones que se paguen aparte.
> El **tipo de negocio** que usted elige solo deja a la vista las funciones que su negocio usa; no cambia su plan ni su precio.

## Contenido

1. [Lo que gana su negocio con cada función](#1-lo-que-gana-su-negocio-con-cada-función)
   - [Tipo de negocio y funciones del negocio](#tipo-de-negocio-y-funciones-del-negocio)
   - [Ventas en la caja](#ventas-en-la-caja)
   - [Turnos y cierres de caja](#turnos-y-cierres-de-caja)
   - [Inventario](#inventario)
   - [Compras, proveedores y gastos](#compras-proveedores-y-gastos)
   - [Promociones](#promociones)
   - [Clientes](#clientes)
   - [Reportes y tablero](#reportes-y-tablero)
   - [Facturación electrónica](#facturación-electrónica)
   - [Sistecrédito](#sistecrédito)
   - [Báscula](#báscula)
   - [Nube y portal](#nube-y-portal)
   - [Backups](#backups)
   - [Seguridad y auditoría](#seguridad-y-auditoría)
   - [Lo que viene](#lo-que-viene)
2. [Cómo sacarle el mayor provecho](#2-cómo-sacarle-el-mayor-provecho)
   - [Cierre de caja todos los días](#cierre-de-caja-todos-los-días)
   - [Backups fuera del local](#backups-fuera-del-local)
   - [Conteos cíclicos de inventario](#conteos-cíclicos-de-inventario)
   - [Promociones que sí venden](#promociones-que-sí-venden)
   - [Los reportes clave y cuándo mirarlos](#los-reportes-clave-y-cuándo-mirarlos)
   - [Usuarios, roles y permisos](#usuarios-roles-y-permisos)
   - [Parámetros que vale la pena revisar](#parámetros-que-vale-la-pena-revisar)
3. [Recomendaciones por tipo de negocio](#3-recomendaciones-por-tipo-de-negocio)
4. [Listas de verificación](#4-listas-de-verificación)
   - [Todos los días](#todos-los-días)
   - [Cada semana](#cada-semana)
   - [Cada mes](#cada-mes)

---

## 1. Lo que gana su negocio con cada función

### Tipo de negocio y funciones del negocio

Al instalar, BusinessPost pregunta **"¿Qué tipo de negocio tienes?"** y usted elige una de seis tarjetas: Supermercado o
minimercado, Fruver o carnicería, Droguería, Ropa/calzado/deportes, Ferretería/papelería/miscelánea u Otro tipo de negocio. Según
lo que elija, el programa deja encendidas las funciones que ese negocio usa y crea unas **categorías de ejemplo** (por ejemplo, en
droguería: Medicamentos, Cuidado personal, Primeros auxilios…).

Después puede cambiar todo en **Administración → Configuración → Funciones del negocio**:

- **Tipo de negocio:** elija otro tipo y pulse **Cambiar el tipo de negocio**. Los interruptores vuelven a los valores recomendados
  para ese tipo.
- **Interruptores:** encienda o apague cada función por separado:

| Función | Qué deja a la vista |
|---|---|
| **Báscula y PLU** | Leer el peso de la báscula en la caja, PLU de báscula, tara y el botón **Báscula** en Cajas e impresoras. |
| **Lotes y vencimientos** | Las casillas **Maneja lotes** y **Maneja fecha de vencimiento** en la ficha del producto. |
| **Venta por metro o fracción** | La casilla **Permite cantidades con decimales** en productos que se venden por unidad (1,5 metros de cable). |
| **Precio abierto** | La casilla **Permite precio abierto en la caja**: el precio se escribe al vender. |
| **Promociones** | El menú **Promociones**. |
| **Compras y proveedores** | Los menús **Compras**, **Proveedores** y **Cuentas por pagar**. |
| **Tallas y colores** | Aparece deshabilitada: "Disponible en una próxima versión". |

Lo importante:

- **Apagar una función solo la oculta: no se borra ningún dato.** Si la vuelve a encender, todo aparece de nuevo. Si un producto ya
  tenía datos de una función apagada, el campo se sigue viendo con el aviso "Esta función está apagada; los datos se conservan".
- Si apaga **Promociones**, las promociones activas **siguen aplicándose** en la caja: termínelas antes si ya no las quiere.
- Las etiquetas de báscula y los productos con precio abierto o fracción que ya existen siguen vendiéndose aunque apague la función.
- Solo el **Propietario** y el **Administrador** pueden cambiar el tipo de negocio y los interruptores. Cada cambio queda en la
  **Auditoría** con lo que se encendió y lo que se apagó.

Vea qué elegir en [Recomendaciones por tipo de negocio](#3-recomendaciones-por-tipo-de-negocio).

### Ventas en la caja

![La caja con una venta en curso: promociones aplicadas solas y los botones con su tecla (F3, F4, F6, Supr, F8, F9, F7 y F10).](imagenes/uso/caja-07-venta-con-productos-y-promociones.png)

| Función | Lo que gana |
|---|---|
| Venta con lector de código de barras o buscando por nombre (**F2**) | Atención rápida y sin errores de digitación. |
| Varias unidades con `3*código` y **F4** para cambiar la cantidad | Menos escaneos repetidos en compras grandes. |
| **F5 Consultar precio** sin vender | Responde al cliente sin abrir una venta. |
| **F8 Suspender** y **F9 Recuperar** | Si al cliente le falta algo, atiende al siguiente sin perder la venta. |
| **F10 Cobrar** con varios medios en la misma venta (efectivo, datáfono, transferencia, Sistecrédito…) | El cliente paga como quiera y la caja cuadra por cada medio de pago. |
| **F3 Cliente** de la venta | Sabe quién le compra y qué compra (vea [Clientes](#clientes)). |
| **Supr Anular línea** (quitar un producto antes de cobrar) | Quien tiene el permiso de anular líneas corrige solo; si no lo tiene, la caja pide la autorización del supervisor. Cada línea quitada queda en la **Auditoría** y en el reporte **Antifraude por cajero**. |
| **F6 Descuento** y **F7 Cancelar venta** (la venta en curso) | Se pueden hacer, pero quedan registrados y, si el cajero no tiene permiso, piden la **autorización de un supervisor** en la misma pantalla. |
| Funciona **sin Internet** | La tienda sigue vendiendo aunque se caiga el Internet; lo pendiente (nube, facturas, backups en la nube) se envía solo cuando vuelve. |
| **F1** muestra todos los atajos | El cajero nuevo aprende solo. |

> Una venta **ya cobrada** todavía no se puede anular ni cambiar desde la caja: vea [Lo que viene](#lo-que-viene).

> **Multicaja:** las cajas adicionales trabajan contra el **equipo principal** por la red del local. Si el equipo principal se
> apaga o la red falla, la caja muestra "Sin conexión con el servidor de la tienda" y la venta en curso no se pierde; se reintenta
> sola. Por eso conviene tener una **UPS** (respaldo de energía) en el equipo principal y en el equipo de red.

### Turnos y cierres de caja

![Cerrar turno: conteo ciego del efectivo por valor y de los demás medios de pago.](imagenes/uso/caja-40-cerrar-turno-conteo.png)

- **Abrir turno:** el cajero cuenta la base del cajón por denominaciones. Queda claro con cuánto empezó.
- **Cerrar turno a ciegas:** el cajero cuenta el efectivo y escribe el total de los demás medios **sin ver lo esperado**. El
  sistema compara al confirmar. Así el conteo es honesto y no se "acomoda" a lo que dice el sistema.
- **Reporte Z (cierre):** se imprime al cerrar y se puede reimprimir.
- **Administración → Turnos de caja:** ve todos los turnos (abiertos, en cierre, cerrados) y los que están **Por revisar**. El
  supervisor revisa la diferencia y deja su revisión registrada.

**Lo que gana:** sabe cada día cuánto debía haber y cuánto hubo, por cajero y por medio de pago, y las diferencias no pasan
desapercibidas.

### Inventario

![Existencias con el filtro «Solo bajo el mínimo»: los productos que hay que pedir.](imagenes/uso/adm-21-existencias-bajo-el-minimo.png)

| Función (menú **Inventario**) | Lo que gana |
|---|---|
| **Productos y precios**: datos, precios por lista y sucursal, presentaciones (por ejemplo caja × 12), varios códigos de barras, impuestos | Un solo lugar para mantener el catálogo; los cambios de precio quedan en la auditoría. |
| **Existencias** por bodega, con costo promedio, valor y **mínimo** | Sabe qué tiene, cuánto vale y qué se está acabando. |
| **Conteos** completos o **parciales** (por categorías o productos) | Corrige diferencias sin cerrar la tienda (vea [Conteos cíclicos](#conteos-cíclicos-de-inventario)). |
| **Ajustes** con motivo, y **Ajuste rápido** para el producto que está en la tienda pero el sistema no tiene | Toda entrada o salida tiene una razón y un responsable. |
| **Traslados** entre bodegas (en tránsito, recibido, recibido con diferencias) | Controla lo que se mueve entre bodegas o sucursales. |
| **Importar desde Excel** productos, precios y saldo inicial, con vista previa de errores | Arranca rápido y cambia muchos precios de una vez. |
| **Etiquetas** de góndola y adhesivos con código de barras (hojas A4 o rollo de etiquetas), por producto, categoría, cambios de precio o compra | La góndola muestra el mismo precio y la misma oferta que cobra la caja; los productos sin código reciben un código interno. |
| **Reabastecimiento**: mínimos y máximos por bodega y **pedido sugerido** que crea las órdenes de compra por proveedor | No se queda sin mercancía ni compra de más. |
| **Lotes y vencimientos** por producto (con la función **Lotes y vencimientos** encendida) | Vende primero lo que vence primero y no vende vencidos sin autorización. |

### Compras, proveedores y gastos

Están en el menú **Compras y proveedores** (se ve con la función **Compras y proveedores** encendida).

- **Compras:** órdenes de compra (borrador, enviada, recibida en parte, recibida) y **facturas del proveedor** con lotes y
  vencimientos. Al confirmar una compra, las existencias y el costo se actualizan solos.
- **Proveedores** y **Cuentas por pagar:** sabe a quién le debe, cuánto y desde cuándo.
- **Gastos** (menú **Ventas y caja**): registre arriendo, servicios, domicilios, etc. Si el gasto sale de la caja, el efectivo se
  descuenta de ese turno en el mismo momento, y el cierre sigue cuadrando.

**Lo que gana:** el costo de cada producto está al día, lo que hace que el reporte de **utilidad** sea real, y no se le pasan los
pagos a proveedores.

### Promociones

![Promoción · Simular: una venta de ejemplo calculada con el mismo motor de la caja.](imagenes/uso/adm-69-promocion-simular.png)

En **Clientes y promociones → Promociones** (con la función **Promociones** encendida) puede crear cinco tipos:

| Tipo | Ejemplo |
|---|---|
| **Lleve N pague M** | Lleve 3 pague 2 en jabones. |
| **Precio especial** | Arroz a $3.900 esta semana. |
| **Descuento en porcentaje** | 20 % en toda la categoría Aseo o en una marca. |
| **Precio por cantidad** | 1 a $2.000, 3 por $5.000. |
| **Combo a precio fijo** | Perro caliente + gaseosa a $8.000. |

Cada promoción se aplica a un producto, una categoría o una marca, con fechas y días de la semana. Empieza en **Borrador**, se
puede **Simular** antes de activarla, y luego se **pausa** o se **termina**. La caja la aplica sola: el cajero no tiene que
acordarse.

**Lo que gana:** promociones que se aplican igual en todas las cajas, sin descuentos "a ojo", y un reporte de cuánto le costó
cada una (**Descuentos y promociones**).

### Clientes

- Ficha del cliente con documento, celular, correo y **autorización del tratamiento de sus datos** (Ley 1581).
- **Grupos y listas de precios**: por ejemplo, un grupo MAYORISTAS con su propia lista de precios.
- Pestañas **Compras** e **Historial**: total comprado, ticket promedio, productos frecuentes, primera y última compra.
- Marca **Siempre pide factura** para quien la necesita.

**Lo que gana:** conoce a sus mejores clientes, les puede dar precios especiales y cumple la ley de protección de datos (el detalle
por cliente en los reportes solo aparece si el cliente autorizó).

### Reportes y tablero

![Tablero: ventas netas, tiquetes, ticket promedio, utilidad bruta, ventas por hora y lo más vendido.](imagenes/uso/adm-02-tablero.png)

- El **Tablero** muestra las ventas de hoy, las ventas por hora, los más vendidos y un recuadro **Requiere atención** con lo
  pendiente: cierres con diferencia por revisar, productos bajo el mínimo, alertas de backups, incidentes de la auditoría y
  solicitudes de datos personales por vencer. Cada aviso lleva a su pantalla.
- **Ventas y caja → Reportes** trae cerca de 40 reportes en ocho grupos: Ventas, Utilidad, Impuestos, Inventario, Compras y gastos,
  Caja, Antifraude y Auditoría. Todos se exportan a **Excel, CSV o PDF**.

Vea cuáles mirar y cuándo en [Los reportes clave](#los-reportes-clave-y-cuándo-mirarlos).

### Facturación electrónica

En **Ventas y caja → Facturación electrónica** (con su cuenta de Factus) elige el modo:

- **Apagada:** comprobante interno.
- **Solo cuando el cliente pide factura** (para la transición).
- **Factura electrónica en cada venta.**

Primero se trabaja en **Pruebas (sandbox)** y después en **Producción** (documentos reales ante la DIAN). La pestaña **Rangos de
numeración** avisa cuando un rango está **por agotarse o vencer**, y **Alertas y conciliación** compara cada día las ventas contra
las facturas aceptadas. Si el proveedor de facturación o el Internet fallan, la venta no se detiene: el documento queda pendiente
y se reintenta.

**Lo que gana:** cumple con la DIAN sin frenar la caja y sabe todos los días si alguna venta quedó sin factura aceptada.

### Sistecrédito

En **Configuración → Medios de pago** está la tarjeta **Sistecrédito**: pegue las tres llaves de su convenio y pulse **Conectar**.
Si la prueba funciona, Sistecrédito aparece como forma de pago en la caja: el cliente escanea un código QR con su celular, elige
las cuotas y confirma. La plata la paga Sistecrédito directamente a la tienda.

**Lo que gana:** vende a clientes que quieren pagar a cuotas, sin dar crédito usted mismo. Necesita Internet solo para ese cobro.
Si un cobro aprobado no terminó en venta, aparece en **Cobros por revisar** para que lo resuelva.

### Báscula

Necesita la función **Báscula y PLU** encendida (viene así en Supermercado y en Fruver o carnicería). Si está apagada, la caja no
lee la báscula y en el producto no salen el PLU, "Se vende por báscula" ni la tara.

![Cajas e impresoras · Báscula: marca y protocolo, puerto, esperas y la opción de digitar el peso. El peso en vivo aparece al pulsar Probar báscula con la báscula conectada.](imagenes/uso/integ-03-bascula.png)

- **Báscula etiquetadora** (sección de carnes o fruver): imprime una etiqueta con el peso o el precio y la caja la lee como un
  código de barras. En el producto se registra el **PLU de báscula**.
- **Báscula conectada a la caja** (cable serial o USB-serial): se configura en **Administración → Cajas e impresoras**, botón **Báscula** de cada caja (Toledo 8217, CAS
  o genérica) y se prueba con el **peso en vivo**. En la caja, al registrar un producto marcado **"Se vende por báscula"**, el
  sistema lee el peso solo, espera a que esté estable y descuenta la **tara** del producto. **Ins** hace tara manual.
- Si la báscula no responde, se puede digitar el peso solo con permiso o con autorización del supervisor; en la caja la línea queda
  marcada "Peso digitado" y en el tiquete el peso sale seguido de " (digitado)".
- Unidades de peso: kilogramo, gramo, libra internacional y **Libra (500 g)**, la de uso en Colombia.

**Lo que gana:** cobra exactamente lo que pesa, sin errores de digitación, y sabe cuándo un peso fue digitado a mano.

### Nube y portal

- Cada venta sube a la nube en cuanto hay Internet. Puede forzarlo en **Administración → Sincronización → Sincronizar ahora**.
- En el **portal web** de BusinessPost (desde cualquier celular o computador) usted ve **Datos de las tiendas**: ventas por día,
  ventas recientes, cierres de caja y existencias, por sucursal.
- Si un día no hubo Internet en la tienda, puede **Exportar paquete .possync** y cargarlo en el portal desde otro equipo
  (**Cargar paquete .possync**).
- En el portal también están **Mi suscripción**, **Descargas** (instalador y manuales) y **Mi cuenta**. La mensualidad se paga en
  línea desde **Administración → Licencia → Pagar mensualidad** (Nequi, PSE, tarjeta o Bancolombia).

**Lo que gana:** mira su negocio sin estar en el local.

### Backups

BusinessPost hace copias de seguridad solo, sin que nadie se acuerde:

- **Durante el día**, cada 4 horas (de 8:00 a.m. a 10:00 p.m.).
- **Cada noche** a las 11:30 p.m.
- **Después de los cierres de caja.**
- **Una restauración de prueba cada semana** (domingo de madrugada) para comprobar que las copias sí sirven.

Las copias van cifradas. En **Administración → Backups** ve el historial, puede pulsar **Respaldar ahora** y recibe avisos si el
último backup verificado es muy antiguo o si algún destino está fallando. Elegir a dónde van las copias (disco USB, otro equipo
o la nube) desde esa pantalla: **próximamente en Administración**.

**Lo que gana:** si se daña el computador, se lo roban o se quema el disco, su información se recupera.

### Seguridad y auditoría

- Cada persona entra con **su propio usuario** (contraseña en la administración; código de caja y PIN en la caja).
- **Roles** listos para usar (vea [Usuarios, roles y permisos](#usuarios-roles-y-permisos)).
- Bloqueo automático tras varios intentos fallidos y cierre de sesión por inactividad.
- **Administración → Auditoría** registra quién hizo qué y cuándo, con severidad (Información, Advertencia, Crítica), y permite
  **Verificar la integridad** de la bitácora.

**Lo que gana:** cada descuento, línea quitada, venta cancelada, cambio de precio o cambio en las funciones del negocio tiene nombre
propio.

### Lo que viene

Estas funciones todavía **no tienen pantalla**; llegarán en próximas versiones:

| Función | Dónde estará |
|---|---|
| **Cambios y devoluciones** de mercancía | Próximamente en la caja. |
| **Reintegro de dinero por garantía** (solo lo autorizará el propietario) | Próximamente en la caja. |
| **Anular una venta ya cobrada** | Próximamente en la caja. |
| **Retiros de efectivo** del cajón | Próximamente en la caja. |
| **Elegir el destino de los backups** (disco USB, otro equipo, nube) | Próximamente en Administración → Backups. |
| **Tallas y colores** en un mismo producto | Próximamente (ya aparece en Funciones del negocio, deshabilitada). |

Mientras tanto, antes de cobrar el cajero puede **quitar líneas (Supr)** o **cancelar la venta en curso (F7)**.

---

## 2. Cómo sacarle el mayor provecho

### Cierre de caja todos los días

1. Cada cajero **cierra su turno al terminar** (botón **Cerrar turno** en la caja). Nunca deje un turno abierto de un día para
   otro: el sistema alerta los turnos abiertos por más de 14 horas.
2. El conteo es **a ciegas**: pida a los cajeros que cuenten con calma y por denominaciones.
3. Si la diferencia supera $5.000 (valor que usted puede cambiar), el cajero debe escribir una observación y el turno queda **Por
   revisar**.
4. El supervisor entra a **Turnos de caja**, filtra **Por revisar**, abre cada turno y pulsa **Revisar** para dejar su revisión.
   Si un cajero se fue sin cerrar, el supervisor usa **Cerrar como supervisor**.
5. Guarde o archive el **Reporte Z** de cada turno.

> **Consejo:** una diferencia pequeña repetida todos los días con el mismo cajero dice más que una diferencia grande una sola vez.
> Revise el reporte **Antifraude por cajero** cada semana.

### Backups fuera del local

Los backups automáticos se guardan en el equipo principal. Eso protege contra un error, pero **no** contra un robo, un incendio o
un disco dañado. Por eso:

1. **Código de recuperación:** se muestra una sola vez en la instalación (o en **Backups → Generar el código** si quedó pendiente).
   Imprímalo y guárdelo **fuera del local**, por ejemplo en su casa. Sin ese código, los backups solo se abren en el mismo equipo.
2. **Destino externo:** poder elegir desde la pantalla un disco USB, una carpeta en otro equipo de la red o la copia en la nube
   llegará **próximamente en Administración → Backups**. Cuando esté, use un destino fuera del equipo principal.
3. **Mire la pantalla Backups una vez por semana:** que el último resultado sea correcto y que no haya avisos de destinos fallando.
4. La restauración la hace un técnico desde el equipo principal, nunca desde el navegador ([guía de recuperación](guia-recuperacion.md)).

### Conteos cíclicos de inventario

En lugar de cerrar la tienda un día para contar todo, cuente **un poco cada semana** con un **conteo parcial**:

1. **Inventario → Conteos → Nuevo conteo**, alcance **Parcial (categorías o productos)**.
2. Elija una o dos categorías por semana. Empiece por las de **más valor** o **más robo** (licores, aseo, cuidado personal, carnes).
3. Cuente con el lector o digitando el código; si quiere, anote la **ubicación** (góndola, bodega).
4. Revise las diferencias antes de aprobar el conteo. Los ajustes grandes (desde $500.000 a costo) necesitan la **aprobación de
   otro usuario**.
5. Haga un **conteo completo** una o dos veces al año.

> Por defecto, en un conteo los productos **no contados se excluyen** (no se ponen en cero). Si quiere lo contrario, cambie el
> parámetro correspondiente en **Configuración → Parámetros**.

Complemente con el reporte **Ajustes de inventario por motivo**: si un motivo crece mes a mes, ahí hay un problema.

### Promociones que sí venden

- **Simule siempre** antes de activar (pestaña **Simular**): vea cómo queda el precio.
- Use **fechas de inicio y fin** y días de la semana (por ejemplo, martes de fruver). Una promoción sin fin se olvida.
- **Liquide lo que está por vencer:** revise **Lotes por vencer y vencidos** y cree una promoción de esos productos antes de que
  venzan.
- **Mida el resultado:** compare **Ventas por producto** de la semana anterior con la semana de la promoción, y revise en
  **Descuentos y promociones** cuánto le costó.
- Si hay alguien encargado, dele el rol **Encargado de promociones**: crea y activa promociones sin acceso a la caja ni a costos.

### Los reportes clave y cuándo mirarlos

Todos están en **Ventas y caja → Reportes** y se exportan a Excel, CSV o PDF.

| Cuándo | Reporte | Para qué |
|---|---|---|
| **Cada día** | **Tablero** (Requiere atención) | Ver lo pendiente de un vistazo. |
| | **Resumen diario de ventas** | Total vendido, descuentos, impuestos y anuladas del día. |
| | **Cierres de caja** | Esperado, contado y diferencia por medio de pago. |
| | **Ventas por medio de pago** | Cuadrar efectivo, datáfono y transferencias con el banco. |
| **Cada semana** | **Productos bajo mínimo** | Hacer el pedido: trae el sugerido de compra y el proveedor preferido. |
| | **Lotes por vencer y vencidos** | Liquidar o devolver a tiempo. |
| | **Ventas por producto** y **Ventas por hora** | Qué se vende y a qué hora necesita más cajeros. |
| | **Descuentos y promociones** | Quién dio descuentos y quién los autorizó. |
| | **Antifraude por cajero** | Cajeros con más anulaciones, cancelaciones o descuentos que el promedio. |
| | **Edades de cartera por pagar** | Qué facturas de proveedores vencen. |
| **Cada mes** | **Utilidad por producto** y **Utilidad por categoría** | Dónde gana y dónde pierde. |
| | **Inventario valorizado** y **Productos sin rotación** | Cuánta plata tiene quieta en estantería. |
| | **Impuestos generados en ventas**, **Impuestos de compras** y **Libro de ventas diario** | Para su contador. |
| | **Conciliación de facturación electrónica** | Que todas las ventas tengan factura aceptada. |
| | **Gastos por categoría** y **Compras por proveedor** | Controlar gastos y negociar con proveedores. |
| | **Cambios de precios, costos e impuestos** y **Actividad fuera de horario** | Revisar cambios sensibles y movimientos a horas raras. |

### Usuarios, roles y permisos

Cree los usuarios en **Administración → Usuarios → Nuevo usuario**. Cada persona tiene **su propio usuario**: nunca comparta el
del propietario.

| Rol | Para quién | Puede |
|---|---|---|
| **Propietario** | El dueño | Todo. Solo él maneja el código de recuperación de los backups y activa la licencia (y autorizará el reintegro por garantía cuando llegue, vea [Lo que viene](#lo-que-viene)). |
| **Administrador** | Administrador del local | Toda la operación y la configuración (incluido el tipo de negocio y las funciones), salvo lo exclusivo del propietario. |
| **Supervisor de caja** | Jefe de cajas | Vender, autorizar descuentos, cancelaciones de la venta en curso, precios abiertos, vencidos y peso digitado; revisar y cerrar turnos; ver reportes de ventas, caja y antifraude. |
| **Cajero** | Cajeros | Vender, reimprimir y crear clientes rápidos. **Quitar una línea antes de cobrar** (**Supr**) depende del permiso de anular líneas: si no lo tiene, pide supervisor; siempre queda en la auditoría. **No** da descuentos ni cancela la venta sin supervisor. |
| **Inventario** | Bodeguero | Productos, existencias, conteos, ajustes, traslados e importación; también registra compras y define los **mínimos y máximos**. |
| **Compras** | Comprador | Proveedores, órdenes, facturas de compra y cuentas por pagar. También define los **mínimos y máximos** de existencias (quien compra suele fijarlos). |
| **Contador** | Su contador | Consultar reportes contables, impuestos, auditoría y exportar. No modifica nada. |
| **Encargado de promociones** | Mercadeo | Crear, simular, activar y terminar promociones. |

Buenas prácticas:

- **Autorizaciones de descuento:** deje a los cajeros con el rol **Cajero**. Cuando necesiten dar un descuento (**F6**), cancelar
  una venta (**F7**), vender un lote vencido o digitar un peso, aparece la ventana **Autorización de supervisor**: el supervisor
  escribe su código y PIN en la misma caja y la acción queda registrada con su nombre. La autorización dura 2 minutos.
- **Pocos supervisores:** cada supervisor puede autorizar; tenga uno o dos por turno, no más.
- **PIN personal:** pida que nadie preste su PIN. Si alguien se va del negocio, **Inactívelo** ese mismo día.
- **Contraseñas:** mínimo 8 caracteres; tras 5 intentos fallidos (3 con el PIN) el usuario se bloquea 15 minutos. Se puede
  **Desbloquear** desde Usuarios.
- **Sesiones:** la administración se cierra sola tras 30 minutos sin uso y la caja tras 15. No deje la administración abierta en
  la caja.
- Revise cada mes el reporte **Seguridad: ingresos, bloqueos, contraseñas, roles y autorizaciones**.

### Parámetros que vale la pena revisar

En **Configuración → Parámetros** (para toda la empresa o por sucursal) puede ajustar, entre otros:

| Parámetro | Valor inicial |
|---|---|
| Arqueo ciego (el cajero no ve lo esperado) | Encendido |
| Diferencia del cierre que exige observación y revisión | $5.000 |
| Horas de turno abierto para alertar | 14 |
| Redondeo del pago en efectivo | $50 |
| Valor de un ajuste de inventario que requiere aprobación de otro usuario | $500.000 |
| Días de anticipación para alertar lotes por vencer | 30 |
| Qué hacer si un precio queda por debajo del costo | Advertir (o bloquear) |
| Venta a Consumidor final que exige identificar al cliente | 0 = sin tope |

Cambie un parámetro solo cuando entienda el efecto; el cambio queda en la auditoría. En la lista también verá el tope de efectivo en
el cajón y los días para aceptar cambios de mercancía: se usarán con los retiros de efectivo y los cambios, que llegan próximamente
(vea [Lo que viene](#lo-que-viene)).

---

## 3. Recomendaciones por tipo de negocio

Elija su tipo de negocio en la instalación (pantalla **"¿Qué tipo de negocio tienes?"**) o después en **Administración →
Configuración → Funciones del negocio** (vea [Tipo de negocio y funciones del negocio](#tipo-de-negocio-y-funciones-del-negocio)).
Cada tipo enciende estas funciones al comenzar; usted puede encender o apagar cualquiera cuando quiera, sin perder datos:

| Tipo de negocio | Báscula y PLU | Lotes y vencimientos | Venta por metro o fracción | Precio abierto | Promociones | Compras y proveedores |
|---|---|---|---|---|---|---|
| Supermercado o minimercado | ✓ | ✓ | | | ✓ | ✓ |
| Fruver o carnicería | ✓ | | | | ✓ | ✓ |
| Droguería | | ✓ | | | ✓ | ✓ |
| Ropa, calzado o deportes | | | | | ✓ | ✓ |
| Ferretería, papelería o miscelánea | | | ✓ | ✓ | ✓ | ✓ |
| Otro tipo de negocio | | | | | ✓ | ✓ |

Además, al instalar se crean **categorías de ejemplo** según el tipo; puede cambiarlas cuando quiera. Las opciones de cada producto
están en su ficha (**Productos y precios → Datos**) y solo se ven si la función está encendida.

> Si su equipo se instaló antes de que existieran los tipos de negocio, quedó como **Supermercado o minimercado** con todas las
> funciones que ya usaba encendidas: nada desapareció.

### Supermercado o minimercado

- **Elija:** Supermercado o minimercado. **Queda encendido:** báscula y PLU, lotes y vencimientos, promociones y compras.
  **Categorías de ejemplo:** Abarrotes, Lácteos y huevos, Bebidas, Frutas y verduras, Carnes, Aseo y hogar, Cuidado personal.
- Marque **Maneja lotes** y **Maneja fecha de vencimiento** en lácteos, carnes frías, panadería y medicamentos de venta libre.
- Use **báscula** para fruver y carnes (etiquetadora en la sección o conectada a la caja).
- Ponga el **mínimo** y el **máximo** en los productos de alta rotación y haga el pedido con **Inventario → Reabastecimiento →
  Pedido sugerido**, que arma las órdenes de compra por proveedor.
- Use **promociones** semanales y **listas de precios** para clientes mayoristas o tenderos.
- Si vende cable, tela u otros productos por fracción, encienda **Venta por metro o fracción**.
- Multicaja: mire **Ventas por hora** y **Ventas por caja** para saber cuántas cajas abrir en las horas pico.

### Fruver o carnicería

- **Elija:** Fruver o carnicería. **Queda encendido:** báscula y PLU, promociones y compras. **Categorías de ejemplo:** Frutas,
  Verduras, Carnes, Pollo, Pescado, Abarrotes.
- Marque **Se vende por báscula** en todo lo que se vende por peso y configure la **Tara (g)** de la bolsa o la bandeja.
- Elija el precio por **Kilogramo** o por **Libra (500 g)** según la costumbre de sus clientes.
- Si tiene báscula etiquetadora, registre el **PLU de báscula** de cada producto.
- Revise cada semana el reporte **Ajustes de inventario por motivo**: en perecederos la merma es el mayor costo escondido.
- **Lotes y vencimientos** viene apagado; enciéndalo si vende carnes empacadas o productos con fecha.

### Droguería

- **Elija:** Droguería. **Queda encendido:** lotes y vencimientos, promociones y compras (la báscula queda apagada).
  **Categorías de ejemplo:** Medicamentos, Cuidado personal, Bebés y maternidad, Primeros auxilios, Vitaminas y suplementos.
- Marque **Maneja lotes** y **Maneja fecha de vencimiento** en **todos** los medicamentos y registre lote y vencimiento en cada
  factura de compra.
- Revise **Lotes por vencer y vencidos** cada semana; si la caja intenta vender un lote vencido, pide autorización del supervisor.
- Use las **presentaciones** (caja, blíster, unidad) con su propio precio y código de barras.
- Registre al cliente (**F3**) cuando lo pida y marque **Siempre pide factura** a quien la necesite.

### Ropa, calzado y deportes

- **Elija:** Ropa, calzado o deportes. **Queda encendido:** promociones y compras (báscula y vencimientos quedan apagados).
  **Categorías de ejemplo:** Hombre, Mujer, Niños, Calzado, Accesorios.
- **Tallas y colores** dentro de un mismo producto llega en una próxima versión (en Funciones del negocio aparece deshabilitada).
  Mientras tanto, cree **un producto por talla o color** con su propio código de barras (por ejemplo "Tenis X talla 38") y
  agrúpelos en la misma **categoría** y **marca**.
- Los **cambios de talla** en la caja también llegan próximamente (vea [Lo que viene](#lo-que-viene)).
- Use **Descuento en porcentaje** por categoría o marca para temporadas y liquidaciones, y **Productos sin rotación** para saber
  qué liquidar.
- Haga conteos parciales por categoría antes y después de cada temporada.

### Ferretería, papelería o miscelánea

- **Elija:** Ferretería, papelería o miscelánea. **Queda encendido:** venta por metro o fracción, precio abierto, promociones y
  compras. **Categorías de ejemplo:** Herramientas, Eléctricos, Plomería, Pinturas, Tornillería, Papelería.
- Marque **Permite cantidades con decimales** en lo que se vende por metro o por fracción (cable, manguera, puntillas) y elija la
  unidad adecuada (metro, centímetro, kilogramo, litro, galón).
- Use **Permite precio abierto en la caja** solo en productos que de verdad cambian de precio (cortes, trabajos especiales); el
  precio lo fija quien tiene permiso o con autorización del supervisor.
- Cree **presentaciones** (caja, paquete, resma) con su propio precio.
- El catálogo suele ser grande: cárguelo con **Importar desde Excel** y use **Productos sin rotación** para no comprar de más.

### Otro tipo de negocio

- **Elija:** Otro tipo de negocio. **Queda encendido:** lo básico (ventas, inventario, promociones y compras). Solo se crea la
  categoría "General".
- Empiece por lo básico: productos con código de barras, precios, **Abrir turno** y **Cerrar turno** todos los días.
- Para servicios (por ejemplo, una recarga o una instalación) use el tipo **Servicio (sin inventario)**.
- Si no usa compras o promociones, apáguelas para tener un menú más corto; encienda báscula, lotes, fracción o precio abierto
  solo si los necesita.

---

## 4. Listas de verificación

Imprima estas listas y péguelas cerca del computador de la administración.

### Todos los días

| ✓ | Tarea | Quién |
|---|---|---|
| ☐ | Cada cajero abre su turno contando la base | Cajeros |
| ☐ | Cada cajero cierra su turno al terminar (no quedan turnos abiertos) | Cajeros |
| ☐ | Revisar **Turnos de caja → Por revisar** y registrar la revisión de cada diferencia | Supervisor |
| ☐ | Mirar el recuadro **Requiere atención** del Tablero | Administrador |
| ☐ | Revisar el **Resumen diario de ventas** y **Ventas por medio de pago** contra el datáfono y las transferencias | Administrador |
| ☐ | Si factura electrónicamente: que no haya documentos pendientes o rechazados (**Facturación electrónica → Alertas y conciliación**) | Administrador |
| ☐ | Si hubo un día sin Internet: **Sincronización → Sincronizar ahora** cuando vuelva | Administrador |

### Cada semana

| ✓ | Tarea | Quién |
|---|---|---|
| ☐ | **Backups**: último resultado correcto y sin avisos de destinos fallando | Administrador |
| ☐ | Un **conteo parcial** de una o dos categorías | Inventario |
| ☐ | **Reabastecimiento → Pedido sugerido** → crear y enviar las órdenes de compra | Compras |
| ☐ | **Etiquetas → Cambios de precio desde una fecha** → cambiar las etiquetas de la góndola | Inventario |
| ☐ | **Lotes por vencer y vencidos** → promoción, devolución o retiro | Inventario |
| ☐ | **Antifraude por cajero** y **Descuentos y promociones** | Propietario o supervisor |
| ☐ | **Edades de cartera por pagar** → programar pagos | Compras |
| ☐ | Terminar las promociones que ya no aplican | Encargado de promociones |
| ☐ | Revisar en el portal **Datos de las tiendas** que la información esté llegando | Propietario |

### Cada mes

| ✓ | Tarea | Quién |
|---|---|---|
| ☐ | **Utilidad por producto** y **por categoría**: ajustar precios o negociar costos | Propietario |
| ☐ | **Inventario valorizado** y **Productos sin rotación** | Propietario |
| ☐ | Exportar para el contador: **Impuestos generados en ventas**, **Impuestos de compras**, **Libro de ventas diario** | Contador |
| ☐ | **Conciliación de facturación electrónica** del mes | Contador |
| ☐ | Revisar en **Facturación electrónica → Rangos de numeración** que ninguno esté por agotarse o vencer | Administrador |
| ☐ | **Gastos por categoría** y **Compras por proveedor** | Propietario |
| ☐ | **Seguridad**, **Cambios de precios, costos e impuestos** y **Actividad fuera de horario** | Propietario |
| ☐ | En **Usuarios**: inactivar a quien ya no trabaja y revisar los roles | Administrador |
| ☐ | En **Auditoría**: **Verificar la integridad** | Propietario |
| ☐ | **Licencia**: revisar la vigencia y pagar la mensualidad a tiempo | Propietario |
| ☐ | **Actualizaciones**: instalar la versión nueva fuera del horario de ventas (el servidor se reinicia) | Administrador |
| ☐ | Confirmar que el código de recuperación sigue guardado fuera del local | Propietario |

---

¿Dudas? Comuníquese con BusinessPost. Los manuales actualizados están en el portal, en **Descargas**.
