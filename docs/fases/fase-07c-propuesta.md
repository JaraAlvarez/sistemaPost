# Fase 7-C · Perfiles de negocio y básculas (catálogo de compatibles) — Propuesta

> Estado: **APROBADA en lo conceptual** · 2026-10-17 · **parte A implementada** (ver "Informe 7-C parte A") (seis tipos de negocio aprobados; báscula de etiqueta + báscula conectada;
> catálogo de básculas con imagen y referencia, cambio de báscula por daño o mejora). Empieza cuando termine la Fase 13-B.
> Base: [doc 01](../01-analisis-producto.md) (peso variable, báscula conectada), [doc 02](../02-modulos.md) §Devices,
> [doc 03](../03-arquitectura.md) (periféricos como interfaz + driver), [ADR-0036](../adr/0036-agente-de-caja.md) (agente de caja),
> RN-CAT-03 y RN-SAL-16 ([doc 05](../05-reglas-y-estados.md)), [plan de variantes](plan-variantes-ropa-calzado.md).

## 1. Perfiles de negocio y funciones activables

En el instalador (paso "Tu negocio") y en **Configuración → Funciones del negocio**:

| Perfil | Se enciende | Se oculta |
|---|---|---|
| Supermercado / minimercado | Báscula, PLU, lotes y vencimientos, promociones, compras | Tallas y colores |
| Fruver / carnicería | Báscula (conectada y de etiqueta), kilo o libra, tara | Tallas; lotes opcional |
| Droguería | Lotes y vencimientos obligatorios, alertas | Báscula, tallas |
| Ropa / calzado / deportes | Tallas y colores (cuando exista la fase de variantes), cambios por talla, etiquetas | Báscula, PLU, vencimientos |
| Ferretería / papelería / miscelánea | Venta por metro o fracción, precio abierto | Báscula, tallas, vencimientos |
| Otro | Lo básico | — |

- **No es un plan de precios** (ADR-0015 sigue: todas las funciones en Caja Única y Multicaja). Solo ordena pantallas, menús y
  valores iniciales (unidades, categorías de ejemplo, palabras como "prenda").
- Cada función es un interruptor que el dueño puede cambiar; el perfil es un punto de partida. Apagar una función **oculta**, nunca
  borra datos.
- La nube conoce el perfil de cada cliente (portal: filtro y estadística).

## 2. Básculas: dos formas

| Forma | Cómo funciona | Estado |
|---|---|---|
| **Báscula etiquetadora** (sección de carnes o fruver) | Pesa, imprime etiqueta EAN-13 con PLU + peso o precio; la caja la escanea | **Ya funciona** (D4-12, RN-SAL-16) |
| **Báscula conectada a la caja** | Cable serial/USB al computador de la caja; el agente lee el peso en vivo | **Nueva** |
| Escáner-báscula de caja (opcional) | Escáner de mostrador con báscula integrada (tipo supermercado grande) | Nueva, si hay demanda |

## 3. Catálogo de básculas compatibles (lo que pediste)

En **Configuración → Cajas e impresoras → Báscula** (y en el instalador si el perfil usa báscula):

1. **Galería** con tarjetas: **imagen**, **marca y referencia**, tipo (de mostrador conectada · etiquetadora · escáner-báscula),
   conexión (serial RS-232 / USB-serial / red) y la etiqueta **"Compatible con BusinessPost"**.
2. Al elegir una: **guía de instalación ilustrada de ese modelo** (qué cable usar, en qué puerto conectarla, qué configurar en la
   báscula: protocolo y velocidad) y el sistema aplica la configuración correcta (protocolo, velocidad, bits, comando de lectura).
3. **"Probar báscula"**: el peso en vivo en pantalla, indicador de "estable", prueba de tara; no deja guardar si no lee.
4. **Cambiar de báscula** (daño o mejora): asistente "Reemplazar báscula" que conserva el historial (qué báscula tuvo cada caja y
   desde cuándo), desactiva la anterior y repite la prueba con la nueva. Queda en la auditoría.
5. **Modelo no listado**: opción "Otra báscula (configuración manual)" para técnicos, con protocolos genéricos.
6. Las etiquetadoras también salen en el catálogo: al elegir una, se crea sola la **regla de código de barras** de ese modelo
   (prefijo, PLU, peso/precio) — lo que hoy se configura a mano.

**Catálogo administrado desde tu portal** (nube): tú agregas o retiras modelos (imagen, referencia, protocolo, guía) y las tiendas
lo reciben con la licencia/sincronización; así no hace falta una versión nueva del programa para sumar una báscula compatible.

**Imágenes:** las fotos de producto de cada fabricante tienen derechos de autor. Opciones: (a) fotos propias o de tus
distribuidores (recomendado: tomarlas a las básculas que vendas o instales), (b) ilustraciones propias con el estilo de BusinessPost,
(c) fotos oficiales con permiso del fabricante o distribuidor. Por defecto se entrega con ilustraciones propias por tipo, y desde el
portal se reemplaza cada imagen por la foto real.

## 4. En la caja

- Producto por peso: al escanearlo o buscarlo, si la caja tiene báscula conectada aparece **"Pesar"** (o se pesa solo al escanear,
  configurable): peso en vivo → al estabilizarse se agrega **0,700 kg × $3.000/kg = $2.100**.
- **Tara** (bolsa, bandeja) por producto o tecla rápida. Sin báscula, el cajero digita el peso (como hoy).
- No agrega con peso inestable, en cero o negativo; muestra el peso en el tiquete.
- **Kilo o libra**: precio por kg o por **libra de 500 g** (uso colombiano), configurable por producto; ⚙️ factor de la libra.

## 5. Primeros modelos (a confirmar con fichas técnicas y pruebas reales)

| Tipo | Candidatos comunes en Colombia | Protocolo |
|---|---|---|
| Mostrador conectada | CAS PD-II, CAS ER Plus, Torrey PCR/LPC, Moretti | CAS / Toledo 8217 (muchas lo emulan) |
| Etiquetadora | CAS CL5200, Dibal serie L/M, Epelsa | Reglas EAN-13 prefijo 20–28 |
| Escáner-báscula | Datalogic Magellan 8x00 | SASI / NCR |

El primer driver será **Toledo 8217** (lo emulan muchas marcas) + **CAS**; los demás se agregan como drivers sin tocar el POS
(doc 03: interfaz + driver por marca).

## 6. Lo que necesito de ti

1. **Qué básculas tienes o venderás** (aunque sea una) para probar con el equipo real: sin una báscula física no se puede validar la
   lectura (se prueba con un simulador por puerto serial virtual).
2. **Fotos** de esas básculas (o autorización de tu distribuidor para usar las suyas).

## 7. Partes

| Parte | Contenido |
|---|---|
| A | Perfiles de negocio y Funciones del negocio (configuración, menús, instalador) |
| B | Driver de báscula en el agente de caja (Toledo 8217 + CAS) + "Probar báscula" + simulador para pruebas |
| C | "Pesar" en la caja, tara, kilo/libra |
| D | Catálogo de básculas en el portal (imagen, referencia, guía) y galería en la tienda; asistente "Reemplazar báscula" |

## 8. Estado al pausar (2026-10-17)

> Pausada por decisión del dueño hasta comprar una báscula real. Rama `basculas` (sin publicar ni unir a `main`).

**Hecho (parte B, en el agente de caja):**
- Interfaz `IScaleDriver` + drivers **Toledo 8217** (pide "W"; respuesta `STX 00.700 CR` o `STX ? estado CR`; 9600 7E1),
  **CAS 22 bytes** (`ST/US/OL,GS/NT,…peso…kg CR LF`; 9600 8N1, continuo) y **genérico** (número + unidad; estabilidad por software).
  Fuentes de los formatos anotadas en `src/Terminal/Pos.Terminal.Agent/Scales/ScaleDrivers.cs`.
- Configuración de una báscula por caja (puerto, velocidad, bits, paridad, parada, pedido/continuo, comando, tiempo de espera,
  decimales; marca, modelo y código de catálogo para la parte D), guardada en `C:\ProgramData\BusinessPost\agent\scale.json`.
- Endpoints: `GET /scale/status`, `GET /scale/weight`, `GET /scale/stream` (Server-Sent Events), `POST /scale/tare` (tara por software),
  `PUT /scale/config`, `POST /scale/test`; `POST /scale/simulator` solo en desarrollo. CORS ahora admite PUT.
- **Simulador** en memoria (Toledo 8217 y CAS): sin puertos virtuales ni cambios en el equipo.
- `WeightUnits` (Pos.SharedKernel): kg → cantidad en KG, G, LB y **LB500** (libra de 500 g).

**Pendiente:**
- Parte B: guardar la báscula en el servidor (tabla `org.terminal_scales`, migración ≥ V2026.10.040) y enviarla al agente desde la caja.
- Unidad `LB500` "Libra (500 g)" en `R__ref__units_of_measure.sql` (+ código DIAN en el mapeo de Factus).
- Parte C completa: sección "Báscula" en Configuración → Cajas (probar, peso en vivo, tara), panel "Pesar" en la caja (agregar al
  estabilizarse, tecla rápida, rechazos, respaldo digitando el peso), línea `SCALE_LIVE` en ventas y peso en el tiquete.
- Guía "Báscula conectada" en `docs/guia-piloto.md`. Probar con una báscula real (cable serial/USB-serial y protocolo configurado).

**Pruebas:** `Pos.Terminal.Agent.Tests` 107/107 correctas (incluye tramas válidas, inestables, cero, negativas, sobrepeso, basura,
tramas cortadas, tiempo de espera y endpoints); `Pos.SharedKernel.UnitTests` 103/103. Agente compila en Release sin advertencias.

## Decisión del propietario (2026-10-17): sin botón "Pesar"

La lectura de la báscula es **automática** según el producto, no por un botón:

1. En el producto se marca **"Se vende por báscula"** (ya existe el campo). Ese es el único dato que decide si un producto depende de
   la báscula.
2. En la caja el cajero **escanea o escribe el código** (por ejemplo, el de papa) y da **Enter**: si el producto es de báscula y la caja
   tiene báscula configurada, BusinessPost **lee el peso en ese momento** y agrega la línea con ese peso
   (0,700 kg × $3.000/kg = $2.100).
3. Casos:
   - **Peso inestable** (el producto se está moviendo): la línea espera hasta ~3 s ⚙️ mostrando "Esperando peso estable…" y se agrega
     sola al estabilizarse.
   - **Báscula en cero o vacía**: aviso "Ponga el producto en la báscula" y espera hasta ~5 s ⚙️; si no llega peso, no agrega nada.
   - **Sobrepeso, peso negativo o báscula desconectada**: aviso claro; en desconexión se puede **digitar el peso** (con permiso
     ⚙️, queda marcado en la línea como "peso digitado").
   - **Etiqueta de báscula** (EAN-13 prefijo 20–28): como hoy, el peso viene en la etiqueta y no se lee la báscula.
4. La tara se configura por producto (bolsa/bandeja) y se descuenta sola; la tara manual queda como tecla opcional.
5. La línea guarda si el peso fue **leído de la báscula**, **de la etiqueta** o **digitado** (auditoría y reportes).

Esto reemplaza en §4 el botón "Pesar" y el "agregar automáticamente al estabilizar" opcional.

## Informe 7-C (partes B y C) — 2026-10-17

**Hecho** (rama `basculas`; migraciones V2026.10.040–042, numeradas para no chocar con la 035 de otra rama):

| Parte | Qué quedó |
|---|---|
| B · Agente | Drivers **Toledo 8217** (pide "W"; `STX 00.700 CR` o `STX ? estado CR`), **CAS 22 bytes** (`ST/US/OL,GS/NT,…kg`) y **genérico**; simulador en memoria; endpoints `/scale/status`, `/scale/weight`, `/scale/stream` (SSE), `/scale/tare`, `/scale/config`, `/scale/test` y `/scale/simulator` (solo desarrollo). Configuración guardada en `C:\ProgramData\BusinessPost\agent\scale.json`. |
| B · Servidor | Tabla `org.terminal_scales` (una por caja): driver, puerto y opciones serie, marca, modelo y código de catálogo (listo para la parte D), tiempos de espera (3 s / 5 s) y "permitir digitar el peso". `GET/PUT /organization/terminals/{id}/scale`. La caja la entrega a su agente al abrir. |
| B · Administración | Configuración → Cajas e impresoras → **Báscula**: marca/protocolo, puerto (lista del agente), opciones avanzadas, **Probar báscula** con peso en vivo, "Estable", tara y quitar tara. |
| C · Producto | "Se vende por báscula" ya no exige PLU ni kilos (cualquier unidad de peso); campo **Tara (g)**; unidad **Libra (500 g)** (`LB500`), que la factura electrónica envía en kilos (KGM). |
| C · Caja | Lectura automática al registrar (sin botón Pesar): espera peso estable, avisa báscula vacía, sobrepeso o negativo; sin báscula, peso digitado con permiso `sales.weight.manual` o supervisor; tecla **Ins** = tara manual. |
| C · Venta y tiquete | Origen del peso por línea: **SCALE** (báscula), **LABEL** (etiqueta) o **MANUAL** (digitado, auditado `SALE_MANUAL_WEIGHT`); cada pesada en su línea; tiquete "0,700 kg x $3.000/kg" y "(digitado)". |

**Pruebas**: agente 107/107 · Sales 76/76 · Catalog 41/41 · Organization 37/37 · SharedKernel 103/103 · Factus 56/56 ·
ArchitectureTests 23/23 · Database.Tests 79/79 · integración (báscula, ventas y catálogo, filtradas) correctas. Servidor y agente
compilan en Release sin advertencias.

**Pendiente (necesita una báscula real)**: confirmar con el equipo el formato exacto de su protocolo (decimales, STX, paridad), la
estabilidad en modo continuo de CAS y el comportamiento del cable USB-serial; afinar los tiempos de espera en la tienda. La galería y
el catálogo de básculas (parte D) y los perfiles de negocio (parte A) siguen pendientes.

## Informe 7-C parte A (perfiles de negocio y funciones activables) — 2026-10-17

**Hecho** (rama `perfiles`; migraciones POS **V2026.10.043** y nube **V2026.10.010**).

**Funciones activables** (las que existen hoy en el programa; se eligió un conjunto pequeño):

| Código | Nombre en pantalla | Qué oculta cuando está apagada |
|---|---|---|
| `SCALE` | Báscula y PLU | Producto: PLU, "Se vende por báscula" y tara. Cajas e impresoras: botón Báscula. Caja: no lee la báscula (el servidor responde `featureEnabled = false` en `/terminals/{id}/scale`) |
| `LOTS_EXPIRY` | Lotes y vencimientos | Producto: "Maneja lotes" y "Maneja fecha de vencimiento" |
| `FRACTIONAL_SALE` | Venta por metro o fracción | Producto por unidad: "Permite cantidades con decimales" (peso y volumen siguen con decimales) |
| `OPEN_PRICE` | Precio abierto | Producto: "Permite precio abierto" |
| `PROMOTIONS` | Promociones | Menú Promociones (las promociones activas siguen aplicándose; se avisa en el interruptor) |
| `PURCHASING` | Compras y proveedores | Menú Compras, Proveedores y Cuentas por pagar |
| `VARIANTS` | Tallas y colores | Próximamente: se guarda, la interfaz la muestra deshabilitada ("Disponible en una próxima versión") |

No se incluyó el crédito a clientes (cartera): está repartido en clientes, caja y medios de pago y lo usan todos los tipos de negocio.

**Valores iniciales por tipo de negocio** (✓ = encendida):

| Tipo (código) | Báscula y PLU | Lotes y venc. | Fracción | Precio abierto | Promociones | Compras | Tallas y colores | Categorías de ejemplo |
|---|---|---|---|---|---|---|---|---|
| Supermercado / minimercado (`SUPERMARKET`) | ✓ | ✓ | | | ✓ | ✓ | | Abarrotes, Lácteos y huevos, Bebidas, Frutas y verduras, Carnes, Aseo y hogar, Cuidado personal |
| Fruver / carnicería (`FRESH_MARKET`) | ✓ | | | | ✓ | ✓ | | Frutas, Verduras, Carnes, Pollo, Pescado, Abarrotes |
| Droguería (`PHARMACY`) | | ✓ | | | ✓ | ✓ | | Medicamentos, Cuidado personal, Bebés y maternidad, Primeros auxilios, Vitaminas y suplementos |
| Ropa / calzado / deportes (`APPAREL`) | | | | | ✓ | ✓ | ✓ (próximamente) | Hombre, Mujer, Niños, Calzado, Accesorios |
| Ferretería / papelería / miscelánea (`HARDWARE`) | | | ✓ | ✓ | ✓ | ✓ | | Herramientas, Eléctricos, Plomería, Pinturas, Tornillería, Papelería |
| Otro (`OTHER`) | | | | | ✓ | ✓ | | (solo "General") |

**Decisiones**

- **D7CA-01 · Almacenamiento:** dos columnas en `org.companies`: `business_profile` (MAYÚSCULAS_CON_GUION, con CHECK) y
  `enabled_features` (códigos ordenados separados por coma). Sin tablas nuevas; la empresa ya viaja con su `xmin`.
- **D7CA-02 · Compatibilidad al actualizar:** las empresas existentes quedan como `SUPERMARKET` con **todas** las funciones que ya
  veían (báscula, lotes, fracción, precio abierto, promociones y compras): nada desaparece después de la actualización.
- **D7CA-03 · Asistente sin tipo:** `POST /api/v1/setup` acepta `businessProfile` opcional. Si no viene (instaladores anteriores,
  scripts) se usa `SUPERMARKET` y **no** se crean categorías de ejemplo (todo queda como antes de 7-C). Un código desconocido se
  rechaza (400). Las categorías de ejemplo solo se crean si el instalador eligió el tipo y el catálogo no tiene más que "General".
  Las unidades no cambian: el catálogo de unidades es común (KG, Libra 500 g, metro, centímetro…).
- **D7CA-04 · Apagar = ocultar:** apagar una función nunca borra ni modifica datos. Si un producto ya tiene datos de una función
  apagada, el campo se sigue viendo con el aviso *"Esta función está apagada; los datos se conservan"*; las pantallas de compras y
  promociones muestran el mismo aviso y se abren desde un enlace en el interruptor. Al guardar el producto, los valores ocultos se
  envían sin cambios.
- **D7CA-05 · Validación en el servidor (ligera):** solo la báscula: con `SCALE` apagada, `GET /organization/terminals/{id}/scale`
  devuelve `featureEnabled = false` y la caja no lee ni configura el agente (la configuración guardada no se toca). Las ventas con
  etiqueta de báscula y los productos con precio abierto o fracción ya creados siguen funcionando: no se rompe nada existente.
- **D7CA-06 · API:** `GET /organization/business-features` (cualquier sesión), `PUT /organization/business-features` y
  `PUT /organization/business-profile` (permiso `organization.company.manage`, el mismo de los datos de la empresa). `/auth/me` trae
  `business` (tipo y funciones) para ocultar menús y campos. Cambiar el tipo deja los interruptores en los valores de ese tipo.
- **D7CA-07 · Auditoría:** acción explícita `BUSINESS_FEATURES_CHANGED` (severidad WARNING) con un resumen legible
  ("Encendidas: …", "Apagadas (se ocultan, los datos se conservan): …") y valores antes/después. El asistente inicial anota el tipo y
  las funciones en `SETUP_COMPLETED`.
- **D7CA-08 · Nube:** campo opcional `BusinessProfile` en `ActivationRequest` y `CheckinRequest` (contratos compatibles: los POS
  anteriores no lo envían y la nube conserva el último dato; un código desconocido se ignora sin rechazar el check-in). La nube lo
  guarda en `licensing.organizations.business_profile`; el portal muestra **"Tipo de negocio"** en la ficha de la empresa y en la
  tabla, y en **Empresas** hay un filtro con el número de empresas de cada tipo. Se eligió el check-in de la licencia (diario y
  siempre activo) en lugar de la sincronización (opcional).
- **D7CA-09 · Instalador premium:** nueva pantalla "¿Qué tipo de negocio tienes?" con seis tarjetas (mismo estilo de "Este equipo",
  dos filas de tres) antes del formulario de "Tu negocio", dentro del mismo paso del panel izquierdo. El asistente clásico
  (`/instalacion`) pregunta lo mismo con seis opciones.

**Pruebas:** unidades de Organization 56/56 (presets, códigos, empresa, textos de la interfaz), Catalog 42/42 (categorías de ejemplo),
Licensing.Contracts 57/57, Identity 33/33, Installer.Setup 32/32 (tipos y tarjetas de la página), ArchitectureTests 23/23,
Database.Tests 80/80 (valores de las empresas existentes y CHECK), Cloud.Database.Tests 30/30, Cloud.IntegrationTests 69/69 (la tienda
informa el tipo al activar y en el check-in; un POS anterior no lo borra) e integración del POS: `Phase7C` 9/9 (asistente con cada
tipo, sin tipo, tipo inválido, encender/apagar con auditoría y permisos, cambio de tipo, báscula apagada en la caja) y las afectadas
`Phase2` 15/15, `Phase4` 15/15, `Phase7` 9/9, `Phase10`, `Phase12`, `Phase13` 4/4, `ServerEndpointsTests` 9/9, `ErrorHandlingTests` 9/9.

**Pendiente:** tallas y colores (fase de variantes); palabras propias por tipo ("prenda") cuando exista esa fase; la estadística por
tipo en el tablero del portal (hoy: filtro con conteo en Empresas).

## Correcciones de la báscula (revisión de código) — 2026-10-17

Rama `fix-bascula`; migraciones POS **V2026.10.045** (`sales.sale_lines`: `scale_gross_kg`, `weighed_at`) y **V2026.10.046**
(`org.terminal_scales.min_weight_grams`), numeradas para no chocar con la 044 de la corrección de Sistecrédito.

| Hallazgo | Corrección |
|---|---|
| A2 · Se cobraba el peso del producto anterior | Después de agregar una línea pesada, la siguiente pesada solo vale si la báscula **quedó vacía** o marca un peso estable **distinto** del último en más de **±5 g ⚙️**. Si no, la caja muestra **"Retire el producto anterior de la báscula"** y espera (como con el peso inestable; se rinde a los segundos de "Espera producto"). El estado vive por caja y pasa de una venta a otra (`ScaleRepeatGuard`); tras cada pesada la caja vigila en segundo plano (hasta 2 min) cuándo se vacía la báscula. El **servidor** guarda el peso bruto en la línea y rechaza (`SALES.SCALE_WEIGHT_REPEATED`) el mismo peso bruto (±5 g) que la última línea de báscula activa de la venta dentro de 2 min ⚙️ si la caja no informa que la báscula quedó vacía. Una línea eliminada no cuenta (se elimina para volver a pesar). |
| M6 · Sin peso mínimo | **Peso mínimo (banda de cero)** por caja, por defecto **20 g ⚙️** (0 a 1000 g; Configuración → Cajas → Báscula → "Peso mínimo (g)"): por debajo la báscula cuenta como **vacía** (la deriva de 0,002 kg no se cobra) y un negativo dentro de la banda también es "vacía". |
| M7 · Doble tara y tara del agente eterna | **Una sola regla**: el agente entrega `GrossKg` (lo que marca la báscula), `TareKg` (tara manual, tecla Ins) y `WeightKg = GrossKg − TareKg`, más `ScaleNet` (la báscula ya taró). La tara manual **solo vale para la siguiente pesada**: la caja la quita al agregar la línea, el agente la quita sola cuando la báscula queda vacía (y quieta) y nunca pasa de una configuración a otra. La **tara del producto** la descuenta el servidor **una vez** y solo si no hubo otra tara: `neto = WeightKg − (TareKg = 0 y no ScaleNet ? taraProducto : 0)`. El peso digitado es el neto (sin tara). |
| M8 · "Probar báscula" cambiaba la báscula en uso | La prueba es una **sesión aparte** en el agente (`POST /scale/test` abre y lee, `GET /scale/test`, `POST /scale/test/tare`, `POST /scale/test/stop`; se cierra sola al minuto sin lecturas): su propia configuración, conexión y tara; la báscula en uso y su tara no cambian. Si comparten el puerto COM, cada lectura cierra el de la otra y lo reabre. En Configuración → Cajas el botón **solo se habilita en el computador de esa caja** (aviso claro en las demás; se puede guardar desde cualquiera). |
| M9 · Peso arbitrario sin permiso | Con `weightKg` el **origen es obligatorio** (`SALES.WEIGHT_SOURCE_REQUIRED`; ya no se supone SCALE). SCALE exige la **lectura del agente** (`scale`: bruto, tara, neto de báscula, estable, hora de lectura): sin ella `SALES.SCALE_READING_REQUIRED`, inestable `SALES.SCALE_WEIGHT_UNSTABLE`, bruto − tara ≠ peso `SALES.INVALID_SCALE_READING`. MANUAL sigue solo por `/lines/manual-weight` (permiso `sales.weight.manual` o supervisor, auditado `SALE_MANUAL_WEIGHT`). La hora de lectura no se compara con el reloj del servidor (los equipos pueden tener la hora corrida); la repetición la cubre A2. |
| M10 · Línea pesada editable | La cantidad de una línea pesada (báscula, etiqueta o digitada) **no se edita** (`SALES.WEIGHED_LINE_QUANTITY`): se elimina y se vuelve a pesar. F4 en la caja lo explica. |
| B6 · Desbordamiento | Un peso fuera de rango (más de 99.999 kg) es una **trama con error** (`ERROR`, sin peso), nunca un 500; también se atrapa `OverflowException` y el flujo SSE sigue. |
| B7 · Toledo continuo con trama cortada | En modo continuo el lector se **sincroniza con STX** (también con el bit de paridad) y descarta las tramas sin inicio ("0.700" de "10.700"). |
| B8 · CAS pegaba identificador y lámparas | CAS se lee **por posición**: tras `EE,PP,` vienen identificador (1 byte), lámparas (1 byte), coma opcional y el peso en 8 bytes; el campo de peso se valida entero (signo, relleno, dígitos y punto). La variante corta (`ST,GS,+0000.700kg`) sigue; el "último número" queda solo en el driver genérico. |

**Pruebas:** agente 131/131 · Sales 84/84 · SharedKernel 110/110 · Catalog 42/42 · Organization 57/57 · ArchitectureTests 23/23 ·
Database.Tests 80/80 · integración `Phase7C` 11/11 y `ScaleSalesTests` 1/1.

## Segunda revisión de la báscula — 2026-10-17

Rama `fix-bascula`; migraciones POS **V2026.10.048** (`org.terminal_scales`: clave pública de la báscula) y **V2026.10.049**
(`sales.scale_interlocks`, `sales.scale_readings`). Reemplaza la regla "peso distinto ±5 g" de la primera revisión (A2) y la
"lectura del agente sin firma" (M9).

| Decisión / hallazgo | Cómo quedó |
|---|---|
| **D1 · Retorno a cero** | Después de agregar una línea pesada, la siguiente exige que la báscula haya quedado **vacía** (cualquier lectura con \|peso\| ≤ peso mínimo ⚙️, estable o no). Ya no vale "otro peso" (el tomate encima de la carne). La caja muestra **"Retire el producto de la báscula"** y espera como con el peso inestable. El agente anota la última vez que vio la báscula vacía y, después de cada pesada aceptada (`POST /scale/accepted`), un **vigilante** (`ScaleEmptyMonitor`) la lee cada 250 ms hasta verla vacía: un paso rápido por cero cuenta aunque la pantalla no esté leyendo. |
| **D2 · Tara manual** | La tara de la tecla Ins **solo** se quita al agregar la línea o al cancelar con Esc (ya no al quedar vacía la báscula). La tara por producto sigue igual. |
| **D3 · Lecturas firmadas** | Al **Guardar** la báscula en el computador de la caja, el agente crea una clave **ECDSA P-256** (`POST /scale/pairing`): la privada queda en el agente protegida con **DPAPI de la máquina** (`scale-signing.json`) y la pública se registra en el servidor (`PUT /organization/terminals/{id}/scale/reading-key`, permiso de cajas, auditado). Volver a guardar **rota** la clave. Cada lectura de la báscula en uso lleva un texto canónico firmado (`BP-SCALE-1\|caja\|lectura\|hora\|bruto\|tara\|neto\|estable\|neto-báscula\|estado\|vacía-ms\|vacía-desde-la-aceptada`). El servidor verifica la firma con la clave de la caja, que la lectura sea de esa caja, que no se haya usado (`sales.scale_readings`, clave primaria = id de la lectura) y que no sea más de 30 s ⚙️ anterior a la última aceptada **según el reloj del agente** (nunca contra el reloj del servidor). El **retorno a cero vive en el servidor por caja** (`sales.scale_interlocks`): la siguiente pesada exige `vacía-ms` posterior a la última lectura aceptada; recargar la página (F5) o empezar otra venta no lo salta. El peso, la tara y la estabilidad se toman de la lectura firmada, no del cliente. Sin firma válida: `SALES.SCALE_READING_REQUIRED`; repetida: `SALES.SCALE_READING_REUSED`; vieja: `SALES.SCALE_READING_STALE`; sin vaciar: `SALES.SCALE_NOT_EMPTIED`. El peso digitado no cambia (permiso + auditoría). Cada línea de báscula guarda la lectura cruda y su firma (auditoría). |
| ¿Por qué ECDSA y no HMAC? | El agente no habla con el servidor: todo pasa por el navegador de la caja. Con HMAC el secreto tendría que viajar por el navegador al vincular y guardarse en el servidor (protegido con DPAPI); con una firma asimétrica solo viaja la clave **pública**, el servidor no guarda ningún secreto y una copia de la base de datos no permite firmar pesos. El resto (nonce, hora del agente, estado del retorno a cero en el servidor) es lo pedido. |
| BN1 · Eliminar y volver a pesar | Eliminar la línea de la **última** pesada libera el retorno a cero en el servidor y en el agente (`POST /scale/accepted { release }`). |
| BN3 · Prueba y venta en la misma COM | Con la misma COM y los mismos parámetros, la prueba lee por la conexión de la báscula en uso (no cierra ni reabre el puerto). Con otros parámetros la prueba toma el puerto y la venta responde `SCALE.IN_TEST` ("detenga la prueba"). Abrir el puerto reintenta una vez si Windows aún no lo liberó. |
| BN4 · Prueba sin cerrar | Un temporizador cierra la prueba a los 60 s ⚙️ sin lecturas (`Agent:ScaleTestIdleTimeout`), aunque nadie vuelva a llamar al agente. |
| BN6 · "Última línea" | Ya no se busca en las líneas de la venta: el estado del retorno a cero es por caja en el servidor. |
| BN7 · Toledo continuo con velocidad o paridad equivocadas | Los bytes recibidos sin ninguna trama válida (sin STX o sin fin de línea) cuentan: "La báscula respondió, pero no en el formato … revise el protocolo, la velocidad y la paridad". |
| BN10 · Pantalla vieja en el navegador | La interfaz envía `X-Pos-Client-Build: 2`. Un peso de báscula sin esa cabecera recibe `CLIENT.OUTDATED` ("La pantalla de la caja está desactualizada: recárguela (Ctrl+F5)"); la interfaz nueva que reciba ese código se recarga sola. |

**Notas para la versión (al publicar):** después de actualizar, **en cada caja con báscula** un administrador debe abrir
Configuración → Cajas e impresoras → Báscula **en el computador de esa caja** y pulsar **Guardar** (vincula la báscula); hasta entonces
la caja avisa "la báscula no está vinculada" y solo se puede digitar el peso (con permiso). Si una caja tiene la pantalla de venta
abierta desde antes de la actualización, debe recargarla (Ctrl+F5).

**Pruebas:** agente 142/142 · Sales 88/88 · SharedKernel 103/103 · Catalog 42/42 · Organization 58/58 · ArchitectureTests 23/23 ·
Database.Tests 80/80 · integración `Phase7C` 12/12 y `Phase7` 9/9.

## Tercera revisión de la báscula — 2026-10-17

Migración POS **V2026.10.050** (orden de las lecturas por arranque y secuencia; limpieza de lecturas). Formato firmado `BP-SCALE-2`.

| Hallazgo | Cómo quedó |
|---|---|
| BB1 · Vacía por el neto | "Vacía" es el **bruto** dentro de la banda de cero (\|bruto\| ≤ peso mínimo, también con deriva negativa). Con tara manual (Ins) o la tara física de la báscula (trama neta `N`/`NT`) el neto en cero **no** cuenta; si la trama no trae el bruto real, no se marca vacía. |
| BB2 · Quién vincula | El agente no tiene una credencial que el servidor pueda verificar (la del equipo vive en el navegador de la caja), así que se aplicó la opción (b): `PUT …/scale/reading-key` solo se acepta desde una **sesión de caja de esa misma caja** (su equipo emparejado o el propio servidor en Caja Única), con el permiso nuevo `organization.scale.link` o la **autorización de un supervisor** (código y PIN; el POS no maneja TOTP, así que no aplica). Un administrador desde otro equipo o con un token robado de la administración recibe `ORGANIZATION.SCALE_LINK_TERMINAL`. Queda en la auditoría (`SCALE_KEY_LINKED`, con quién autorizó) y el **Tablero** muestra "La clave de la báscula de la caja X cambió" durante 7 días. Se vincula en la caja: **F1 → Vincular báscula**. |
| BB3 · Clave del agente | La clave privada se cifra con DPAPI de la **cuenta del agente** (SYSTEM en el servicio), sin entropía fija, y el archivo tiene permisos solo para SYSTEM, Administradores y esa cuenta (sin herencia). |
| BB4 · Reloj | El servidor exige \|hora de la lectura − hora del servidor\| ≤ **5 min ⚙️** (`sales.scale_reading_max_skew_seconds`) y rechaza lecturas del futuro. El orden entre lecturas usa el **arranque y la secuencia** del agente, nunca la hora: un salto del reloj no deja la caja trabada. Si el agente se reinicia, basta con ver la báscula vacía desde que arrancó. El agente usa el reloj monótono para sus esperas. |
| BB5 · Reiniciar báscula | **F1 → Reiniciar báscula** (o el botón en el aviso "Retire el producto"): permiso nuevo `sales.scale.reset` o supervisor, auditado `SALE_SCALE_RESET`; libera el retorno a cero en el servidor y luego en el agente. |
| BN1 · Coherencia | Al eliminar una pesada, la caja consulta al servidor (`GET /sales/scale/interlock`) y solo libera el agente si el servidor liberó (si la última pesada fue otra, en otra venta, los dos siguen esperando). |
| BB6 · Vincular en el agente | Además de la protección de todo el agente (equipo y Host locales, orígenes permitidos), `POST /scale/pairing` exige el `Origin` de la interfaz de la caja. |
| BB7 · Simulador | Las lecturas de la báscula simulada nunca se firman. |
| BB8 · Vigilante | Después de una pesada aceptada el vigilante lee hasta 10 min ⚙️; además vigila 15 s después de cualquier lectura con peso, así que cambiar A por B antes de que la caja confirme la línea no deja el aviso "Retire el producto" sin motivo. |
| BB9 · Doble envío | La misma lectura enviada dos veces a la vez: la segunda recibe 409 (`SALES.SCALE_READING_REUSED` o `SALES.SCALE_CONCURRENT`), nunca 500. |
| BB10 · Limpieza | Las lecturas firmadas se guardan 90 días: al pesar, una vez al día por caja, se borran las más viejas (no hay una tarea de mantenimiento general en el servidor de la tienda). |
| BN8 · Tara | Queda aceptado y documentado: la tara manual se quita al agregar la línea, con Esc y con Ins sobre la báscula vacía. |
| Caja · F10 | `NuevoSistecredito` desechaba el `CancellationTokenSource` de la pesada sin dejarlo en nulo: el siguiente `Cancel()` fallaba (F10 dejaba de abrir el cobro y volver a pesar mostraba "Ocurrió un error inesperado"). Ahora solo se cancela y queda en nulo. A 1366×768 el botón «Confirmar cobro» queda fijo abajo de la columna de cobro (que se desplaza). |

**Pruebas:** agente 147/147 · Sales 89/89 · SharedKernel 103/103 · Catalog 42/42 · Organization 58/58 · Identity 33/33 ·
ArchitectureTests 23/23 · Database.Tests 80/80 · integración `Phase7C` 13/13, `Phase7` 9/9 y `Phase3` 24/24.

## Cuarta revisión de la báscula — 2026-10-17

Migración POS **V2026.10.051** (arranques retirados del agente; invalida las claves de báscula registradas antes).

> **Aviso para la caja y el manual (BK4):** **No use la tecla de tara de la báscula; use Ins en la caja.** Con la tara puesta en la
> báscula, muchas básculas (la genérica y algunas Toledo o CAS) envían el peso neto sin avisarlo y la caja puede creer que la báscula
> quedó vacía. Si la báscula sí lo avisa (Toledo `N`, CAS `NT`, genérica `NET`/`N.W.`/`NT`), la caja muestra "Quite la tara de la
> báscula (tecla de tara) para continuar". El aviso también sale en Configuración → Cajas e impresoras → Báscula.

| Hallazgo | Cómo quedó |
|---|---|
| BK1 · Vincular sin dejar la caja sin pesar | Primero el permiso o la autorización del supervisor (sin el permiso, la caja hace una consulta sin clave para que el servidor pida el supervisor antes de tocar el agente). Luego el agente prepara una clave **pendiente** (`POST /scale/pairing`, solo en memoria; la vigente sigue firmando), la caja la registra en el servidor y solo si el servidor la aceptó la activa (`POST /scale/pairing/commit`). Si el registro falla o se cancela, la pendiente se descarta (con otro intento o al reiniciar el agente) y la caja sigue pesando con la clave vigente. |
| BK2 · Arranques anteriores | El servidor guarda por caja los últimos 20 arranques del agente reemplazados por uno posterior (`retired_boot_ids`): una lectura de un arranque retirado nunca vale (`SALES.SCALE_READING_STALE`). Se descartó "rechazar si la hora es anterior": un salto del reloj podría trabar la caja. Límite: un arranque que nunca tuvo una pesada aceptada no queda retirado (sus lecturas igual exigen la ventana de 5 min, el retorno a cero y el nonce). |
| BK3 · Claves viejas | El agente borra al arrancar la clave del formato anterior (`dpapi:` con entropía fija). La migración 051 invalida las claves registradas antes: cada caja vuelve a vincular con F1 → Vincular báscula; la caja avisa "Báscula no vinculada: vincule con F1" y el Tablero muestra "La báscula de la caja X no está vinculada". La clave va en la carpeta `agent\keys` con permisos solo para SYSTEM, Administradores y la cuenta del agente (BK5); el `.tmp` se borra antes de crearlo. |
| BK4 · Tara física | Aviso en la configuración y aquí; la báscula genérica reconoce `NET`, `N.W.` y `NT` como peso neto. |
| BB1 · Mensaje | Con la trama neta, el aviso dice "Quite la tara de la báscula (tecla de tara) para continuar". |
| BB4 · Mensajes | `SALES.SCALE_CLOCK_SKEW`: "El reloj de la caja está desfasado (más de 5 minutos frente al servidor): revise la fecha y hora del computador de la caja". `SALES.SCALE_READING_STALE`: lectura fuera de orden. |
| BB5 · Auditoría | "Reiniciar báscula" muestra el código de la caja. |
| BK6/BK7 · Tablero | La lista de básculas se arma con dos consultas (sin una por caja) y, si falla, el Tablero se muestra igual. |
| Caja · CTS | La pesada se detiene con `Cancel` y luego `Dispose`; el bucle revisa la cancelación antes de usar el token. |
| BB2(b) · Riesgo residual aceptado | La clave pública viaja por la interfaz de la caja (JavaScript) entre el agente y el servidor. Alguien con acceso a esa pantalla podría registrar otra clave, pero solo con una sesión de caja de esa misma caja y la autorización de un supervisor presente; queda auditado y a la vista en el Tablero. |

**Pruebas:** agente 150/150 · Sales 90/90 · SharedKernel 103/103 · Catalog 42/42 · Organization 58/58 · Identity 33/33 · ArchitectureTests 23/23 · Database.Tests 80/80 · integración `Phase7` 9/9, `Phase3` 24/24 y `Phase7C` 14/14 (incluye vincular desde el equipo de otra caja en Multicaja,
arranque retirado, clave vigente tras un intento fallido y alerta de báscula sin vincular).

## Quinta revisión de la báscula — 2026-10-17

Migración POS **V2026.10.052** (número de arranque del agente). Formato firmado **`BP-SCALE-3`** (las claves ya se invalidaron en la V051,
así que toda caja vincula de nuevo con un agente que firma este formato; las lecturas `BP-SCALE-2` no se aceptan).

| Hallazgo | Cómo quedó |
|---|---|
| BQ1 · Licencia restringida | `SetTerminalScaleKeyCommand` (vincular) y `ResetScaleInterlockCommand` (reiniciar) se permiten con la licencia restringida: una tienda puede volver a vincular la báscula después de actualizar. |
| BQ2 · Pendiente en disco | La clave pendiente se guarda en `agent\keys\scale-signing.pending.json` (misma ACL): la confirmación vale aunque el agente se haya reiniciado. Si la confirmación falla, la caja repite una vez preparar → registrar → confirmar (si la autorización del supervisor ya se usó, el servidor la pide de nuevo). |
| BQ3 · Número de arranque | El agente guarda en `keys` un número de arranque que sube en cada arranque y lo firma en cada lectura. El servidor guarda el más alto aceptado por caja (`last_boot_counter`) y rechaza uno menor: un arranque anterior nunca vale, aunque no haya tenido pesadas aceptadas. |
| BQ4 · Cobro abierto | Con el cobro (u otro panel) abierto no se empieza a pesar: "Producto se vende por báscula: cierre el cobro (Esc) antes de pesarlo". |
| BQ5 · Carreras | Preparar y confirmar van bajo el mismo candado; una confirmación de una pendiente reemplazada o ya confirmada, o con el agente cerrándose, responde 409 (`SCALE.PAIRING_NOT_PENDING`), no 500. |
| BQ6 · Cancelar | Los botones Cancelar (pesar y peso digitado) y "Reiniciar báscula" pasan por `CancelarPesaje` (quitan la tara manual de la pesada). |
| BQ7 · Eliminar una pesada | Si la consulta al servidor falla (también sin red), la caja informa que la línea se eliminó y deja el agente como está; si responde, informa si ya se puede volver a pesar. |
| BQ8 · Hora | El servidor no puede distinguir un reloj desfasado de una lectura guardada más de 5 minutos: el mensaje nombra las dos causas ("vuelva a registrar el producto; si se repite, revise la fecha y hora del computador de la caja"). |
| BQ9 · Agente viejo | Si el agente no tiene la confirmación (404), la caja dice "Actualice el programa de la caja". |
| BQ10 · Función apagada | Con "Báscula y PLU" apagada, el Tablero no avisa "báscula no vinculada". |
| BK3 · Permisos | Al arrancar, el agente repara los permisos de la carpeta `keys` y de sus archivos. |
| BK4 · Neto | La báscula genérica también reconoce "PESO NETO" y "NT" pegado al número. |

**Pruebas:** agente 163/163 · Sales 118/118 · Cash 71/71 · SharedKernel 103/103 · Catalog 117/117 · Organization 58/58 ·
Identity 33/33 · Printing 59/59 · ArchitectureTests 23/23 · Database.Tests 81/81 · integración `Phase7C` 14/14, `Phase7` 9/9,
`Phase7B` 24/24, `Phase3` 24/24 y `Labels` 3/3.
