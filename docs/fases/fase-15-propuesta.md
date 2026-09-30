# Fase 15 · Interfaz: caja y backoffice — Propuesta

> Estado: **APROBADA POR ANTICIPADO** · 2026-09-30. Pediste ejecutar las Fases 15, 16 y 17 sin pedir confirmación: se aplican las
> recomendaciones de este documento y quedan escritas para tu revisión.
> Requisitos previos: Fases 1 a 14. Toda la lógica ya existe en la API (`/api/v1`); esta fase no cambia reglas de negocio.
> Base: plan [12 §S, fase 15](../12-plan-riesgos-decisiones.md), doc [03 "Capa de interfaz"](../03-arquitectura.md), riesgo R-08 (UI ligera).

**Convenciones:** 🔒 = decisión difícil de cambiar después. ⚙️ = configurable. **§** = "sección".

## 0. Objetivo y alcance

Después de esta fase **el propietario y los cajeros usan el sistema en pantalla**, sin herramientas técnicas:
- el cajero entra con su código y PIN, abre la jornada, vende con el lector de códigos y el teclado, cobra, imprime y cierra;
- el propietario entra al backoffice: tablero, productos y precios, existencias, reportes, usuarios, licencia, backups y actualizaciones.

| Incluido | Excluido (fase) |
|---|---|
| **Caja**: ingreso con PIN, abrir jornada, venta (escáner siempre enfocado, teclas rápidas), cantidad, anular línea, cliente, suspender y recuperar, cobro con varios medios y cambio, impresión por el agente, reimpresión, cierre ciego | Pantallas de compras, clientes (ficha completa), promociones, conteos, traslados, gastos y cuentas por pagar (Fase 17) |
| Autorización de supervisor en pantalla (descuentos, anulaciones) | Caja autónoma sin servidor (posterior) |
| **Backoffice**: tablero, productos (buscar, crear, editar, precio), existencias por bodega, **todos los reportes** (catálogo genérico con exportación a Excel, CSV y PDF), usuarios y roles, licencia, backups y código de recuperación, actualizaciones, auditoría e incidentes | Sincronización con la nube y portal del cliente (Fase 16) |
| Emparejar el equipo de una caja Multicaja en pantalla | |
| Avisos permanentes: licencia, backups, integridad | |
| Diseño responsivo (caja 1024×768 o más; backoffice también en tableta) | Aplicación móvil |

## 1. Decisiones

| # | Decisión | Recomendación y motivo |
|---|---|---|
| D15-01 🔒 | Tecnología | **Blazor WebAssembly + MudBlazor (MIT)**. Mismo lenguaje C# de todo el sistema (un solo equipo, una sola cadena de compilación, sin Node en el proyecto); MudBlazor ya está aprobado en el portal de la nube. La aplicación corre **en el navegador del equipo** y solo habla con la API (la regla de doc 03 se mantiene: la UI no tiene lógica de negocio). Alternativa descartada: React + TypeScript (segundo lenguaje y cadena de compilación con Node); Blazor Server (la pantalla se congela si se corta el circuito) |
| D15-02 🔒 | Dónde se sirve | La sirve el **servidor de la tienda** en `/` (localhost en Caja Única; `https://servidor:5443/` en Multicaja). Sin instalar nada en las cajas aparte del agente de impresión. La cabecera `Content-Security-Policy` protege la aplicación |
| D15-03 | Cáscara | **Edge en modo aplicación** (`msedge --app=URL --kiosk` opcional) desde el acceso directo del instalador; Edge viene con Windows. Sin Electron ni Tauri (R-08) |
| D15-04 | Credencial de la caja | Al emparejar, el equipo guarda su id y secreto en el almacenamiento del navegador de ESE equipo y los envía en cada petición (`X-Device-Id`, `X-Device-Secret`). El token de sesión vive solo en memoria de la pestaña |
| D15-05 | Impresión | La caja envía el tiquete al **agente local** (`http://localhost:5490`, Fase 7) con la configuración de la impresora de la caja; si el agente no responde, avisa y permite reimprimir |
| D15-06 | Teclado | Caja: F2 buscar producto, F4 cantidad, F8 suspender, F9 recuperar, F10 cobrar, Supr anular línea, Esc cancelar; el campo del escáner recupera el foco tras cada acción |
| D15-07 | Idioma y formato | Todo en español de Colombia: pesos sin decimales visibles cuando son enteros, fechas `dd/MM/yyyy`, errores de la API tal cual (ya vienen en español) |

## 2. Estructura

```
src/Client/Pos.Client            Blazor WebAssembly: Pages/Caja/*, Pages/Admin/*, servicios ApiClient, Session, AgentPrinter
src/Server/Pos.Server.Host       Sirve la aplicación (archivos del marco Blazor + index.html de reserva) y la CSP
```

## 3. Camino de las fases siguientes (aprobado por anticipado)

| Fase | Contenido |
|---|---|
| **16 · Sincronización y portal del cliente** | La visión del propietario (revisión de la Fase 2 §9–§10, ADR-0014): cada venta y cada cambio de maestros sube a la nube en cuanto hay Internet (bandeja de salida ya existente), paquete `.possync` cifrado para cuando no hubo Internet, nube multiempresa que recibe y consolida, portal web del cliente para consultar ventas e inventario de sus sucursales |
| **17 · Backoffice completo y preparación del piloto** | Pantallas restantes (compras, proveedores, clientes, promociones, conteos, traslados, gastos, cuentas por pagar, jornadas y arqueos, configuración), guía del piloto y lista de verificación en tienda |

## 4. Validación (según tu forma de trabajo)

Yo: compilación sin advertencias, una prueba que sirve la aplicación desde el servidor (archivos del marco y CSP), y un recorrido de humo en
el navegador integrado (entrar, vender, cobrar, cerrar; backoffice: tablero, productos, reportes). Tú: el uso real con el lector y la impresora.
