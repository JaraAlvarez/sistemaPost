# Manual de administración de BusinessPost

Este manual explica, sección por sección, el menú de la **Administración** de BusinessPost: para qué sirve cada pantalla, cómo hacer
cada tarea paso a paso y qué buenas prácticas le ahorran dinero y dolores de cabeza. Está escrito para dueños y administradores de
cualquier comercio (supermercado, fruver o carnicería, droguería, ropa y calzado, ferretería o papelería, y otros), sin conocimientos
técnicos.

Las imágenes muestran las pantallas reales con datos de ejemplo de una tienda ficticia (Supermercado La Economía SAS, sucursal
Centro). Si está empezando, haga primero la **Puesta en marcha**; para conectar la báscula, la impresora, la facturación electrónica o
Sistecrédito, use el **Manual de integraciones y facturación**.

> **Importante:** cada persona ve en el menú solo lo que su rol le permite. Si una opción de este manual no le aparece, pídale al
> propietario o al administrador que revise su rol en **Administración → Usuarios**. Algunas opciones dependen además de las
> **funciones del negocio** (por ejemplo, Promociones o Compras): vea la sección 22.

## Contenido

1. Entrar y moverse por la administración
2. Tablero
3. Productos y precios
4. Existencias
5. Reabastecimiento: mínimos, máximos y pedido sugerido
6. Conteos de inventario
7. Traslados
8. Ajustes de inventario
9. Importar desde Excel
10. Compras: facturas y órdenes de compra
11. Proveedores
12. Cuentas por pagar
13. Clientes y grupos
14. Listas de precio
15. Promociones
16. Etiquetas de góndola
17. Turnos de caja: reportes X y Z
18. Gastos
19. Facturación electrónica: conectar Factus
20. Reportes
21. Usuarios
22. Configuración
23. Cajas e impresoras
24. Auditoría
25. Licencia
26. Backups
27. Actualizaciones y sincronización
28. Preguntas frecuentes

---

## 1. Entrar y moverse por la administración

### Entrar

1. Abra BusinessPost. En la pantalla de ingreso elija la pestaña **Administración**.
2. Escriba su usuario y su contraseña y pulse **Entrar** (o Enter).

![Ingreso a la administración.](imagenes/uso/adm-01-ingreso-administracion.png)

La pestaña **Caja** es para vender: se entra con el código de cajero y el PIN. Si está en la administración y pulsa **Ir a la caja**,
el programa le pide entrar como cajero.

![Para vender hay que entrar con el código de caja.](imagenes/uso/adm-08-caja-desde-la-administracion.png)

### El menú

A la izquierda está el menú, organizado en grupos:

| Grupo | Opciones |
|---|---|
| **Tablero** | Resumen del día. |
| **Ventas y caja** | Turnos de caja, Gastos, Reportes, Facturación electrónica. |
| **Inventario** | Productos y precios, Etiquetas, Existencias, Reabastecimiento, Conteos, Traslados, Ajustes, Importar desde Excel. |
| **Compras y proveedores** | Compras, Proveedores, Cuentas por pagar. |
| **Clientes y promociones** | Clientes, Promociones. |
| **Administración** | Usuarios, Configuración, Cajas e impresoras, Auditoría, Licencia, Sincronización, Backups, Actualizaciones. |

En la barra de arriba: el botón **☰** contrae el menú para dejar solo los iconos, el botón de luna o sol cambia entre **modo claro y
oscuro**, y a la derecha está su nombre. Al pulsarlo se abre el menú del usuario: **Cambiar contraseña**, **Cambiar PIN de caja**, **Modo
claro**, **Modo oscuro**, **Como el sistema** y **Salir**.

![El menú contraído: solo los iconos.](imagenes/uso/adm-07-menu-compacto.png)

![El tablero en modo oscuro.](imagenes/uso/adm-06-tablero-modo-oscuro.png)

### Cambiar su contraseña

En el menú de su nombre, elija **Cambiar contraseña**. Escriba la contraseña actual y dos veces la nueva. La pantalla le dice el mínimo
de caracteres que exige su empresa (8 de fábrica; el parámetro de longitud mínima lo puede subir). La nueva no puede contener su nombre
de usuario, ni ser una contraseña común (como 12345678 o «colombia»), ni una de sus últimas contraseñas. Use una frase fácil de recordar.
Si escribe mal la contraseña actual varias veces, su usuario se bloquea unos minutos, igual que al entrar.

![Cambiar contraseña.](imagenes/uso/adm-05-cambiar-contrasena.png)

### Cambiar su PIN de caja

El PIN es el que usa para entrar a la caja y para autorizar como supervisor. En el menú de su nombre, elija **Cambiar PIN de caja**:

- **Si ya tiene PIN:** escriba el **PIN actual**, el **nuevo PIN** y repítalo, y pulse **Cambiar PIN**. Aparece "PIN de caja cambiado."
- **Si todavía no tiene PIN** (por ejemplo, el propietario que instaló el sistema con su cuenta del portal): la página dice "Defina su PIN
  de caja". Confirme su **contraseña**, elija su **código de caja** si no tiene uno (3 a 6 dígitos, que no use otro usuario) y escriba el
  PIN dos veces. Pulse **Guardar PIN**: "PIN de caja guardado. Ya puede entrar a la caja con su código y su PIN."

El PIN tiene exactamente los dígitos que fija la empresa y no puede ser una secuencia (1234), un mismo dígito repetido (1111), un PIN muy
común (1212, 2580…) ni igual a su código de caja. Al cambiarlo se cierran sus otras sesiones de caja abiertas. Su código de caja no se
cambia aquí: lo cambia otro administrador en **Usuarios** (sección 21).

![Cambiar PIN de caja (ya tiene PIN): PIN actual y nuevo PIN.](imagenes/uso/adm-05b-cambiar-pin.png)

![Definir el PIN de caja por primera vez: contraseña, código de caja y PIN.](imagenes/uso/adm-05c-definir-pin.png)

> **Consejo:** la administración se cierra sola después de un rato sin uso. Aun así, pulse **Salir** si deja el computador, sobre
> todo si está en la caja: así nadie hace cambios con su usuario.

## 2. Tablero

**Para qué sirve:** ver de un vistazo cómo va el día y qué necesita su atención.

Muestra un saludo, las **ventas netas** de hoy, el número de **tiquetes**, el **ticket promedio**, la **utilidad bruta**, las **ventas
por hora** y los **más vendidos**. Con **Ver reportes** salta al catálogo de reportes.

![Tablero: ventas de hoy, ventas por hora y más vendidos.](imagenes/uso/adm-02-tablero.png)

Más abajo, el bloque **Operación** reúne lo pendiente: productos bajo el mínimo, lotes por vencer, ventas anuladas hoy, turnos
abiertos y cierres con diferencia sin revisar. Cuando hay algo urgente, aparece el recuadro **Requiere atención**; cada aviso lleva a
su pantalla. Ahí aparecen también, por ejemplo, "La báscula de la caja … no está vinculada", "La clave de la báscula de la caja … cambió"
(durante unos días después de vincularla) y "La facturación electrónica quedó apagada: conéctela en Facturación → Configuración".

![Tablero (abajo): los diez más vendidos y el bloque Operación.](imagenes/uso/adm-03-tablero-continuacion.png)

**Buenas prácticas**

- Mírelo al abrir, a mediodía y al cerrar. Toma un minuto.
- Si aparecen **cierres con diferencia sin revisar**, revíselos ese mismo día (sección 17).
- Si hay **productos bajo el mínimo**, haga el pedido (sección 5).

## 3. Productos y precios

**Para qué sirve:** mantener el catálogo: nombres, códigos de barras, precios, impuestos y presentaciones.

Abra **Inventario → Productos y precios**. La lista muestra SKU, nombre, categoría, código de barras, precio y estado. Escriba en el
buscador para encontrar un producto.

![Inventario → Productos y precios.](imagenes/uso/adm-10-productos-lista.png)

### Crear un producto

1. Pulse **Nuevo producto**.
2. Escriba el **nombre** y, si quiere, el **nombre corto** que sale en el tiquete. Si deja el **SKU** vacío, se genera uno.
3. Elija la **categoría**, la **marca** (opcional), la **unidad base** y el **tipo** (inventariable o servicio).
4. Escriba el **código de barras** de fábrica. Si lo deja vacío, se genera uno interno.
5. Escriba el **precio de venta con IVA incluido**.
6. Según su negocio, marque las opciones que apliquen (báscula, decimales, precio abierto, lotes y vencimiento).
7. Guarde.

![Nuevo producto: formulario vacío.](imagenes/uso/adm-12-producto-nuevo.png)

> **Consejo:** para muchos productos a la vez, use **Importar desde Excel** (sección 9).

### Las pestañas del producto

Al abrir un producto verá sus pestañas:

| Pestaña | Para qué sirve |
|---|---|
| **Datos** | Nombre, categoría, marca, unidad, tipo, PLU y opciones de venta. Botones **Cambiar precio**, **Inactivar** y **Descontinuar**. |
| **Precios** | Precio vigente de la lista por defecto y el historial de cambios. |
| **Presentaciones** | Empaques (caja × 24, paca × 6) con su factor, si se venden o se compran, y su propio código y precio. |
| **Códigos de barras** | Todos los códigos del producto; agregar uno de fábrica o **Generar código interno**. |
| **Impuestos** | El IVA (y otros impuestos) del producto. |
| **Existencias** | Cantidad por bodega, costo promedio y valor, y la tabla de **Mínimos y máximos**. |

![Producto → Datos.](imagenes/uso/adm-13-producto-datos.png)

![Producto → Precios: precio vigente e historial.](imagenes/uso/adm-14-producto-precios.png)

![Producto → Presentaciones.](imagenes/uso/adm-15-producto-presentaciones.png)

![Producto → Códigos de barras.](imagenes/uso/adm-16-producto-codigos-de-barras.png)

![Producto → Impuestos.](imagenes/uso/adm-17-producto-impuestos.png)

![Producto → Existencias por bodega.](imagenes/uso/adm-18-producto-existencias.png)

### Cambiar un precio

1. En la lista, pulse **Cambiar** en la fila del producto (o **Cambiar precio** dentro del producto).
2. Escriba el **nuevo precio con IVA incluido**.
3. Si quiere que rija desde una fecha futura, elíjala en **Rige desde**.
4. Guarde. El cambio queda en la Auditoría.

![Cambiar precio con fecha de vigencia.](imagenes/uso/adm-11-producto-cambiar-precio.png)

Dentro del producto, la ventana de cambiar precio también deja elegir la **lista de precios**, la **sucursal** y la **presentación**.
Después del cambio, imprima la etiqueta nueva en **Inventario → Etiquetas** (sección 16).

### Productos que se venden por peso

Para fruver, carnes o granos a granel (con la función **Báscula y PLU** encendida):

- **Unidad base:** Kilogramo, Gramo o **Libra (500 g)**, la de uso en Colombia.
- **PLU de báscula:** el número que usa la báscula etiquetadora.
- **Se vende por báscula:** la caja lee el peso al registrar el producto.
- **Tara (g):** el peso de la bolsa o bandeja, que se descuenta solo.

![Producto por peso: unidad KG, PLU 101, "Se vende por báscula" y tara.](imagenes/uso/adm-19-producto-por-peso.png)

### Otras opciones según el negocio

| Opción | Para qué | Función que debe estar encendida |
|---|---|---|
| **Permite cantidades con decimales** | Vender 1,5 metros de cable o manguera. | Venta por metro o fracción |
| **Permite precio abierto en la caja** | Productos cuyo precio cambia (cortes, trabajos especiales); el precio lo fija quien tiene permiso o el supervisor. | Precio abierto |
| **Maneja lotes** y **Maneja fecha de vencimiento** | Medicamentos, lácteos, carnes frías: se registra el lote y el vencimiento en cada compra, y la caja avisa si intenta vender un lote vencido. | Lotes y vencimientos |

### Tallas y colores (variantes)

Un mismo producto con varias tallas y colores **todavía no existe**: llegará en una próxima versión. Mientras tanto, cree **un producto
por talla o color**, cada uno con su código de barras (por ejemplo, "Tenis X talla 38" y "Tenis X talla 39"), y agrúpelos en la misma
categoría y marca. Así puede ver ventas y existencias de cada uno.

### Inactivar o descontinuar

- **Inactivar:** el producto deja de venderse por ahora, pero puede volver.
- **Descontinuar:** ya no lo va a manejar más.

Ninguna de las dos borra el historial: las ventas y movimientos pasados se conservan.

**Buenas prácticas**

- Un solo producto por artículo; si llega con otro código de barras, agréguelo en **Códigos de barras** en lugar de crear otro producto.
- Programe los cambios de precio con **Rige desde** y el mismo día imprima las etiquetas nuevas (sección 16).
- Revise de vez en cuando el reporte **Cambios de precios, costos e impuestos**.

## 4. Existencias

**Para qué sirve:** saber cuánto tiene de cada producto, en qué bodega, cuánto le costó y cuánto vale.

Abra **Inventario → Existencias**. Verá el saldo por bodega, el mínimo, el costo promedio y el valor total.

![Inventario → Existencias.](imagenes/uso/adm-20-existencias.png)

Marque **Solo bajo el mínimo** para ver lo que se está acabando; esos productos llevan la etiqueta **Bajo el mínimo**.

![Existencias: solo los productos bajo el mínimo.](imagenes/uso/adm-21-existencias-bajo-el-minimo.png)

> **Consejo:** si una existencia sale negativa o no cuadra con lo que hay en la góndola, no la corrija a ojo: haga un **conteo** de
> ese producto (sección 6) para que el ajuste quede justificado.

## 5. Reabastecimiento: mínimos, máximos y pedido sugerido

**Para qué sirve:** no quedarse sin mercancía y no comprar de más. Cada producto puede tener, en cada bodega:

| Dato | Qué significa |
|---|---|
| **Mínimo** | De ahí para abajo hay que pedir. |
| **Punto de pedido** (opcional) | Pedir antes de llegar al mínimo. Si lo usa, ponga también el máximo o la cantidad a pedir. |
| **Máximo** | Hasta dónde llenar. |
| **Cantidad a pedir** (opcional) | Lo que se pide si no hay máximo. |

Esta pantalla está en **Inventario → Reabastecimiento**. El pedido sugerido se ve con la función **Compras y proveedores** encendida y el
permiso de consultar compras; la pantalla también la ve quien puede definir mínimos y máximos (el propietario, el administrador y los
roles Inventario y Compras).

### Tres formas de poner los mínimos y máximos

1. **Uno por uno:** en el producto, pestaña **Existencias**, sección **Mínimos y máximos**: escriba los números en la fila de la
   bodega y pulse **Guardar** en esa fila.
2. **Desde Excel:** en **Importar desde Excel → Mínimos y máximos**, descargue la plantilla (columnas `sku` o `codigo_barras`,
   `minimo` y, opcionales, `maximo`, `punto_pedido` y `bodega`), súbala, revise la vista previa y pulse **Aplicar**.
3. **Calculados con la venta:** en **Reabastecimiento**, pestaña **Calcular mínimos sugeridos**, elija la bodega, los **días de venta**
   que quiere mirar, los **días de entrega** del proveedor y los **días de cobertura**, y pulse **Calcular**. Revise la tabla (puede
   cambiar cualquier número o desmarcar productos) y pulse **Guardar los marcados**. El punto de pedido que ya tuviera se conserva.

![Reabastecimiento → Calcular mínimos sugeridos: bodega, días de venta, días de entrega y días de cobertura.](imagenes/uso/adm-56-reabastecimiento-calcular-minimos.png)

Al importar desde Excel, la vista previa marca los errores fila por fila (producto que no existe o descontinuado, máximo menor que el
mínimo, producto repetido, decimales en un producto por unidad) y una celda vacía de máximo o punto de pedido **conserva** lo que ya
tenía. Si usa punto de pedido, el sistema exige también el máximo o la cantidad a pedir.

Cómo calcula los sugeridos:

| Cálculo | Fórmula | Ejemplo |
|---|---|---|
| Venta diaria | Lo vendido en los días elegidos ÷ días | 90 unidades en 30 días = 3 diarias |
| Mínimo | Venta diaria × días de entrega | 3 × 4 días = 12 |
| Máximo | Venta diaria × (días de entrega + días de cobertura) | 3 × (4 + 7) = 33 |

### Hacer el pedido sugerido

1. Abra **Inventario → Reabastecimiento**, pestaña **Pedido sugerido**. Aparecen los productos bajo el mínimo (o en el punto de
   pedido), agrupados por **proveedor** (el preferido del producto o, si no tiene, el de la compra más reciente). Puede filtrar por
   bodega, categoría y proveedor.
2. Revise la columna **Pedir**: es el máximo menos lo que hay y lo que ya está pedido. Sin máximo, se pide la cantidad a pedir o, si
   no la tiene, hasta el punto de pedido o el mínimo. Si el proveedor vende por caja, se redondea a cajas completas (faltan 30 y la
   caja es de 24: se piden 2 cajas).
3. Cambie las cantidades que quiera y marque o desmarque productos (o un proveedor completo).
4. Los productos del grupo **Sin proveedor** necesitan uno: elíjalo por producto o para todos los marcados.
![Reabastecimiento → Pedido sugerido: tres productos bajo el mínimo, todavía sin proveedor asignado.](imagenes/uso/adm-55-reabastecimiento-pedido-sugerido.png)

5. Pulse **Crear órdenes de compra**. Se crea **una orden en borrador por proveedor y bodega**, con el último costo del proveedor y
   enlaces a cada una. Si una no se puede crear (por ejemplo, el proveedor está bloqueado), no se crea ninguna y el aviso dice cuál
   falló.
6. Vaya a **Compras → Órdenes de compra** para revisarlas, aprobarlas y enviarlas (sección 10).

> **Importante:** lo "ya pedido" son las órdenes que todavía no llegan. Por eso el pedido sugerido no pide dos veces lo mismo. Si
> otra persona pidió algún producto mientras usted revisaba, el sistema lo avisa y lo resalta para que lo revise.

**Buenas prácticas**

- Calcule los mínimos con al menos 30 días de venta real y recalcúlelos cada temporada (diciembre, regreso a clases).
- Haga el pedido el mismo día de la semana con cada proveedor.
- Asigne a cada producto su **proveedor preferido**: el pedido sale ordenado por proveedor.
- Si hay más de 5.000 productos para pedir, la pantalla muestra los primeros 5.000: filtre por categoría o proveedor.

## 6. Conteos de inventario

**Para qué sirve:** comparar lo que hay en la tienda con lo que dice el sistema y corregir las diferencias con respaldo.

![Inventario → Conteos.](imagenes/uso/adm-22-conteos-lista.png)

### Paso a paso

1. Abra **Inventario → Conteos** y pulse **Nuevo conteo**.
2. Elija la **bodega** y el **tipo**: **Completo** (todos los productos) o **Parcial** (algunas categorías o productos). La casilla
   **Conteo ciego** viene marcada: quien cuenta no ve la cantidad del sistema. Desmárquela solo si quiere mostrarla.
3. Guarde y pulse **Iniciar**: el sistema "congela" la cantidad teórica en ese momento.

![Nuevo conteo: bodega, tipo y conteo ciego.](imagenes/uso/adm-23-conteo-nuevo.png)

4. Cuente con el lector: escanee el código y pulse Enter. Para registrar varias unidades a la vez escriba `cantidad*código`, por
   ejemplo `12*7701234000014`. Si quiere, anote la **ubicación** (góndola, estante).
5. El contador "Contados X de Y" le dice cuánto falta.

![Conteo en curso: lecturas, teórico, contado y diferencia.](imagenes/uso/adm-24-conteo-detalle.png)

6. Pulse **Pasar a revisión**. Revise las diferencias; los productos marcados **Recontar** conviene contarlos otra vez.
7. Pulse **Aprobar y ajustar**: las diferencias se aplican al inventario.

> **Importante:** en un conteo, los productos que **no se contaron no se ponen en cero**: se excluyen. Los ajustes grandes (desde
> $500.000 a costo, según Parámetros) necesitan la aprobación de otro usuario.

**Buenas prácticas**

- Haga **conteos parciales** cada semana (una o dos categorías), empezando por las de más valor o más pérdida: licores, aseo, cuidado
  personal, carnes.
- Haga un conteo completo una o dos veces al año.
- Cuente cuando no haya mucho movimiento, o en una bodega que no esté vendiendo en ese momento.

## 7. Traslados

**Para qué sirve:** mover mercancía de una bodega a otra (por ejemplo, de la bodega trasera al piso de venta, o entre sucursales)
dejando registro de lo que salió y lo que llegó.

![Inventario → Traslados: uno recibido y uno en tránsito.](imagenes/uso/adm-25-traslados-lista.png)

1. Pulse **Nuevo traslado**.
2. Elija la **bodega de origen** y la **bodega de destino**.
3. Busque cada producto (nombre, SKU o código y Enter), escriba la cantidad y agréguelo.
4. Pulse **Guardar borrador** si todavía no sale, o **Guardar y despachar** si ya sale. La mercancía queda **en tránsito**.

![Nuevo traslado.](imagenes/uso/adm-26-traslado-nuevo.png)

5. Quien recibe abre el traslado, pulsa **Recibir**, revisa las cantidades que llegaron y pulsa **Confirmar recepción**. Si llegó menos,
   el traslado queda **Recibido con diferencias**.

![Traslado en tránsito, listo para recibir.](imagenes/uso/adm-27-traslado-detalle-en-transito.png)

> **Consejo:** que quien despacha y quien recibe sean personas distintas. Así las diferencias salen a la luz.

## 8. Ajustes de inventario

**Para qué sirve:** registrar entradas o salidas que no son ventas ni compras: averías, vencidos, consumo interno, saldo inicial.
Cada ajuste lleva un **motivo** y un responsable.

![Inventario → Ajustes.](imagenes/uso/adm-28-ajustes-lista.png)

### Nuevo ajuste

1. Pulse **Nuevo ajuste**, elija la **bodega** y el **motivo**. Algunos motivos exigen una observación.
2. Busque cada producto, escriba la cantidad y agréguelo. Según el motivo: los de salida (avería, vencido) descuentan; en el ajuste
   general, una cantidad positiva entra y una negativa sale.
3. Revise y pulse **Aplicar ajuste**. Si supera el tope de aprobación, queda **Pendiente de aprobación** hasta que otro usuario lo
   apruebe con **Aprobar y aplicar**.

![Ajuste por avería con su motivo y observación.](imagenes/uso/adm-29-ajuste-detalle-averia.png)

### Ajuste rápido

Para el producto que **está en la tienda pero el sistema no tiene** (por ejemplo, unidades que aparecieron en la bodega y
nunca se registraron), use **Ajuste rápido**: elija la bodega, el producto, la cantidad que entra y escriba el motivo (de 5 a 300
caracteres). Entra de inmediato.

![Ajuste rápido.](imagenes/uso/adm-38-ajuste-rapido.png)

**Buenas prácticas**

- Revise cada mes el reporte **Ajustes de inventario por motivo**. Si un motivo crece mes a mes (averías, faltantes), ahí hay un
  problema que atender.
- Use el ajuste rápido solo para emergencias; después haga un conteo de ese producto.

## 9. Importar desde Excel

**Para qué sirve:** cargar o actualizar muchos productos, precios, saldos o mínimos de una sola vez.

En **Inventario → Importar desde Excel** hay cuatro cargas:

| Carga | Plantilla | Qué hace |
|---|---|---|
| **Productos** | Plantilla de productos | Crea productos nuevos y actualiza los existentes. |
| **Precios** | Plantilla de precios | Cambia precios (por SKU o código de barras, presentación, sucursal y fecha de vigencia). |
| **Saldo inicial** | Columnas `sku`, `cantidad`, `costo` | Crea un ajuste de saldo inicial en borrador. |
| **Mínimos y máximos** | Plantilla de mínimos y máximos | Fija mínimos, máximos y puntos de pedido. |

![Inventario → Importar desde Excel.](imagenes/uso/adm-30-importar.png)

Siempre funciona igual: descargue la plantilla, llénela, súbala, revise la **vista previa** (filas nuevas, que cambian, sin cambios y
con errores) y pulse **Aplicar**. **Nada cambia hasta que pulse Aplicar**, y solo se aplica si ninguna fila tiene errores. Una celda
vacía no borra el dato que ya existe.

![Vista previa de una importación de productos.](imagenes/uso/adm-31-importar-vista-previa.png)

> **Consejo:** para un cambio general de precios (por ejemplo, un alza de un proveedor), exporte el reporte de productos, ajuste los
> precios en Excel y súbalos con la **plantilla de precios**. Es más rápido y más seguro que cambiarlos uno por uno.

El detalle de las columnas de productos está en el manual de **Puesta en marcha**.

## 10. Compras: facturas y órdenes de compra

**Para qué sirve:** registrar lo que le compra a sus proveedores. Al confirmar una factura de compra, las existencias y el costo
promedio se actualizan solos, y la deuda pasa a Cuentas por pagar.

Está en **Compras y proveedores → Compras** (con la función **Compras y proveedores** encendida). Tiene dos pestañas: **Facturas de
compra** y **Órdenes de compra**.

![Compras → Facturas de compra.](imagenes/uso/adm-40-compras-facturas.png)

### Registrar una factura de compra

1. Pulse **Nueva factura de compra**.
2. Elija el **proveedor** y escriba el número de su factura, la fecha y la forma de pago (contado o crédito).
3. Agregue cada producto con la cantidad y el costo. Si el producto maneja lotes, escriba el **lote** y el **vencimiento**.
4. Pulse **Guardar borrador** y revise los totales contra la factura en papel.
5. Pulse **Confirmar compra**. Desde ese momento entra la mercancía.
6. Si quiere, pulse **Imprimir etiquetas** en la compra confirmada: abre Etiquetas con lo que llegó (sección 16).

![Nueva factura de compra.](imagenes/uso/adm-44-compra-nueva.png)

![Una compra confirmada a crédito, con sus productos y totales.](imagenes/uso/adm-42-compra-detalle.png)

### Órdenes de compra

Una orden de compra es el pedido que usted le hace al proveedor antes de que llegue la mercancía. Su recorrido es: **Borrador →
Aprobada → Enviada → Recibida en parte → Recibida**.

![Compras → Órdenes de compra.](imagenes/uso/adm-41-compras-ordenes.png)

1. Pulse **Nueva orden de compra** (o créela desde el **pedido sugerido**, sección 5).
2. Elija el proveedor y agregue los productos con sus cantidades.
3. Pulse **Aprobar** y luego **Enviar** cuando se la mande al proveedor.
4. Cuando llegue la mercancía con la factura, pulse **Registrar factura** en la orden: la factura se arma con los productos de la orden.
5. Si la orden ya no va, pulse **Cancelar orden**.

![Nueva orden de compra.](imagenes/uso/adm-45-orden-nueva.png)

![Orden en borrador, con los botones Aprobar y Cancelar orden.](imagenes/uso/adm-43-orden-detalle-borrador.png)

> **Importante:** una compra confirmada solo se puede **Anular** (no editar). Revise bien el borrador antes de confirmar.

**Buenas prácticas**

- Registre la compra el mismo día que llega la mercancía: así el costo y la utilidad son reales.
- Compare siempre cantidades y costos contra la factura física antes de confirmar.
- En droguería y perecederos, no deje de registrar lote y vencimiento.

## 11. Proveedores

**Para qué sirve:** tener los datos y condiciones de cada proveedor, y saber cuánto le debe.

![Compras y proveedores → Proveedores.](imagenes/uso/adm-46-proveedores-lista.png)

1. Pulse **Nuevo proveedor**, escriba el NIT, el nombre, el contacto y las condiciones (plazo de pago en días, si factura) y pulse
   **Crear proveedor**.
2. Al abrir un proveedor verá sus **Datos** y su **Estado de cuenta** (saldo, facturas y pagos).

![Nuevo proveedor.](imagenes/uso/adm-49-proveedor-nuevo.png)

![Proveedor → Datos: condiciones de pago.](imagenes/uso/adm-47-proveedor-datos.png)

![Proveedor → Estado de cuenta.](imagenes/uso/adm-48-proveedor-estado-de-cuenta.png)

- **Bloquear:** no se le pueden hacer nuevas compras ni órdenes (por ejemplo, mientras se resuelve un reclamo).
- **Inactivar:** ya no trabaja con ese proveedor.

## 12. Cuentas por pagar

**Para qué sirve:** saber a quién le debe, cuánto y desde cuándo, y registrar los pagos.

En **Compras y proveedores → Cuentas por pagar** hay cuatro pestañas:

| Pestaña | Qué muestra |
|---|---|
| **Facturas por pagar** | Facturas pendientes, con el botón **Pagar**. |
| **Cartera por edades** | Saldos por rangos de días (al día, 1 a 30, 31 a 60…). |
| **Vencimientos** | Lo vencido y lo que vence en los próximos 7 días. |
| **Pagos** | Pagos registrados; un pago mal hecho se puede **Anular**. |

![Facturas por pagar.](imagenes/uso/adm-50-cxp-facturas-por-pagar.png)

![Cartera por edades.](imagenes/uso/adm-51-cxp-cartera-por-edades.png)

![Vencimientos.](imagenes/uso/adm-52-cxp-vencimientos.png)

### Registrar un pago

1. Pulse **Pagar** en la factura (o **Registrar pago** arriba).
2. Marque la factura o las facturas que paga y el valor de cada una.
3. Elija el **medio** (por ejemplo, transferencia) y escriba la **referencia** (número del comprobante).
4. Revise el **Total del pago** y pulse **Registrar**.

![Registrar pago a proveedor.](imagenes/uso/adm-54-registrar-pago-proveedor.png)

![Pagos registrados.](imagenes/uso/adm-53-cxp-pagos.png)

> **Consejo:** revise la pestaña **Vencimientos** cada semana y programe los pagos. Pagar a tiempo le da poder para negociar
> mejores precios y plazos.

## 13. Clientes y grupos

**Para qué sirve:** conocer a sus clientes, darles precios especiales y cumplir la ley de protección de datos.

![Clientes y promociones → Clientes.](imagenes/uso/adm-60-clientes-lista.png)

### Crear un cliente

1. Pulse **Nuevo cliente**.
2. Escriba tipo y número de documento, nombre, celular y correo.
3. Elija su **grupo**.
4. Marque si el cliente **autorizó el tratamiento de sus datos** (Ley 1581). Si no lo autoriza, puede seguir comprando, pero su
   detalle no aparece en los reportes.
5. Si siempre necesita factura a su nombre, márquelo.
6. Guarde.

![Nuevo cliente.](imagenes/uso/adm-63-cliente-nuevo.png)

Al abrir un cliente verá sus **Datos**, sus **Compras** (total comprado, ticket promedio, productos frecuentes) y su **Historial** de
ventas. En la caja, el cajero también puede crear clientes rápidos con F3.

![Cliente → Datos.](imagenes/uso/adm-64-cliente-datos.png)

![Cliente → Compras: resumen y productos frecuentes.](imagenes/uso/adm-65-cliente-compras.png)

![Cliente → Historial de ventas.](imagenes/uso/adm-66-cliente-historial.png)

### Grupos de clientes

En la pestaña **Grupos** organiza a los clientes (por ejemplo, General, Empleados, Mayoristas). Cada grupo puede tener su **lista de
precios**: todos los clientes del grupo compran con esos precios.

1. Pulse **Nuevo grupo**.
2. Escriba el código y el nombre y elija la lista de precios.
3. Pulse **Guardar grupo**.

![Grupos de clientes con su lista de precios.](imagenes/uso/adm-61-clientes-grupos.png)

![Nuevo grupo.](imagenes/uso/adm-62-grupo-nuevo.png)

## 14. Listas de precio

**Para qué sirve:** vender el mismo producto a precios distintos según el cliente (empleados, mayoristas, tenderos).

Así funcionan:

- Además de la lista por defecto (la de todos los clientes), puede haber otras listas, por ejemplo **Empleados** (5 % menos) o
  **Mayorista** (8 % menos).
- Una lista se asigna a un **grupo de clientes** (sección 13) o directamente a un cliente (**Lista de precios propia**, en su ficha).
- Cuando el cajero asigna el cliente a la venta (F3), la caja usa los precios de su lista.
- El precio de un producto en una lista se cambia en el producto: **Cambiar precio**, eligiendo la **lista de precios**.

> **Importante:** crear una lista nueva todavía no tiene pantalla en la administración. Si necesita una, pídala al soporte de
> BusinessPost; después usted la asigna a grupos y clientes desde estas pantallas.

> **Consejo:** use las listas de precio para descuentos permanentes de un tipo de cliente, y las **promociones** para descuentos
> temporales para todos.

## 15. Promociones

**Para qué sirve:** ofertas que la caja aplica sola, igual en todas las cajas, sin descuentos "a ojo". Está en **Clientes y
promociones → Promociones** (con la función **Promociones** encendida).

![Clientes y promociones → Promociones.](imagenes/uso/adm-67-promociones-lista.png)

### Tipos de promoción

| Tipo | Ejemplo |
|---|---|
| **Lleve N pague M** | Lleve 3 pague 2 en jabones. |
| **Precio especial** | Arroz a $3.900 esta semana. |
| **Descuento en porcentaje** | 10 % en toda la categoría Lácteos el fin de semana. |
| **Precio por cantidad** | 1 a $2.000, 3 por $5.000. |
| **Combo a precio fijo** | Café + panela + huevos a $36.000. |

![La lista de tipos de promoción.](imagenes/uso/adm-71-promocion-tipos.png)

### Crear una promoción

1. Pulse **Nueva promoción**.
2. Escriba el **nombre** y elija el **tipo**. Llene el valor según el tipo (precio, porcentaje, cantidades).
3. En **Aplica a**, agregue los productos, categorías o marcas.
4. En **Vigencia**, elija desde qué día y hasta qué día; si quiere, los **días de la semana** (ninguno marcado = todos) y el horario.
5. Opcional: el máximo de veces por venta y el **texto en el tiquete**.
6. Pulse **Guardar borrador**.

![Nueva promoción.](imagenes/uso/adm-70-promocion-nueva.png)

### Simular y activar

1. Abra la promoción y vaya a la pestaña **Simular**. Arme una venta de ejemplo y pulse **Simular**: el resultado se calcula con el
   mismo motor de la caja.
2. Si todo está bien, pulse **Activar**.
3. Para detenerla un tiempo, **Pausar**; para acabarla del todo, **Terminar**.

![Promoción → Resumen.](imagenes/uso/adm-68-promocion-detalle.png)

![Promoción → Simular: total y descuento calculados.](imagenes/uso/adm-69-promocion-simular.png)

**Buenas prácticas**

- **Simule siempre** antes de activar.
- Ponga **fecha de fin**: una promoción sin fin se olvida.
- Liquide lo que está por vencer: revise el reporte **Lotes por vencer y vencidos** y cree una promoción a tiempo.
- Mida el resultado con **Ventas por producto** y **Descuentos y promociones**.
- Si apaga la función Promociones, las activas siguen aplicándose: termínelas primero.

## 16. Etiquetas de góndola

**Para qué sirve:** imprimir las etiquetas de precio de la góndola (habladores) y los adhesivos con código de barras, con el **mismo
precio que cobra la caja**. Está en **Inventario → Etiquetas**. La puede usar quien consulta productos; generar códigos internos exige
poder modificar productos.

La pantalla tiene tres pasos: **1. ¿Qué productos?**, **2. Revise la lista** y **3. Imprimir**.

![Inventario → Etiquetas: la lista armada, con el precio, la oferta y la cantidad de cada producto.](imagenes/uso/adm-57-etiquetas-lista.png)

### 1. Arme la lista

En **Agregar productos por** elija cómo traerlos. Puede combinar varias formas; si un producto ya estaba en la lista, se toma la cantidad
nueva.

| Forma | Qué trae | Cuándo usarla |
|---|---|---|
| **Buscar productos uno por uno** | El producto que busque por nombre, SKU o código (botón **Agregar**). | Una o pocas etiquetas. |
| **Una categoría** | Todos los productos activos de la categoría, con sus subcategorías. | Renovar las etiquetas de un pasillo. |
| **Cambios de precio desde una fecha** | Los productos cuyo precio nuevo ya rige y empezó desde ese día. | Después de subir o bajar precios. |
| **Productos de una compra** | Lo que llegó en una compra, con una etiqueta por unidad (2 cajas × 12 = 24); lo que se vende por peso lleva una sola. | Al recibir mercancía. Se escribe el **número interno** de la compra, no el de la factura del proveedor. |
| **Productos sin código de barras** | Los productos que no tienen código. | Para ponerles adhesivo con código. |

La opción **Productos de una compra** aparece con la función Compras y proveedores encendida. En una compra confirmada también está el
botón **Imprimir etiquetas**, que abre esta pantalla con esa compra.

Los productos **descontinuados** no salen (la pantalla avisa cuántos se omitieron) y los **inactivos** salen marcados como "(inactivo)"
para que usted decida. Si su empresa tiene varias sucursales, elija arriba la **Sucursal (precios y promociones)**: los precios y las
ofertas dependen de ella.

### 2. Revise la lista

Para cada producto verá:

- **Precio:** el precio al público, calculado igual que en la caja (con IVA e impuestos fijos como la bolsa o el IBUA). Los centavos se
  suben al peso, para que la góndola nunca diga menos de lo que se cobra.
- **Oferta:** la promoción vigente hoy en la sucursal. Un precio especial o un porcentaje sale con el precio normal tachado y "hasta
  dd/mm" si la promoción tiene fecha final; "Lleve 3 pague 2" o "Desde 3 und" salen como texto. Las promociones que solo rigen ciertos
  días u horas **no** se imprimen en la góndola.
- **Precio por kilo o por litro**, debajo del nombre, cuando el producto tiene contenido neto (por ejemplo, café de 500 g).
- **Cantidad:** cuántas copias imprimir (0 = no se imprime). Con el ícono de la papelera quita el producto; **Vaciar lista** empieza de
  nuevo.

**Productos sin código de barras:** la etiqueta lleva el SKU, que la caja también lee. Si prefiere un código de barras, pulse **Generar
código interno**: el sistema crea un código EAN-13 que empieza por **29**, lo guarda en el producto y lo deja en la Auditoría. Pide
confirmación antes. Si algún producto no se pudo (ya tenía código o está descontinuado), se informa sin detener los demás.

### 3. Imprima

![Tamaños de etiqueta: hojas A4 y adhesivos para impresora de etiquetas.](imagenes/uso/adm-58-etiquetas-tamanos.png)

Elija el **Tamaño de la etiqueta**, marque si quiere **Imprimir la fecha** y pulse **Descargar PDF**:

| Tamaño | Papel | Cómo imprimir |
|---|---|---|
| **Hoja A4 · 24 etiquetas de góndola (66 × 35 mm)** | Hoja carta o A4, adhesiva o normal | Imprima **al 100 %** (sin "ajustar a la página") y recorte por las líneas grises. |
| **Hoja A4 · 14 etiquetas grandes (100 × 40 mm)** | Igual que la anterior | Igual; sirve también para códigos largos que no caben en la pequeña. |
| **Adhesivo 40 × 25 mm** | Rollo de impresora de etiquetas (Zebra, Xprinter, TSC…) | El PDF viene al tamaño exacto, una etiqueta por página. En Windows elija ese tamaño de papel y etiquetas con separación (gap). |
| **Adhesivo 50 × 30 mm** | Rollo de impresora de etiquetas | Igual que el anterior. |

En un computador de caja aparece además **Imprimir en la impresora de tiquetes**: imprime nombre, precio y código de barras una tras
otra en el papel de tiquetes, para recortar a mano. Sirve para salir del paso; para la góndola es mejor el PDF.

> **Importante:** si un código de barras no cabe bien en la etiqueta elegida (por ejemplo, un código largo en el adhesivo de 40 × 25
> mm), la pantalla lo avisa. Las barras demasiado delgadas no se leen: use la hoja de 14 etiquetas grandes o genere el código interno.

Límites: hasta 2.000 etiquetas por archivo, 200 en la impresora de tiquetes y 500 copias por producto. Si necesita más, imprima por
partes.

**Buenas prácticas**

- Después de cada cambio de precios, use **Cambios de precio desde una fecha** y cambie las etiquetas **el mismo día**. Un precio
  distinto en góndola y en caja genera reclamos y desconfianza.
- Al recibir mercancía, imprima las etiquetas desde la compra: salen justas para lo que llegó.
- Haga una prueba con una sola hoja antes de imprimir cientos: confirme que se imprime al 100 % y que el lector de la caja lee el
  código.

## 17. Turnos de caja: reportes X y Z

**Para qué sirve:** controlar cada turno de cada cajero: con cuánto empezó, cuánto vendió por cada medio de pago, cuánto contó al
cerrar y la diferencia.

Abra **Ventas y caja → Turnos de caja**. Puede buscar por fechas y filtrar los **Por revisar** (cierres con diferencia mayor al tope).

![Ventas y caja → Turnos de caja.](imagenes/uso/adm-72-turnos-lista.png)

### Turno cerrado y reporte Z

Al abrir un turno cerrado verá tres pestañas:

- **Totales por medio de pago:** esperado, contado y diferencia de cada medio, la nota del cajero y el código de control.
- **Movimientos:** apertura, ventas, ingresos, gastos y retiros.
- **Reporte Z (cierre):** el reporte del cierre, con **Imprimir** y **Descargar**.

![Turno cerrado: totales con diferencia y la nota del cajero.](imagenes/uso/adm-73-turno-cerrado-totales.png)

![Movimientos del turno.](imagenes/uso/adm-74-turno-cerrado-movimientos.png)

![Reporte Z del turno.](imagenes/uso/adm-75-turno-cerrado-reporte-z.png)

### Turno abierto y reporte X

En un turno abierto, la pestaña **Reporte X (parcial)** muestra cómo va el turno sin cerrarlo. Sirve, por ejemplo, para un cambio de
supervisor a mitad del día.

![Turno abierto.](imagenes/uso/adm-76-turno-abierto-totales.png)

![Reporte X (parcial).](imagenes/uso/adm-77-turno-abierto-reporte-x.png)

| Reporte | Cuándo | Qué hace |
|---|---|---|
| **X (parcial)** | En cualquier momento del turno | Muestra lo acumulado. No cierra nada. |
| **Z (cierre)** | Al cerrar el turno | Cierra el turno, se imprime solo y lleva un código de control. |

Si en el turno hubo cobros de Sistecrédito que no se usaron en una venta (por ejemplo, con «No usar este crédito») o que Sistecrédito
aprobó tarde, los reportes X y Z traen el bloque **SISTECREDITO A REVISAR**: cuántos son, el total y cada uno con el documento del
cliente (oculto en parte) y su estado (**por revisar**, **no usado** o **revisado**). No es plata de la caja: revíselos y revérselos en el
portal de Sistecrédito, y márquelos como revisados en **Configuración → Medios de pago**, tarjeta Sistecrédito, **Cobros por revisar**
(el detalle está en el Manual de integraciones y facturación).

### Revisar una diferencia

1. Abra el turno **Por revisar** y lea la nota del cajero.
2. Pulse **Revisar**, escriba su revisión y pulse **Registrar revisión**.

### Cerrar como supervisor

Si un cajero se fue sin cerrar su turno, el supervisor abre el turno, pulsa **Cerrar como supervisor**, escribe lo contado por cada
medio y el motivo, y confirma.

![Cerrar como supervisor.](imagenes/uso/adm-78-turno-cerrar-como-supervisor.png)

**Buenas prácticas**

- Ningún turno debe quedar abierto de un día para otro (el sistema avisa después de 14 horas, según Parámetros).
- Revise los turnos **Por revisar** el mismo día.
- Una diferencia pequeña repetida con el mismo cajero dice más que una grande una sola vez: mire el reporte **Antifraude por cajero**.

## 18. Gastos

**Para qué sirve:** registrar arriendo, servicios, fletes, aseo y demás gastos del negocio, para saber la utilidad real. Si el gasto
se pagó con plata de la caja, se descuenta del turno y el cierre sigue cuadrando.

![Ventas y caja → Gastos.](imagenes/uso/adm-79-gastos-lista.png)

1. Pulse **Registrar gasto**.
2. Elija la **categoría**, escriba la descripción, el **valor total pagado** y el **IVA incluido** (si aplica).
3. Elija el **medio de pago**, la **referencia o número de factura** (opcional) y la fecha (vacío = hoy).
4. Si salió del cajón, elija el turno en **Pagado con dinero de la caja**.
5. Pulse **Registrar**.

![Registrar gasto.](imagenes/uso/adm-80-gasto-registrar.png)

En la pestaña **Categorías** puede crear las suyas con **Nueva categoría**. Un gasto mal registrado se **Anula** con un motivo.

![Categorías de gasto.](imagenes/uso/adm-81-gastos-categorias.png)

> **Consejo:** guarde la factura de cada gasto y escriba su número en la referencia. A fin de mes, su contador se lo agradecerá.

## 19. Facturación electrónica: conectar Factus

**Para qué sirve:** que las ventas salgan como **factura electrónica** ante la DIAN, por medio de **Factus**. Está en **Ventas y caja →
Facturación electrónica**, con las pestañas **Configuración**, **Rangos de numeración**, **Documentos** y **Alertas y conciliación**.
Arriba a la derecha siempre dice en qué modo está ("Modo: …"). Conectar y cambiar la configuración lo hace quien tiene el permiso de
configurar la facturación electrónica (el propietario o el administrador).

BusinessPost trae **Factus** como proveedor de fábrica, pero la facturación **nace apagada**: mientras no conecte su cuenta y la conexión
quede **verificada**, no se envía nada a la DIAN y cada venta sale con comprobante interno. Lo que debe tener a mano (contrato, resolución,
credenciales) y la revisión diaria están en el **Manual de integraciones y facturación**.

### Conectar

1. Abra la pestaña **Configuración**. La tarjeta **Factus · facturación electrónica** dice **Desconectado**.
2. Escriba los cuatro datos que le entregó Factus: **Usuario (correo de Factus)**, **Contraseña**, **Client ID** y **Client secret**.
3. Elija el **Ambiente**: **Pruebas (sandbox)** o **Producción (documentos reales ante la DIAN)**. Pruebas y producción tienen
   credenciales distintas.
4. Elija el **Modo de emisión al conectar**: **Solo cuando el cliente pide factura (transición)** o **Factura electrónica en cada venta**.
   Para el segundo, marque **Tengo la cotización de Factus por volumen y el visto bueno de mi contador**.
5. Pulse **Conectar**. BusinessPost guarda las llaves cifradas con la facturación apagada, prueba la conexión con Factus y,
   **solo si funciona**, trae los rangos de numeración y enciende el modo elegido: "¡Conectado con Factus en …! … rango(s) de numeración traídos:
   asígnelos a cada sucursal en la pestaña Rangos." Si no funciona, dice "No se pudo conectar: …" y la facturación sigue apagada.
6. En **Rangos de numeración**, asigne a cada sucursal (o caja) su rango con **Asignar**.

![Factus desconectado: los cuatro datos, el ambiente y el modo de emisión.](imagenes/uso/integ-07-facturacion-sin-configurar.png)

![Factus conectado: ambiente, modo, usuario y la última validación de la clave.](imagenes/uso/integ-11-facturacion-configurada.png)

Las llaves nunca se vuelven a mostrar ni quedan en la auditoría: la tarjeta solo muestra el usuario en parte. Si ya hay llaves guardadas,
puede dejar los cuatro datos vacíos y volver a pulsar **Conectar** para cambiar solo el ambiente o el modo.

### Con Factus conectado

| Botón | Qué hace |
|---|---|
| **Volver a probar** | Verifica otra vez las llaves con Factus: "La conexión con Factus funciona." (o el motivo del problema). Se puede repetir cada pocos segundos. |
| **Cambiar llaves** | Prueba las llaves nuevas **sin guardarlas**; solo si Factus las acepta se guardan y la facturación sigue igual ("Llaves cambiadas…"). Si fallan, no cambia nada ("No se cambiaron las llaves: …"). Con las casillas vacías no hace nada. |
| **Modo de emisión** y **Cambiar modo** | Pasa de "solo cuando el cliente pide factura" a "en cada venta" (con la casilla de la cotización) o al revés. |
| **Desconectar** | Apaga la facturación electrónica: las ventas vuelven al comprobante interno. Las llaves quedan guardadas cifradas. |

![Cambiar llaves: se prueban con Factus antes de guardarlas.](imagenes/uso/integ-11b-facturacion-cambiar-llaves.png)

Para pasar de pruebas a producción (o al revés): **Desconectar** y **Conectar** otra vez con las llaves y el ambiente nuevos.

**Rangos por ambiente.** Pruebas y producción tienen cada uno sus rangos de numeración: BusinessPost solo usa los del ambiente vigente y
no deja asignar un rango de otro ambiente. Si faltan los del ambiente actual, la tarjeta avisa "Faltan los rangos de numeración de …:
hasta sincronizarlos no se envía nada (nunca se usan los de otro ambiente)" con el botón **Sincronizar rangos**.

### Documentos retenidos: enviar o no enviar el atraso

Si quedaron documentos electrónicos en cola **antes de conectar** (cuando no había proveedor conectado o con otras llaves), la tarjeta
muestra "N documento(s) electrónico(s) retenido(s)". **No se envían solos**; usted decide:

- **Enviar ahora** (solo con Factus conectado): pide confirmar con el número de documentos. Factus no recibe la fecha de la venta, así
  que se emiten **con la fecha de hoy**. La DIAN fija un plazo para transmitir (consúltelo con su contador): fuera de él o con datos
  viejos pueden ser rechazados.
- **No enviar**: pide confirmar **dos veces** y **no se puede deshacer**. Los documentos quedan como **NO ENVIADOS**, con el comprobante
  interno, y la decisión queda en la auditoría con su usuario.

Los documentos de **otro ambiente** nunca se envían en el actual: solo se pueden marcar como no enviados. Los que ya se intentaron enviar
quedan **en revisión** ("consulte su estado en el panel de Factus") y **No enviar** no los toca.

> **Importante:** si después de una actualización la tarjeta dice "La facturación electrónica quedó apagada al actualizar … porque estaba
> encendida sin una conexión verificada con Factus", conéctela otra vez con **Conectar**. El Tablero también lo avisa.

## 20. Reportes

**Para qué sirve:** responder preguntas de su negocio con cifras: cuánto vendió, cuánto ganó, qué se vende, qué no rota, quién dio
descuentos, cuánto debe.

Abra **Ventas y caja → Reportes**. Los reportes están organizados por grupos (Ventas, Utilidad, Impuestos, Inventario, Compras y
gastos, Caja, Antifraude y Auditoría).

![Ventas y caja → Reportes: el catálogo por grupos.](imagenes/uso/adm-82-reportes.png)

1. Elija un reporte de la lista.
2. Elija las fechas y los filtros (sucursal, bodega, etc.).
3. Pulse **Ver** para verlo en pantalla, o expórtelo a **Excel**, **CSV** o **PDF**.

![Resumen diario de ventas.](imagenes/uso/adm-83-reporte-resumen-diario.png)

![Ventas por medio de pago.](imagenes/uso/adm-84-reporte-ventas-por-medio-de-pago.png)

![Ventas por producto.](imagenes/uso/adm-85-reporte-ventas-por-producto.png)

![Utilidad por producto.](imagenes/uso/adm-86-reporte-utilidad-por-producto.png)

![Inventario valorizado por bodega y producto.](imagenes/uso/adm-87-reporte-inventario-valorizado.png)

![Cierres de caja.](imagenes/uso/adm-88-reporte-cierres-de-caja.png)

### Qué mirar y cuándo

| Cuándo | Reporte | Para qué |
|---|---|---|
| Cada día | **Resumen diario de ventas** | Total vendido, descuentos, impuestos y anuladas. |
| Cada día | **Cierres de caja** y **Ventas por medio de pago** | Cuadrar efectivo, datáfono y transferencias. |
| Cada semana | **Productos bajo mínimo** | Hacer el pedido. |
| Cada semana | **Lotes por vencer y vencidos** | Liquidar o devolver a tiempo. |
| Cada semana | **Ventas por producto** y **Ventas por hora** | Qué se vende y cuándo necesita más cajeros. |
| Cada semana | **Descuentos y promociones** y **Antifraude por cajero** | Quién dio descuentos y quién anula o cancela más que el promedio. |
| Cada mes | **Utilidad por producto** y **por categoría** | Dónde gana y dónde pierde. |
| Cada mes | **Inventario valorizado** y **Productos sin rotación** | Cuánta plata tiene quieta en estantería. |
| Cada mes | **Impuestos generados en ventas**, **Impuestos de compras** y **Libro de ventas diario** | Para su contador. |
| Cada mes | **Gastos por categoría** y **Compras por proveedor** | Controlar gastos y negociar con proveedores. |

## 21. Usuarios

**Para qué sirve:** que cada persona entre con su propio usuario y vea solo lo que necesita.

La lista muestra el **usuario** (el suyo dice **Usted**), el nombre, los **roles** (los que valen en una sola sucursal dicen "(una
sucursal)"), el **código de caja** ("· sin PIN" si tiene código pero no PIN), el estado y el último ingreso. Cada fila tiene **Detalle** y,
según el estado, **Desbloquear**, **Inactivar** o **Activar**.

![Administración → Usuarios.](imagenes/uso/adm-90-usuarios-lista.png)

### Crear un usuario

1. Pulse **Nuevo usuario**.
2. Escriba el **usuario** (minúsculas, dígitos, punto o guion; o un correo), el **nombre completo** y una **contraseña inicial** (la
   persona la cambia la primera vez que entra).
3. Marque **uno o varios roles**. Cada rol muestra su descripción.
4. Si va a vender, escriba el **código de caja** (3 a 6 dígitos) y el **PIN** (exactamente los dígitos que fija la empresa). Si da código
   de caja, el PIN es obligatorio.
5. Pulse **Crear**. Aparece "Usuario creado. Deberá cambiar la contraseña la primera vez que entre."

![Nuevo usuario: usuario, nombre, contraseña inicial y roles.](imagenes/uso/adm-91-usuario-nuevo.png)

![Nuevo usuario (abajo): los roles con su descripción, el código de caja y el PIN.](imagenes/uso/adm-92-usuario-nuevo-roles.png)

| Rol | Puede |
|---|---|
| **Propietario** | Todo. Es el único que activa la licencia y maneja el código de recuperación. |
| **Administrador** | Toda la operación y la configuración, salvo lo exclusivo del propietario. |
| **Supervisor de caja** | Vender y autorizar descuentos, cancelaciones, peso digitado, reiniciar o vincular la báscula y lotes vencidos; revisar y cerrar turnos; ver reportes de caja. |
| **Cajero** | Vender, reimprimir y crear clientes rápidos. Quitar una línea antes de cobrar depende del permiso de anular líneas (si no lo tiene, pide supervisor). |
| **Inventario** | Productos, existencias, conteos, ajustes, traslados e importación; también registra compras y define mínimos y máximos. |
| **Compras** | Proveedores, órdenes, facturas de compra, cuentas por pagar, y definir mínimos y máximos. |
| **Contador** | Consultar reportes, impuestos y auditoría, y exportarlos. No modifica nada. |
| **Encargado de promociones** | Crear, simular, activar y terminar promociones. |

Los roles vienen listos; sus permisos todavía no se editan desde la pantalla. Un rol creado por soporte para su empresa aparece con la
marca "rol propio".

### Detalle del usuario

Pulse el usuario o **Detalle**. Arriba se ve el estado, si **debe cambiar la contraseña al entrar** y el último ingreso. Cada bloque se
guarda por separado y solo aparece si su usuario tiene el permiso que pide:

- **Datos:** nombre completo y correo. Pulse **Guardar datos**.
- **Roles:** las casillas de los roles. Pulse **Guardar roles** ("Roles guardados. Se aplican desde su siguiente acción."). Si la persona
  tiene roles solo en una sucursal, se muestran aparte y se conservan al guardar. Quitar el rol **Propietario** pide confirmación.
- **Caja:** el código de caja y si tiene PIN. **Restablecer PIN** (o **Asignar código y PIN** si no tiene): escriba el código de caja (3 a
  6 dígitos, único), el PIN nuevo y repítalo, y pulse **Restablecer PIN**. Aparece "PIN de … restablecido. Entra a la caja con el código
  …". Sus sesiones de caja abiertas se cierran.
- **Contraseña:** **Restablecer contraseña** abre el campo **Contraseña temporal**: escríbala o pulse **Generar** (crea una al azar, fácil
  de leer y con la longitud mínima de la empresa). Pulse **Restablecer contraseña** y confirme. La persona deberá cambiarla al entrar y sus
  sesiones abiertas se cierran ya. Entréguele la contraseña temporal en persona.

![Detalle del usuario: estado, datos y roles.](imagenes/uso/adm-91b-usuario-detalle.png)

![Detalle del usuario (abajo): caja y contraseña.](imagenes/uso/adm-91c-usuario-detalle-continuacion.png)

![Restablecer el PIN de caja de otro usuario.](imagenes/uso/adm-91d-usuario-restablecer-pin.png)

![Restablecer la contraseña con una contraseña temporal (tapada en la imagen).](imagenes/uso/adm-91e-usuario-restablecer-contrasena.png)

En **su propio usuario** el detalle avisa que sus roles y su estado los cambia otro administrador; su contraseña y su PIN se cambian desde
el menú de su nombre (**Cambiar contraseña** y **Cambiar PIN de caja**, sección 1).

### Quién puede gestionar a quién

- Solo puede gestionar (editar, cambiar roles, restablecer PIN o contraseña, inactivar, activar o desbloquear) a quien
  **no tiene más permisos que usted**. Un administrador no puede gestionar al propietario.
- Solo puede asignar roles cuyos permisos usted también tiene.
- Nadie cambia sus propios roles, permisos o estado.
- Siempre debe quedar **un Propietario activo**, y al menos un Propietario o Administrador activo.

Los mensajes que puede ver:

| Mensaje | Qué significa |
|---|---|
| "No puede gestionar a un usuario que tiene permisos que usted no tiene (por ejemplo, el Propietario)." | La persona tiene más permisos que usted. Si su rol es una copia personalizada (por ejemplo «Gerente»), puede que le falten permisos que una versión nueva agregó a los roles de sistema: pida al Propietario que se los agregue. |
| "No puede conceder permisos que usted no tiene." | Intentó asignar un rol con permisos que usted no tiene. |
| "No puede cambiar sus propios roles, permisos o estado." | Pídaselo a otro administrador. |
| "Debe quedar al menos un Propietario activo: asigne el rol Propietario a otro usuario antes de quitárselo a este." | Primero dele el rol Propietario a otra persona. |
| "Debe quedar al menos un Propietario o Administrador activo." | No se puede inactivar o degradar al último. |
| "Para su propio PIN use «Cambiar PIN de caja» en su menú de usuario." / "Para su propia contraseña use 'cambiar contraseña'." | Lo suyo se cambia desde el menú de su nombre. |
| "Ya existe un cajero con ese código." | Elija otro código de caja. |
| "El PIN no puede ser una secuencia ni repetir el mismo dígito." / "Ese PIN es demasiado común (como 1212 o 2580): elija otro." / "El PIN no puede ser igual al código de caja." | Elija otro PIN. |
| "Demasiados intentos fallidos: espere unos minutos o pida a un administrador que lo desbloquee." | Al cambiar su contraseña o su PIN escribió mal el actual varias veces. |

### Inactivar, activar y desbloquear

En la lista de usuarios, cada fila tiene sus botones (no aparecen en su propio usuario):

- **Inactivar:** para quien ya no trabaja en el negocio. Hágalo el mismo día. Ya no podrá entrar a la caja ni a la administración.
- **Activar:** devuelve el acceso a un usuario inactivo.
- **Desbloquear:** tras varios intentos fallidos, el usuario queda bloqueado unos minutos; desde aquí se desbloquea antes.

**Buenas prácticas**

- Nunca comparta el usuario del propietario.
- Tenga al menos dos personas con el rol Propietario o Administrador, para que una pueda ayudar a la otra.
- Uno o dos supervisores por turno, no más.
- Revise cada mes que todos los usuarios activos sigan trabajando con usted y que su rol sea el correcto.

## 22. Configuración

En **Administración → Configuración** están los datos de base del negocio. Su uso se explica paso a paso en el manual de **Puesta en
marcha**; aquí, un resumen:

| Pestaña | Para qué |
|---|---|
| **Empresa** | Razón social, nombre comercial, régimen, municipio, dirección y contacto. El NIT no se cambia. |
| **Funciones del negocio** | Tipo de negocio e interruptores de Báscula y PLU, Lotes y vencimientos, Venta por metro o fracción, Precio abierto, Promociones y Compras y proveedores. Apagar una función la oculta, no borra datos. |
| **Sucursales y bodegas** | Sedes y sus bodegas. Cada sucursal nueva trae PISO (piso de venta), AVERIAS y TRANSITO; otras, como una bodega trasera, las crea usted. |
| **Medios de pago** | Medios que ve el cajero al cobrar, y la conexión con Sistecrédito. |
| **Backups** | Dónde se guardan las copias: disco externo o carpeta de red (sección 26). |
| **Parámetros** | Reglas del sistema: tope de diferencia en cierres, redondeo del efectivo, aprobación de ajustes, alertas de vencimiento, backups. |

![Configuración → Funciones del negocio: el tipo de negocio y un interruptor por función.](imagenes/uso/conf-15-funciones-del-negocio.png)

![Configuración → Parámetros.](imagenes/uso/conf-12-parametros.png)

> **Importante:** cambiar el tipo de negocio deja los interruptores en los valores recomendados para ese tipo. Cada cambio de
> configuración queda en la Auditoría.

## 23. Cajas e impresoras

**Para qué sirve:** administrar las cajas de cada sucursal, su impresora de tiquetes y su báscula.

![Administración → Cajas e impresoras.](imagenes/uso/adm-93-cajas-lista.png)

| Botón | Qué hace |
|---|---|
| **Nueva caja** | Crea otra caja en una sucursal. |
| **Editar** | Cambia el nombre o la bodega de la que vende la caja. |
| **Impresora** | Conexión, ancho del papel, página de códigos, corte y cajón monedero. |
| **Báscula** | Marca, puerto, peso mínimo y prueba de la báscula (con la función Báscula y PLU encendida). La báscula se **vincula** después en la pantalla de venta de esa caja: **F1 → Vincular báscula**, con un supervisor. |
| **Inactivar** | Deja la caja fuera de uso. |
| **Bloquear** | Impide vender en esa caja hasta que la desbloquee. |
| **Emparejar equipo** (solo Multicaja) | Muestra el código para unir el computador de esa caja: en el equipo nuevo se abre BusinessPost, se elige **Emparejar este equipo** y se escribe el código. |
| **Emparejar PC administrativo** (solo Multicaja, arriba) | Igual, para un computador de oficina que solo usa la administración. |

En **Multicaja**, en **Equipos emparejados** aparecen los computadores de cada caja adicional. En **Caja Única**, el mismo equipo hace
todo y no se emparejan otros.

La configuración de la impresora y de la báscula está explicada en el **Manual de integraciones y facturación**.

## 24. Auditoría

**Para qué sirve:** saber quién hizo qué y cuándo. Cada descuento, línea quitada, venta cancelada, cambio de precio, ajuste,
cambio de configuración o ingreso queda registrado con su severidad (información, advertencia o crítica).

![Administración → Auditoría.](imagenes/uso/adm-94-auditoria.png)

Pulse **Verificar la integridad** para comprobar que nadie alteró la bitácora. El resultado esperado es "La bitácora está íntegra".

![Verificación de la integridad de la bitácora.](imagenes/uso/adm-95-auditoria-verificada.png)

> **Consejo:** verifique la integridad una vez al mes y revise los eventos críticos. Si algo no le cuadra, la auditoría le dice
> exactamente quién y cuándo.

## 25. Licencia

**Para qué sirve:** ver el estado de su licencia (edición, clave, vigencia y última verificación) y activarla.

Para activar: escriba la clave (empieza por `POS-`) y pulse **Activar**. Necesita Internet en ese momento, y **solo el usuario
Propietario** puede hacerlo.

> **Importante:** si instaló en demostración, active la licencia antes de que se cumplan **30 días** desde la instalación. Si no, la
> licencia queda **Restringida**: puede consultar, exportar y respaldar su información, pero las cajas **no pueden abrir turnos**.

![Administración → Licencia, todavía sin activar.](imagenes/uso/adm-96-licencia.png)

Cuando la licencia está activa y su mensualidad está por vencer, en esta misma pantalla aparece **Pagar mensualidad** (Nequi, PSE,
tarjeta o Bancolombia) y, después de pagar, **Ya pagué, verificar**. El detalle está en el **Manual de integraciones y facturación**.

> **Importante:** pague la mensualidad a tiempo. La licencia se verifica sola una vez al día cuando hay Internet.

## 26. Backups

**Para qué sirve:** tener copias de seguridad de toda su información por si el computador se daña, se pierde o se lo roban.

BusinessPost hace copias **solo**, sin que nadie se acuerde:

| Cuándo | Copia |
|---|---|
| Durante el día | Cada 4 horas, en el horario de la tienda |
| Cada noche | Una copia completa |
| Después de los cierres de caja | Una copia tras cada cierre |
| Cada semana | Una restauración de prueba para comprobar que las copias sirven |

### Historial (Administración → Backups)

En **Administración → Backups** verá el **Historial** de las copias (fecha, tipo, resultado —**Correcto y verificado**, **Correcto** o
**Falló** con el motivo—, tamaño y a qué destinos se copió) y el botón **Respaldar ahora**, para hacer una copia en el momento (por
ejemplo, antes de una actualización): "Backup en cola: aparecerá en el historial en unos minutos."

Arriba aparecen los avisos: "Falta confirmar el código de recuperación…" (con **Generar el código**), "El último backup verificado es
muy antiguo" y "… destino(s) de backup fallando". El Tablero también los muestra.

![Administración → Backups: historial de las copias.](imagenes/uso/adm-97-backups.png)

> **Importante:** el **código de recuperación** es la llave para abrir las copias en otro equipo. Si la pantalla le pide generarlo, se
> muestra **una sola vez**: imprímalo, guárdelo **fuera del local** y escríbalo de nuevo para confirmarlo. La restauración la hace un
> técnico desde el servidor, nunca desde el navegador.

### Dónde se guardan (Configuración → Backups)

Cada copia se crea primero en la **carpeta del servidor**, que es fija, y desde ahí se copia a los **destinos** activos. Para tener una
copia fuera del computador, agregue un **disco externo** o una **carpeta de red** en **Administración → Configuración**, pestaña
**Backups**. Agregar o cambiar destinos lo hace quien tiene el permiso de configurar los destinos de los backups (el propietario o el
administrador).

La pestaña muestra la carpeta del servidor, el **último backup** (con el resultado de cada copia) y el último correcto, con los botones
**Respaldar ahora**, **Actualizar** y **Ver historial**. Debajo, la tabla de **Destinos**: nombre, tipo (carpeta del servidor, disco
externo, carpeta de red o nube), carpeta, qué copias recibe, cuántas conserva (diarias, semanales y mensuales) y el estado (**Activo**,
**Inactivo** o **Fallando**, con la fecha de la última copia). En los discos y carpetas de red están **Cambiar** y **Probar**.

![Configuración → Backups: carpeta del servidor, último backup y destinos.](imagenes/uso/adm-97b-backups-destinos.png)

**Agregar un disco externo**

1. Conecte el disco al **computador servidor** de la tienda.
2. Pulse **Agregar disco externo**.
3. Escriba un **Nombre** y la **Carpeta** con la letra del disco (por ejemplo `E:\BackupsTienda`). Debe ser **otro disco**, no el del
   servidor, y debe existir la carpeta o su carpeta superior (la carpeta final se crea en la primera copia).
4. Si quiere, escriba la **Etiqueta del disco**: así no se copia a otro disco que quede con la misma letra.
5. En **Copiar los backups** marque cuáles recibe: **Programados (cada pocas horas)**, **Nocturno**, **Al cerrar caja** y **Manuales**.
6. Deje **Activo** marcado y elija cuántas copias conserva: **Diarios**, **Semanales** y **Mensuales**.
7. Pulse **Comprobar carpeta**. La comprobación la hace el servidor: escribe, lee y borra un archivo pequeño (no crea carpetas) y dice si
   la carpeta sirve.
8. Pulse **Guardar** ("Destino «…» guardado."). Al guardar un destino activo se comprueba otra vez.

Si después retira el disco, no es un error: las copias quedan **pendientes** y se hacen al conectarlo.

![Agregar disco externo: carpeta, etiqueta, qué copias recibe y cuántas conserva, con el resultado de «Comprobar carpeta».](imagenes/uso/adm-97c-backups-disco-externo.png)

**Agregar una carpeta de red**

Igual que el disco, con **Agregar carpeta de red** y una ruta de red `\\equipo\carpeta` (no use letras mapeadas como `Z:`). Tenga en
cuenta:

- Debe ser **otro equipo de la red de la tienda** (dirección privada 10.x, 172.16–31.x o 192.168.x). No se aceptan direcciones de Internet
  ni el mismo computador servidor.
- El servicio de BusinessPost entra a la red con la **cuenta del equipo servidor** (no con su usuario de Windows): dé permiso de escritura
  a esa cuenta en la carpeta compartida. En un NAS o equipo **sin dominio** esa cuenta no existe y la copia puede fallar: use un disco
  externo o pida ayuda a soporte.
- **Seguridad:** las copias van cifradas, pero quien tenga acceso a la carpeta puede borrarlas. Dé permiso solo a la cuenta del equipo
  servidor y no la comparta con «Todos».

![Agregar carpeta de red: la ruta y los avisos de la cuenta del equipo y de seguridad.](imagenes/uso/adm-97d-backups-carpeta-de-red.png)

**Buenas prácticas**

- Tenga al menos un destino **fuera del computador servidor** (disco externo o carpeta de red) y, si es un disco, llévese uno a casa de
  vez en cuando.
- Use **Probar** después de cambiar el disco o la carpeta de red.
- Revise una vez por semana que el último resultado sea correcto y que no haya destinos **Fallando**.

## 27. Actualizaciones y sincronización

### Actualizaciones

En **Administración → Actualizaciones** ve la versión instalada y si hay una nueva. Cuando la haya, pulse **Instalar ahora (reinicia
el servidor)**.

![Administración → Actualizaciones.](imagenes/uso/adm-98-actualizaciones.png)

> **Importante:** instale las actualizaciones **fuera del horario de ventas**: el servidor se reinicia y las cajas se detienen unos
> minutos. Haga antes un **Respaldar ahora**.

### Sincronización con la nube

En **Administración → Sincronización** ve lo que está pendiente de subir a la nube. Las ventas suben solas cuando hay Internet;
**Sincronizar ahora** lo fuerza. Si un día no hubo Internet, **Exportar paquete .possync** le permite llevar la información en una
memoria y cargarla en el portal desde otro equipo. Desde el portal de BusinessPost ve sus ventas, cierres y existencias desde
cualquier lugar. El detalle está en el **Manual de integraciones y facturación**.

## 28. Preguntas frecuentes

**¿Cómo anulo una venta que ya se cobró o hago una devolución?**
Todavía no se puede desde la pantalla: las devoluciones, los cambios de mercancía y la anulación de una venta cobrada llegarán
próximamente. Antes de cobrar, el cajero sí puede quitar una línea (Supr) o cancelar la venta en curso (F7, con supervisor).

**Un producto no sale en la caja o sale con otro precio.**
Búsquelo en **Productos y precios**: revise que esté **activo**, que tenga el código de barras que se está escaneando (pestaña
Códigos de barras) y su precio vigente (pestaña Precios). Revise también si tiene una promoción activa o si el cliente tiene una lista
de precios propia.

**La existencia de un producto es negativa.**
Se vendió más de lo que el sistema tenía: casi siempre falta registrar una compra o un traslado. Registre lo que falte; si no
encuentra la causa, haga un conteo de ese producto.

**¿Por qué un turno aparece "Por revisar"?**
Porque la diferencia del cierre superó el tope fijado en Parámetros (por defecto $5.000). Ábralo, lea la nota del cajero y pulse
**Revisar**.

**Un cajero se fue sin cerrar el turno.**
Un supervisor abre el turno en **Turnos de caja** y usa **Cerrar como supervisor**.

**Un usuario quedó bloqueado.**
Después de varios intentos fallidos el usuario se bloquea por unos minutos. En **Usuarios** puede **Desbloquearlo** antes.

**Una persona olvidó su contraseña o su PIN de caja.**
En **Usuarios**, abra su **Detalle** y use **Restablecer contraseña** (con una contraseña temporal que cambiará al entrar) o **Restablecer
PIN**. Solo puede hacerlo con quien no tiene más permisos que usted.

**Olvidé la contraseña del propietario.**
Si instaló con su cuenta del portal, el usuario propietario es el correo del portal y su contraseña es la que usaba en el portal
al instalar. Si hay otro Propietario, él puede restablecerle la contraseña en **Usuarios** (un administrador no puede, porque el
propietario tiene más permisos). Si aun así no puede entrar, comuníquese con el soporte de BusinessPost. Por eso conviene que haya más
de una persona con el rol Propietario.

**¿Puedo borrar un producto, un cliente o un proveedor?**
No se borran para no perder el historial: se **inactivan**. Inactivos no estorban en la operación diaria.

**Apagué una función y desaparecieron unas pantallas. ¿Perdí la información?**
No. Apagar una función solo la oculta. Vuelva a encenderla en **Configuración → Funciones del negocio** y todo aparece de nuevo.

**¿Cómo manejo tallas y colores?**
Por ahora, un producto por talla o color con su propio código de barras. La función Tallas y colores llegará en una próxima versión.

**¿Cómo imprimo etiquetas de góndola?**
En **Inventario → Etiquetas**: arme la lista (por ejemplo, con los cambios de precio desde una fecha), revise y descargue el PDF en el
tamaño de su papel (sección 16).

**¿El sistema funciona sin Internet?**
Sí. Se sigue vendiendo; lo pendiente (nube, facturas electrónicas, verificación de la licencia) se envía cuando vuelve el Internet.
Solo Sistecrédito y la activación de la licencia necesitan Internet en el momento.

**¿Cada cuánto debo revisar los backups?**
Una vez por semana: que el último resultado sea correcto y que no haya avisos.

**¿Qué le doy a mi contador?**
Cree un usuario con el rol **Contador**: consulta y exporta reportes, impuestos y auditoría, sin poder cambiar nada.

---

¿Dudas? Comuníquese con BusinessPost. Los manuales actualizados están en el portal, en **Descargas**.
