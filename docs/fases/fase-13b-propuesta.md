# Fase 13-B · Instalador con marca, arranque confiable y descargas en el portal del cliente — Propuesta

> Estado: **APROBADA (v2)** · 2026-10-17 ("dale, empieza con la A y la C"); orden A y C → B → E → D. (v1 respondida: logo y colores de tutiendanueva.com, texto del
> aliado aprobado, descargas solo desde el portal; v2 agrega lo que pediste: experiencia premium, ventana propia y cuenta del portal).
> Origen: pediste mostrar la marca (y a tutiendanueva.com como aliado) durante la instalación, y que el cliente pueda descargar el
> instalador y el manual desde su panel. Al instalar la 1.0.1 en un equipo real encontramos además fallas del primer arranque.
> Base: [Fase 13](fase-13-propuesta.md) (instalador y actualizaciones), [Fase 16](fase-16-propuesta.md) (portal del cliente),
> [Fase 12-C](fase-12c-propuesta.md) (pago en línea), [guía de instalación](../guia-instalacion.md) §8 (firma y precalentamiento).

## 0. Lo que pasó al instalar la 1.0.1 en este equipo (2026-10-17)

| Hora | Qué pasó | Problema |
|---|---|---|
| 09:22:47 | Terminó de copiar archivos; empezó "Base de datos" | La pantalla quedó con la barra llena y sin texto ~4 minutos: parece trabado |
| 09:24:05 | Creó los servicios y Windows intentó arrancar el servidor | — |
| 09:24:36 | **Windows se rindió a los 30 s**: "El servicio no respondió a tiempo" | El instalador **no precalienta** (solo lo hace el actualizador). Defender revisa cada .exe nuevo sin firma: medimos **356 s** |
| 09:26:41 | El instalador abrió el navegador | "localhost rechazó la conexión": abre antes de que el sistema responda |
| Después | Hubo que iniciar el servicio a mano (services.msc) | Un cliente no sabría hacerlo |
| Final | Asistente "Todo listo" | **No hay botón para entrar al sistema** |
| Portal | El cliente cambia su contraseña temporal y cae en "Sin acceso" | Corregido ya (commit `ed0adb2`) |

## 1. Alcance

| Incluido | Excluido |
|---|---|
| **Arranque confiable**: precalentar servidor, migrador, agente y actualizador ANTES de crear los servicios; tiempo de inicio del servicio ampliado; si aun así no arranca, reintentar y explicar qué hacer | Comprar el certificado de firma (tú); al tenerlo se firma con el script que ya existe |
| **Progreso visible**: texto del paso en curso ("Revisando archivos con Windows Defender — puede tardar unos minutos", "Creando la base de datos", "Iniciando BusinessPost") | Instalador MSI / Microsoft Store |
| **Esperar a que el sistema responda** antes de abrir el navegador (con mensaje si tarda) | |
| **Marca**: imagen lateral de bienvenida y final, logo pequeño en cada página, ícono propio del instalador y de BusinessPost | |
| **Aliado comercial durante la instalación**: carrusel en la página "Instalando" (BusinessPost → funciones → **"Aliado comercial: tutiendanueva.com — crea tu tienda en línea"**), y en la página final una casilla "Conocer tutiendanueva.com" (desmarcada por defecto) | Publicidad que abra páginas sin que el usuario lo pida |
| **Asistente "Todo listo"**: botón **"Entrar a BusinessPost"** y recordatorio de guardar el código de recuperación | |
| **Portal del cliente → Descargas**: "Descargar instalador" (última versión publicada en `/updates`), **manual de instalación** (página con capturas + PDF), versión y fecha | Descargas para el público sin usuario |
| **Manual de instalación** con las capturas de esta prueba: tipo de instalación, backup, asistente de 4 pasos, primer ingreso | |

## 2. Decisiones

| # | Decisión | Motivo |
|---|---|---|
| D13B-01 | El instalador ejecuta `--precalentar` de cada programa (en paralelo, máx. 15 min) antes de `sc create`, y configura el servicio del servidor con inicio **retrasado** + tiempo de espera de 120 s (`ServicesPipeTimeout` no se toca: es global de Windows) | Lo medido: 356 s de Defender. Sin firma, este es el único arreglo que no exige al cliente iniciar el servicio a mano |
| D13B-02 | Las páginas del instalador muestran el paso en curso; el último paso espera `/health/live` hasta 5 min antes de abrir el navegador | El cliente no debe ver "rechazó la conexión" |
| D13B-03 | Imágenes de marca en el repo (`installer/assets/`): 164×314 (bienvenida), 55×55 (logo), carrusel 497×314 PNG. Las del aliado salen de los colores y el logo de tutiendanueva (es tu marca) | Inno Setup las admite sin componentes extra |
| D13B-04 | El aliado se muestra como imagen y texto, **sin abrir el navegador solo**; la casilla final va desmarcada | No molestar ni parecer software publicitario (Defender lo marca) |
| D13B-05 | Descargas en el portal: solo para usuarios con sesión (rol Cliente, Soporte, Superadministrador); el archivo se sirve desde `/updates` (Caddy) con el nombre de la última versión del manifiesto | No hace falta subir el .exe a otro sitio |
| D13B-06 | El manual vive en el repo (`docs/manual-instalacion.md`) y el portal lo muestra como página; el PDF se genera al armar la versión | Una sola fuente, siempre al día |

## 3. Validación

Instalación completa en este equipo (desinstalar la 1.0.1 primero) y en uno limpio si tienes: el servicio arranca solo, el navegador abre
en el asistente, "Entrar a BusinessPost" lleva al ingreso. Portal: el cliente ve Descargas y baja el instalador y el manual.

## 4. Preguntas

1. **Imágenes del aliado:** ¿uso el logo y los colores de tutiendanueva.com tal como están en su página, o tienes un diseño propio
   para esto?
2. **Texto del aliado:** propuesta: "¿Quieres vender también por internet? Crea tu tienda en tutiendanueva.com". ¿Lo cambias?
3. **Descargas públicas:** ¿el instalador solo para clientes con usuario (recomendado), o también un enlace público en tu página de
   ventas?

## 5. Respuestas de la v1

| Pregunta | Respuesta |
|---|---|
| Logo y colores del aliado | Los de tutiendanueva.com tal como están en su página |
| Texto del aliado | "¿Quieres vender también por internet? Crea tu tienda en tutiendanueva.com" |
| Descargas | Por ahora solo desde el portal, con usuario |

## 6. v2 · Lo que agregaste: instalación "premium" de punta a punta

### 6.1 Ventana propia en vez del navegador (D13B-07)

Hoy el acceso directo abre **Microsoft Edge en modo aplicación** (`msedge --app=http://localhost:5480`): no es "modo pruebas", es la
forma actual de mostrar la interfaz. Se ve como navegador ("localhost", página de error de Edge). Propuesta: un programa propio
**`BusinessPost.exe`** (ventana de Windows con **WebView2**, el mismo motor de Edge que ya trae Windows 10/11):

- Ícono, nombre y barra de tareas de BusinessPost; sin barra de direcciones ni menús del navegador; no se puede navegar a otros sitios.
- **Pantalla de carga con la marca** mientras el sistema arranca ("Iniciando BusinessPost…"), en vez de "localhost rechazó la conexión".
- **Caja a pantalla completa** (modo quiosco) opcional; recuerda tamaño y posición; un solo programa abierto a la vez.
- La interfaz es la misma de hoy (no se reescribe nada): solo cambia la "cáscara".

### 6.2 Qué es "el servidor" y por qué existe (sin cambio de arquitectura, sí de nombres)

"Servidor" **no es un servidor en internet**: es un **programa de fondo en el mismo computador** (servicio de Windows) que guarda los
datos en la base local, imprime, hace los backups y verifica la licencia. Funciona **sin internet**; internet solo se usa para la
licencia, el pago y la sincronización con la nube.

- **Una sola caja:** el "servidor" y la caja están en el mismo equipo. Para el cliente es un programa instalado y ya.
- **Varias cajas:** uno de los equipos guarda los datos y las demás cajas se conectan a él por la red del local (no por internet), para
  que todas vean el mismo inventario y los mismos precios.
- Existe como servicio para que los backups, la impresión y la licencia sigan funcionando aunque cierren la ventana, y para que pasar de
  Caja Única a Multicaja no exija reinstalar.

Propuesta de **nombres claros** en el instalador: "Este equipo es la única caja" · "Este es el equipo principal (tengo varias
cajas)" · "Esta es una caja adicional". La palabra "servidor" desaparece de lo que ve el cliente.

### 6.3 Instalador premium (D13B-08)

Inno Setup permite imágenes, pero su aspecto sigue siendo el de un instalador clásico de Windows. Para un resultado "premium":
**instalador propio con la marca** (`BusinessPost-Setup.exe`, ventana sin bordes de Windows, diseño del portal: degradado, tarjetas,
tipografía Inter) que hace todo el trabajo por debajo con el paquete actual:

1. Bienvenida con la marca y "¿Cómo usarás este equipo?" (tres tarjetas con dibujo: una caja / equipo principal / caja adicional).
2. **Licencia primero:** "Escribe la clave que recibiste" o "Probar 30 días". Con la clave, la nube devuelve la empresa (razón social,
   NIT) y el plan, así el cliente no los vuelve a escribir.
3. **Cuenta del propietario = su cuenta del portal:** "Ingresa con tu correo del portal de BusinessPost" (o Google). Se valida en línea
   una vez y queda guardada en el equipo para trabajar sin internet (D13B-09).
4. Instalación con progreso real por pasos y el **carrusel** (marca, funciones, aliado tutiendanueva.com).
5. Datos del local que faltan: sucursal, dirección y municipio (buscador), impresora y cajón (prueba de impresión), con validaciones
   claras (el dígito de verificación del NIT se calcula solo).
6. Código de recuperación (con botón "Imprimir").
7. **"Todo listo"** con botón **"Abrir BusinessPost"**, y en la administración un recorrido corto: crear cajeros y roles, cargar
   productos.

### 6.4 Cuenta del portal en el POS (D13B-09)

- El propietario entra a la **administración del POS con el mismo correo y contraseña del portal** (o Google, cuando haya internet).
- El POS guarda una copia segura (hash) para entrar **sin internet**; si cambia la contraseña en el portal, el POS la actualiza en el
  siguiente reporte diario.
- **Cajeros y roles** se crean desde la administración del POS (ya existe: Usuarios, roles y permisos); los cajeros usan código + PIN y
  no necesitan cuenta en el portal.

### 6.5 Guía de instalación para el usuario (D13B-10)

`docs/manual-instalacion.md` + PDF, con capturas reales de cada pantalla del instalador nuevo y qué responder en cada paso; en el portal
del cliente (Descargas) y enlazada desde el instalador.

## 7. Orden propuesto (por partes, cada una se prueba y se publica)

| Parte | Contenido | Tamaño |
|---|---|---|
| A | Arreglos del primer arranque (precalentar, progreso, esperar, "Abrir BusinessPost") + nombres claros | Pequeña |
| B | Ventana propia `BusinessPost.exe` (WebView2) con pantalla de carga | Media |
| C | Portal → Descargas + manual de instalación | Pequeña |
| D | Instalador premium propio (licencia primero, cuenta del portal, carrusel con el aliado) | Grande |
| E | Cuenta del portal en el POS (correo/contraseña/Google del propietario) | Media |

## 8. Desinstalación premium (pedido del 2026-10-17)

Con el mismo diseño del instalador premium (parte D):
- Pantalla con la marca que explica qué se va a quitar y qué **se conserva** (por defecto los datos de la tienda se conservan).
- Botón **"Hacer un backup final"** antes de desinstalar y la ruta donde quedó.
- Casilla **"Borrar también los datos de la tienda"**, desmarcada, con doble confirmación y el código de recuperación.
- **Libera el equipo en la licencia** (para activar la misma clave en otro computador) si hay internet.
- Pregunta opcional "¿Por qué desinstalas BusinessPost?" (se envía a la nube solo si el usuario acepta).
- Detiene los servicios con orden y quita la regla del firewall (ya lo hace hoy).

## 9. Informe parcial D/E (2026-10-17)

> **Informe: la ventana se actualiza con el paquete.** `BusinessPost.exe` quedaba fuera del ZIP del actualizador y las tiendas seguían
> con la ventana vieja. Ahora va en `app\<versión>\desktop` (con su aviso de licencia): `build-installer.ps1` la publica allí y revisa
> que el ZIP traiga `server`, `migrator`, `agent` y `desktop`; los accesos directos abren `{app}\app\current\desktop\BusinessPost.exe`,
> así que el cambio de la unión `app\current` la actualiza y la vuelta atrás la devuelve, sin sobrescribir nunca un `.exe` en uso.
> El actualizador la precalienta al descomprimir, no cierra la ventana abierta (sigue funcionando y la nueva se usa al reabrirla) y
> no borra una versión vieja mientras su ventana siga abierta. Las cajas la reciben en el mismo paquete que les sirve el equipo
> principal. Compatibilidad: en instalaciones previas (accesos o ventana anclada en `{app}\desktop`) el actualizador refresca esa
> copia en cada cambio de versión (si está abierta la renombra a `*.anterior`) y el instalador también, si existe. Pruebas:
> `UpdateTests` (paquete con `desktop/`, cambio y vuelta atrás con uniones reales, ventana abierta, limpieza). Futuro opcional: que la
> ventana avise "Hay una versión nueva, se usará la próxima vez que abra BusinessPost".

Producto para **cualquier comercio**: los textos nuevos dicen "tu negocio"; el lema del ingreso pasa a "El punto de venta de su negocio."
y "Hecho en Colombia para su negocio".

### 9.1 Parte D · Instalador premium (D13B-08)

| Decisión | Elegido | Motivo |
|---|---|---|
| Empaque | **Envoltorio Inno sin páginas** (`installer/BusinessPost-Premium.iss`) que lleva el premium y el paquete clásico (sin recomprimir) y abre el premium; un solo archivo `BusinessPost-Setup-<versión>.exe` | Lo más simple y confiable: no se incrusta un paquete de cientos de MB dentro de un ensamblado .NET (arranque lento, memoria), se firma igual que hoy y el clásico queda de respaldo si falta WebView2 |
| Programa | `src/Installer/Pos.Installer.Setup` → `BusinessPost-Setup.exe`: WinForms + WebView2 (misma versión que la ventana propia, `Microsoft.Web.WebView2` 1.0.3912.50), ventana sin marco de 960×600, barra propia (arrastrar, minimizar, cerrar con confirmación), páginas incrustadas | Reproduce la maqueta aprobada tal cual (mismo CSS) |
| Permisos | El envoltorio pide administrador; el premium (manifiesto `asInvoker`) y el paquete lo heredan. Abierto solo, el premium se reabre como administrador | **Un solo aviso** de Windows en todos los caminos; `--maqueta` sin permisos |
| Avance real | El paquete corre con `/VERYSILENT /LOG=`; el premium lee el registro mientras se escribe: `Progreso: N` (copia), `Paso: …` (parte A) y `Resultado: …` | Sin adivinar: las etapas son las del instalador; la copia es un porcentaje real |
| Parámetros | `/PREMIUM=1 /MODO= /SERVIDOR= /HUELLA= /BACKUP= /RECUPERACION=<archivo>` (`ExpandConstant('{param:…}')`) y `/BORRARDATOS=` al desinstalar | El asistente clásico sigue igual sin parámetros |
| Datos faltantes | Paso **"Tu negocio"** (nombre comercial, persona, IVA, municipio con buscador, dirección, teléfono, correo; DV automático). Municipios del mismo `R__ref__seed_colombia.sql` del POS, incrustado | La licencia solo trae razón social, NIT y ciudad |
| Configuración | Asistente inicial por la API local en una llamada + ingreso + activación + código de recuperación (mostrado con "Imprimir" y casilla "Ya lo guardé") | Sin navegador ni segundo asistente |
| Desinstalar (§8) | Mismo programa con `--desinstalar` (queda como `UninstallString`); backup final con el migrador en Documentos; liberar con usuario de la tienda; borrar datos con doble confirmación ("BORRAR"); motivo solo con autorización | — |

**Nube:** `POST /v1/license-previews` (clave → razón social, NIT, edición, estado y vencimiento; solo claves vigentes ACTIVE/TRIAL; misma
búsqueda en tiempo constante que la activación, límite por IP y por licencia; ningún id ni dato de contacto).

### 9.2 Parte E · Cuenta del propietario = cuenta del portal (D13B-09)

- **Nube:** `POST /v1/owner-links` (clave + correo + contraseña [+ código TOTP]). Solo un usuario **Cliente** de la cuenta dueña de la
  empresa de la clave; mismas reglas que el ingreso al portal (tiempo constante, bloqueo tras 5 intentos, TOTP si lo tiene activo o si
  el portal lo exige, no acepta contraseñas temporales). Devuelve un **enlace firmado** (JWS EdDSA `pos-owner-link+jws`, misma clave de
  las licencias, vence en 2 h; lleva correo, nombre y NIT, nunca la contraseña). Auditoría: `PORTAL_OWNER_LINK_ISSUED/FAILED/REJECTED`
  y `OWNER_LINK_ISSUED`. El módulo de licencias usa `IPortalCustomerVerifier` (Abstractions) sin depender del de usuarios.
- **POS:** `OwnerInput.PortalLinkToken`. El asistente verifica el enlace sin conexión (`IPortalOwnerLinkVerifier`), exige el NIT de la
  empresa que se configura, crea al Propietario con el **correo como usuario** y la **misma contraseña** (hash local; la política local
  no se aplica porque ya cumplió la del portal) y lo marca (`identity.users.portal_linked_at`). Migración `V2026.10.035`: el usuario
  admite un correo (hasta 120). Cajeros y roles siguen locales.
- **Actualizar la contraseña en el check-in diario: FUTURO.** La nube no tiene hoy un verificador seguro para el POS (enviar el hash
  Argon2 del portal a cada tienda multiplicaría las copias atacables). Por ahora la contraseña local se conserva; si el dueño la cambia
  en el portal, la cambia también en BusinessPost.
- **Google: FUTURO (TODO).** Google bloquea con frecuencia el ingreso dentro de un WebView incrustado, así que la opción prevista es
  de tipo "código de dispositivo": el premium pide a la nube un código de un solo uso atado a la clave, abre el ingreso con Google del
  portal en el **navegador del sistema** y consulta cada pocos segundos hasta que el CLIENTE de la cuenta lo aprueba; entonces la nube
  entrega el mismo enlace firmado. Como con Google no hay contraseña, el premium pediría además una contraseña local para el
  Propietario (para entrar sin internet). Hoy el botón "Continuar con Google" explica que por ahora se usa el correo y la contraseña del portal.

### 9.3 Pruebas

Nube: `InstallerApiTests` (vista previa válida/sin secretos, inválida/revocada/suspendida; enlace del dueño con TOTP, cuenta de otra
empresa, superadministrador, contraseña incorrecta = correo inexistente, bloqueo, límite por licencia y por IP). POS:
`PortalOwnerSetupTests` (crea al Propietario con el correo y la contraseña del portal; rechaza enlace alterado, vencido, de otra clave
o de otro NIT; el propietario local sigue igual). Lógica del instalador: `Pos.Installer.Setup.Tests` (registro de Inno, DV del NIT
contra el del POS, municipios, parámetros).

### 9.4 Qué revisar en la instalación real

1. Un solo aviso de permisos; "Preparando el instalador…" y luego la ventana con la marca. 2. Clave → empresa; cuenta del portal (con
y sin doble factor). 3. La lista avanza con el paso real (Defender puede tardar). 4. Código de recuperación: Imprimir. 5. "Abrir
BusinessPost" abre la ventana propia sin permisos de administrador; el ingreso con el correo y la contraseña del portal. 6. La casilla
del aliado abre tutiendanueva.com solo si se marcó. 7. Configuración → Aplicaciones → Desinstalar abre la desinstalación premium.

**Riesgos:** sin firma de código, SmartScreen avisa en el archivo descargado; WebView2 elevado (funciona, pero es un caso menos común);
el desinstalador de Inno se copia a sí mismo (el premium espera hasta 5 minutos a que la instalación desaparezca del registro).
