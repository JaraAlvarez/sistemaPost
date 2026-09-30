# Revisión de seguridad (Fase 14, OWASP ASVS nivel 1)

> Revisión interna del servidor de tienda, la nube de licencias (12-A) y el instalador. **No reemplaza la prueba de penetración externa**,
> que se contrata antes de vender (pregunta 4 de la [propuesta](fases/fase-14-propuesta.md), aprobada).
> Leyenda: ✅ cumple · 🔧 corregido en esta fase · ⚠️ pendiente con plan.

| Área ASVS | Estado | Evidencia o acción |
|---|---|---|
| V2 Autenticación | ✅ | Contraseñas Argon2id (NSec), bloqueo por intentos, PIN solo en cajas emparejadas, contraseña temporal que obliga a cambiarla, límite de peticiones en ingreso y emparejamiento. Portal de la nube con TOTP obligatorio |
| V3 Sesiones | ✅ | Tokens opacos (ADR-0016) con caducidad por inactividad y máxima, atados al equipo que los abrió, revocables; cookie `__Host-` en el portal |
| V4 Control de acceso | ✅ | Cada endpoint declara permiso o anónimo justificado (`AllowAnonymousByDesign`); autorización de supervisor de un solo uso; la licencia restringida se aplica en el backend (ADR-0053, prueba R10) |
| V5 Validación y codificación | ✅ | FluentValidation en cada comando; SQL siempre parametrizado (Dapper/EF); ProblemDetails sin trazas |
| V6 Criptografía | ✅ | AES-256-GCM en backups, Ed25519 en licencias y actualizaciones, SHA-256 en auditoría; claves de firma fuera del VPS |
| V7 Errores y registros | ✅ | Registro sin secretos; datos personales enmascarados en la bitácora (`[PersonalData]`, ADR-0049); paquete de soporte sin secretos ni datos de clientes |
| V8 Protección de datos | 🔧 | **Hallazgo:** DPAPI "de máquina" lo puede descifrar cualquier programa del equipo. **Corrección:** el instalador deja `C:\ProgramData\PosSupermercado` solo para SYSTEM y Administradores (PostgreSQL conserva su carpeta). |
| V9 Comunicaciones | ✅ / ⚠️ | HTTP solo en `localhost`; en la LAN, HTTPS con certificado propio y huella fijada al emparejar (ADR-0018). ⚠️ La nube usa Let's Encrypt (Caddy). Firma Authenticode del instalador: antes de vender |
| V10 Código malicioso / integridad | ✅ | Actualizaciones con manifiesto firmado, huella y tamaño del paquete (ADR-0056); tokens de licencia firmados; auditoría encadenada con sello externo |
| V12 Archivos | ✅ | Carga de CSV/XLSX con límite de tamaño y validación por fila; el agente de caja solo escribe en carpetas permitidas |
| V13 API | ✅ / ⚠️ | Rutas versionadas `/api/v1`; manifiesto y paquete de actualización públicos a propósito (firmados). ⚠️ El asistente `/instalacion` usa un script en línea; la Fase 15 agrega la política de contenido (CSP) al servir la interfaz |
| V14 Configuración | ✅ | `dotnet list package --vulnerable` en `build.ps1` (hoy sin hallazgos); compilación con advertencias como errores y analizadores; Scalar/OpenAPI solo en desarrollo; claves y huellas de desarrollo ignoradas en producción |

## Pendientes antes de vender
1. Prueba de penetración externa (servidor de tienda en LAN, nube, instalador).
2. Certificado de firma de código (Authenticode) para el instalador y los ejecutables.
3. CSP y cabeceras de seguridad al servir la interfaz de la Fase 15 desde el servidor de tienda.
