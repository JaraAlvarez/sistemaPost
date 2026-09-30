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
