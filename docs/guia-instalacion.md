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
       -LicenseServer https://licencias.midominio.com/ -UpdateBaseUrl https://licencias.midominio.com/updates/ `
       -SigningKey D:\claves\actualizaciones.pem -Notes "Primera versión"
   ```
   Queda en `artifacts\releases`: el instalador `PosSupermercado-Setup-1.0.0.exe`, el paquete `PosSupermercado-1.0.0.zip` y el
   manifiesto firmado `stable.json`.
5. Para que las tiendas se actualicen solas, suba el ZIP y `stable.json` a la carpeta `updates/` del VPS (despliegue-nube §16).

## 3. Instalar

### Todo en uno (una sola caja)
1. Ejecute el instalador → **Todo en uno** → deje en blanco la recuperación → Instalar.
2. Al terminar se abre el navegador en `http://localhost:5480/instalacion`: datos de la empresa y del propietario, la clave de licencia
   (o siga en demostración 30 días) y el **código de recuperación**. Imprímalo y guárdelo fuera del local.
3. En el escritorio queda el acceso "POS Supermercado".

### Multicaja
1. En el **servidor**: el instalador con **Servidor (Multicaja)** y el asistente como arriba. Se abren los puertos 5443 (HTTPS) y 5444
   (búsqueda) solo en la red privada.
2. En cada **caja**: el instalador con **Caja**. Busca solo el servidor y propone su dirección y la **huella del certificado**. Compárela con
   la que muestra la administración del servidor. Si no lo encuentra, escríbalas a mano.
3. Empareje cada caja con el código que genera la administración del servidor: **Administración → Cajas e impresoras → Generar código de emparejamiento**; en la caja, escríbalo en "Emparejar este equipo".

### Recuperar en un computador nuevo
En el instalador (modo Todo en uno o Servidor) elija el archivo `.posbak` y escriba el código de recuperación. La instalación crea la base
de datos, restaura el backup y verifica la auditoría. Después active de nuevo la licencia (el equipo cambió) y vuelva a emparejar las cajas.

## 4. Actualizaciones

- El servicio **PosSupermercado-Updater** revisa cada 6 horas si hay una versión nueva, la descarga y la verifica (firma y huella).
- La instala a las **02:00** si no hay jornadas abiertas. Antes hace un **backup obligatorio**.
- Si algo falla, vuelve solo a la versión anterior y, si hace falta, a la base de datos del backup previo. Queda en la auditoría.
- Para instalar ya, sin esperar: **Administración → Actualizaciones → Instalar ahora**. Úselo con las cajas cerradas.
- Las cajas se actualizan desde el servidor de la tienda, sin Internet.

## 5. Desinstalar

Panel de control → Programas → POS Supermercado. Por defecto se **conservan** los datos y los backups en `C:\ProgramData\PosSupermercado`, y
una reinstalación los reutiliza. Para borrarlos hay que confirmar dos veces. Haga antes una copia en un disco externo.

## 6. Soporte

Menú Inicio → POS Supermercado → **Paquete de soporte**: deja en el escritorio un ZIP con los registros, las versiones y el estado de los
servicios, sin contraseñas ni datos de clientes. Envíelo a soporte.

## 7. Lista de verificación manual (tu validación de la Fase 13)

| # | Prueba | Esperado |
|---|---|---|
| 1 | Instalar Todo en uno en Windows 10 22H2 limpio (máquina virtual o *Windows Sandbox*) | Servicios `-DB`, `-Server`, `-TerminalAgent`, `-Updater` en ejecución; el asistente se abre solo |
| 2 | Completar el asistente | Empresa creada, licencia o demostración, código confirmado |
| 3 | Repetir en Windows 11 | Igual |
| 4 | Servidor + una caja en la misma red | La caja encuentra el servidor y muestra la huella correcta |
| 5 | Recuperar un backup en otro equipo | Tienda con los datos del backup en menos de una hora |
| 6 | Publicar 1.0.1 en `updates/` y usar "instalar ahora" | Versión 1.0.1 activa, `UPDATE_APPLIED` en la auditoría |
| 7 | Publicar una versión que no arranca | Vuelve sola a la anterior, `UPDATE_ROLLED_BACK` en la auditoría |
| 8 | Desinstalar sin borrar datos y reinstalar | Los datos siguen ahí |
