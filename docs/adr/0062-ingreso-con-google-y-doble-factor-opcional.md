# ADR-0062 · Ingreso al portal con Google y doble factor (TOTP) opcional

- **Estado:** Aceptada · 2026-09-30 · Nube (portal de licencias) · Decisión del propietario; reemplaza la regla "TOTP obligatorio"
  de la Fase 12-A (doc 09 §Portal, L-08)

## Contexto
En la Fase 12-A el portal exigía contraseña + TOTP a todos. En producción (`https://businesspost.tutiendanueva.com/`, detrás de
Cloudflare y Caddy) el dueño ingresó con la contraseña temporal y la página de enrolamiento del TOTP terminó en un "HTTP ERROR 400"
sin registro en el servidor. La causa (ver "Incidente del 400") no era el TOTP en sí, pero el propietario decidió que el ingreso
normal sea **"Ingresar con Google"** (su equipo ya usa cuentas de Google con su propio segundo factor) y que el TOTP quede como opción.

## Decisión
- **Google (OAuth 2.0 / OpenID Connect)** con `Microsoft.AspNetCore.Authentication.Google` (MIT). Botón "Ingresar con Google" en
  `/cuenta/ingresar` (formulario POST con token antifalsificación → `/cuenta/google` → Google → callback `/signin-google` →
  `/cuenta/google/completar`). Alcances `openid email profile`, `SaveTokens=false` (no se guarda ningún token de Google), PKCE.
  La identidad de Google vive solo en una cookie **temporal** (`__Secure-pos-google`, 5 minutos, `HttpOnly`, `SameSite=Lax`) que
  `/cuenta/google/completar` lee y borra de inmediato.
- **Quién entra:** solo un correo que Google marque como **verificado** (`email_verified`) e igual, sin distinguir mayúsculas, al de
  un usuario humano **activo** y sin bloqueo del portal. **Nunca** se crean usuarios. Correo desconocido, sin verificar o usuario
  deshabilitado → el mismo mensaje genérico; usuario bloqueado → el aviso de bloqueo. Todo intento queda en la auditoría
  (`PORTAL_LOGIN_FAILED`; el de un correo desconocido, sin entidad y con el correo en el resumen).
- **Misma sesión del portal:** el ingreso con Google crea la sesión opaca de siempre (ADR-0016; cookie `__Host-pos-portal`
  `Secure`/`HttpOnly`/`SameSite=Strict`), auditada como `PORTAL_LOGIN_SUCCEEDED` con **método GOOGLE**, IP y agente de usuario.
  Las rutas de Google usan el mismo límite por IP que los accesos (`Cloud:Security:LoginPermitsPerMinute`). La sesión guarda su
  método (`portal.portal_sessions.auth_method`: `PASSWORD` | `GOOGLE`, migración V2026.10.008).
- **Vuelta de Google con cookie `Strict`:** la respuesta de `/cuenta/google/completar` NO es una redirección HTTP sino una página
  mínima con `meta refresh` al destino: una redirección seguiría siendo parte de la cadena que empezó en Google (entre sitios) y el
  navegador no enviaría la cookie `SameSite=Strict` recién creada. Así se conserva `Strict` sin relajarla a `Lax`.
- **Configuración:** `Portal:Google:ClientId` y `Portal:Google:ClientSecret` (en el contenedor `Portal__Google__ClientId/ClientSecret`,
  desde `.env`). Sin ambos, el esquema de Google **no se registra** (se decide con la configuración final, al resolver las
  opciones): no hay botón, `/cuenta/google` y `/signin-google` responden 404 y todo sigue como antes. El secreto nunca se registra.
- **TOTP opcional:** `Portal:RequireTotp` (por defecto `false`).
  - `false`: la contraseña sola crea la sesión activa (`LoginChallengeDto.Stage = "ACTIVE"`); a quien activó su TOTP se le sigue
    pidiendo el código. En "Mi cuenta" (y en `/admin/auth/me/totp…`) cada usuario lo activa (QR + primer código) o lo desactiva
    (con un código vigente); ambos cierran sus otras sesiones y conservan la actual.
  - `true`: comportamiento de la Fase 12-A (todos enrolan; no se puede desactivar).
  - El ingreso con Google **nunca** pide el TOTP: la cuenta de Google es el factor fuerte.
- **Contraseña temporal:** con contraseña, sigue siendo obligatorio cambiarla (sin permisos hasta hacerlo). **Con Google no**: el
  usuario no usó la temporal, así que la obligación se evalúa por sesión (`PortalSession.RequiresPasswordChange`: temporal **y**
  método `PASSWORD`). "Mi cuenta" le avisa que su contraseña de respaldo sigue siendo la temporal y puede cambiarla.

## Incidente del 400 (causa y arreglo)
- **Reproducción:** host en `Production` detrás de Caddy en Docker (`tls internal`, `reverse_proxy`, `TrustForwardedHeaders`) con
  registros en Debug. Ingresar → contraseña temporal → `/cuenta/activar-doble-factor` → esperar a que venza la sesión pendiente
  (5 min) → enviar el código ⇒ `400`, 0 bytes, `text/html`, y en el log solo el Debug *"The antiforgery middleware already failed
  to validate the current token"*. Chrome muestra una respuesta 4xx vacía como "HTTP ERROR 400" y la URL del formulario (la misma
  del GET), por eso parecía un fallo del GET.
- **Causa:** la cookie de la sesión PENDIENTE autenticaba al navegador como una identidad; el token antifalsificación del formulario
  del código quedaba atado a esa identidad (ASP.NET incluye un hash de los reclamos). Al vencer la sesión pendiente (instalar la
  aplicación y escanear el QR toma más de 5 minutos) el envío llegaba anónimo, el token no coincidía y Blazor respondía 400 sin
  cuerpo; con `Microsoft.AspNetCore` en `Warning`, nada quedaba en los registros. Lo mismo pasaba en `/cuenta/segundo-factor` y
  con cualquier formulario de `/cuenta` cuya sesión cambiara entre mostrarlo y enviarlo.
- **Arreglo de raíz:** en el navegador, una sesión pendiente ya **no es una identidad** (`AuthenticateResult.NoResult`; las páginas
  de `/cuenta` leen la cookie directamente, la API `/admin` con Bearer no cambia). El token del formulario es anónimo antes y
  después, y la sesión vencida produce el mensaje legible "La sesión venció o fue cerrada. Ingrese de nuevo."
- **Endurecimiento:** `AccountFormExpiredMiddleware` convierte cualquier POST a `/cuenta` con token antifalsificación inválido
  (llaves cambiadas, pestaña vieja, sesión activa vencida) en una redirección a `/cuenta/ingresar?vencido=1` con aviso, y lo
  registra como **advertencia** (visible con la configuración de producción).
- Pruebas: `PortalAccountFormTests` (cookie pendiente vencida → 200 con el aviso; token inválido → redirección con aviso).

## Consecuencias
- ✅ El dueño y su equipo entran con Google sin instalar un autenticador; la contraseña queda de respaldo.
- ✅ Sin configuración de Google no cambia nada; el TOTP obligatorio se recupera con `Portal__RequireTotp=true`.
- ⚠️ La seguridad de quien entra con Google depende de su cuenta de Google (recomendar su verificación en dos pasos). Un usuario
  deshabilitado o bloqueado en el portal no entra aunque Google lo autentique.
- ⚠️ El correo del usuario del portal debe ser exactamente su correo de Google (incluidos Google Workspace y alias `@gmail.com`).
- ⚠️ Detrás de un proxy, la URI de redirección se arma con el esquema y el host que ve la aplicación: exige
  `Cloud__TrustForwardedHeaders=true` (HTTPS) y, con `Cloud:PathBase`, la URI incluye la ruta (`…/businesspost/signin-google`).
