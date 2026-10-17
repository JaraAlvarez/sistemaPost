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
   Queda en `artifacts\releases`: el instalador `BusinessPost-Setup-1.0.0.exe`, el paquete `BusinessPost-1.0.0.zip` y el
   manifiesto firmado `stable.json`.
5. Para que las tiendas se actualicen solas, suba el ZIP y `stable.json` a la carpeta `updates/` del VPS (despliegue-nube §16).

## 3. Instalar

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
- Para instalar ya, sin esperar: **Administración → Actualizaciones → Instalar ahora**. Úselo con las cajas cerradas.
- Las cajas se actualizan desde el servidor de la tienda, sin Internet.

## 5. Desinstalar

Panel de control → Programas → BusinessPost. Por defecto se **conservan** los datos y los backups en `C:\ProgramData\BusinessPost`, y
una reinstalación los reutiliza. Para borrarlos hay que confirmar dos veces. Haga antes una copia en un disco externo.

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
  Defender"): servidor, migrador, agente de caja y actualizador a la vez, con un límite de 15 minutos. En la instalación de la 1.0.1, sin
  este paso, Windows se rindió a los 30 s ("El servicio no respondió a tiempo", eventos 7009 y 7000) porque Defender tardó 356 s.
- **Mientras no haya certificado:** después de instalar o actualizar, espere unos minutos al primer arranque y no reinicie el servicio a
  mano; el instalador espera a que BusinessPost responda (y lo vuelve a iniciar si hace falta) y Windows lo reinicia solo si se cae.
- **En desarrollo:** para evitar la espera después de cada compilación, arranque el servidor con
  `dotnet src/Server/Pos.Server.Host/bin/Debug/net10.0/Pos.Server.Host.dll` en lugar del `.exe`.

## 9. Ventana propia BusinessPost.exe (Fase 13-B, D13B-07)

- **Qué es:** el acceso directo "BusinessPost" (escritorio y menú Inicio) abre `C:\Program Files\BusinessPost\desktop\BusinessPost.exe`,
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
- **Actualizaciones:** `BusinessPost.exe` se actualiza con el instalador (no va en el ZIP del actualizador). El ícono se genera con
  `tools/scripts/generar-icono-escritorio.ps1`.
