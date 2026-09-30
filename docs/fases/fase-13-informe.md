# Fase 13 · Instalador y actualizaciones — Informe de implementación

- **Estado:** Implementada — pendiente de tu validación · 2026-09-30
- **Propuesta:** [fase-13-propuesta.md](fase-13-propuesta.md) (aprobada con las recomendaciones de la §11)
- **ADR:** [0055](../adr/0055-instalador-inno-setup-y-postgresql-empaquetado.md) · [0056](../adr/0056-versiones-lado-a-lado-y-actualizador-firmado.md)
- **Guía:** [guia-instalacion.md](../guia-instalacion.md) (armar la versión, instalar, actualizar, desinstalar, lista de verificación manual)
- **Pruebas manuales de la API:** [http/fase-13.http](../../http/fase-13.http)

## 1. Qué se entregó

| Bloque | Entregado |
|---|---|
| 13.1 Publicación y paquete | `tools/scripts/build-installer.ps1`: publica servidor, migrador y agente (autocontenidos, ReadyToRun) y el actualizador (un solo archivo), agrega PostgreSQL 18 sin extras, arma el ZIP de actualización, firma el manifiesto y compila el instalador. `publish-server.ps1` con ReadyToRun |
| 13.2 Instalador y PostgreSQL | `installer/PosSupermercado.iss` (Inno Setup 6, español): modos Todo en uno / Servidor / Caja, recuperación desde backup, servicios con reinicio automático, firewall de la red privada, accesos directos, desinstalación que conserva los datos. `Pos.Server.Migrator install`: `initdb` + servicio `PosSupermercado-DB`, BD y roles con contraseñas aleatorias, migraciones y `server.json` cifrado con DPAPI; idempotente |
| 13.3 Asistente y emparejamiento | Página `/instalacion` (empresa, propietario, licencia o demostración, código de recuperación). Descubrimiento UDP (puerto 5444) en el servidor Multicaja y `Pos.Server.Updater discover` para el instalador de la caja |
| 13.4 Actualizador | Proyecto `Pos.Server.Updater` (servicio): manifiesto firmado, descarga con verificación de huella, ventana segura sin jornadas abiertas, "instalar ahora", backup previo, migración, cambio de versión por unión NTFS, salud y **vuelta atrás automática**. Modo Caja: se actualiza desde el servidor con la huella del certificado fijada. `Pos.Updates.Contracts` (manifiesto, SemVer, historial, descubrimiento) y `tools/Pos.Release` (clave, firma, verificación) |
| 13.5 Desinstalación, soporte y docs | `Pos.Server.Migrator support-bundle`, desinstalación con doble confirmación para borrar datos, guía de instalación, ADRs, Caddy sirve `/updates/` en el VPS |

### Servidor
- `GET /api/v1/system/updates` (estado e historial), `POST /install-now`, y para las cajas `GET /manifest` y `/package` (públicos, firmados).
- El historial del actualizador se audita al arrancar, una sola vez: `UPDATE_DOWNLOADED`, `UPDATE_APPLIED`, `UPDATE_FAILED`,
  `UPDATE_ROLLED_BACK`; además `UPDATE_INSTALL_REQUESTED`.
- 2 permisos nuevos (95 en total): `system.update.view` y `system.update.manage`.
- `Pos:Server:LanDiscovery` (por defecto `true`) apaga el descubrimiento UDP; las pruebas lo apagan.
- El migrador toma las conexiones de `server.json` si no se pasan (`migrate`, `restore` con la del superusuario): el actualizador no pone
  secretos en la línea de comandos.

## 2. Validación hecha (corta, según tu forma de trabajo)

| Prueba | Resultado |
|---|---|
| Compilación de toda la solución (incluidos el actualizador y la herramienta de publicación) | ✅ sin errores ni advertencias |
| `UpdateTests` (11): firma válida, manifiesto alterado, clave desconocida, otro producto, JSON dañado; SemVer; descubrimiento; orquestador: actualización correcta, espera con jornadas abiertas e "instalar ahora", migración fallida sin cambios, versión que no arranca → vuelta atrás con restauración, caja sin backup ni migración | ✅ 11/11 |
| `UpdateApiTests`: estado del actualizador, historial auditado una sola vez, "instalar ahora", manifiesto para las cajas sin sesión y el estado con sesión, `/instalacion` | ✅ |
| Arquitectura (23), conformidad y catálogo de permisos, esquemas (74) | ✅ |
| `Pos.Release`: clave nueva → firma de un paquete → verificación | ✅ |

**No se pudo probar aquí** (tu validación, guía §7): este equipo no tiene Inno Setup ni los binarios de PostgreSQL, así que **no se compiló
el `.exe`** ni se ejecutó `migrator install` de verdad. Tampoco se probaron los servicios de Windows reales, la unión NTFS ni el
descubrimiento UDP entre dos equipos. La lógica que decide (orquestador, firma, historial) sí está probada.

## 3. Cambios frente a la propuesta

- **Recuperar desde backup** está en el instalador, no en la página web: así se respeta el ADR-0052 (restaurar solo desde la consola del
  servidor, nunca desde el navegador).
- `TERMINAL_UPDATED` no se audita en el servidor: la caja no tiene sesión para informarlo; queda en su registro de eventos.
- El actualizador se actualiza a sí mismo solo con el instalador (v1).
- La prueba automática del instalador (instalación silenciosa y desinstalación) queda para cuando Inno Setup esté instalado; la lista manual
  de la guía §7 la reemplaza.

## 4. Pendientes

- Compilar y probar el instalador en Windows 10 y 11 (guía §7).
- Certificado de firma de código antes de vender.
- Pantallas de actualizaciones, emparejamiento y licencia: Fase 15.
