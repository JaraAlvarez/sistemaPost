# Fase 15 · Interfaz: caja y backoffice — Informe

- **Estado:** Implementada — pendiente de tu validación · 2026-09-30
- **Propuesta:** [fase-15-propuesta.md](fase-15-propuesta.md) (aprobada por anticipado: pediste ejecutar 15, 16 y 17 sin confirmación)

## 1. Qué se entregó

| Parte | Entregado |
|---|---|
| Tecnología | `src/Client/Pos.Client`: Blazor WebAssembly + MudBlazor, servida por el servidor de la tienda en `/` con su política de contenido (CSP). No tiene reglas de negocio: todo lo decide la API |
| Ingreso | Pestañas **Caja** (código y PIN) y **Administración** (usuario y contraseña); si la tienda no está configurada lleva a `/instalacion`; cambio obligatorio de la contraseña temporal; en Multicaja, un equipo de la red sin emparejar va a **Emparejar este equipo** (código de la administración) |
| Caja (`/caja`) | Abrir la jornada contando la base por denominación; escáner siempre enfocado (código de barras, PLU o SKU; `3*código` para la cantidad); líneas con precio, descuentos y promociones; totales grandes; **F2** buscar, **F4** cantidad, **F8** suspender, **F9** recuperar, **F10** cobrar, **Supr** anular línea, **Esc** cerrar; cobro con varios medios, referencia del datáfono y cambio; autorización de supervisor en pantalla cuando la API la pide (anular, lotes vencidos…); impresión por el agente local con aviso si no responde; reimprimir la última; cierre ciego con reporte Z y sello |
| Backoffice (`/admin`) | Tablero del día; productos (buscar, crear, cambiar o programar el precio); existencias (bajo el mínimo); **todos los reportes** con parámetros y exportación a Excel, CSV y PDF; usuarios (crear, activar, inactivar, desbloquear); auditoría (buscar, severidad, verificar, reconocer incidentes); licencia (activar, verificar, liberar); backups (respaldar, historial, código de recuperación); actualizaciones (estado, instalar ahora) |
| Avisos | Licencia (demostración, gracia, restringida), alertas de backups e incidentes de integridad en todas las pantallas del backoffice; estado de la licencia en la barra de la caja |
| Instalación | Accesos directos que abren **Edge en modo aplicación**; en modo Caja el instalador autoriza el origen del servidor en el agente de impresión (`C:\ProgramData\PosSupermercado\config\agent.json`) |

## 2. Validación hecha

- Compilación sin errores ni advertencias; `InterfaceTests` (la interfaz se sirve con CSP en `/`, `/caja` y `/admin/…`, los archivos
  estáticos llegan con su tipo, la API conserva sus 404); arquitectura (23), actualizaciones, errores, ventas y equipos (27) en verde.
- **Recorrido en el navegador** contra un servidor real con una base de datos desechable:
  - ingreso de la cajera con código y PIN;
  - abrir la jornada con $100.000;
  - escanear 2 productos, uno con `3*código`;
  - cobrar con F10 y dar $4.900 de cambio;
  - segunda venta con Enter;
  - cerrar la jornada con el arqueo exacto: diferencia $0, reporte Z con el sello;
  - entrar como propietario: tablero con las ventas del día, reporte diario de ventas y productos.
- **Encontrado y corregido en el recorrido:**
  - el enrutamiento se quedaba con los archivos de la interfaz (`UseRouting` ahora va después de los archivos estáticos);
  - `/` sin página por defecto;
  - "Crédito por cambio" aparecía como medio de pago (es interno);
  - el botón de cobro no se activaba con el valor escrito;
  - montos partidos en dos líneas;
  - los navegadores podían quedarse con una copia vieja de los estilos (los archivos llevan `?v=15`).
- **No probado aquí:** la impresora real (no hay agente ni impresora en este equipo; la interfaz avisa y la venta queda cobrada) y el
  emparejamiento de una caja por la red.

## 3. Cómo probarlo tú

1. Compila y arranca el servidor como siempre (`dev-db.ps1`, `dotnet run --project src/Server/Pos.Server.Host`) y abre
   `http://localhost:5480/`. Si la base es nueva, te lleva a `/instalacion`.
2. Para tener datos rápido: `dotnet run --project tools/Pos.LoadTest -- seed --products 200 --terminals 1`. Crea el propietario
   `dueno` y un cajero con código 601 (el PIN queda en `carga.json`).
3. Con el agente de impresión corriendo (`Pos.Terminal.Agent`) y la impresora configurada en la caja, el tiquete sale solo.

## 4. Pendiente (Fase 17)

Pantallas de compras, proveedores, clientes y su ficha, promociones, conteos, traslados, gastos, cuentas por pagar, revisión de jornadas,
configuración, cajas e impresoras y emparejamiento desde la administración. Hoy se hacen por la API.
