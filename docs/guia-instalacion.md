# Guía de instalación y actualización (Fase 13)

> Para el propietario y para quien instala en la tienda. Decisiones técnicas: ADR [0055](adr/0055-instalador-inno-setup-y-postgresql-empaquetado.md)
> y [0056](adr/0056-versiones-lado-a-lado-y-actualizador-firmado.md). Recuperación ante un equipo dañado: [guia-recuperacion.md](guia-recuperacion.md).

## 1. Requisitos del equipo

- Windows 10 22H2 u 11, de 64 bits, con al menos 4 GB de RAM (recomendado 8 GB) y disco SSD.
- Permisos de administrador para instalar.
- Multicaja: todos los equipos en la misma red, con la red marcada como **privada** en Windows.

## 2. Armar la versión (quien publica, una vez por versión)

1. Instale Inno Setup 6: `winget install JRSoftware.InnoSetup`.
2. Descargue el ZIP de **binarios** de PostgreSQL 18 para Windows x64 (EDB, "zip archive").
3. La primera vez, genere la clave de firma de actualizaciones y guárdela **fuera** del VPS:
   `dotnet run --project tools/Pos.Release -- new-key --out D:\claves\actualizaciones.pem`.
   Copie la línea que imprime a `src/Server/Pos.Server.Updater/update-keys.json`, y las claves públicas de licencias a
   `src/Modules/Licensing/Pos.Modules.Licensing.Infrastructure/trusted-keys.json` (despliegue-nube §15).
4. Arme la versión:

   ```powershell
   ./tools/scripts/build-installer.ps1 -Version 1.0.0 -PostgresZip C:\descargas\postgresql-18-windows-x64-binaries.zip `
       -LicenseServer https://businesspost.tutiendanueva.com/ -UpdateBaseUrl https://businesspost.tutiendanueva.com/updates/ `
       -SigningKey D:\claves\actualizaciones.pem -Notes "Primera versión"
   ```
   Queda en `artifacts\releases`: el instalador descargable `BusinessPost-Setup-1.0.0.exe` (instalador premium con el paquete
   clásico adentro, §10), el paquete clásico `BusinessPost-Clasico-1.0.0.exe` (soporte), el paquete `BusinessPost-1.0.0.zip` y el
   manifiesto firmado `stable.json`. El portal (Descargas) entrega `BusinessPost-Setup-<versión>.exe`.
5. Para que las tiendas se actualicen solas, suba el ZIP y `stable.json` a la carpeta `updates/` del VPS (despliegue-nube §16).
   El manifiesto **vence** (90 días por defecto, `--expires-days`) y lleva un **número de secuencia** creciente (la hora de la firma,
   `--sequence`): el actualizador rechaza uno vencido o con una secuencia menor que la última que vio, y solo lo descarga por **https**.
   Antes de que venza, vuelva a firmar la misma versión (`dotnet run --project tools/Pos.Release -- sign …`) y suba el `stable.json`
   nuevo. **Tarea periódica (cada semana, en el VPS o en el equipo de quien publica):**
   `dotnet run --project tools/Pos.Release -- verify --manifest stable.json --public-key <x>`: si faltan menos de 15 días para que
   venza, responde con el código 3 y el aviso "vuelva a firmarlo"; entonces firme otra vez y suba el `stable.json` nuevo. Si vence, los
   equipos principales dejan de aceptarlo (las tiendas no se actualizan, pero siguen funcionando).
   Una clave de actualizaciones comprometida se marca `"status": "REVOKED"` en `update-keys.json` (deja de aceptarse en la
   versión siguiente).

## 3. Instalar

Desde la 1.1.0 el archivo descargable abre el **instalador premium** (§10): una ventana con la marca que pregunta lo mismo con tarjetas,
trae la empresa con la clave de licencia, crea al Propietario con la cuenta del portal y configura la tienda sola. Lo que sigue describe
las opciones y los pasos por debajo; el manual para el cliente es `docs/manual-instalacion.md`.

**Capturas del manual y PDF.** Las imágenes de `docs/imagenes/instalacion/` salen del instalador real en modo maqueta (nunca instala
nada): `BusinessPost-Setup.exe --maqueta --pantalla <nombre> --capturar <archivo.png> [--esperar ms]` abre esa pantalla con datos de
ejemplo (Supermercado La Economía SAS), guarda la imagen con WebView2 y se cierra. Pantallas con datos completos: `bienvenida`,
`equipo`, `licencia`, `cuenta-ejemplo`, `negocio-ejemplo`, `instalando-ejemplo`, `codigo-ejemplo`, `listo-ejemplo`, `principal`,
`recuperar`, `error`; con `--desinstalar`, `d-inicio`. Las del POS se toman de la base de desarrollo (Edge sin ventana, 1280×800).
Después de cambiar el manual o las capturas, regenere el PDF con `node tools/scripts/manual-pdf.mjs` (despliegue-nube §16).

El instalador pregunta **"¿Cómo usará este equipo?"** con tres opciones:

| Opción en el instalador | Cuándo elegirla | Nombre técnico |
|---|---|---|
| **Este equipo es la única caja (Caja Única)** | El local tiene una sola caja | Todo en uno |
| **Este es el equipo principal: guarda los datos y las demás cajas se conectan a él (Multicaja)** | Local con varias cajas: se instala primero, en el equipo que guarda los datos | Servidor |
| **Esta es una caja adicional: se conecta al equipo principal del local (Multicaja)** | Cada una de las demás cajas | Caja |

"Equipo principal" no es un servidor en Internet: es un computador del local que guarda los datos; las cajas se conectan a él por la red
del local, sin Internet.

### Una sola caja
1. Ejecute el instalador → **Este equipo es la única caja** → deje en blanco la recuperación → Instalar.
2. Espere a que termine (ver §3.1, la primera vez puede tardar varios minutos). Al final se abre el navegador en el **asistente de
   configuración**: datos de la empresa y del propietario, la clave de licencia (o siga en demostración 30 días) y el **código de
   recuperación**. Imprímalo y guárdelo fuera del local.
3. En el último paso ("Todo listo") pulse **Abrir BusinessPost** para entrar. En el escritorio queda el acceso "BusinessPost".

### Varias cajas (Multicaja)
1. En el **equipo principal**: el instalador con **Este es el equipo principal** y el asistente como arriba. Se abren los puertos 5443
   (HTTPS) y 5444 (búsqueda) solo en la red privada.
2. En cada **caja adicional**: el instalador con **Esta es una caja adicional**. Busca solo el equipo principal y propone su dirección y la
   **huella del certificado**. Compárela con la que muestra la administración del equipo principal (Cajas e impresoras). Si no lo
   encuentra, escríbalas a mano. Al terminar se abre la dirección del equipo principal.
3. Empareje cada caja con el código que genera la administración del equipo principal: **Administración → Cajas e impresoras → Generar
   código de emparejamiento**; en la caja, escríbalo en "Emparejar este equipo".

### 3.1 Qué muestra el instalador al final (Fase 13-B)

Después de copiar los archivos, la pantalla "Instalando" muestra el paso en curso con una barra en movimiento:

| Texto en pantalla | Qué hace | Cuánto tarda (sin firma de código) |
|---|---|---|
| Revisando los archivos con Windows Defender (la primera vez puede tardar varios minutos)… | Abre cada programa una vez para que Defender lo revise ahora y no al arrancar el servicio (§8) | De 1 a 6 minutos (máximo 15) |
| Creando la base de datos… | PostgreSQL, la base de datos y la configuración (solo en el equipo principal o la única caja) | Alrededor de 1 minuto |
| Recuperando los datos desde el backup… | Solo si eligió un archivo `.posbak` | Según el tamaño del backup |
| Registrando los servicios de BusinessPost… | Crea los servicios de Windows (BusinessPost se inicia solo al encender el equipo) | Segundos |
| Iniciando BusinessPost… | Inicia los servicios | Segundos |
| Esperando a que BusinessPost responda… | Espera a que el sistema conteste; si el servicio no alcanzó a iniciar, lo vuelve a iniciar (hasta 3 veces) | Normalmente segundos; máximo 5 minutos |

**No cierre el instalador** mientras muestra estos pasos. Solo cuando BusinessPost responde se abre el navegador (en la caja adicional se
abre la dirección del equipo principal).

**Si BusinessPost no responde en 5 minutos**, el instalador lo dice con un mensaje en lugar de abrir una página en blanco. Qué hacer:

1. Espere unos minutos y abra el acceso **BusinessPost** del escritorio (si la tienda no está configurada, lleva al asistente).
2. Si no abre, **reinicie el computador**: BusinessPost se inicia solo al encenderlo.
3. Si aun así no abre: menú Inicio → BusinessPost → **Paquete de soporte**, y envíe a soporte el archivo que queda en el escritorio.

En la caja adicional, si el programa de impresión no responde, el instalador avisa y abre igual la dirección del equipo principal: se
puede vender; si no imprime, reinicie el computador.

El detalle de cada paso (tiempos del precalentamiento, reinicios del servicio) queda en el registro de la instalación:
`%TEMP%\Setup Log <fecha> #<n>.txt` del usuario que instaló.

### Recuperar en un computador nuevo
En el instalador (única caja o equipo principal) elija el archivo `.posbak` y escriba el código de recuperación. La instalación crea la
base de datos, restaura el backup y verifica la auditoría. Después active de nuevo la licencia (el equipo cambió) y vuelva a emparejar
las cajas.

## 4. Actualizaciones

- El servicio **BusinessPost-Updater** revisa cada 6 horas si hay una versión nueva, la descarga y la verifica (firma y huella).
- La instala a las **02:00** si no hay jornadas abiertas. Antes hace un **backup obligatorio**.
- Si algo falla, vuelve solo a la versión anterior y, si hace falta, a la base de datos del backup previo. Queda en la auditoría.
- **1.2.0 en tiendas con 1.0.x o 1.1.0: con el instalador**, no por actualización automática. El manifiesto `stable.json` de la 1.2.0
  no se publica para esas tiendas hasta validarlo en la VM. Primero hay que desplegar la nube nueva (despliegue-nube §10). Si una tienda vieja
  igual la recibe sola, su actualizador escribe "no se aplicó ningún cambio" aunque la migración falló, y quien vuelve atrás es el
  migrador 1.2.0 (códigos 9/10, docs/notas-de-version.md y docs/guia-recuperacion.md).
- Para instalar ya, sin esperar: **Administración → Actualizaciones → Instalar ahora**. Úselo con las cajas cerradas.
- Las cajas se actualizan desde el servidor de la tienda, sin Internet.
- **La ventana BusinessPost.exe también se actualiza** con el paquete (va en `app\<versión>\desktop`, en el servidor y en las cajas).
  Si está abierta durante la actualización **no se cierra**: sigue funcionando (muestra la pantalla de carga mientras el servicio
  reinicia y vuelve a entrar sola) y la versión nueva se usa **la próxima vez que se abra BusinessPost**. Si la actualización vuelve
  atrás, también vuelve la ventana anterior. Detalle en §9.
- **Cajas:** se actualizan de madrugada, en los primeros 15 minutos después de encender el equipo (antes de vender) o, si el
  propietario pidió "Instalar ahora" en el equipo principal, en los 30 minutos siguientes al pedido (o al encender, durante 24 horas).
  Mientras haya un pedido vigente consultan cada 15 minutos. Para reconocer que el equipo "acaba de encender" aunque Windows tenga el
  **inicio rápido** activado (el sistema hiberna en vez de apagarse), el actualizador combina el arranque del sistema, el de su propio
  servicio y un salto de más de 10 minutos en el reloj entre dos ciclos. **Hay que probarlo en una caja real** con el inicio rápido
  activado: apagar en la noche y comprobar en la mañana, en el historial de Actualizaciones, que instaló al encender.
- Cada caja informa su versión al equipo principal; el backoffice avisa de las cajas que tengan una versión anterior a la mínima que
  pide la versión instalada (`--min-terminal` al firmar; sin ese dato no hay mínimo).
- **"Una actualización automática quedó a medias y requiere soporte"** (aviso rojo del backoffice; `needsSupport` en el estado del
  actualizador): una actualización se cortó y no se pudo deshacer sola en 3 intentos. BusinessPost sigue con la versión anterior y no
  instala otra mientras tanto. Salida: **vuelva a ejecutar el instalador de la versión actual** (archiva el registro pendiente y deja todo
  coherente) o **llame a soporte** (paquete de soporte y `C:\ProgramData\BusinessPost\updates\update-in-progress.json`; si hubo que
  restaurar la base, docs/guia-recuperacion.md).

## 5. Desinstalar

Configuración → Aplicaciones → BusinessPost → Desinstalar abre la **desinstalación premium** (§10.4): explica qué se quita y qué se
conserva, ofrece **Hacer un backup final** (queda en Documentos\BusinessPost - backup final), **liberar el equipo de la licencia** con el
usuario de la tienda y, si se marca, borra los datos con doble confirmación. Por defecto se **conservan** los datos y los backups en
`C:\ProgramData\BusinessPost`, y una reinstalación los reutiliza. Con el paquete clásico (o si falta la copia del instalador premium),
el desinstalador de siempre pregunta dos veces antes de borrar los datos.

## 6. Soporte

Menú Inicio → BusinessPost → **Paquete de soporte**: deja en el escritorio un ZIP con los registros, las versiones y el estado de los
servicios, sin contraseñas ni datos de clientes. Envíelo a soporte.

## 7. Lista de verificación manual (tu validación de la Fase 13)

| # | Prueba | Esperado |
|---|---|---|
| 1 | Instalar "única caja" en Windows 10 22H2 limpio (máquina virtual o *Windows Sandbox*) | La pantalla muestra cada paso (§3.1); servicios `-DB`, `-Server`, `-TerminalAgent`, `-Updater` en ejecución sin iniciarlos a mano; el asistente se abre solo y sin "rechazó la conexión" |
| 2 | Completar el asistente | Empresa creada, licencia o demostración, código confirmado; "Abrir BusinessPost" lleva al ingreso |
| 3 | Repetir en Windows 11 | Igual |
| 4 | Servidor + una caja en la misma red | La caja encuentra el servidor y muestra la huella correcta |
| 5 | Recuperar un backup en otro equipo | Tienda con los datos del backup en menos de una hora |
| 6 | Publicar 1.0.1 en `updates/` y usar "instalar ahora" | Versión 1.0.1 activa, `UPDATE_APPLIED` en la auditoría |
| 7 | Publicar una versión que no arranca | Vuelve sola a la anterior, `UPDATE_ROLLED_BACK` en la auditoría |
| 8 | Desinstalar sin borrar datos y reinstalar | Los datos siguen ahí |
| 9 | Actualizar con la ventana BusinessPost.exe abierta (servidor y una caja) | La ventana no se cierra y vuelve a entrar sola; al cerrarla y abrirla de nuevo es la versión nueva; en una vuelta atrás abre la anterior |

## 8. Firma de código y primer arranque

Windows Defender **retiene** cada `.exe` nuevo y sin firma mientras lo revisa en la nube, sin usar CPU. En pruebas (Fase 17-C) el servidor
recién compilado tardó **más de 5 minutos** en arrancar con su `.exe` sin firma y 13 s con el mismo código cargado por `dotnet.exe`
(firmado por Microsoft). En una tienda pasa en el **primer arranque después de instalar o actualizar**, y puede superar los 30 s que Windows
da a un servicio para iniciar (error 1053) y hacer que el actualizador vuelva a la versión anterior.

- **Solución:** firmar los ejecutables con un certificado de firma de código (Authenticode, OV o EV) a nombre de la empresa:
  `build-installer.ps1 … -CodeSigningThumbprint <huella>` firma los `Pos.*.exe`, `Pos.*.dll`, el actualizador y el instalador con sello de
  tiempo. Un certificado EV además evita el aviso de SmartScreen al instalar.
- **Precalentamiento (automático):** al descomprimir una versión nueva, el actualizador corre sus programas con `--precalentar` (cargan sus
  archivos y terminan, sin abrir puertos ni tocar la base de datos) mientras la tienda sigue vendiendo con la versión anterior. En pruebas:
  precalentar tomó 44 s y el arranque real después del cambio, **4 s** (sin precalentar: 4 min 23 s).
- **Al instalar (Fase 13-B):** el instalador hace lo mismo antes de crear los servicios (paso "Revisando los archivos con Windows
  Defender"): servidor, migrador, agente de caja y actualizador a la vez, con un límite de 15 minutos. Desde el instalador rápido (§11) el runtime de .NET
  está una sola vez y cada programa lee sus archivos en paralelo. En la instalación de la 1.0.1, sin
  este paso, Windows se rindió a los 30 s ("El servicio no respondió a tiempo", eventos 7009 y 7000) porque Defender tardó 356 s.
- **Mientras no haya certificado:** después de instalar o actualizar, espere unos minutos al primer arranque y no reinicie el servicio a
  mano; el instalador espera a que BusinessPost responda (y lo vuelve a iniciar si hace falta) y Windows lo reinicia solo si se cae.
- **En desarrollo:** para evitar la espera después de cada compilación, arranque el servidor con
  `dotnet src/Server/Pos.Server.Host/bin/Debug/net10.0/Pos.Server.Host.dll` en lugar del `.exe`.

## 9. Ventana propia BusinessPost.exe (Fase 13-B, D13B-07)

- **Qué es:** el acceso directo "BusinessPost" (escritorio y menú Inicio) abre `C:\Program Files\BusinessPost\app\current\desktop\BusinessPost.exe`,
  una ventana de Windows con WebView2 (el motor de Edge que ya trae Windows 10/11) en lugar de Edge en modo aplicación. Mismo
  programa para "única caja", "equipo principal" y "caja adicional". Si falta el `.exe`, el instalador deja los accesos de Edge de antes.
- **A dónde se conecta:** `--url <dirección>` en la línea de comandos (pruebas y soporte; con `--huella <sha256>` para un certificado
  propio) → `C:\ProgramData\BusinessPost\config\desktop.json` (opcional: `StartUrl`, `ServerCertificateThumbprint` y
  `AllowedOrigins`, sitios extra que pueden abrirse dentro de la ventana) → caja adicional: `updater.json` (`ServerUrl` y la huella
  del equipo principal, que escribe el instalador) → `http://localhost:<Pos:Server:Port de server.json>/` (5480 por defecto).
- **Pantalla de carga:** propia, dentro del programa. Consulta `/health/live` cada segundo: "Iniciando BusinessPost…", a los 20 s el
  aviso de la revisión de Windows y a los 5 min un error con "Reintentar" y "Cómo resolverlo". Si la conexión se cae con la ventana
  abierta (reinicio del servicio, actualización), vuelve a esta pantalla y entra sola cuando responde; nunca se ve la página de error de Edge.
- **Reglas:** solo navega dentro de la dirección de BusinessPost; los enlaces a otros sitios (pagar la mensualidad, tutiendanueva.com) se
  abren en el navegador predeterminado. Sin herramientas de desarrollo, sin zoom con Ctrl + rueda y con el menú del clic derecho reducido
  a Cortar/Copiar/Pegar. Imprimir (`window.print`) y las descargas (cuadro "Guardar como") funcionan. **F11** pone o quita la pantalla
  completa (útil en la caja). La caja adicional acepta el certificado propio del equipo principal solo si su huella coincide.
- **Por usuario de Windows:** `%LOCALAPPDATA%\BusinessPost\desktop.json` (tamaño, posición, maximizada, pantalla completa),
  `desktop.log` (registro para soporte) y `WebView2\` (datos del motor). Un solo programa abierto a la vez: abrirlo otra vez trae al
  frente la ventana existente.
- **Si no abre:** si Windows no tiene "Microsoft Edge WebView2 Runtime" (raro en Windows 10/11), el programa lo dice y ofrece abrir la
  página oficial de descarga. `BusinessPost.exe --precalentar` carga el programa y termina (para el paso de Windows Defender del instalador).
- **Actualizaciones:** `BusinessPost.exe` va **dentro de cada versión** (`app\<versión>\desktop`, con `LICENSE-WebView2.txt`) y en el
  ZIP del actualizador, así que se actualiza solo, también en las cajas (que reciben el mismo paquete del equipo principal). Al
  descomprimir la versión nueva el actualizador la precalienta (`--precalentar`, sin abrir ventanas); el cambio de `app\current` hace
  que el acceso directo abra la nueva y la vuelta atrás devuelve la anterior. Una ventana abierta nunca se cierra ni se sobrescribe:
  sigue corriendo desde la carpeta de su versión, que la limpieza de versiones viejas no borra mientras siga abierta.
  **Instalaciones anteriores** (accesos directos o ventana anclada a la barra de tareas en `C:\Program Files\BusinessPost\desktop`):
  el actualizador refresca esa carpeta en cada cambio de versión (si el programa está abierto, lo renombra a `*.anterior` y deja el
  nuevo para la próxima apertura; el renombrado se borra en el siguiente cambio) y reinstalar deja los accesos directos nuevos. El
  ícono se genera con `tools/scripts/generar-icono-escritorio.ps1`.

## 10. Instalador premium (Fase 13-B, D13B-08 y D13B-09)

### 10.1 Cómo está armado

| Pieza | Qué es |
|---|---|
| `BusinessPost-Setup-<versión>.exe` (descargable) | Envoltorio Inno sin páginas (`installer/BusinessPost-Premium.iss`): pide permisos de administrador **una vez**, muestra "Preparando el instalador…", saca los dos programas a su carpeta temporal y abre el premium. No se registra en "Aplicaciones instaladas". |
| `BusinessPost-Setup.exe` (premium) | `src/Installer/Pos.Installer.Setup`: ventana WinForms sin marco de 960×600 con WebView2 y las páginas del diseño aprobado incrustadas (nada se carga de internet). Autocontenido, un solo archivo. |
| `BusinessPost-Paquete.exe` | El paquete clásico (`installer/BusinessPost.iss`, `BusinessPost-Clasico-<versión>.exe`), ejecutado **en silencio** por el premium. |

- **Permisos:** el envoltorio pide la elevación; el premium y el paquete la heredan (un solo aviso de Windows). Abierto solo (por
  ejemplo, para desinstalar), el premium se vuelve a abrir como administrador con un aviso. `--maqueta` no pide permisos.
- **Sin WebView2** (Windows muy desactualizado): el premium sale con código 3 y el envoltorio abre el asistente clásico.
- **Servidor de licencias:** se compila dentro del premium (`-p:PosLicenseServer`, el mismo `-LicenseServer` del build). Sin él solo
  queda "Probar gratis 30 días".

### 10.2 Parámetros del paquete clásico en silencio

`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP- /LOG="<registro>" /PREMIUM=1 /MODO=unica|principal|adicional`, más
`/SERVIDOR=` y `/HUELLA=` (caja adicional) o `/BACKUP=` y `/RECUPERACION=<archivo con el código>` (recuperar; el código no va en la
línea de comandos y el archivo se borra al leerlo). Con `/PREMIUM=1` el paquete no abre el navegador al final.

El premium lee el registro mientras se escribe: `Progreso: N` (copia de archivos, de `CurInstallProgressChanged`), `Paso: …` (las
mismas etapas de §3.1) y `Resultado: OK | SIN_RESPUESTA | ERROR <paso>`. Los registros quedan en `%TEMP%\BusinessPost-Setup\`
(`instalacion-<fecha>.log`, `desinstalacion-<fecha>.log`) del administrador que instaló.

### 10.3 Configuración automática (equipo principal o única caja)

> **Desde 1.2.1 el premium no pide ningún dato** (Bienvenida → Instalando → ¡Listo!). Configura la tienda con `POST /api/v1/setup`
> **sin Propietario**: SUPERMARKET, empresa provisional (`Company.ProvisionalNit` = `0-0`, dirección `Por completar`, Bogotá), S01 ·
> Principal y C01 · Caja 1; no activa licencia ni genera el código de recuperación. Si la configuración falla, **Reintentar** repite
> solo ese paso. Al abrir BusinessPost, `Ingresar` ve `OwnerPending` y muestra el **primer ingreso** (`Shared/PrimerIngreso.razor`):
> `POST /api/v1/setup/owner/portal-link` (local, solo sin Propietario; la nube `POST /v1/owner-links` acepta ahora la clave **opcional**:
> sin clave valida a cualquier usuario Cliente y elige la empresa de su cuenta con licencia vigente) → `POST /api/v1/setup/owner` con el
> enlace (si la empresa es provisional toma su NIT, razón social y tipo de negocio) → login → `POST /api/v1/license/activate` con la
> clave o con el **enlace** (`ownerLinkToken`, claim `lic`). El Tablero avisa "Complete los datos de su empresa" mientras
> `CompanyDto.DataPending`. Pendiente: "Continuar con Google" en el primer ingreso y pasar a equipo principal al activar una licencia
> Multicaja (hoy la instalación queda como única caja). Lo de abajo describe el flujo anterior (1.2.0).

Cuando el paquete termina, el premium espera `http://localhost:5480/health/live` (hasta 10 minutos) y, por la API local:
`POST /api/v1/setup` (empresa con los datos de la licencia y de "Tu negocio", sucursal **S01 · Principal**, caja **C01 · Caja 1** y
el Propietario) → `POST /auth/login` → `POST /license/activate` (si falla, sigue en prueba y lo avisa) → `POST /backups/recovery-code`
(se muestra con **Imprimir**) → `POST /backups/recovery-code/confirm` cuando el dueño marca que lo guardó. Si la tienda ya estaba
configurada (reinstalación sobre los datos conservados), no cambia nada.

**Cuenta del propietario = cuenta del portal (D13B-09):** con la clave, el premium envía a la nube `POST /v1/owner-links` (clave,
correo, contraseña y, si el usuario tiene doble factor, el código). La nube acepta solo un usuario **Cliente** de la cuenta dueña de la
empresa, con las mismas reglas del portal (bloqueo por intentos, nada de contraseñas temporales) y devuelve un **enlace firmado**
(Ed25519, misma clave de las licencias, vence en 2 horas, sin la contraseña). El servidor local lo verifica sin conexión y crea al
Propietario con el **correo del portal** como usuario y la **misma contraseña** (el hash se calcula en el equipo). La contraseña solo
viaja del premium al servidor local por `localhost`.

**"Continuar con Google" (§9.5 de la propuesta 13-B):** Google no deja entrar dentro de la ventana del instalador (WebView), así que
el premium pide a la nube `POST /v1/owner-link-requests` (la clave), abre la página `…/cuenta/vincular-instalador?r=<id>` en el
**navegador de Windows** (solo HTTPS y del mismo servidor de licencias) y muestra un código corto para comparar. El dueño entra al
portal como siempre (Google o contraseña) y pulsa **Autorizar**; el premium consulta `POST /v1/owner-link-requests/{id}/poll` cada 5 s
y recibe el **mismo enlace firmado**. Como con Google no hay contraseña del portal que reutilizar, el premium pide entonces una
**contraseña de BusinessPost** (12+ caracteres, letras y números): con el correo y esa contraseña el dueño entra a la administración en
el equipo, también sin internet.

### 10.4 Desinstalación premium

Después de instalar, el premium se copia a `{app}\instalador\BusinessPost-Setup.exe` y queda como `UninstallString` de BusinessPost
(el desinstalador de Inno se guarda en `BusinessPostInnoUninstallString`). Al desinstalar se copia a la carpeta temporal, hace el
backup final con el migrador (`backup --kind MANUAL --output Documentos\BusinessPost - backup final`), libera el equipo con
`POST /license/deactivate` si el dueño escribe su usuario (el motivo viaja solo si lo autoriza) y ejecuta el desinstalador de Inno en
silencio con `/BORRARDATOS=si|no`. Una reinstalación vuelve a registrar el premium.

### 10.5 Revisar el diseño sin instalar

`BusinessPost-Setup.exe --maqueta` recorre las pantallas con datos de ejemplo (no instala, no pide permisos);
`--maqueta --pantalla licencia|cuenta|negocio|instalando|codigo|principal` abre directo una pantalla y `--maqueta --desinstalar` la
desinstalación.

## 11. Instalador rápido: runtime compartido y precalentamiento en paralelo

La instalación tardaba sobre todo en copiar archivos y en el paso "Revisando los archivos con Windows Defender": el servidor, el migrador
y el agente de caja traían **cada uno** su copia del runtime de .NET (583 archivos y 212 MB repetidos que se copiaban y que Defender
revisaba tres veces). **No** se desactiva ni se excluye Windows Defender: se le da menos que revisar y se lee en paralelo.

- **Una sola copia del runtime** en `app\<versión>\dotnet` (estructura de una instalación de .NET: `host\fxr` y `shared\` con
  Microsoft.NETCore.App y Microsoft.AspNetCore.App de la misma versión de parche, firmados por Microsoft). `build-installer.ps1` publica
  `server`, `migrator` y `agent` *framework-dependent* con `AppHostRelativeDotNet=..\dotnet`: cada `.exe` busca el runtime SOLO en
  `..\dotnet` (no usa `DOTNET_ROOT` ni un .NET instalado en el equipo; el cliente no instala nada). El runtime sale de la instalación de
  .NET del equipo que arma la versión (la versión 10.0.x más alta que tenga NETCore y ASP.NET Core) y el script revisa que los tres
  programas arranquen con él (`--precalentar`) antes de seguir.
- El runtime va **dentro de la versión**: el paquete de actualización (ZIP) lo lleva, su manifiesto de huellas lo cubre como al resto y
  la vuelta atrás vuelve también al runtime anterior. Las rutas no cambian (`app\current\server\Pos.Server.Host.exe`, etc.), así que los
  servicios, la unión `app\current` y los actualizadores ya instalados en las tiendas siguen funcionando con los paquetes nuevos.
- **Siguen autocontenidos** (un solo `.exe`): la ventana `BusinessPost.exe` (los actualizadores anteriores la copian también a
  `{app}\desktop`, fuera de la versión) y el actualizador (vive en `{app}\updater`, fuera de las versiones; ahora comprimido: 125 → 56 MB).
- **Recortes:** sin `.pdb` ni XML de documentación en el paquete (quedan en `BusinessPost-<versión>-simbolos.zip`, solo para soporte),
  sin `BlazorDebugProxy` (11 MB, solo para depurar en desarrollo) y solo recursos satélite `es;en` (`Directory.Build.props`). No se usa
  *trimming*: el servidor y el migrador usan reflexión (EF, Blazor, Npgsql) y con el runtime compartido no aplica.
- **Precalentamiento:** `--precalentar` lee primero, en paralelo (un lector por procesador), solo lo que el programa puede cargar (los
  archivos de su carpeta y los del runtime que resolvió el host, `TRUSTED_PLATFORM_ASSEMBLIES`; nada de `pgsql\share` ni documentos) y
  después carga sus ensamblados, también en paralelo. El actualizador precalienta los cuatro programas a la vez (antes uno tras otro).
- **Reinstalar la misma versión:** `[InstallDelete]` borra antes `server`, `migrator`, `agent` y `dotnet` de esa versión (quedaban el
  runtime propio de antes y los `.pdb`); `desktop` no se toca.
- **Espera del final:** `esperar-servicio.ps1` consulta cada 0,5 s (antes 2 s).

Cifras con la 1.2.0 armada en el mismo equipo (con manifiesto firmado, es decir, con el paquete para las cajas adentro):

| | Antes | Después |
|---|---|---|
| Archivos del paquete (app + updater + pgsql) | 3 153 | 2 410 |
| MB sin comprimir | 754 | 486 |
| server / migrator / agent / dotnet | 931 / 269 / 350 / — archivos | 401 / 60 / 14 / 338 archivos |
| Actualizador | 125 MB | 56 MB |
| Paquete de actualización (ZIP) | 237,5 MB | 148,8 MB |
| Paquete clásico | 482,6 MB | 340,5 MB |
| Instalador descargable | 538,6 MB | 394,2 MB |

Sin el paquete para las cajas (versión sin firmar) el descargable pasa de unos 301 MB a unos 245 MB. Lo que más pesa ahora es
`pgsql` (1 588 archivos, 141 MB), que no cambió.
