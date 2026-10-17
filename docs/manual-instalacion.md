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

1. Haga doble clic en el archivo `BusinessPost-Setup-…exe`.
2. Si aparece el aviso azul **"Windows protegió su PC"**, pulse **Más información** y luego **Ejecutar de todas formas**. Este
   aviso sale porque el instalador todavía no tiene firma digital; el archivo es seguro si lo descargó del portal de BusinessPost.
3. Windows pregunta si permite que la aplicación haga cambios en el equipo: pulse **Sí**.
4. En **"¿Cómo usará este equipo?"** elija una opción:
   - **Este equipo es la única caja:** el local tiene una sola caja. Es la opción más común.
   - **Este es el equipo principal: guarda los datos y las demás cajas se conectan a él (Multicaja):** el local tiene varias
     cajas y este es el computador principal. Instálelo primero.
   - **Esta es una caja adicional: se conecta al equipo principal del local (Multicaja):** una caja más de un local que ya
     tiene instalado el equipo principal.
5. En **"Recuperar desde un backup"** deje todo **en blanco** y pulse **Siguiente**. Esta página solo se usa cuando un computador
   nuevo reemplaza a uno dañado o robado (vea la sección 9).
6. Solo para una caja adicional: el instalador busca solo el equipo principal en la red del local. Si los datos no aparecen,
   cópielos de la administración del equipo principal, en **Administración → Cajas e impresoras**.
7. Pulse **Instalar** y espere.

La primera instalación **puede tardar varios minutos** (a veces más de 5): Windows revisa los archivos con su antivirus y
BusinessPost prepara la base de datos de la tienda. El instalador muestra en qué paso va. No cierre la ventana ni apague el
computador mientras tanto.

Al terminar, BusinessPost se abre solo. En el escritorio queda el acceso directo **BusinessPost** para abrirlo cuando quiera.
BusinessPost se inicia solo cada vez que se enciende el computador.

## 4. Configurar la tienda la primera vez

En el equipo de la única caja o en el equipo principal se abre la **Configuración inicial**. Tiene cuatro pasos y se hace una
sola vez. Los datos se pueden cambiar después desde la administración.

### Paso 1. Empresa

- **Empresa:** razón social, nombre comercial, tipo de persona (jurídica o natural), tipo de documento, número del NIT **sin** el
  dígito de verificación y, aparte, el **dígito de verificación**, el **régimen**, el **municipio** (escriba al menos tres
  letras y elíjalo de la lista), la dirección, el teléfono y el correo.
- **Sucursal y caja:** un código y un nombre para la sucursal (por ejemplo `001` y "Principal") y para la caja (por ejemplo
  `01` y "Caja 1").
- **Propietario:** es el usuario con acceso total. Escriba un **usuario**, su nombre, una **contraseña** de mínimo 10
  caracteres, un **código de caja** (3 a 6 números) y un **PIN** (4 a 6 números). Con el usuario y la contraseña entra a la
  administración; con el código de caja y el PIN entra a vender.

Pulse **Guardar y continuar**.

### Paso 2. Licencia

- Si tiene la clave, péguela en **Clave de licencia** y pulse **Activar** (necesita Internet).
- Si aún no la tiene, pulse **Seguir en demostración**. Todo funciona durante 30 días y el tiquete dice "DEMOSTRACIÓN". Puede
  activar la licencia después (vea la sección 6).

### Paso 3. Código de recuperación

Pulse **Generar el código**. Este código es la única forma de abrir los backups de la tienda en otro computador si este se
daña o se lo roban.

- **Se muestra una sola vez.** Imprímalo o anótelo con cuidado.
- **Guárdelo fuera del local** (por ejemplo, en su casa). No lo deje pegado en el computador.
- Escríbalo de nuevo para confirmar que lo guardó y pulse **Confirmar**.

### Paso 4. Todo listo

Verá el mensaje **"Todo listo"**: la tienda quedó configurada y los backups automáticos ya están activos. Pulse **Abrir
BusinessPost**.

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

Instale BusinessPost en el computador nuevo con la misma opción que tenía el anterior. En la página **"Recuperar desde un
backup"** escriba el **código de recuperación** que guardó en el paso 3 y, en la página siguiente, elija el archivo del backup
(termina en `.posbak`). Si no tiene el archivo del backup, comuníquese con soporte antes de instalar.
