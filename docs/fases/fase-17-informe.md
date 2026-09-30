# Fase 17 · Backoffice completo y preparación del piloto — Informe

- **Estado:** Implementada — pendiente de tu validación · 2026-09-30
- **Propuesta:** [fase-17-propuesta.md](fase-17-propuesta.md) (aprobada por anticipado: pediste ejecutar 15, 16 y 17 sin confirmación)
- **Guía del piloto:** [guia-piloto.md](../guia-piloto.md)

## 1. Qué se entregó

| Menú | Pantalla | Qué hace |
|---|---|---|
| Ventas y caja | **Jornadas de caja** (`/admin/jornadas`) | Buscar por fechas y estado, "por revisar"; totales por medio de pago, diferencias, movimientos, reporte X/Z (descargable); revisar con nota; cerrar como supervisor una jornada abandonada |
| | **Gastos** (`/admin/gastos`) | Registrar (categoría, IVA, medio de pago, desde una jornada abierta), anular con motivo, categorías de dos niveles |
| Inventario | **Conteos** (`/admin/conteos`) | Completo o parcial (categorías/productos), ciego; iniciar, registrar con el lector (`cantidad*código`, el cursor se queda en el campo), revisar diferencias, aprobar, anular |
| | **Traslados** (`/admin/traslados`) | Crear, despachar, recibir (cantidades recibidas), anular |
| | **Ajustes** (`/admin/ajustes`) | Crear por motivo (el costo solo en saldo inicial), publicar (o queda por aprobar), aprobar, anular; ajuste rápido |
| | **Importar desde Excel** (`/admin/importar`) | Plantillas, productos y precios con vista previa fila por fila y aplicar/descartar; **saldo inicial** de inventario (crea el ajuste en borrador) |
| Compras y proveedores | **Compras** (`/admin/compras`) | Facturas de compra (líneas, presentaciones, lotes y vencimientos, total de la factura, contabilizar, anular) y órdenes de compra (aprobar, enviar, cerrar, cancelar, registrar la factura contra la orden) |
| | **Proveedores** (`/admin/proveedores`) | Buscar, crear (reutiliza el tercero si ya existe), editar, activar/bloquear/inactivar, estado de cuenta |
| | **Cuentas por pagar** (`/admin/cuentas-por-pagar`) | Vencido, por vencer, total; facturas por pagar, cartera por edades, vencimientos; registrar pagos a varias facturas y anularlos |
| Clientes y promociones | **Clientes** (`/admin/clientes`) | Buscar, crear con consentimiento de datos, editar, bloquear/activar, ficha (resumen, productos, historial), grupos y lista de precios |
| | **Promociones** (`/admin/promociones`) | Los cinco tipos (lleve X pague Y, precio especial, % de descuento, precio por cantidad, combo), vigencia, días y horas, activar, pausar, terminar, **simular** |
| Administración | **Configuración** (`/admin/configuracion`) | Empresa, sucursales y bodegas, medios de pago, parámetros (empresa o sucursal) |
| | **Cajas e impresoras** (`/admin/cajas`) | Cajas (crear, editar, activar/inactivar/bloquear), impresora de tiquetes por caja, equipos emparejados (revocar), **código de emparejamiento** (Multicaja) |

- El menú de la administración quedó **agrupado** (D17-04) y cada opción aparece solo con su permiso.
- `ApiClient.UploadAsync` para subir archivos (importaciones).
- [Guía del piloto](../guia-piloto.md): preparación, instalación en la tienda, capacitación, primer día, primera semana, criterios de
  éxito y plan de reversa. La guía de instalación ya apunta a las pantallas (emparejar, instalar ahora).

## 2. Validación hecha

- Compilación sin errores ni advertencias; `InterfaceTests` con las rutas nuevas.
- **Recorrido en el navegador** contra un servidor real con base de datos desechable:
  - las 15 pantallas nuevas o cambiadas cargan sin errores con el propietario;
  - crear un proveedor → factura de compra con 10 unidades a $2.000 (+IVA) → total $23.800 → contabilizar;
  - la compra aparece en la lista, la cuenta por pagar de $23.800 en Cuentas por pagar y las existencias del producto suben;
  - conteo completo del piso de venta: iniciar (60 productos), registrar `5*código` con el lector; el cursor se queda en el campo.
- **Encontrado y corregido en el recorrido:**
  - las listas enviaban `?status=` vacío y el servidor devolvía nada (ahora el filtro se omite si es "Todos");
  - los formularios de inventario proponían la bodega de averías: ahora la de piso de venta va primero;
  - el paginador de conteos estaba en inglés.
- **No probado aquí:** los demás flujos de cada pantalla (pagos, promociones, clientes, traslados, jornadas, configuración, cajas). Las
  formas de los datos se verificaron contra los contratos de la API y sus pruebas de integración, pero el recorrido completo es tuyo (§3).

## 3. Cómo probarlo tú

Siga la [guía del piloto](../guia-piloto.md) §2 y §3 en su equipo con datos de prueba: es el mismo recorrido que hará la tienda. Anote todo
lo que confunda o falte; eso define la versión 1.1.

## 4. Puntos a revisar (dudas de implementación)

- **Editar clientes:** la ficha del cliente no trae nombres y apellidos por separado; sin el permiso `parties.party.view` la pantalla pide
  volver a escribirlos. Mejora sugerida: agregarlos al DTO del cliente.
- **Reporte X/Z en Jornadas:** se muestra y se descarga como texto; la reimpresión por el agente queda para la v1.1.
- **Pagos a proveedores desde la caja** (con jornada abierta) no están en la pantalla; se registran desde la administración.
- Enumeraciones: la API devuelve algunos valores en mayúsculas (`WINDOWS_SPOOLER`) y recibe los nombres (`WindowsSpooler`); las pantallas
  convierten. Si al guardar una impresora o un parámetro aparece un error de formato, avísame.
- Fuera de alcance (v1.1 según el piloto): cuentas bancarias y retenciones de proveedores, devoluciones a proveedor, solicitudes de datos
  personales y políticas de privacidad, editor de roles.
