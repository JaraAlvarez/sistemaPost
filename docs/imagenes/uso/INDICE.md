# Índice de capturas de los manuales de uso

185 capturas reales de BusinessPost (versión 0.1.0, Caja Única) para escribir: (1) Guía de puesta en marcha, (2) Manual de
administración, (3) Manual de caja, (4) Integraciones y facturación y (5) Beneficios y buenas prácticas.

- **Tienda de ejemplo** (datos ficticios): *Supermercado La Economía SAS*, NIT 900.000.000-5, sucursal **Centro** (S01), caja **C01**,
  Calle 50 # 45-20, Medellín. 36 productos (28 por unidad en Abarrotes, Lácteos, Aseo y Bebidas; 8 por peso en Fruver y Carnes, PLU
  101–105 y 201–203), 3 proveedores, 5 clientes, 2 listas de precio (Empleados −5 %, Mayorista −8 %), 4 promociones (3 activas).
- **Usuarios**: `dueno` «Dueño de la tienda» (propietario, código 100), **Ana Cajera** (601), Jorge Cajero (602), **Pedro Supervisor**
  (supervisor de caja, 701), Laura Administradora (administradora), Camilo Bodega (inventario). Contraseñas y PIN son solo de esta base de
  desarrollo; no los publique en los manuales.
- **Turnos**: S01C01-000001 (Jorge, cerrado por la API con −$2.000 de diferencia), S01C01-000002 (Ana, todo el flujo de caja por la
  pantalla, cerrado sin diferencia con reporte Z), S01C01-000003 (Ana, abierto con 6 ventas). Fecha de negocio: sábado 17/10/2026
  (por eso aplica la promoción «Lácteos 10 % fin de semana»).
- **Facturación electrónica** encendida en modo «Factura electrónica en cada venta», ambiente de pruebas, rango **SETP** asignado a
  Centro: cada venta sale con número fiscal SETP99000xxxx, CUFE y QR (proveedor fiscal simulado del entorno de desarrollo, ver notas).
- Tamaño: 1280×800 (las `caja-36…38` a 1366×768; los tiquetes `integ-19/20` a su tamaño). PNG de 256 colores, 7,8 MB en total.
- **Regenerar**: `node tools/scripts/capturas-uso.mjs --tiquetes <carpeta del agente>` (requisitos y fases en el encabezado del script).
  Las capturas dependen del orden: la numeración de cada prefijo es el orden de lectura sugerido.

Prefijos: `conf-` puesta en marcha y configuración · `adm-` administración · `caja-` caja · `integ-` integraciones (impresora, báscula,
Sistecrédito, facturación electrónica, nube, tiquetes).

## Notas importantes para quien escribe

1. **Se ocultaron con CSS** (solo en la imagen, no en el producto): la franja amarilla «Modo demostración · 1 alerta de backups» de la
   administración, el aviso azul «Modo demostración…» y la etiqueta «Demostración · 30 días» de la caja, la tarjeta «1 alerta(s) de
   backups» del tablero y la línea técnica «Proveedor: FAKE · adaptador activo: FAKE» de Facturación (y la fila «Proveedor» del detalle
   del documento, que queda en blanco). Son propias de una base de desarrollo recién instalada. `caja-03`, `adm-96` y `adm-97` se dejaron
   sin ocultar a propósito.
2. **Licencia en demostración**: el tiquete impreso (`integ-19`) dice «*** DEMOSTRACIÓN *** Sistema sin licencia activada»; con licencia
   activa esas dos líneas no salen.
3. **Proveedor fiscal simulado**: los documentos «Aceptado/VALIDATED», el CUFE y el QR los produce el proveedor de pruebas (no se habló con
   Factus ni con la DIAN). En `integ-16` el evento dice «Documento validado por la DIAN (simulado)»; en una instalación real dice lo que
   responda Factus. Las credenciales de Factus y las llaves de Sistecrédito de las capturas son de ejemplo.
4. **Sistecrédito** se dejó «Conectado» guardando llaves de ejemplo por la API (sin probarlas: «Conectar» las prueba contra Sistecrédito
   por Internet). Por eso no hay captura del código QR de cobro ni de cuotas.
5. **Báscula**: en `caja-09…11` la lectura viene de la báscula simulada del agente de desarrollo; la configuración que se muestra
   (`integ-03`) es una Mettler Toledo 8217. En `caja-35` la báscula configurada (Toledo en COM7) no está conectada: el mensaje real es
   «No se pudo leer la báscula (COM7): Could not find file 'COM7'.» (texto técnico en inglés de Windows; conviene mencionarlo).
6. **Errores del producto vistos al capturar** (para el dueño, no para el manual):
   - **Caja, F10 / Esc después de pesar** (`Pages/Caja/Caja.razor.cs`, método `NuevoSistecredito`, líneas ~1180–1181): se hace
     `_pesoCts?.Cancel(); _pesoCts?.Dispose();` sin poner `_pesoCts = null`. Después de pesar un producto en la pantalla, la siguiente
     vez que se cierra un panel o se abre el cobro, `Cancel()` sobre el objeto ya liberado lanza `ObjectDisposedException`: **F10 no abre
     el cobro** (la excepción se pierde en la llamada desde JavaScript) o aparece **«Ocurrió un error inesperado en la pantalla»** al pesar
     otra vez. Recargar la página (la venta no se pierde) lo arregla. El script recarga la caja cuando pasa (lo anota en la consola).
   - **Menú del usuario** (nombre arriba a la derecha, MudMenu de MudBlazor 9.11): no abrió con clic, teclado ni eventos de puntero en
     Edge sin ventana; no hay captura. Confirmar en un navegador normal; si tampoco abre, «Cambiar contraseña» y «Salir» quedan sin
     acceso desde la administración (la captura `adm-05` se tomó entrando por la dirección `/cambiar-clave`).
   - En `adm-24` (detalle de conteo) el campo «Código de barras o SKU» se dibuja como un recuadro alto y vacío.
   - A 1366×768 (`caja-37`) el botón «Confirmar cobro» queda cortado al borde inferior; F10 sigue funcionando. A 1280×800 cabe justo.
   - Montos con centavos: con productos por peso el total muestra centavos ($ 88.210,88); al cobrar en efectivo se redondea a $ 50
     («Ajuste al peso» en el tiquete). Algunos campos numéricos muestran 4 decimales (500,0000 g en `adm-13`, cantidades 1,0000 en
     `adm-69`, 753732,00 en `adm-54`).
7. Las capturas de **Backups** (`adm-97`) muestran «Falló: No se encontró pg_dump.exe»: en el equipo de desarrollo no está PostgreSQL
   cliente. En una instalación real el instalador lo trae y aquí se ven los backups verificados. Úsela solo para señalar el botón
   «Respaldar ahora».
8. Devoluciones y **cambios de mercancía** no tienen pantalla: existen solo en la API (`POST /api/v1/exchanges`, reintegro por garantía).
9. **Retoma con main (b83a6db):** las capturas `conf-`, `adm-` (menos `adm-97`) e `integ-01…03` e `integ-11` se volvieron a tomar con
   main, datos de ejemplo claramente falsos (NIT 900.000.000-5, cédulas 1000000001…, correos @example.co) y el código de recuperación
   tapado en `conf-05`; en `adm-98` se tapó el hash del build. Las `caja-` e `integ-04…20` siguen siendo de la toma anterior (en
   `caja-28` se cambió la cédula por 1000000001). Fase nueva `complementos`: `conf-15` y `adm-55…58`. **Error del producto visto:** en
   Etiquetas, la lista «Categoría (incluye sus subcategorías)» muestra la ruta interna de la categoría (identificadores) en lugar del
   nombre (`Pages/Admin/Etiquetas.razor`, opción `@category.Path`); por eso `adm-57` se armó buscando productos uno por uno.
10. Todas las ventas se hicieron dentro de la misma hora (gráfico «Ventas por hora» con una sola barra).
11. **Retoma con main (33de5b0, báscula firmada, usuarios, backups y Factus):** se volvieron a tomar todas las `caja-`, `integ-03…14`,
    `adm-90…92` y las nuevas `adm-05b/05c`, `adm-91b…91e`, `adm-97b…97d`, `integ-11b` y `caja-44…47` (fases `asistente`, `datos`,
    `caja` y la nueva `novedades`, con `--solo` para copiar solo esas). Contenedor PostgreSQL propio en el puerto 5494. Cambios:
    - La **báscula simulada nunca firma** sus lecturas (Fase 7-C): con peso estable la caja dice «Báscula no vinculada…» y abre
      «Digitar el peso» (`caja-11`), así que ya no hay captura de una línea agregada por la báscula (antes `caja-11-venta-con-producto-
      pesado`, borrada). `caja-09/10` siguen siendo reales (cero e inestable no necesitan firma). `caja-44` («Retire el producto de la
      báscula») se logró avisando al agente una pesada aceptada con la lectura del simulador; `caja-45/46` son «Reiniciar báscula» con
      supervisor; `caja-47` es F1 → «Vincular báscula» pidiendo supervisor (se canceló: el simulador no puede firmar). La báscula
      vinculada pesando de verdad necesita el agente con una báscula real: no hay captura.
    - El PIN de Ana pasó a 5824 (2580 ahora es un «PIN común» y el servidor lo rechaza). El asistente pide la contraseña dos veces.
    - Por la API (`turnoApi`) los productos por peso entran como **peso digitado** con supervisor (sin lectura firmada no hay SCALE).
    - La carpeta de datos temporal del servidor de desarrollo se muestra como `C:\ProgramData\BusinessPost\…` (`adm-97b…d`) y en
      `adm-97b` se ocultó la fila «Último backup» (fallo de pg_dump del equipo de desarrollo, nota 7). `adm-97` no se cambió.
    - En la tarjeta Factus se oculta el aviso «Este servidor usa el proveedor SIMULADO (desarrollo)». La contraseña temporal de
      `adm-91e` está tapada con puntos.
    - No se pudo capturar (sin Internet ni llaves reales): un crédito de Sistecrédito aprobado (la X → «No usar este crédito», el aviso
      de créditos por revisar en el cierre y el bloque SISTECREDITO A REVISAR del reporte Z) ni los documentos retenidos de Factus
      («Enviar ahora» / «No enviar»). Los manuales los describen sin imagen.
    - **Textos del producto para revisar** (no se cambiaron): el error `SALES.SCALE_READING_REQUIRED` todavía dice «vincúlela … →
      Báscula → Guardar» (ahora se vincula con F1 en la caja); la tarjeta Factus, cuando el adaptador es NONE, dice que la activación por
      defecto «está pendiente de aprobación» (ya se aprobó: FACTUS es el de fábrica).

## Puesta en marcha y configuración (`conf-`)

| Archivo | Pantalla | Qué muestra | Ruta / menú | Textos notables |
|---|---|---|---|---|
| conf-01-asistente-empresa.png | Asistente de configuración inicial, paso 1 | Formulario lleno: empresa (razón social, NIT 900000000 + DV 5, tipo de negocio, régimen, municipio MEDELLÍN (05001), dirección, teléfono, correo), sucursal S01 Centro, caja C01 | `http://localhost:5480/instalacion` (lo abre el instalador) | «Configuración inicial», pasos 1. Empresa · 2. Licencia · 3. Código de recuperación · 4. Listo |
| conf-02-asistente-empresa-continuacion.png | Asistente, paso 1 (abajo) | Propietario: usuario, nombre, contraseña (mín. 10), código de caja y PIN; botón | igual | «Propietario (acceso total)», «Guardar y continuar» |
| conf-03-asistente-licencia.png | Asistente, paso 2 | Clave de licencia o seguir en demostración 30 días | igual | «Activar», «Seguir en demostración», el tiquete dice "DEMOSTRACIÓN" |
| conf-04-asistente-codigo-recuperacion.png | Asistente, paso 3 | Explicación del código de recuperación de backups | igual | «Se muestra una sola vez», «Generar el código» |
| conf-05-asistente-codigo-generado.png | Asistente, paso 3 | Código generado (de esta base de prueba) y campo para escribirlo de nuevo | igual | «Escríbalo de nuevo para confirmar que lo guardó» |
| conf-06-asistente-todo-listo.png | Asistente, paso 4 | Fin del asistente | igual | «✓ Todo listo», «Abrir BusinessPost» |
| conf-07-empresa.png | Configuración → Empresa | Datos de la empresa editables (identificación no se cambia) | Administración → Configuración | «Identificación (no se puede cambiar)», País CO · Moneda COP |
| conf-08-sucursales.png | Configuración → Sucursales y bodegas | Sucursal S01 (esta tienda) | pestaña «Sucursales y bodegas» | «Nueva sucursal», «Bodegas», «Editar», «Inactivar» |
| conf-09-bodegas.png | Bodegas de Centro | AVERIAS, BOD (Bodega trasera, depósito), PISO (Piso de venta, vende), TRANSITO | botón «Bodegas» | «Nueva bodega» |
| conf-10-medios-de-pago.png | Configuración → Medios de pago | Tabla: Efectivo, Débito, Crédito, Transferencia, Nequi, Daviplata, Bono, Sistecrédito, con código DIAN, si pide referencia y orden | pestaña «Medios de pago» | «Nuevo medio de pago» |
| conf-11-nuevo-medio-de-pago.png | Nuevo medio de pago | Formulario: código, tipo, nombre, código DIAN, orden en la caja, pide referencia | botón «Nuevo medio de pago» | |
| conf-12-parametros.png | Configuración → Parámetros | Lista de parámetros con nivel (empresa/sucursal/caja), valor y origen | pestaña «Parámetros» | «valor por defecto», «Cambiar» |
| conf-13-parametros-continuacion.png | Parámetros (más abajo) | Más parámetros (backups, caja, documentos…) | igual | |
| conf-14-parametro-cambiar.png | Cambiar un parámetro | Ventana con descripción, clave técnica, valor actual y nuevo valor | botón «Cambiar» | «Número, lista o valor JSON» |
| conf-15-funciones-del-negocio.png | Configuración → Funciones del negocio | Tipo Supermercado / minimercado y los siete interruptores (Tallas y colores deshabilitada) | pestaña «Funciones del negocio» | «no se borra ningún dato», «Disponible en una próxima versión» |

## Administración (`adm-`)

| Archivo | Pantalla | Qué muestra | Ruta / menú | Textos notables |
|---|---|---|---|---|
| adm-01-ingreso-administracion.png | Ingreso | Pestaña «Administración» con usuario y contraseña escritos | `/ingresar` → pestaña Administración | «Bienvenido», «Entrar» |
| adm-02-tablero.png | Tablero | Saludo, ventas netas, tiquetes, ticket promedio, utilidad bruta, ventas por hora, más vendidos | Tablero | «Buenas tardes, Dueño», «Ver reportes» |
| adm-03-tablero-continuacion.png | Tablero (abajo) | Top 10 y bloque «Operación»: productos bajo el mínimo (3), lotes por vencer, anulados hoy, turnos abiertos | Tablero | «0 cierre(s) con diferencia sin revisar» |
| adm-05-cambiar-contrasena.png | Cambiar contraseña | Contraseña actual, nueva y repetición | `/cambiar-clave` (menú del usuario → Cambiar contraseña) | «Mínimo 10 caracteres» |
| adm-05b-cambiar-pin.png | Cambie su PIN de caja | El propietario (ya tiene PIN): PIN actual, código de caja 100, nuevo PIN y repetirlo | `/cambiar-pin` (menú del usuario → Cambiar PIN de caja) | «Cambiar PIN», «Exactamente 4 dígitos, sin secuencias…» |
| adm-05c-definir-pin.png | Defina su PIN de caja | Laura Administradora (sin código ni PIN): su contraseña, código de caja, nuevo PIN | igual, con un usuario sin PIN | «Aún no tiene PIN de caja…», «Guardar PIN» |
| adm-06-tablero-modo-oscuro.png | Tablero en modo oscuro | Mismo tablero con el tema oscuro | botón luna/sol de la barra superior | |
| adm-07-menu-compacto.png | Menú contraído | Menú lateral solo con iconos | botón ☰ de la barra superior | |
| adm-08-caja-desde-la-administracion.png | «Para vender, entre como cajero» | Lo que se ve al pulsar «Ir a la caja» con sesión de administración | botón «Ir a la caja» | «Entrar con código de caja», «Volver a la administración» |
| adm-10-productos-lista.png | Productos y precios | Lista con SKU, nombre, categoría, código de barras, precio, estado | Inventario → Productos y precios | «Nuevo producto», «Cambiar» (precio) |
| adm-11-producto-cambiar-precio.png | Cambiar precio | Ventana con precio actual y nuevo precio (IVA incluido), «Rige desde» | «Cambiar» en la fila | «El cambio queda en la auditoría» |
| adm-12-producto-nuevo.png | Nuevo producto | Formulario vacío: SKU, nombre, categoría, marca, unidad, tipo, PLU, contenido, lotes/vencimiento, código y precio | «Nuevo producto» | «Vacío = se genera uno automáticamente» |
| adm-13-producto-datos.png | Producto → Datos | Café Sello Rojo 500 g | clic en el producto | «Cambiar precio», «Inactivar», «Descontinuar» |
| adm-14-producto-precios.png | Producto → Precios | Precio vigente e historial | pestaña Precios | «Precio vigente (lista por defecto)» |
| adm-15-producto-presentaciones.png | Producto → Presentaciones | Presentaciones (caja x24, etc.) | pestaña Presentaciones | «Agregar presentación» |
| adm-16-producto-codigos-de-barras.png | Producto → Códigos de barras | EAN-13 principal, agregar código de fábrica o generar interno | pestaña Códigos de barras | «Generar código interno» |
| adm-17-producto-impuestos.png | Producto → Impuestos | IVA del producto | pestaña Impuestos | |
| adm-18-producto-existencias.png | Producto → Existencias | Total en la sucursal y por bodega, costo promedio, valor | pestaña Existencias | |
| adm-19-producto-por-peso.png | Producto por peso | Tomate chonto: unidad KG, PLU 101, «Se vende por báscula», tara (g) | clic en un producto de Fruver | «Se vende por báscula (la caja lee el peso al registrarlo)» |
| adm-20-existencias.png | Existencias | Saldos por bodega (BOD, PISO), mínimo, costo promedio, valor | Inventario → Existencias | «Solo bajo el mínimo» |
| adm-21-existencias-bajo-el-minimo.png | Existencias bajo el mínimo | Cerveza Águila y Huevos AA con etiqueta | casilla «Solo bajo el mínimo» | «Bajo el mínimo» |
| adm-22-conteos-lista.png | Conteos | Conteo S01-000001 en curso | Inventario → Conteos | «Nuevo conteo» |
| adm-23-conteo-nuevo.png | Nuevo conteo | Bodega, tipo, ciego | «Nuevo conteo» | |
| adm-24-conteo-detalle.png | Conteo en curso | Ciego, «Contados 4 de 36», campo del lector, cantidad, ubicación, teórico/contado/diferencia | clic en el conteo | «Código de barras o SKU (cantidad*código; Enter registra)», «Pasar a revisión», «Anular» |
| adm-25-traslados-lista.png | Traslados | Uno recibido y uno en tránsito | Inventario → Traslados | «Nuevo traslado» |
| adm-26-traslado-nuevo.png | Nuevo traslado | Bodega origen y destino, productos | «Nuevo traslado» | «Guardar borrador», «Guardar y despachar» |
| adm-27-traslado-detalle-en-transito.png | Traslado en tránsito | De Bodega trasera a Piso de venta, despachado | clic en el traslado | «Recibir» |
| adm-28-ajustes-lista.png | Ajustes de inventario | Saldo inicial y avería aplicados | Inventario → Ajustes | «Ajuste rápido», «Nuevo ajuste» |
| adm-29-ajuste-detalle-averia.png | Ajuste por avería | 2 yogures y 1 chocolate, motivo avería | clic en S01-000002 | «Empaques rotos al descargar» |
| adm-30-importar.png | Importar desde Excel | Plantillas, subir productos/precios, saldo inicial | Inventario → Importar desde Excel | «Plantilla de productos», «Subir productos» |
| adm-31-importar-vista-previa.png | Vista previa de la importación | 36 filas nuevas, avisos de categorías y marcas a crear | tras subir el CSV | «Aplicar», «Descartar», «Estado: vista previa» |
| adm-32-importar-aplicado.png | Importación aplicada | Mismo lote en estado aplicada | «Aplicar» | «Importación aplicada» |
| adm-33-importar-saldo-inicial.png | Saldo inicial | Sección de saldo inicial y mensaje del ajuste creado | «Subir saldo inicial» | «Ajuste S01-000001 creado en borrador con 36 productos» |
| adm-34-ajustes-saldo-inicial-en-borrador.png | Ajustes con el saldo en borrador | El ajuste S01-000001 en «Borrador» | Inventario → Ajustes | |
| adm-35-ajuste-saldo-inicial-borrador.png | Ajuste de saldo inicial | Productos, cantidades y costo unitario | clic en el ajuste | «Aplicar ajuste», «Anular» |
| adm-36-ajuste-aplicar-confirmacion.png | Confirmar | Ventana de confirmación | «Aplicar ajuste» | «quedará por aprobar si lo exige la política» |
| adm-37-ajuste-saldo-inicial-aplicado.png | Ajuste aplicado | Estado aplicado, fecha contable | «Aplicar» | «Ajuste aplicado al inventario.» |
| adm-38-ajuste-rapido.png | Ajuste rápido | Entrada inmediata de un producto que está en la tienda | «Ajuste rápido» | «Motivo (de 5 a 300 caracteres)» |
| adm-40-compras-facturas.png | Compras → Facturas | Dos facturas confirmadas | Compras y proveedores → Compras | «Nueva factura de compra», «Nueva orden de compra» |
| adm-41-compras-ordenes.png | Compras → Órdenes | Orden enviada y orden en borrador | pestaña Órdenes de compra | «Aprobar», «Enviar», «Registrar factura» |
| adm-42-compra-detalle.png | Compra S01-000001 | Factura FE-20871 de Lácteos del Valle a crédito, productos, totales | clic en la compra | «Confirmada», «Anular» |
| adm-43-orden-detalle-borrador.png | Orden S01-000002 | Orden en borrador a Aseo Total | clic en la orden | «Aprobar», «Cancelar orden» |
| adm-44-compra-nueva.png | Nueva factura de compra | Datos de la factura y productos | «Nueva factura de compra» | «Guardar borrador» |
| adm-45-orden-nueva.png | Nueva orden de compra | Formulario vacío | «Nueva orden de compra» | |
| adm-46-proveedores-lista.png | Proveedores | 3 proveedores | Compras y proveedores → Proveedores | «Nuevo proveedor» |
| adm-47-proveedor-datos.png | Proveedor → Datos | Condiciones (plazo 30 días, factura) | clic en el proveedor | «Bloquear», «Inactivar» |
| adm-48-proveedor-estado-de-cuenta.png | Proveedor → Estado de cuenta | Saldo $221.536, facturas y pagos | pestaña Estado de cuenta | |
| adm-49-proveedor-nuevo.png | Nuevo proveedor | Formulario | «Nuevo proveedor» | |
| adm-50-cxp-facturas-por-pagar.png | Cuentas por pagar → Facturas por pagar | Facturas pendientes con «Pagar» | Compras y proveedores → Cuentas por pagar | «Registrar pago» |
| adm-51-cxp-cartera-por-edades.png | Cartera por edades | Saldos por rangos de días | pestaña | |
| adm-52-cxp-vencimientos.png | Vencimientos | Vencidas y por vencer en 7 días | pestaña | |
| adm-53-cxp-pagos.png | Pagos | Pago TRF-004512 | pestaña | «Anular» |
| adm-54-registrar-pago-proveedor.png | Registrar pago a proveedor | Factura AND-5512 marcada, medio Transferencia, referencia | «Pagar» en la factura | «Total del pago» |
| adm-55-reabastecimiento-pedido-sugerido.png | Reabastecimiento → Pedido sugerido | Cerveza Águila, Gaseosa Coca-Cola y Huevos AA bajo el mínimo, en el grupo «Sin proveedor» | Inventario → Reabastecimiento | «Asignar proveedor a los marcados», «Crear órdenes de compra» |
| adm-56-reabastecimiento-calcular-minimos.png | Reabastecimiento → Calcular mínimos sugeridos | Bodega PISO, 30 días de venta, 3 de entrega, 7 de cobertura (sin resultados: las ventas de la demostración son de hoy) | pestaña «Calcular mínimos sugeridos» | «Calcular», «Guardar los marcados» |
| adm-57-etiquetas-lista.png | Etiquetas | Lista de 4 productos con precio, precio por kilo o litro y cantidad; aviso de producto sin código y «Generar código interno (1)» | Inventario → Etiquetas | «1. ¿Qué productos?», «2. Revise la lista» |
| adm-58-etiquetas-tamanos.png | Etiquetas → Imprimir | Lista «Tamaño de la etiqueta» abierta: hojas A4 de 24 y 14, adhesivos 40 × 25 y 50 × 30 | paso «3. Imprimir» | «Imprimir la fecha», «Descargar PDF» |
| adm-60-clientes-lista.png | Clientes | 5 clientes con grupo | Clientes y promociones → Clientes | «Nuevo cliente» |
| adm-61-clientes-grupos.png | Grupos de clientes | General, Empleados (lista Empleados), Mayoristas | pestaña Grupos | «Nuevo grupo» |
| adm-62-grupo-nuevo.png | Nuevo grupo | Código, nombre, lista de precios | «Nuevo grupo» | |
| adm-63-cliente-nuevo.png | Nuevo cliente | Datos del cliente y autorización de datos | «Nuevo cliente» | |
| adm-64-cliente-datos.png | Cliente → Datos | María Fernanda Ríos Patiño, grupo y lista | clic en el cliente | «Autorizó datos: Sí (política v1)» |
| adm-65-cliente-compras.png | Cliente → Compras | Resumen y productos frecuentes | pestaña Compras | |
| adm-66-cliente-historial.png | Cliente → Historial | Ventas del cliente | pestaña Historial | |
| adm-67-promociones-lista.png | Promociones | 3 activas y 1 borrador | Clientes y promociones → Promociones | «Nueva promoción» |
| adm-68-promocion-detalle.png | Promoción → Resumen | Combo desayuno (Café + Panela + Huevos a $36.000) | clic en la promoción | «Pausar», «Terminar» |
| adm-69-promocion-simular.png | Promoción → Simular | Resultado: total $36.000, descuento $4.000 | pestaña Simular → «Simular» | «Venta de ejemplo calculada con el mismo motor de la caja» |
| adm-70-promocion-nueva.png | Nueva promoción | Tipo, aplica a, vigencia, días, horas, texto del tiquete | «Nueva promoción» | «Guardar borrador» |
| adm-71-promocion-tipos.png | Tipos de promoción | Lleve N pague M, Precio especial, Descuento en porcentaje, Precio por cantidad, Combo a precio fijo | lista «Tipo de promoción» | |
| adm-72-turnos-lista.png | Turnos de caja | 3 turnos: abierto, cerrado $0, cerrado −$2.000 | Ventas y caja → Turnos de caja | «Por revisar», «Buscar» |
| adm-73-turno-cerrado-totales.png | Turno cerrado → Totales | Esperado/contado/diferencia por medio, nota del cajero, código de control | clic en el turno de Jorge | «Faltaron $2.000 en monedas» |
| adm-74-turno-cerrado-movimientos.png | Turno → Movimientos | Apertura, ventas, ingreso, gasto, retiro | pestaña Movimientos (19) | |
| adm-75-turno-cerrado-reporte-z.png | Turno → Reporte Z | Reporte Z en pantalla | pestaña Reporte Z (cierre) | «Imprimir», «Descargar» |
| adm-76-turno-abierto-totales.png | Turno abierto | Turno de Ana en curso | clic en el turno abierto | «Cerrar como supervisor» |
| adm-77-turno-abierto-reporte-x.png | Reporte X | Reporte parcial | pestaña Reporte X (parcial) | |
| adm-78-turno-cerrar-como-supervisor.png | Cerrar como supervisor | Conteo por monto y motivo | «Cerrar como supervisor» | «Para turnos que el cajero dejó abiertos» |
| adm-79-gastos-lista.png | Gastos | Arriendo, servicios, flete, gasto de caja | Ventas y caja → Gastos | «Registrar gasto», «Anular» |
| adm-80-gasto-registrar.png | Registrar gasto | Categoría, valor, IVA, medio, referencia, fecha, si sale de la caja | «Registrar gasto» | «Pagado con dinero de la caja (opcional)» |
| adm-81-gastos-categorias.png | Categorías de gasto | Servicios públicos, Arriendo, Aseo y cafetería… | pestaña Categorías | |
| adm-82-reportes.png | Reportes | Catálogo de 46 reportes por grupo | Ventas y caja → Reportes | «Elija un reporte de la lista» |
| adm-83-reporte-resumen-diario.png | Resumen diario de ventas | Fechas, Ver/Excel/CSV/PDF y resultado | clic en el reporte → Ver | |
| adm-84-reporte-ventas-por-medio-de-pago.png | Ventas por medio de pago | | igual | |
| adm-85-reporte-ventas-por-producto.png | Ventas por producto | | igual | |
| adm-86-reporte-utilidad-por-producto.png | Utilidad por producto | | igual | |
| adm-87-reporte-inventario-valorizado.png | Inventario valorizado | Por bodega y producto | igual | «Cuadra con el kardex» |
| adm-88-reporte-cierres-de-caja.png | Cierres de caja | | igual | |
| adm-90-usuarios-lista.png | Usuarios | 6 usuarios con roles, código de caja, estado, último ingreso; «Usted» en el propio | Administración → Usuarios | «Nuevo usuario», «Detalle», «Inactivar» |
| adm-91-usuario-nuevo.png | Nuevo usuario | Usuario, nombre, contraseña inicial y los roles como casillas (Cajero marcado) | «Nuevo usuario» | «Contraseña inicial (la cambia al entrar)» |
| adm-92-usuario-nuevo-roles.png | Nuevo usuario (abajo) | Roles con su descripción, código de caja 603 y PIN | igual, desplazado | «Si da código de caja, el PIN es obligatorio.» |
| adm-91b-usuario-detalle.png | Detalle del usuario | Ana Cajera: estado, «Debe cambiar la contraseña al entrar», último ingreso, datos y roles | «Detalle» o el nombre de usuario | «Guardar datos» |
| adm-91c-usuario-detalle-continuacion.png | Detalle (abajo) | Roles, regla de jerarquía, Caja (código 601, PIN asignado) y Contraseña | igual, desplazado | «Guardar roles», «Restablecer PIN», «Restablecer contraseña» |
| adm-91d-usuario-restablecer-pin.png | Restablecer PIN | Código de caja, PIN nuevo y repetirlo | «Restablecer PIN» | «Sin secuencias (1234), repeticiones (1111)…» |
| adm-91e-usuario-restablecer-contrasena.png | Restablecer contraseña | Contraseña temporal generada (tapada) | «Restablecer contraseña» → «Generar» | «Al entrar deberá cambiarla; sus sesiones abiertas se cierran ya.» |
| adm-93-cajas-lista.png | Cajas e impresoras | Caja C01 con Editar/Impresora/Báscula/Inactivar/Bloquear | Administración → Cajas e impresoras | «Edición Caja Única: este equipo hace todo; no se emparejan otros equipos.» |
| adm-94-auditoria.png | Auditoría | Bitácora de acciones | Administración → Auditoría | «Verificar la integridad» |
| adm-95-auditoria-verificada.png | Auditoría verificada | Resultado de la verificación | «Verificar la integridad» | «La bitácora está íntegra.» |
| adm-96-licencia.png | Licencia | Estado demostración, 30 días, campo de clave | Administración → Licencia | «Activar» (sin la tarjeta «Pagar mensualidad», ver abajo) |
| adm-97-backups.png | Backups | Botón y alerta; historial con fallos de desarrollo (nota 7) | Administración → Backups | «Respaldar ahora» |
| adm-97b-backups-destinos.png | Dónde se guardan los backups | Carpeta del servidor (fija) y tabla de destinos | Configuración → pestaña «Backups» | «Agregar disco externo», «Agregar carpeta de red» |
| adm-97c-backups-disco-externo.png | Nuevo destino · Disco externo | E:\BackupsTienda, etiqueta RESPALDO, copias y retención, resultado de «Comprobar carpeta» (la unidad no existe en el equipo de pruebas) | «Agregar disco externo» | «La unidad E:\ no está conectada o no está lista.» |
| adm-97d-backups-carpeta-de-red.png | Nuevo destino · Carpeta de red | \\192.168.1.20\respaldos\tienda y los avisos de cuenta del equipo y seguridad | «Agregar carpeta de red» | «LocalSystem», «Seguridad: los backups van cifrados…» |
| adm-98-actualizaciones.png | Actualizaciones | Versión instalada y estado | Administración → Actualizaciones | |

## Caja (`caja-`) — orden del flujo de Ana Cajera

| Archivo | Pantalla | Qué muestra | Cómo se llega | Textos notables |
|---|---|---|---|---|
| caja-01-ingreso.png | Ingreso | Pestaña «Caja» | `/ingresar` | «Código de cajero», «PIN», «Entrar a la caja» |
| caja-02-ingreso-codigo-y-pin.png | Ingreso | Código y PIN escritos (ocultos) | | |
| caja-03-aviso-modo-demostracion.png | Abrir turno (sin ocultar) | Aviso real de demostración en la caja | después de entrar | «Modo demostración: quedan 30 días. Las ventas son reales…» |
| caja-04-abrir-turno.png | Abrir turno | Conteo de la base por monto de cada billete/moneda | primera vez en el turno | «Base del cajón» |
| caja-05-abrir-turno-conteo.png | Abrir turno | Base de $300.000 contada | | «= 2 billetes» bajo cada valor |
| caja-06-venta-vacia.png | Venta vacía | Lista para vender | «Abrir turno» | «Turno S01C01-000002 abierto.», «Lista para vender» |
| caja-07-venta-con-productos-y-promociones.png | Venta | 5 productos escaneados, «2*» y «3*», promociones aplicadas solas | lector + Enter | «Lácteos 10 % fin de semana», «Lleve 3 pague 2», «Ahorro del cliente» |
| caja-08-busqueda-por-nombre.png | Búsqueda por nombre | Lista bajo el escáner al escribir «leche» | escribir el nombre | ↑ ↓ eligen, Enter agrega |
| caja-09-bascula-ponga-el-producto.png | Báscula | Producto de peso registrado, báscula en cero | escanear/registrar Tomate | «Ponga el producto en la báscula» |
| caja-10-bascula-esperando-peso-estable.png | Báscula | Peso moviéndose | | «Esperando peso estable…» |
| caja-11-bascula-no-vinculada.png | Digitar el peso | Peso estable pero la báscula no está vinculada: la caja abre «Digitar el peso» | registrar Tomate con peso estable | «Báscula no vinculada: vincule con F1 → Vincular báscula (con la autorización de un supervisor).» |
| caja-12-consulta-de-precio-lista.png | Consultar precio | Lista al escribir «detergente» | F5 | «No se agrega a la venta» |
| caja-13-consulta-de-precio.png | Consultar precio | Precio grande, IVA, existencias | ↓ Enter | «Existencias: 35 und» |
| caja-14-cliente-buscar.png | Cliente de la venta | Búsqueda «rios» y resultado | F3 | «Consumidor final», «Cédula, NIT, nombre o celular» |
| caja-15-cliente-asignado.png | Venta con cliente | Cliente en el panel derecho | clic en el cliente | «Cliente: María Fernanda Ríos Patiño.» |
| caja-16-cambiar-cantidad.png | Cambiar cantidad | Ventana sobre la línea seleccionada | F4 | «Aplicar (Enter)» |
| caja-17-descuento.png | Descuento | 10 % a la línea, motivo | F6 | «A la línea seleccionada / A toda la venta», «Porcentaje (%) / Valor en pesos ($)» |
| caja-18-autorizacion-de-supervisor.png | Autorización de supervisor | La cajera no tiene el permiso | «Aplicar descuento» | «Esta acción requiere la autorización de un supervisor (sales.discount.apply).» |
| caja-19-autorizacion-de-supervisor-datos.png | Autorización | Código y PIN del supervisor, motivo | | «Autorizar» |
| caja-20-venta-con-cliente-y-descuento.png | Venta | Línea con «Descuento manual» y cliente | | «Descuento aplicado.» |
| caja-21-cobro.png | Cobrar | Panel de cobro con efectivo por defecto y valores rápidos | F10 | «Confirmar cobro F10», «Pago mixto: …» |
| caja-22-cobro-efectivo-con-cambio.png | Cobrar en efectivo | Recibido $100.000, cambio | escribir el valor | «CAMBIO» |
| caja-23-venta-cobrada.png | Venta cobrada | Total, recibido, cambio grande y estado fiscal | F10 / Enter | «Factura electrónica SETP99000xxxx validada por la DIAN» |
| caja-24-pago-mixto-primer-medio.png | Pago mixto | Nequi $15.000 con referencia agregado; falta | elegir Nequi, valor, referencia, pulsar Nequi otra vez | «FALTA» |
| caja-25-pago-mixto-completo.png | Pago mixto | Nequi + efectivo, cambio | Efectivo, valor, pulsar Efectivo | |
| caja-26-cobro-tarjeta-con-referencia.png | Tarjeta débito | Medio con referencia del datáfono | elegir Tarjeta débito | «Referencia (aprobación del datáfono)» |
| caja-27-venta-cobrada-con-tarjeta.png | Venta cobrada | Cambio $0 | F10 | |
| caja-28-cobro-sistecredito.png | Sistecrédito | Tipo y número de documento, botón de generar (no se pulsó) | elegir Sistecrédito | «Generar cobro de $ …», «Sistecrédito necesita internet en este momento.» |
| caja-29-linea-anulada.png | Anular línea | Línea marcada «Anulada» en $0 | seleccionar línea + Supr | |
| caja-30-venta-suspendida.png | Venta suspendida | Venta vacía y contador en la barra | F8 | «Venta suspendida. Recupérela con F9.», «1 suspendida» |
| caja-31-ventas-suspendidas.png | Ventas suspendidas | Lista para recuperar | F9 | |
| caja-32-venta-recuperada.png | Venta recuperada | La venta vuelve con sus líneas | clic en la suspendida | |
| caja-33-cancelar-venta.png | Cancelar venta | Confirmación (luego pide supervisor) | F7 | «¿Cancelar la venta?», «Sí, cancelar venta» |
| caja-34-ayuda-atajos.png | Atajos de la caja | Lista completa con báscula activa y los botones de la báscula | F1 | «Vincular báscula», «Reiniciar báscula», «Entendido» |
| caja-35-peso-digitado.png | Digitar el peso | La báscula no responde; peso a mano (pide supervisor) | registrar producto de peso | «No se pudo leer la báscula (COM7)…», «peso digitado» |
| caja-36-venta-1366x768.png | Venta a 1366×768 | Línea «Peso digitado» | | |
| caja-37-cobro-1366x768.png | Cobro a 1366×768 | Botón confirmar cortado abajo (usar F10) | F10 | |
| caja-38-venta-cobrada-1366x768.png | Venta cobrada a 1366×768 | | | |
| caja-39-cerrar-turno.png | Cerrar turno | Conteo ciego vacío | botón «Cerrar turno» | «El sistema no le muestra lo esperado: compara al confirmar.» |
| caja-40-cerrar-turno-conteo.png | Cerrar turno | Efectivo contado por monto y otros medios | | «Efectivo contado» |
| caja-41-cerrar-turno-conteo-continuacion.png | Cerrar turno (abajo) | Otros medios, nota de diferencia | | «Volver a vender», «Confirmar cierre» |
| caja-42-reporte-z.png | Reporte Z | Reporte Z en pantalla (se imprime solo) | «Confirmar cierre» | «Turno cerrado · Reporte Z (cierre)», «Reporte Z enviado a la impresora.» |
| caja-43-reporte-z-continuacion.png | Reporte Z (abajo) | Código de control y botones | | «Reimprimir Z», «Terminar» |
| caja-44-bascula-retire-el-producto.png | Báscula | La báscula no volvió a cero después de una pesada | registrar Banano con peso en la báscula | «Retire el producto de la báscula», «Reiniciar báscula (supervisor)» |
| caja-45-reiniciar-bascula-supervisor.png | Autorización de supervisor | Reiniciar báscula sin el permiso | «Reiniciar báscula (supervisor)» | «Autorización de supervisor» |
| caja-46-bascula-reiniciada.png | Venta vacía | Aviso de báscula reiniciada | después de autorizar | «Báscula reiniciada: ya puede pesar el siguiente producto.» |
| caja-47-vincular-bascula-supervisor.png | Autorización de supervisor | Vincular báscula sin el permiso | F1 → «Vincular báscula» | «…(organization.scale.link)» |

## Integraciones y facturación (`integ-`)

| Archivo | Pantalla | Qué muestra | Ruta / menú | Textos notables |
|---|---|---|---|---|
| integ-01-impresora-archivo.png | Impresora de tiquetes | Valores por defecto: conexión Archivo, 80 mm, PC850, corte, cajón pin 2 | Cajas e impresoras → «Impresora» | «La caja aún no tiene impresora configurada» |
| integ-02-impresora-red.png | Impresora de red | Conexión «Red», 192.168.1.50:9100 | igual | Conexiones: Archivo, Red, Impresora instalada en Windows, Puerto serie/USB (COMx) |
| integ-03-bascula.png | Báscula de la caja | Toledo 8217 (desplazada): esperas, peso mínimo 20 g, digitar el peso, aviso de la tecla de tara, «aún no está vinculada» y el aviso de que «Probar báscula» solo se usa en el computador de la caja | Cajas e impresoras → «Báscula» | «No use la tecla de tara de la báscula; use Ins en la caja.» |
| integ-04-sistecredito-sin-conectar.png | Sistecrédito sin conectar | Explicación y las 3 llaves + ambiente | Configuración → Medios de pago (abajo) | «Conecte su cuenta de aliado» |
| integ-05-sistecredito-llaves-escritas.png | Sistecrédito | Llaves escritas (ocultas) antes de «Conectar» | igual | «Conectar» |
| integ-06-sistecredito-conectado.png | Sistecrédito conectado | Activo en la caja, Producción, llave terminada en la01 | igual | «Volver a probar», «Cambiar llaves», «Desconectar» |
| integ-07-facturacion-sin-configurar.png | Facturación electrónica | Tarjeta Factus «Desconectado», modo apagada | Ventas y caja → Facturación electrónica | «Conecte su cuenta de Factus», «Conectar» |
| integ-08-facturacion-credenciales.png | Datos de Factus | Usuario, contraseña, Client ID, Client secret (de ejemplo), ambiente Pruebas | igual | «Pruebas y producción tienen credenciales distintas…» |
| integ-09-facturacion-modos-de-emision.png | Modos de emisión | Solo cuando el cliente pide factura / En cada venta | lista «Modo de emisión al conectar» | |
| integ-10-facturacion-cada-venta-advertencia.png | Advertencia | Aviso de costo por volumen y casilla de visto bueno | elegir «en cada venta» | «Tengo la cotización de Factus por volumen y el visto bueno de mi contador» |
| integ-11-facturacion-configurada.png | Factus conectado | Pruebas (sandbox), factura en cada venta, usuario en parte, clave validada, rangos sincronizados | «Conectar» | «¡Conectado con Factus en pruebas (sandbox)!…», «Volver a probar», «Cambiar llaves», «Desconectar» |
| integ-11b-facturacion-cambiar-llaves.png | Cambiar llaves | Cuatro datos nuevos; se prueban antes de guardar | «Cambiar llaves» | «Probar y cambiar» |
| integ-12-rangos-sincronizados.png | Rangos de numeración | NA, NC, SETP (factura), DS (documento soporte) | pestaña Rangos → «Sincronizar desde Factus» | «4 rango(s) sincronizado(s) desde el proveedor.» |
| integ-13-rango-asignar.png | Asignar rango | Rango SETP a la sucursal Centro | «Asignar» en la fila SETP | |
| integ-14-rango-asignado.png | Rango asignado | Columna «Asignado a: Centro» (tabla desplazada a la derecha) | | «Rango asignado.» |
| integ-15-facturacion-documentos.png | Documentos | Facturas aceptadas con número fiscal | pestaña Documentos → Consultar | «Aceptado» |
| integ-16-facturacion-documento-detalle.png | Detalle del documento | Estado, número fiscal, CUFE, adquirente, valores, PDF, consulta DIAN, eventos (nota 3) | «Ver» | |
| integ-17-facturacion-alertas-y-conciliacion.png | Alertas y conciliación | Conciliación diaria | pestaña Alertas y conciliación | |
| integ-18-sincronizacion.png | Sincronización con la nube | Pendiente de subir; sin dirección de la nube en desarrollo | Administración → Sincronización | «Sincronizar ahora», «Exportar paquete .possync», «Falta la dirección de la nube» |
| integ-19-tiquete-de-venta.png | Tiquete impreso | Tiquete real de 80 mm (ESC/POS del agente dibujado): promociones, descuento, peso, cliente, impuestos, factura electrónica con CUFE y QR, código de barras (nota 2) | archivo del agente de impresión | |
| integ-20-tiquete-reporte-z.png | Reporte Z impreso | Z de 80 mm con código de control | archivo del agente | |

## Atajos de teclado

**Caja** (`src/Client/Pos.Client/wwwroot/js/pos.js` registra F1–F10, Esc, Supr e Insert y evita la acción del navegador; la lógica está en
`Pages/Caja/Caja.razor.cs`, método `OnKey`. Ctrl+F5 sí recarga la página).

| Tecla | Acción | Dónde / cuándo |
|---|---|---|
| Enter | Agrega el código escaneado o escrito (código de barras, SKU o PLU; con letras busca por nombre) | escáner |
| `3*código` o `3*nombre` + Enter | Varias unidades | escáner |
| ↑ ↓ | Eligen el producto de la lista de sugerencias; sin lista, mueven la línea seleccionada | escáner |
| Enter (con lista abierta) | Agrega el producto elegido | sugerencias del escáner |
| Esc | Cierra la lista de sugerencias; cierra la ventana o vuelve del panel (cobro, cliente, suspendidas) | en todo momento |
| F1 | Abre/cierra la ayuda de atajos | sin panel abierto (también sin turno) |
| F2 | Vuelve al buscador (escáner) | sin panel |
| F3 | Cliente de la venta | sin panel, con turno |
| F4 | Cambiar cantidad de la línea seleccionada (Enter aplica) | sin panel |
| F5 | Consultar precio sin vender (↑ ↓ y Enter en la lista; Esc cierra) | sin panel o dentro de la consulta (también sin turno) |
| F6 | Descuento (línea o toda la venta, % o $, motivo obligatorio) | sin panel |
| F7 | Cancelar la venta (pide confirmación y, según permisos, supervisor) | sin panel |
| F8 | Suspender la venta | sin panel |
| F9 | Recuperar una venta suspendida | sin panel |
| F10 | Abrir el cobro; dentro del cobro, confirmar el cobro | sin panel / en el cobro |
| Enter | Confirma el cobro | campo «Recibido en …» |
| Supr | Anular la línea seleccionada | sin panel |
| Insert (Ins) | Tara manual de la báscula (bolsa o bandeja) | sin panel o pesando, solo con báscula activa |
| Enter | Aplicar cantidad / Agregar peso digitado / Autorizar (en los campos PIN y motivo del supervisor) | ventanas Cantidad, Digitar el peso, Autorización |
| Teclear sin foco | El lector recupera el foco del escáner solo (no se pierden lecturas) | caja sin ventana abierta |

La barra inferior de la caja muestra solo «F5 consultar precio» y «F1 todos los atajos»; los botones del panel derecho muestran su tecla
(Cliente F3, Cantidad F4, Descuento F6, Anular línea Supr, Suspender F8, Recuperar F9, Cancelar venta F7, Cobrar F10).

**Administración**

| Tecla | Acción | Dónde |
|---|---|---|
| Enter | Registra lo leído (`cantidad*código`) | Conteo en curso, campo «Código de barras o SKU» |
| Enter | Busca el producto | «Producto: nombre, SKU o código (Enter busca)» en Nuevo ajuste, Ajuste rápido y Nuevo traslado |
| Enter | Consulta | Facturación → Documentos, campo «Número, número fiscal o identificación» |
| Enter | Confirma el formulario | Ingreso (Caja y Administración), asistente inicial |

## Pantallas que no se capturaron y por qué

| Pantalla | Motivo |
|---|---|
| Menú del usuario (Cambiar contraseña, Modo claro/oscuro/como el sistema, Salir) | No abrió en Edge sin ventana (posible error, nota 6). Se capturó la pantalla de cambiar contraseña por su dirección. |
| Tarjeta «Pagar mensualidad» de Licencia (QR y «Ya pagué, verificar») | Solo aparece cuando el servidor de licencias en la nube envía un enlace de pago; requiere licencia activada contra la nube. |
| Licencia activa / restringida | Sin clave real ni servidor de licencias. |
| Código QR de cobro de Sistecrédito, espera de confirmación, rechazo y «Cobros por revisar» | Generar el cobro llama a Sistecrédito por Internet con llaves reales. |
| Resultado de «Conectar» / «Volver a probar» de Sistecrédito | Prueba las llaves contra Sistecrédito por Internet. |
| Devolución y cambio de mercancía, reintegro por garantía, anulación de una venta cobrada | No hay pantalla (solo API). |
| Historial de backups exitosos, destinos (USB, red, nube) y verificación | Falta pg_dump en el equipo de desarrollo; los destinos se configuran por API. |
| Emparejar una caja adicional y equipos emparejados (Multicaja) | La tienda es Caja Única («no se emparejan otros equipos»). Ver capturas 09 del manual de instalación. |
| Sincronización con envíos a la nube | Sin dirección de la nube en desarrollo (se ve el aviso). |
| Actualización descargada lista para instalar | No hay actualizador ni paquete en desarrollo. |
| Factura rechazada / corregir comprador / contingencia | El proveedor simulado acepta todo en esta corrida. |
| Venta «Sin conexión» con el servidor | No se simuló la caída de red. |
| Documento soporte (compras a proveedores que no facturan) | Los tres proveedores facturan. |
| Roles y permisos (editar un rol) | No hay pantalla: los roles se marcan al crear el usuario (`adm-91/92`) o en su detalle (`adm-91b`). |
