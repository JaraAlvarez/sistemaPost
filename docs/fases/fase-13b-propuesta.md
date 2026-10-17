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
- **Google: IMPLEMENTADO** (ver §9.5).

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

### 9.5 "Continuar con Google" en el instalador (2026-10-17)

Google bloquea su ingreso dentro de un WebView incrustado. Se implementó un flujo tipo **OAuth 2.0 Device Authorization (RFC 8628)**
con el navegador del sistema; el lado del POS no cambia (recibe el mismo enlace firmado).

| Decisión | Elegido | Motivo |
|---|---|---|
| Solicitud | `POST /v1/owner-link-requests {licenseKey}` → `{requestId, pollToken, browserUrl, userCode, expiresIn: 600, interval: 5}`. Misma búsqueda de la clave (tiempo constante) y límites por IP y por licencia que la vista previa; **máximo 5 abiertas por licencia** | Anónima por diseño: la autentica la clave; quien autoriza es el dueño con su sesión |
| Secreto de consulta | 256 bits aleatorios (base64url) solo en el instalador; la nube guarda su **SHA-256** (`licensing.owner_link_requests`, migración `V2026.10.010`). Se envía en el CUERPO de la consulta, nunca en la URL | Una URL abierta en el navegador o en un registro no permite recoger el enlace |
| Página | `/cuenta/vincular-instalador?r=<id>` (renderizada en el servidor, sin scripts, formulario POST con antifalsificación). Sin sesión lleva al **ingreso normal** del portal (contraseña o Google, misma cookie `SameSite=Strict`) y vuelve a la página (`SafeReturnUrl` admite SOLO esta ruta de `/cuenta`). Muestra "¿Autorizar la instalación de BusinessPost para <Empresa> (NIT …)?", el **código para comparar** y Autorizar / Cancelar | Reutiliza el ingreso con Google existente (ADR-0062) sin otro cliente OAuth |
| Quién autoriza | Solo un usuario humano activo, sin bloqueo, rol **CLIENTE** y de la **cuenta dueña** de la empresa (mismas reglas que `/v1/owner-links`; `IPortalCustomerVerifier.VerifySignedInAsync`). Otro usuario ve un aviso **sin** el nombre ni el NIT y queda `PORTAL_OWNER_LINK_REJECTED`. Con contraseña temporal (sesión de contraseña) se pide cambiarla primero | Igual que el flujo de correo y contraseña |
| Consulta | `POST /v1/owner-link-requests/{id}/poll {pollToken}` → `PENDING` / `APPROVED` (+ el enlace) / `DENIED` / `EXPIRED`. Antes del intervalo: **429 `LICENSE.OWNER_LINK_SLOW_DOWN`** y el intervalo sube 5 s (máx. 30). Id inexistente o secreto ajeno: el mismo 404 | Como `slow_down` / `authorization_pending` de OAuth |
| Un solo uso | Al entregar el enlace la solicitud queda `CONSUMED` (con el `jti`); otra consulta responde **409 `LICENSE.OWNER_LINK_REQUEST_USED`**. La fila se bloquea (`FOR UPDATE`) al consultar y al decidir. La licencia y la empresa se revisan otra vez al entregar | Sin repeticiones ni carreras |
| Vencimiento | 10 minutos. No se borra nada (L-09): las filas vencidas quedan como rastro | — |
| Auditoría | `OWNER_LINK_REQUEST_CREATED`, `OWNER_LINK_REQUEST_APPROVED` / `_DENIED`, `PORTAL_OWNER_LINK_REJECTED`, y al entregar el mismo `OWNER_LINK_ISSUED` ("Continuar con Google") | — |
| Instalador | "Continuar con Google" → "Te abrimos el navegador para entrar con Google…", el código, un indicador de espera, "Abrirlo otra vez" y **Cancelar**. Abre el navegador con el **usuario normal** (Explorer, no como administrador) y solo si la dirección es **HTTPS del mismo servidor de licencias** (`IngresoGoogle.EsDireccionSegura`). La consulta (`EsperaAutorizacion`, máquina de estados con pruebas) respeta el intervalo, `slow_down`, reintenta sin conexión hasta vencer y se detiene al cancelar o cerrar | Sin WebView para Google |
| **Ingreso sin internet del dueño de Google** | Después de autorizar, el instalador pide una **contraseña de BusinessPost** (12+ caracteres, letras y números, confirmada; la revisan la página y `Puente.cs`). El Propietario queda con el **correo** como usuario y esa contraseña (hash local), marcado como vinculado al portal. **No** se sincroniza con el portal (con Google no hay contraseña del portal) | El POS trabaja sin internet y no tiene ingreso con Google; la alternativa de "usar la contraseña del portal" no existe para quien solo entra con Google |

Pruebas: `InstallerGoogleSignInTests` (creación y hash, autorizar como el cliente correcto, otra cuenta rechazada aunque fuerce el
formulario, cancelar, vencimiento, slow_down, secreto ajeno, un solo uso, enlace con el mismo tipo y firma de `/v1/owner-links`) y
`EsperaAutorizacionTests`/`IngresoGoogleReglasTests` en el instalador. **Despliegue:** requiere publicar la nube (la migración
`V2026.10.010` se aplica sola con `MigrateOnStartup`) y un instalador nuevo; sin la nube nueva, "Continuar con Google" responde 404 y el
instalador lo muestra como error (el correo y la contraseña siguen funcionando).

### 9.6 Revisión de seguridad y robustez del instalador, del enlace del dueño y del portal (2026-10-17)

**Instalador premium.** El final espera `/health/ready` ("Preparando la base de datos…", hasta 20 min) y no `/health/live`: en una
actualización (1.1.x → 1.2.0) el servidor responde 503 mientras migra y el instalador mostraba "Ocurrió un error inesperado" aunque
BusinessPost quedaba instalado; `ConfiguradoAsync` reintenta los 503/errores pasajeros y devuelve un resultado tipado. La espera de
"Continuar con Google" ya no queda "Esperando autorización" para siempre (captura cualquier falla, un 200 con HTML de una red con portal
cautivo y un cuerpo nulo; la página ofrece **Reintentar**). El enlace del dueño queda **atado a la clave** con la que se obtuvo (si el
dueño vuelve y escribe otra clave hay que validar la cuenta de nuevo) y antes de instalar se revisan, sin conexión, el NIT y el
vencimiento. El backup final de la desinstalación se guarda donde el dueño elige, empezando en **sus** Documentos (usuario de
`explorer.exe`, no el administrador del `runas`), con la ruta EXACTA que informa el migrador. Cancelar detiene el paquete y lo que lanzó;
el archivo del código de recuperación se borra solo cuando el paquete terminó. La lectura del registro de Inno guarda la posición exacta
y no parte letras con tilde. La copia temporal del desinstalador se borra al salir (y si no, al reiniciar).

**Paquete Inno (`installer/BusinessPost.iss`).** `PrepareToInstall` detiene TODOS los servicios (actualizador, servidor, agente y base de
datos) y **espera** a que queden detenidos antes de copiar archivos (antes: "archivo en uso" o binarios mezclados); si no se detienen,
la instalación se cancela con un mensaje claro. Los servicios existentes se **reconfiguran** con `sc config` (antes `stop`+`delete`+
`create` sin esperar dejaba el servicio "marcado para eliminación" y el equipo SIN servicio); el servidor depende de `BusinessPost-DB`,
`sc failureflag 1` y los fallos de `sc failure` quedan en el registro. El código de recuperación va al migrador en un **archivo** que se lee
y se borra (`--recovery-code-file`, nunca en la línea de comandos ni en el registro). En una actualización el migrador hace un backup
**PRE_UPDATE** antes de migrar; si la migración falla no se cambia `app\current` (la versión anterior sigue activa y sus servicios vuelven
a arrancar). El paquete recuerda el **modo** instalado (y lo deduce de la configuración en instalaciones anteriores): lo propone al
actualizar y cambiarlo exige confirmación (`/CAMBIARMODO=si` desde el premium), quitando los servicios, la regla del firewall y la
configuración que el modo nuevo no usa (los datos se conservan). Firewall: se borra la regla antes de crearla, solo `remoteip=localsubnet`,
perfiles privado y de dominio; si la red está como **Pública** queda un aviso (página final del premium y mensaje del clásico). Si la
recuperación desde un backup falla, ya no se sigue con una base vacía. Al desinstalar se espera a que los servicios se detengan y se borran
todas las versiones de `app\` (también las del actualizador) y PostgreSQL; los datos solo si se pidió. La URL del equipo principal se
normaliza (origen exacto para el agente) y el JSON se escapa.

**Futuro (I10, no se cambia ahora): cuentas de servicio con menos privilegios.** El servidor, el actualizador y el agente corren como
`LocalSystem` aunque escuchan en la LAN. Pasarlos a cuentas virtuales (`NT SERVICE\BusinessPost-Server`, etc.) exige: permisos de lectura
sobre `ProgramData\BusinessPost\config` (hoy solo SYSTEM y Administradores, a propósito: DPAPI de máquina), acceso a la llave privada del
certificado HTTPS de la LAN, reservar las URL (`netsh http add urlacl`) para 5443/5444, que el actualizador pueda cambiar `app\current`,
detener/iniciar servicios (permisos SCM) y escribir en `Program Files`, y que los backups lleguen a carpetas de red. Es un cambio de
arquitectura con riesgo de dejar tiendas sin servicio: se hará con una fase propia y pruebas en equipos reales (primero el agente, que no
necesita la configuración cifrada).

**Nube.** "Continuar con Google": el dueño **escribe** en el portal el código que muestra su instalador (la página ya no lo muestra; 5
intentos y la solicitud se cancela), su ingreso debe ser **reciente** (10 min; si no, "Ingresar de nuevo"), escribe su **TOTP** si lo tiene
activo o el portal lo exige (también después de Google), ve cuándo y desde qué IP se creó la solicitud, la solicitud vence a los **5
minutos** y las decisiones tienen límite por usuario (migración `V2026.10.012`, `code_attempts`). Inexistente y "de otra cuenta" se ven
igual y abrir la página no escribe en la auditoría (solo las decisiones). Al entregar el enlace se revisa también la suscripción. Portal:
restablecer la contraseña o el TOTP de otro usuario exige ingreso reciente o confirmar con la contraseña/TOTP; a otro superadministrador,
siempre el TOTP; nunca sobre sí mismo; restablecer el TOTP también restablece la contraseña (temporal) por defecto; desactivar el TOTP
propio consume el código. Asistente `/instalacion`: confirmación de la contraseña y "Mostrar", regla real (mínimo 8), correo propio del
propietario, script aparte sin `innerHTML` y CSP sin scripts en línea, manejo de sesión vencida y de "configurada pero sin ingreso".

**Segunda revisión (IN1-IN10, PN1-PN8).** *Paquete Inno:* el desinstalador volvió a detener los servicios (la ruta de PowerShell había
perdido sus barras invertidas; una prueba revisa ahora los `.iss` y `.ps1` del instalador: caracteres de control, `{sys}`/`{app}`/`{tmp}`
sin barra y el BOM). Detener los servicios ya no usa `Stop-Service -Force` (esperaba sin límite a un servicio trabado): pide parar sin
bloquear, espera 90 s y, si vence, cierra el proceso del servicio (PID de `Win32_Service`) y la instalación se cancela; también cierra los
procesos sueltos (actualizador, migrador, `discover`, servidor o agente) que corren desde la carpeta de la instalación. Si se cancela,
vuelven a arrancar SOLO los servicios que estaban en marcha (base de datos → agente → servidor → actualizador). Si una migración falla a
mitad, el migrador **restaura automáticamente el backup PRE_UPDATE** en una BD nueva (sin migrarla: queda en el esquema anterior), revisa
sello y conteos y cambia `server.json` a ella antes de que el paquete vuelva a la versión anterior (código 3); si esa restauración también
falla (o no había backup), sale con el código **8 = restaurar con soporte**: el paquete NO arranca el servidor ni el actualizador
anteriores, el mensaje dice dónde está el backup y queda un archivo `logs\actualizacion-fallida-*.txt` (va en el paquete de soporte).
Cuando la base de datos, la recuperación o el cambio de `app\current` fallan, el paquete termina con código distinto de cero
(`GetCustomSetupExitCode`: 20 base de datos o recuperación, 21 restaurar con soporte, 22 versión activa), conserva en el registro de
Windows la versión y el modo de antes (`DisplayVersion` y `Inno Setup CodeFile: Modo`) y el premium muestra el error con un texto claro.
La vuelta atrás (también en una reinstalación cuya recuperación falla) recupera el actualizador anterior y vuelve a arrancar los servicios
que había. El cambio de modo (quitar servicios y la regla del firewall, renombrar `updater.json`, deshabilitar la base de datos) se hace
recién después de que la base de datos quedó lista y la versión nueva activa; antes de migrar solo se habilita la base de datos de una
caja adicional que pasa a equipo principal (y se vuelve a deshabilitar si falla). `app\current` se cambia creando primero la unión nueva
aparte (`app\current.nueva`): si algo falla, la anterior sigue (o se recrea) y no se registran servicios hacia una ruta inexistente.

*`pgsql\` y `updater\` (IN5).* Se eligió lo más simple y seguro: antes de copiar se guarda `updater\Pos.Server.Updater.exe.anterior` y la
vuelta atrás lo recupera (se borra cuando la instalación termina bien). `pgsql\` **no** se respalda ni se versiona: el paquete trae siempre
PostgreSQL 18 y dentro de la misma versión mayor los binarios son compatibles con el directorio de datos y con la versión anterior de
BusinessPost (que solo usa la conexión). Un cambio de versión MAYOR de PostgreSQL exige `pg_upgrade` y tendrá su propia fase; ese paquete
deberá versionar `pgsql` igual que `app\<versión>`.

*Instalador premium:* detecta el modo instalado igual que el paquete (primero el valor que guardó Inno en
`HKLM\…\Uninstall\{ED01E874-…}_is1`, después la configuración); al cerrar espera (hasta 45 s) a que termine de detener el paquete y borra
los archivos temporales del código de recuperación; busca los Documentos del usuario abriendo `explorer.exe` solo con
`PROCESS_QUERY_LIMITED_INFORMATION` y sin cortar la búsqueda si un proceso falla. *Nube y servidor:* un texto que no tiene la forma del
código del instalador no gasta intentos; al agotar los 5 intentos la página dice "se canceló por demasiados códigos incorrectos"; la
re-autenticación reciente (o confirmar con la contraseña/TOTP; siempre el TOTP si la cuenta es o pasa a ser de superadministrador) se
exige también para crear un superadministrador, cambiar el rol, desactivar y desbloquear usuarios; `/instalacion` y su script se sirven con
`Cache-Control: no-cache`. Las pruebas que adelantan el reloj usan su propio servidor y lo devuelven a la hora real.

**Pendiente (decisión de producto, M14):** revocar los enlaces del dueño ya autorizados (hoy, una vez entregado, el enlace firmado vale
hasta que vence) y exigir el TOTP (`Portal:RequireTotp`) a los usuarios Cliente dueños de una cuenta. No se implementa sin decidirlo.

**Tercera revisión (IL1-IL10).** *Vuelta atrás de la base de datos:* la BD restaurada desde el backup PRE_UPDATE (`pos_rb…`, sin dueños
ni privilegios en el volcado y sin los scripts "always" de la versión nueva) recibe ahora los privilegios que tenía la BD activa para
`pos_app`, `pos_backup` y `pos_migrator`, que son los de la versión ANTERIOR (la migración fallida no llegó a sus scripts "always"); los de
objetos que solo creó la migración fallida se omiten. Antes `pos_app` quedaba sin permisos y el servidor anterior respondía 503. La
revisión posterior usa solo lo que trae el backup (sello y conteos de su encabezado, tolerando objetos que no existen) y no el verificador
de auditoría de la versión nueva. Antes de restaurar se revisa el espacio libre (BD actual + 20 % en el disco de los datos, el doble del
paquete en la carpeta temporal) y hay un límite de 60 minutos (pg_restore se cierra si vence). Hay una prueba con PostgreSQL 18 real
(`Pos.Database.Tests/PreUpdateRollbackTests`): esquema anterior, backup, migración que falla a mitad, restauración y `pos_app` leyendo y
escribiendo con las mismas restricciones de antes. *Paquete:* con los códigos 21 y 22 el servidor y el actualizador quedan en inicio
manual (`sc config start= demand`, con aviso en el registro y en el mensaje) hasta que una instalación termine bien (vuelven a `auto`). Si
en una actualización a otra versión falla la recuperación del backup elegido después de que la base ya se migró, no se arranca el servidor
anterior (código 22). Si la base de datos no se detiene en 90 s se usa `pg_ctl stop -m fast` y luego `-m immediate` antes de cerrar el
proceso; `postgres.exe`/`pg_ctl.exe` de `{app}\pgsql` se cierran solo si siguen vivos. La página final del asistente clásico ya no dice
"completado" cuando algo falló, y una instalación NUEVA que falla no deja registrada la versión nueva. *Premium:* al cerrar la ventana
durante la base de datos ya no se corta el paquete (ni el migrador ni pg_restore): se espera a que termine; si hay que cortarlo antes,
queda el mismo estado del código 22. *Portal:* reactivar, cambiar la cuenta asociada y desactivar a un usuario bloqueado por intentos
también piden confirmar; cambiarse el propio rol, la cuenta o desactivarse da `PORTAL.CANNOT_CHANGE_SELF`; el motivo "se canceló por
demasiados códigos incorrectos" se mantiene al recargar la página (se deduce de `code_attempts`). `confirmation` quedó documentado en
`http/fase-12a.http`.

**Cuarta revisión (IR1-IR6).** La prueba de la vuelta atrás con PostgreSQL real ya no depende de las últimas migraciones del producto: la
"versión anterior" es el catálogo actual y la "versión nueva" son migraciones propias de la prueba. Si la migración falla en un repetible
posterior a `R__reporting__views` (que borra y recrea las vistas) o en los scripts "always", las vistas de `reporting` quedaban sin
privilegios: la restauración (y, sin cambios de esquema, la BD activa) devuelve la lectura de `reporting` a `pos_app`, y después el
instalador corre `apply-always` con el migrador de la versión ANTERIOR (comando nuevo: solo los scripts "always", y solo si la BD está en
su versión). Los migradores viejos no lo tienen (responden 2) y quedan los privilegios copiados. Mientras se migra, el servidor y el
actualizador quedan en inicio manual y el registro de Windows conserva la versión y el modo anteriores; ambos se actualizan recién cuando
la instalación terminó bien (`CreateService` los devuelve a automático; la vuelta atrás también). El servidor, al arrancar, compara el
estado completo de las migraciones (no solo la última versión): con migraciones desconocidas o pendientes no atiende operaciones de negocio
(503 en `/health/ready`) y explica qué hacer. Se eligió no cerrar el proceso: Windows lo reiniciaría en bucle por las acciones de
recuperación y no quedaría dónde leer el motivo. Si la instalación se cancela o se aborta después de detener los servicios y antes de
`ssPostInstall`, `DeinitializeSetup` recupera el actualizador anterior y vuelve a arrancar los servicios que estaban en marcha; el premium
hace lo mismo si se cierra antes de la base de datos. Si vence la espera del premium con el paquete trabajando, no se borra el archivo del
código de recuperación (lo borra Inno, o la próxima ejecución si tiene más de 2 horas). La guía de recuperación explica cómo devolver los
servicios a automático después de restaurar con soporte.
