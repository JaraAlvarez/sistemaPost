# Guía de diseño de BusinessPost

Sistema de diseño de la interfaz de la tienda (`src/Client/Pos.Client`: caja y administración). Todo lo que se describe aquí vive en el
código y se puede cambiar en un solo lugar:

| Qué | Dónde |
|---|---|
| Tema de MudBlazor (colores, tipografía, radios, alturas) | `Services/Tema.cs` (`TemaBusinessPost.Theme`) |
| Preferencias del equipo (modo claro/oscuro, menú compacto) | `Services/Tema.cs` (`Tema`) + `wwwroot/js/pos.js` (`pos.pref`) |
| Tokens propios, diseño de la administración, caja y paneles | `wwwroot/css/app.css` |
| Menú lateral y migas de pan (mapa único de pantallas) | `Services/Navegacion.cs` |
| Componentes comunes | `Shared/` (ver §6) |
| Logo | `Shared/Logo.razor` y `wwwroot/img/businesspost.svg` |
| Fuente | `wwwroot/fonts/` (Inter, SIL OFL 1.1) |

**Principio:** la interfaz funciona **sin Internet**. Nada se carga desde un CDN: fuentes, iconos (los de MudBlazor van en su paquete),
estilos y scripts se sirven desde el servidor de la tienda. La política de contenido (CSP) solo permite `'self'`.

## 1. Principios

1. **Velocidad en la caja.** La cajera no debería necesitar el mouse: el campo de escaneo siempre tiene el foco y cada acción tiene su tecla.
2. **Una cifra importante, grande.** En cada pantalla hay una sola cifra protagonista (el total en la caja, el cambio al cobrar, la venta del día en el tablero).
3. **Estados claros.** Sin jornada, sin conexión, venta suspendida, modo demostración o factura en proceso se ven con color **y** texto (nunca solo color).
4. **Lenguaje simple.** Mensajes en español de Colombia, en segunda persona formal ("Escriba…", "Pida…"). Los errores dicen qué pasó, qué hacer y el código para soporte: *"El producto no existe (código CATALOG.PRODUCT_NOT_FOUND)"* (`ApiException.Texto`).
5. **Coherencia.** Todas las pantallas de la administración usan el mismo encabezado, tablas, estados vacíos, diálogos y avisos.

## 2. Color

Cada par texto/fondo cumple el contraste **AA** (4,5:1) o más. Los valores exactos están en `Services/Tema.cs`.

### Marca

| Token | Claro | Oscuro | Uso |
|---|---|---|---|
| Primario (índigo) | `#3341B0` (8,3:1 sobre blanco) | `#8B9CFF` | Acciones principales, enlaces, selección, foco |
| Secundario (verde azulado) | `#0F766E` (5,5:1) | `#2DD4BF` | Acentos, filtros alternos, barras secundarias |
| Degradado de marca | `#3341B0 → #0F766E` | igual | Logo, avatar, panel de acceso |
| Azul noche | `#111A3A` | `#0A1024` | Menú lateral, barra y total de la caja |

### Semánticos

| Significado | Claro | Oscuro | Uso |
|---|---|---|---|
| Éxito | `#15803D` (5,0:1) | `#4ADE80` | Cobrar, cambio, venta cobrada, promociones aplicadas, variación positiva |
| Advertencia | `#B45309` (5,0:1) | `#FBBF24` | Falta dinero, productos bajo el mínimo, licencia en gracia/demostración |
| Error | `#B91C1C` (6,5:1) | `#F87171` | Sin conexión, anular, cancelar, incidentes, variación negativa |
| Información | `#0369A1` (5,9:1) | `#38BDF8` | Factura en proceso, avisos neutros |

### Neutros

| Token | Claro | Oscuro |
|---|---|---|
| Fondo | `#F4F6FA` | `#0B1120` |
| Superficie (tarjetas, tablas, paneles) | `#FFFFFF` | `#111827` |
| Texto principal | `#111827` | `#E5E7EB` |
| Texto secundario | `#4B5563` (7,6:1) | `#9CA3AF` (7,0:1) |
| Líneas | `#E2E8F0` | `#1F2A3D` |

### Modo claro y oscuro

- El encabezado de la administración tiene el botón de modo y el menú del usuario permite **Claro**, **Oscuro** o **Como el sistema**.
- La preferencia se recuerda en el navegador de ese equipo (`localStorage`, clave `pos.tema`); `pos.js` la aplica antes de que cargue la
  aplicación para que la pantalla de carga no destelle.
- La barra y el total de la caja son oscuros en ambos modos (alto contraste bajo la luz del local).

## 3. Tipografía

- **Inter** (variable 100–900), SIL Open Font License 1.1, empaquetada en `wwwroot/fonts` (subconjuntos *latin*, que cubre todo el
  español, y *latin-ext*). Respaldo: Segoe UI, system-ui. Licencia registrada en [licencias-terceros.md](licencias-terceros.md).
- Cifras con **números tabulares** (`font-variant-numeric: tabular-nums`, clase `.num`): los montos se alinean a la derecha y no "bailan".
- Los botones van en tipo oración (sin MAYÚSCULAS), peso 600.

| Estilo | Tamaño / peso | Uso |
|---|---|---|
| H4 | 28 px / 700 | Título de las pantallas de acceso |
| H5 | 22 px / 700 | Título de cada pantalla (`<h1>`) |
| H6 | 17 px / 650 | Secciones y títulos de tarjetas |
| Cuerpo | 15 px / 400 | Texto general |
| Cuerpo 2 | 14 px / 400 | Tablas, formularios |
| Leyenda | 12,8 px | Ayudas y notas |
| Sobrelínea | 11,5 px / 700, espaciado | Encabezados de tabla, etiquetas de cifras |
| Total de la caja | 40–64 px / 800 | Total a pagar, cambio |

## 4. Espaciado, radios, sombras y logo

- **Espaciado:** escala de 4 px (`--bp-e1` 4 · `--bp-e2` 8 · `--bp-e3` 12 · `--bp-e4` 16 · `--bp-e5` 20 · `--bp-e6` 24 · `--bp-e8` 32 ·
  `--bp-e10` 40 · `--bp-e12` 48). Coincide con las clases `pa-*`/`ma-*` de MudBlazor (1 = 4 px).
- **Radios:** `--bp-radio-s` 6 px (etiquetas) · `--bp-radio-m` 8 px (botones, campos; es el radio por defecto del tema) · `--bp-radio-l` 12 px
  (tarjetas, tablas) · `--bp-radio-xl` 16 px (paneles y diálogos).
- **Sombras:** `--bp-sombra-1` (tarjetas), `--bp-sombra-2` (al pasar el mouse), `--bp-sombra-3` (paneles).
- **Toque:** todo control táctil mide **44 px** o más (`--bp-tactil`); en la caja **56 px** (`--bp-tactil-caja`) y el botón Cobrar 72 px.
- **Logo:** símbolo de una bolsa de mercado con un código de barras sobre el degradado de marca, y el nombre "Business**Post**" (Post en el
  color primario). `<Logo Tamano="32" ConNombre="true" SobreOscuro="false" />`. Sobre fondos oscuros, `SobreOscuro="true"`. No deformar, no
  cambiar los colores del símbolo, dejar alrededor un espacio libre de al menos la mitad del símbolo.

## 5. Estructura de las pantallas

### Acceso (`Shared/PantallaAcceso.razor`)

Ingresar, Cambiar contraseña y Emparejar: panel de marca a la izquierda (degradado, lema y tres beneficios) y el formulario a la derecha;
en pantallas de menos de 900 px solo el formulario con el logo arriba. Campos delineados, botón principal grande (52 px).

### Administración (`Layout/AdminLayout.razor`)

- **Menú lateral** oscuro, agrupado (Ventas y caja · Inventario · Compras y proveedores · Clientes y promociones · Administración); el grupo
  de la pantalla actual se abre solo. Cada opción aparece solo si el usuario tiene el permiso. Se **contrae** a iconos con el botón ☰ (con
  descripción al pasar el mouse); la preferencia se recuerda (`pos.menu`).
- **Encabezado:** sucursal y empresa (si el usuario puede verlas), **Ir a la caja**, modo claro/oscuro y el menú del usuario (cambiar
  contraseña, modo, salir).
- **Contenido** (máx. 1480 px): avisos permanentes (actualización, licencia, backups, integridad) y la pantalla.
- Enlace "Saltar al contenido" para teclado y lectores de pantalla.

### Encabezado de pantalla (`Shared/Pagina.razor`)

`<Pagina Titulo="…" Subtitulo="…" Cargando="…" Error="…"><Acciones>…</Acciones>…</Pagina>`

- **Migas de pan** automáticas desde `Navegacion.cs` (Inicio › Grupo › Pantalla).
- Icono de la sección, título `<h1>` (recibe el foco al navegar), subtítulo opcional y las acciones a la derecha (la principal en
  botón relleno, las demás delineadas).
- **Carga:** la primera vez muestra un **esqueleto** (`Shared/Esqueleto.razor`); al recargar, una barra fina que no mueve la pantalla.
- **Error:** aviso rojo "No se pudo completar." con el mensaje y el código.

## 6. Componentes

| Componente | Uso |
|---|---|
| `Tarjeta` | Indicador: título, cifra grande, icono con el color del significado (`Tono`: primario, secundario, exito, advertencia, error, info, neutro), variación ▲▼ contra una referencia y enlace opcional (`Href`) a la pantalla que la explica |
| `EstadoVacio` | Tablas y listas sin datos: ilustración, mensaje, detalle y acción opcional. `Compacto="true"` dentro de ventanas |
| `Paginador` | Paginador en español para `MudTable` (25/50/100 filas); se oculta si todo cabe en una página. Las tablas de lista llevan `RowsPerPage="25"` |
| `Esqueleto` | Silueta de carga (tarjetas y filas) |
| `Confirmacion` + `Dialogs.ConfirmarAsync(título, mensaje, acción, peligro)` | Diálogo de confirmación uniforme. Con `peligro: true` el botón es rojo (anular, inactivar, bloquear, cancelar, liberar). El foco empieza en **Cancelar**; Esc cierra |
| `Logo` | Logo en SVG |
| `Conteo` | Conteo de efectivo por billete y moneda con el subtotal de cada uno |
| Paneles (`div.panel-fondo > div.panel`) | Ventanas de formularios y de la caja: título con línea, contenido con desplazamiento y **barra de acciones fija abajo** (`div.acciones`: Cancelar a la izquierda de la acción principal). Llevan `role="dialog"` y `aria-modal`. En pantallas pequeñas ocupan toda la pantalla |

### Tablas

Una sola apariencia en toda la administración (estilos globales en `app.css`): borde de 1 px con radio de 12 px, encabezado en sobrelínea
gris, filas densas con resaltado al pasar el mouse, cifras a la derecha con números tabulares, paginador abajo y estado vacío con
ilustración. La búsqueda y los filtros van arriba de la tabla (texto con icono de lupa y selects de estado).

### Formularios

- Etiqueta siempre visible (nunca solo el marcador de posición); ayuda debajo del campo cuando hace falta (`HelperText`).
- Validación en el momento en que se puede saber (p. ej. la contraseña nueva: longitud y coincidencia) y el botón principal deshabilitado
  hasta que el formulario sea válido; los errores del servidor se muestran con su código.
- Acciones destructivas: color de error y confirmación.

## 7. Caja (`Pages/Caja`)

Diseñada para **1024×768 a 1920×1080**, teclado, lector de código de barras y pantalla táctil.

```
┌ Barra (azul noche): logo · Caja · Jornada · Cajero ········· En línea · Licencia · N suspendidas · 10:42 · Reimprimir · Cerrar jornada · Salir ┐
├ Avisos: sin conexión (rojo) · licencia restringida / demostración ─────────────────────────────────────────────────────────────────────────┤
│ [▦ Escanee o escriba el código……………………………] [Buscar F2] │ TOTAL A PAGAR                          12 art. │
│ ┌ # │ Producto (SKU · promoción) │ Cant. │ Precio │ Desc. │ Subtotal ┐ │                           $ 123.450        │
│ │ … la línea seleccionada con barra índigo, la nueva destella …       │ │ Subtotal · Impuestos · Ahorro · Redondeo    │
│ └──────────────────────────────────────────────────────────────────────┘ │ Cliente: Consumidor final                   │
│                                                                          │ [      Cobrar  F10      ]  (verde, 72 px)   │
│                                                                          │ [Cliente F3] [Cantidad F4] [Descuento F6]…  │
└ ↑↓ elegir línea · 3*código · Esc cerrar · F1 atajos ──────────────────────┴──────────────────────────────────────────────┘
```

- El **campo de escaneo** recupera el foco solo: al cerrar una ventana, al tocar fuera de un campo y al teclear sin foco (`pos.js`), así el
  lector nunca pierde una lectura.
- Después de cobrar, la columna derecha muestra **"Venta N cobrada" y el CAMBIO** en grande (verde) y el estado de la **factura
  electrónica** (validada por la DIAN, en proceso, en contingencia o con problema).
- **Estados:** "En línea"/"Sin conexión" (se revisa cada 15 s; barra roja con instrucciones si se cae), licencia (demostración, gracia,
  restringida), ventas suspendidas (clic para recuperarlas), sin jornada abierta (ventana de apertura con el conteo de la base).
- **Cobro:** total, recibido y **cambio/falta** en cifras grandes; medios de pago como botones grandes con icono; valores rápidos de
  billetes (exacto, 10.000, 20.000, 50.000…); referencia del datáfono cuando el medio la pide; pago mixto.

### Atajos de la caja

| Tecla | Acción |
|---|---|
| **Enter** | Agregar el código escrito o escaneado (`3*código` agrega 3 unidades) |
| **↑ / ↓** | Elegir la línea (en el campo de escaneo) |
| **F1** | Ayuda de atajos |
| **F2** | Buscar producto por nombre, SKU o código |
| **F3** | Cliente de la venta (buscar por cédula/NIT, nombre o celular; o consumidor final) |
| **F4** | Cambiar la cantidad de la línea seleccionada |
| **F6** | Descuento a la línea o a toda la venta (% o $, con motivo; pide supervisor si hace falta) |
| **F7** | Cancelar la venta (con confirmación) |
| **F8** | Suspender la venta |
| **F9** | Recuperar una venta suspendida |
| **F10** | Cobrar; dentro del cobro, confirmar |
| **Supr** | Anular la línea seleccionada |
| **Esc** | Cerrar la ventana abierta |
| F5 | Bloqueada en la caja (recargar cerraría la sesión) |

## 8. Accesibilidad

- Contraste AA en todos los pares de color (§2); los estados usan color **y** texto o icono.
- **Foco visible** de 3 px en el color primario (claro u oscuro) en todos los controles.
- Títulos jerárquicos (`h1` por pantalla, `h2` por sección), migas de pan con `aria-current`, regiones con nombre (`nav`, `main`, `aside`).
- Paneles con `role="dialog"`/`aria-modal`, diálogo de confirmación con el foco inicial en Cancelar.
- Avisos que cambian (total, cambio, conexión) con `aria-live`.
- Botones de solo icono con `aria-label` y descripción emergente.
- Se respeta `prefers-reduced-motion` (sin animaciones).
- Todo se puede operar con teclado: la caja por completo con las teclas de §7.
