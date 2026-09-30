# Guía de diseño del portal de la nube

Portal web de BusinessPost (`src/Cloud/Pos.Cloud.Host`, producción en `https://businesspost.tutiendanueva.com/`). Lo usan el
propietario (superadministrador), soporte y, en "Datos de las tiendas", los clientes. Comparte la marca del programa de caja
(índigo sobrio y verde azulado, fuente Inter) pero es independiente: no usa archivos de `src/Client`.

## 1. Principios

- **Claro antes que vistoso.** Cada pantalla responde "¿qué pasa y qué hago?": título, descripción corta, datos y acciones.
- **Todo local.** La CSP del portal (`SecurityHeadersMiddleware`) solo permite `'self'`: la fuente, el logo, los íconos y el script del
  tema se sirven desde el propio servidor. No hay CDN, ni Google Fonts, ni scripts en línea.
- **Nada de lógica en la interfaz.** El rediseño no cambia casos de uso, permisos, cookies, antifalsificación ni rutas.
- **Accesible (WCAG 2.1 AA).** Contraste ≥ 4,5:1, foco visible, estados que no dependen solo del color, textos en español claro.

## 2. Tokens

| Token | Valor | Uso |
|---|---|---|
| Primario (claro / oscuro) | `#3341B0` / `#8B9CFF` | Botones principales, enlaces, foco |
| Secundario | `#0F766E` / `#2DD4BF` | Acento de marca ("Post" del logo), indicadores de instalaciones |
| Éxito · Info · Atención · Error | `#15803D` · `#0369A1` · `#B45309` · `#B91C1C` (claro) | Chips y alertas semánticos |
| Fondo · Superficie | `#F4F6FA` · `#FFFFFF` (claro); `#0B1120` · `#111827` (oscuro) | Página y tarjetas |
| Menú lateral | `#111A3A` (claro) / `#0A1024` (oscuro) | Siempre oscuro; texto `#D5DCE8` |
| Espaciado | escala de 4 px (`--bp-e1`…`--bp-e12`) | Coincide con `pa-*`/`ma-*` de MudBlazor |
| Radios | 6 · 8 · 12 · 16 px | Campos · botones · tarjetas · tarjeta de ingreso |
| Sombras | `--bp-sombra-1/2/3` | Tarjeta · tarjeta al pasar el ratón · ingreso |

El tema de MudBlazor está en `Components/Shared/PortalTheme.cs` (paletas claro/oscuro, tipografía, radio 8 px, barra de 60 px, menú
de 264 px). Los tokens propios y los estilos de las pantallas de acceso están en `wwwroot/app.css`.

## 3. Tipografía

**Inter** variable (100–900), WOFF2 en `wwwroot/fonts` (subconjunto *latin* para el español y *latin-ext*), con `OFL.txt`. Se precarga
el subconjunto latino desde `App.razor`. Escala: título de página 26 px/700, sección 17 px/650, cuerpo 15 px, tablas 14 px, ayudas
13 px. Cifras con `font-variant-numeric: tabular-nums` (clase `num`), identificadores (kid, prefijos de clave, SKU) en monoespaciada
(clase `mono`).

## 4. Marca

`Components/Shared/BrandLogo.razor`: símbolo en SVG en línea (cuadrado índigo con la "B" y un punto verde azulado) y el nombre
"Business**Post**" en texto con Inter. `wwwroot/favicon.svg` es el mismo símbolo.

## 5. Modo claro y oscuro

- **Portal (MudBlazor):** sigue la preferencia del sistema; el botón sol/luna de la barra superior la fija y se recuerda en el
  navegador (`localStorage`, clave `bp.portal.tema`) mediante el módulo `wwwroot/js/tema.js` (mismo origen, permitido por la CSP).
- **Pantallas de acceso:** son formularios renderizados en el servidor sin JavaScript; usan `prefers-color-scheme`.

## 6. Estructura

- **Barra superior:** botón del menú, logo (cuando el menú está cerrado o en el celular), modo claro/oscuro, nombre y rol del usuario
  con su avatar de iniciales y "Cerrar sesión" (formulario POST a `cuenta/salir` con antifalsificación, sin cambios).
- **Menú lateral agrupado** (solo las entradas que el rol puede abrir): Tablero · *Licenciamiento* (Clientes, Empresas, Suscripciones,
  Instalaciones y equipos) · *Operación* (Datos de las tiendas) · *Administración* (Usuarios del portal, Claves de firma, Auditoría)
  · *Personal* (Mi cuenta). En pantallas menores de 960 px el menú se oculta y se abre sobre el contenido.
- **Encabezado de página** (`PageHeader`): migas de pan (`nav` con `aria-current`), título `h1` (recibe el foco al navegar),
  descripción y acciones a la derecha.
- Enlace "Saltar al contenido" al inicio de la página para teclado y lectores de pantalla.

## 7. Componentes del portal (`Components/Shared`)

| Componente | Uso |
|---|---|
| `PageHeader` | Migas, título, descripción, acciones de cada pantalla |
| `StatCard` | Indicador del tablero: ícono de color semántico, etiqueta, valor y pista; con `Href` la tarjeta es un enlace |
| `StatusChip` | Estado con color semántico **e ícono** (verde vigente, azul prueba, ámbar atención, rojo bloqueo, gris neutro) |
| `EmptyState` | Tabla o lista vacía: ícono, título y qué hacer |
| `TableSkeleton` | Esqueleto de carga mientras llega la consulta (anuncia "Cargando…") |
| `ConfirmDialog` + `PortalDialogs` | Confirmación uniforme de acciones delicadas; `ConfirmWithReasonAsync` pide el motivo (mínimo 5 caracteres, el mismo que exige el servidor) |
| `AuthAlert` | Mensaje de error (rojo) o aviso (ámbar) de las pantallas de acceso, con `role="alert"` |

**Tablas:** tarjeta (`bp-card`) con barra de herramientas (`bp-toolbar`: búsqueda con ícono, filtros, contador "X de Y"), tabla
densa con encabezados en mayúsculas pequeñas, desplazamiento horizontal en el celular (`bp-table-wrap`), subtítulo gris en la misma
celda (NIT, correo) y acciones alineadas a la derecha.

**Acciones delicadas** (todas con `ConfirmDialog`): suspender, cancelar, reactivar, cambiar edición y extender gracia de una
suscripción; regenerar y revocar la clave de licencia; liberar un equipo; revocar una clave de firma; restablecer la contraseña o el
doble factor de un usuario; cerrar una sesión; desactivar un cliente o una empresa. El motivo se escribe en el diálogo (antes era un
campo suelto en la pantalla).

## 8. Pantallas de acceso (`/cuenta`)

Diseño de dos columnas en escritorio (panel de marca con degradado índigo → verde azulado y la tarjeta del formulario) y una sola
columna en el celular.

- **Ingresar:** si Google está configurado, **"Continuar con Google"** es la opción principal y el correo con contraseña va debajo,
  tras el separador "o con su correo y contraseña", con botón secundario. Sin Google, el formulario de contraseña es el principal.
- **Botón de Google** según las [pautas de marca de Google Identity](https://developers.google.com/identity/branding-guidelines):
  logotipo "G" oficial a cuatro colores sin modificar (SVG en línea), texto "Continuar con Google", tema claro (fondo `#FFFFFF`,
  borde `#747775`, texto `#1F1F1F`) u oscuro (fondo `#131314`, borde `#8E918F`, texto `#E3E3E3`) según el sistema, 40 px de alto,
  forma de píldora, logotipo de 20 px con 12 px de margen y 10 px hasta el texto, 14 px en peso medio. Las pautas piden Roboto
  Medium: se usa si el equipo la tiene y, si no, Inter en el mismo peso (la CSP impide cargar Roboto desde Google Fonts).
- **Mensajes:** error de credenciales o de Google en rojo; "La página de acceso venció…" (antifalsificación vencida) en ámbar, como
  aviso y no como error. En "Sin acceso", la sesión vencida dice "Su sesión venció o se cerró. Ingrese de nuevo para continuar."
- **Código de verificación y activación del doble factor:** campo grande de 6 dígitos (`inputmode="numeric"`,
  `autocomplete="one-time-code"`), pasos numerados y QR con fondo blanco también en modo oscuro.

## 9. Accesibilidad

- Foco visible de 3 px (`--bp-foco`, índigo en claro y `#AEBBFF` en oscuro/menú lateral).
- Todos los botones de solo ícono tienen `aria-label` (menú, tema, cerrar sesión, cerrar paneles).
- Los estados combinan color, ícono y texto; los contadores de las tablas usan `role="status"`.
- Encabezados en orden (`h1` por página, `h2` por sección, `h3` dentro de las tarjetas) y `th scope`.
- `prefers-reduced-motion` desactiva las transiciones.
- Objetivos táctiles de al menos 40–44 px en el ingreso y botones de MudBlazor en tamaño normal en las acciones.

## 10. Verificación

`dotnet build Pos.slnx -c Release` sin advertencias y `tests/Pos.Cloud.IntegrationTests` en verde (las pruebas buscan el texto
"Continuar con Google" y los formularios de `/cuenta` sin cambios de nombres de campos). Para revisar el diseño en local: PostgreSQL 18
en Docker en un puerto libre, `setup-database`, `generate-signing-key`, `create-superadmin` y el servidor en
`https://localhost:<puerto>` con el certificado de desarrollo (la cookie del portal exige HTTPS).
