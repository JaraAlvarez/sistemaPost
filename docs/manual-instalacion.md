# Manual de instalación de BusinessPost

Este manual explica, paso a paso, cómo instalar BusinessPost en los computadores de su tienda y cómo dejarlo listo para vender. No
necesita conocimientos técnicos: siga los pasos en orden.

> Este mismo manual está en el portal de BusinessPost, en **Descargas → Ver el manual**. Desde ahí también puede imprimirlo o guardarlo como PDF.

## 1. Antes de empezar

Revise que tiene todo esto:

- Un computador con **Windows 10 o Windows 11 de 64 bits**, con las actualizaciones de Windows al día.
- Un **usuario administrador** del computador (el instalador le pedirá permiso para hacer cambios en el equipo).
- **Internet** para activar la licencia. Después la tienda puede vender aunque se caiga el Internet.
- Su **usuario del portal de BusinessPost** (el correo y la contraseña que le entregamos) para descargar el instalador.
- Su **clave de licencia** (empieza por `POS-`). Si todavía no la tiene, puede instalar y usar BusinessPost en demostración
  durante 30 días.
- Los datos de su empresa a la mano: razón social, NIT con el dígito de verificación, régimen, municipio, dirección y teléfono.

¿Cuántas cajas tiene el local?

- **Una sola caja:** instale BusinessPost en ese computador y elija "Este equipo es la única caja".
- **Varias cajas (Multicaja):** instale primero en el **equipo principal** (el que guarda los datos; casi siempre el de la
  oficina o la caja principal) y después en cada **caja adicional**.

## 2. Descargar el instalador

1. Entre al portal de BusinessPost con su correo y su contraseña.
2. En el menú de la izquierda, en **Personal**, abra **Descargas**.
3. Pulse el botón **Descargar instalador**. Se descarga un archivo con un nombre parecido a `BusinessPost-Setup-1.2.3.exe`
   (el número es la versión).
4. Si va a instalar en otro computador, copie ese archivo en una memoria USB.

Si la página dice "Aún no hay una versión publicada para descargar", espere un rato y vuelva a intentarlo, o comuníquese con
BusinessPost.

## 3. Instalar el programa

El instalador de BusinessPost es una ventana con la marca que lo guía paso a paso. A la izquierda verá siempre en qué paso va.

1. Haga doble clic en el archivo `BusinessPost-Setup-…exe`.
2. Si aparece el aviso azul **"Windows protegió su PC"**, pulse **Más información** y luego **Ejecutar de todas formas**. Este
   aviso sale porque el instalador todavía no tiene firma digital; el archivo es seguro si lo descargó del portal de BusinessPost.
3. Windows pregunta si permite que la aplicación haga cambios en el equipo: pulse **Sí**. Es la única vez que lo pregunta.
4. **Bienvenida:** pulse **Comenzar**.
5. **Este equipo — "¿Cómo usarás este equipo?":** elija una tarjeta y pulse **Continuar**:
   - **Es la única caja:** el local tiene una sola caja. Es la opción más común.
   - **Es el equipo principal:** el local tiene varias cajas y este es el computador que guarda los datos. Instálelo primero.
   - **Es una caja adicional:** una caja más de un local que ya tiene instalado el equipo principal (vea "Caja adicional" más abajo).
6. **Licencia — "Activa tu licencia":** escriba su clave (`POS-XXXXX-XXXXX-XXXXX-XXXXX`). Con Internet, el instalador muestra
   enseguida la **razón social, el NIT, el plan y hasta cuándo está vigente**. Revise que sea su empresa y pulse **Continuar**.
   - ¿Aún no tiene clave? Pulse **Probar gratis 30 días**. Todo funciona y puede activar la clave después (sección 6).
   - Si su plan es **Caja Única**, elija "Es la única caja" en el paso anterior.
7. **Tu cuenta:**
   - **Con clave:** escriba el **correo y la contraseña de su portal de BusinessPost** (los mismos con los que descargó el
     instalador). Con esa misma cuenta entrará a la administración de BusinessPost, también sin Internet. Si en el portal tiene
     activo el **código de verificación** (doble factor), el instalador se lo pide: escriba los 6 números de su aplicación.
     Si siempre entra al portal con Google y no recuerda su contraseña, en el portal vaya a **Mi cuenta → Cambiar contraseña**.
   - **En prueba de 30 días:** escriba su nombre, su correo y una contraseña de mínimo 10 caracteres (dos veces).
8. **Tu negocio:** con la clave, la razón social y el NIT ya vienen llenos. Complete el **nombre comercial** (el de su aviso), el
   **tipo de persona**, si es **responsable de IVA**, el **municipio** (escriba y elíjalo de la lista), la **dirección**, el
   teléfono y el correo del negocio. El **dígito de verificación del NIT se calcula solo**. Pulse **Instalar BusinessPost**.
9. **Instalación:** espere. La lista muestra cada paso de verdad: copiando los programas, revisando los archivos con Windows
   Defender (la primera vez **puede tardar varios minutos**), creando la base de datos, preparando la caja, iniciando BusinessPost
   y configurando su negocio. Puede seguir usando el computador; no lo apague.
10. **Código de recuperación:** el instalador muestra el código de los backups. Es la única forma de abrir los backups de su negocio
    en otro computador si este se daña o se lo roban.
    - **Se muestra una sola vez.** Pulse **Imprimir** o anótelo con cuidado.
    - **Guárdelo fuera del local** (por ejemplo, en su casa). No lo deje pegado en el computador.
    - Marque **"Ya lo imprimí o lo anoté…"** y pulse **Continuar**.
11. **¡Todo listo!:** verá el resumen (empresa, tipo de equipo y licencia). Pulse **Abrir BusinessPost**. Si quiere vender también
    por internet, marque **"Quiero conocer tutiendanueva.com"** antes de pulsar el botón (es opcional; viene sin marcar).

En el escritorio queda el acceso directo **BusinessPost**. BusinessPost se inicia solo cada vez que se enciende el computador.

### Caja adicional

En el paso 5 elija **Es una caja adicional**. El instalador busca solo el equipo principal en la red del local y lo muestra:
elíjalo y pulse **Instalar BusinessPost**. Si no aparece, escriba la **dirección** y la **huella del certificado**, que se ven en
la administración del equipo principal, en **Administración → Cajas e impresoras**. Al terminar, empareje la caja (sección 5).

### Si algo sale mal durante la instalación

El instalador muestra **"No pudimos terminar"** con el paso que falló. Pulse **Ver el registro** para abrir el detalle y envíelo a
soporte. Sus datos no se pierden. Si el equipo no tiene el componente de Windows que usa la ventana del instalador (WebView2, viene
con Windows 10 y 11 actualizados), se abre el instalador clásico de siempre: siga sus páginas, son las mismas preguntas.

## 4. Configurar la tienda la primera vez

Con el instalador de la sección 3 **no hay que hacer nada más**: la empresa, la sucursal "Principal", la "Caja 1", su usuario
de propietario, la licencia y el código de recuperación quedan listos durante la instalación. Los datos se pueden cambiar después
desde la administración.

Solo si instaló con el **instalador clásico** (equipos sin WebView2), al terminar se abre en el navegador la **Configuración
inicial**: complete la empresa, la sucursal, la caja y el propietario; luego la licencia (o **Seguir en demostración**) y, por
último, genere el **código de recuperación**, guárdelo fuera del local y confírmelo.

### Su usuario de propietario

- Si instaló **con clave**, su usuario es **el correo de su portal** y su contraseña, **la misma del portal**. Entra a la
  administración aunque no haya Internet. Si después cambia la contraseña en el portal, en BusinessPost siga usando la que
  tenía (por ahora no se cambia sola; cámbiela también en BusinessPost).
- Si instaló **en prueba**, su usuario es el correo que escribió y la contraseña que eligió.
- Para vender en la caja con **código y PIN**, créelos en **Administración → Usuarios** (en su usuario: código de caja y PIN).

## 5. Entrar a BusinessPost

La pantalla de ingreso tiene dos formas de entrar:

- **Caja:** escriba el **código de cajero** y el **PIN** y pulse **Entrar a la caja**. Es para vender.
- **Administración:** escriba el **usuario** y la **contraseña** y pulse **Entrar**. Es para productos, precios, inventario,
  reportes, usuarios y configuración.

### Crear cajeros y otros usuarios

1. Entre a la administración con el usuario del propietario.
2. Abra **Administración → Usuarios** y pulse **Nuevo usuario**.
3. Escriba el usuario, el nombre completo y una contraseña inicial (la persona la cambia la primera vez que entra).
4. Elija el **rol**: por ejemplo **Cajero** (vende y opera su caja), **Supervisor de caja**, **Administrador**, **Inventario**,
   **Compras** o **Contador**. Cada rol solo ve lo que necesita.
5. Para que pueda vender, escriba un **código de caja** (3 a 6 números) y un **PIN de caja** (4 a 6 números).
6. Pulse **Crear**.

### Agregar cajas (Multicaja)

1. Instale BusinessPost en la caja nueva con la opción "Esta es una caja adicional" (sección 3).
2. En el equipo principal, entre a **Administración → Cajas e impresoras** y pulse **Emparejar equipo** en la caja que va a usar.
3. En la caja nueva, abra BusinessPost, elija **Emparejar este equipo** y escriba el código que le mostró el equipo principal.

## 6. Activar la licencia después

Si empezó en demostración:

1. Entre a la administración con el usuario del propietario.
2. Abra **Administración → Licencia**.
3. Pegue la clave en **Clave de licencia** y pulse **Activar** (necesita Internet).

## 7. Pagar la mensualidad en línea

Cuando se acerca el vencimiento, en **Administración → Licencia** aparece el recuadro **Pagar mensualidad**. Pulse el botón
**Pagar mensualidad** y pague con Nequi, PSE, tarjeta o Bancolombia; también puede escanear el código QR con el celular. Al
aprobarse el pago, la licencia se renueva sola.

También puede pagar desde el portal de BusinessPost, en **Personal → Mi suscripción**, con el botón **Pagar**.

## 8. Si algo falla

- **BusinessPost no abre o dice que no responde:** espere unos minutos (la primera vez tarda más). Si sigue igual,
  **reinicie el computador**: BusinessPost se inicia solo al encender.
- **El instalador se demora mucho:** es normal la primera vez. No lo cierre; el instalador muestra en qué paso va.
- **Un usuario quedó bloqueado** por equivocarse varias veces: el propietario pulsa **Desbloquear** en **Administración →
  Usuarios**.
- **Necesita ayuda:** comuníquese con soporte de BusinessPost. En el equipo principal (o el de la única caja) abra el menú
  Inicio de Windows, busque la carpeta **BusinessPost** y abra **Paquete de soporte**. En el escritorio aparece un archivo
  `soporte-….zip` con la información que soporte necesita para ayudarle (sin contraseñas); envíelo cuando se lo pidan.

## 9. Si el computador se daña o se lo roban

Instale BusinessPost en el computador nuevo. En **"Este equipo"** elija la misma opción que tenía el anterior y pulse
**Recuperar desde un backup** (debajo de las tarjetas). Elija el archivo del backup (termina en `.posbak`), escriba el **código de
recuperación** que guardó al instalar y pulse **Instalar y recuperar**. Sus datos, usuarios y licencia vuelven con el backup; si
la licencia pide activarse de nuevo, use su clave en **Administración → Licencia**. Si no tiene el archivo del backup, comuníquese
con soporte antes de instalar.

## 10. La ventana de BusinessPost

BusinessPost se abre en **su propia ventana** (con su ícono y su nombre en la barra de tareas), no en el navegador. Mientras el sistema
arranca verá la pantalla **"Iniciando BusinessPost…"**; la primera vez después de instalar puede tardar unos minutos. Si lleva más de 5
minutos, use **"Reintentar"** o **"Cómo resolverlo"**. En la caja, **F11** pone la ventana en pantalla completa (vuelva a pulsar F11 para
salir). Los enlaces a otras páginas, como pagar la mensualidad, se abren en su navegador de siempre.

## 11. Desinstalar BusinessPost

Abra **Configuración de Windows → Aplicaciones → Aplicaciones instaladas**, busque **BusinessPost** y pulse **Desinstalar**. Se abre
la ventana de BusinessPost (Windows pide permiso una vez):

1. Lea qué **se quita** (los programas, sus servicios, los accesos directos y la regla del firewall) y qué **se conserva** (sus
   ventas, inventario, clientes y backups, en `C:\ProgramData\BusinessPost`; si vuelve a instalar, BusinessPost los usa de nuevo).
2. Recomendado: pulse **Hacer un backup final**. El backup queda en **Documentos → "BusinessPost - backup final"**; cópielo a una
   memoria USB. Para abrirlo en otro computador necesita su código de recuperación.
3. Solo si de verdad quiere borrar todo, marque **"Borrar también los datos del negocio"**: el instalador le pide confirmar dos
   veces (la segunda escribiendo **BORRAR**). No se puede deshacer.
4. Si va a usar su clave en otro computador, en **"Liberar este equipo de la licencia"** escriba su usuario y su contraseña de
   BusinessPost (necesita Internet). Si no lo hace aquí, puede liberarlo después desde el portal.
5. Opcional: cuéntenos por qué desinstala. El motivo solo se envía si marca **"Enviar el motivo a BusinessPost"** (viaja con la
   liberación del equipo).
6. Pulse **Desinstalar** y confirme.
