# Fase 13-B · Instalador con marca, arranque confiable y descargas en el portal del cliente — Propuesta

> Estado: **PROPUESTA — pendiente de tu aprobación** · 2026-10-17
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
