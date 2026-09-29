# ADR-0036 · Agente de caja: impresión ESC/POS y cajón en localhost

- **Estado:** Aceptada · 2026-09-29 · Fase 7 · Decisión D7-14 (pregunta 7 del propietario)

## Contexto
Cada caja tiene su impresora de tiquetes (red, USB/serie o instalada en Windows) y el cajón conectado a ella. El navegador de la
interfaz de caja no puede hablar con esos periféricos. Aún no hay hardware: hay que poder probar sin impresora.

## Decisión
- **Librería `Pos.Printing`** (compartida servidor/agente): tiquete **neutro** (`TicketDocument`, JSON polimórfico: texto, columnas,
  separador, código de barras, QR, avance, corte, cajón), diseño a 42/32 columnas (80/58 mm) y generador **ESC/POS** (PC850 o ASCII,
  negrita, doble tamaño, CODE128, QR, corte, pulso del cajón). El servidor arma el tiquete (cobrar, anular, reimprimir) y lo devuelve
  con `openDrawer`.
- **Impresora por caja** en el servidor (`org.terminal_devices`, migración `V2026.10.019`): `GET/PUT
  /organization/terminals/{id}/receipt-printer` (`ReceiptPrinterDto`: conexión `FILE`, `NETWORK`, `WINDOWS_SPOOLER` o `SERIAL`;
  dirección, papel, página de códigos, corte, cajón y pin). Sin configurar: archivo, 80 mm, PC850, corte, cajón en el pin 2.
- **Agente** `src/Terminal/Pos.Terminal.Agent`: servicio de Windows (.NET 10) que escucha en `localhost:5490` y no guarda ventas.
  API: `POST /print`, `POST /drawer/open`, `GET /status` y `POST /test-page` (tildes, estilos, código de barras, QR, corte y pulso del
  cajón). Un trabajo a la vez. Transportes: archivo, red `IP[:9100]`, spooler de Windows (RAW por `winspool.drv`) y puerto serie.

### Desviaciones respecto de la propuesta (§5.7)
1. **El agente no lee la configuración del servidor ni se autentica con él.** La interfaz de caja lee la impresora del servidor y se
   la entrega en cada petición: `POST /print` `{ticket, printer}`, `POST /drawer/open` `{printer}` y `POST /test-page`
   `{printer, terminalName?}`, donde `printer` es el `ReceiptPrinterDto` tal como lo devuelve el servidor. Así el agente no guarda
   credenciales ni depende del emparejamiento.
2. **Transporte de archivo restringido**: sin dirección escribe en `Agent:FileOutputDirectory` (por defecto
   `C:\ProgramData\{Producto}\agent\output`); otra carpeta solo si está en `Agent:AllowedFileDirectories` (el servicio corre con
   permisos amplios y no autentica a quien lo llama).
3. **Solo el propio equipo**: Kestrel escucha solo en loopback (127.0.0.1 y ::1) y se rechaza toda IP remota no loopback
   (`AGENT.LOCAL_ONLY`) y todo `Host` distinto de `localhost`, `127.0.0.1` o `[::1]` (anti *DNS rebinding*, `AGENT.INVALID_HOST`).
   Con cabecera `Origin`, solo los orígenes de `Agent:AllowedOrigins` (por defecto `http://localhost:5480`; los demás,
   `AGENT.ORIGIN_NOT_ALLOWED`) y CORS solo para ellos, respondiendo a *Private Network Access* de Chrome
   (`Access-Control-Allow-Private-Network`). El cuerpo debe ser `application/json`, lo que obliga al navegador a pedir permiso CORS.
4. **`GET /status` no comprueba la impresora**: informa servicio, versión, equipo, puerto, conexiones disponibles, carpeta de salida,
   puertos serie del equipo y el resultado del último trabajo. La impresora se valida con la página de prueba.
5. **Sin modo "Genérico / Solo texto"**: siempre se envían bytes ESC/POS en RAW; las pruebas sin hardware usan el transporte de archivo
   (bytes comparados byte a byte).
6. **Límites del tiquete** (`TicketValidator`): hasta 2.000 elementos, textos de hasta 2.000 caracteres, código de barras de 1 a 80
   caracteres ASCII imprimibles, QR de hasta 2.000 bytes y avance de 0 a 10 líneas; cuerpo máximo `Agent:MaxRequestBytes` (256 KB);
   nombre de la caja en la página de prueba de hasta 60 caracteres.
7. **Puerto serie** como `COMx[:baudios]` (velocidades estándar de 1.200 a 115.200; por defecto `Agent:SerialBaudRate` = 9.600).
8. **Aún no hay script de instalación del servicio** de Windows: se registra a mano hasta el instalador.

## Consecuencias
- ✅ Las plantillas y la lógica siguen en el servidor; el agente es pequeño y reemplazable; se prueba sin hardware.
- ✅ Una página web cualquiera no puede imprimir ni abrir el cajón.
- ⚠️ Si la impresora falla, la venta ya está guardada: la caja reimprime (`/sales/{id}/reprint`). El agente no sabe si la impresora
  está encendida hasta que imprime.
