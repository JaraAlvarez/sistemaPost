# Fase 13 · Instalador y actualizaciones — Propuesta

> Estado: **APROBADA** (con las recomendaciones de la §11) · 2026-09-29 · [informe](fase-13-informe.md)
> Requisitos previos:
> - El servidor ya corre como **Servicio de Windows** (Fase 1).
> - Los secretos `dpapi:` se leen desde la configuración (Fase 2).
> - Ya existen el HTTPS en la LAN por el puerto 5443 con emparejamiento de cajas (Fase 3, ADR-0018), el agente de caja (Fase 7) y la
>   consola `Pos.Server.Migrator`, que crea la BD, migra con backup previo obligatorio, restaura y verifica (Fases 2 y 11).
> - La Fase 12-B (licencia) es recomendable antes, porque el asistente la activa.
>
> Base:
> - plan [12 §S, fase 13](../12-plan-riesgos-decisiones.md);
> - doc [10, "Q. Actualizaciones" e "Instalador"](../10-offline-backups-actualizaciones.md);
> - pendientes anotados: ADR-0002 (peso de la publicación), ADR-0017 (libsodium), ADR-0018 (firewall y huella del certificado),
>   ADR-0051 (binarios de PostgreSQL para los backups).

**Convenciones:**
- 🔒 = decisión difícil de cambiar después (matriz en la §4).
- ⚙️ = configurable.
- **§** = "sección".

## 0. Objetivo y alcance

Después de esta fase, **una persona sin conocimientos técnicos instala el POS en Windows 10/11 con un solo archivo `.exe`**:
- elige "Todo en uno", "Servidor" o "Caja";
- el instalador deja PostgreSQL, la base de datos, los servicios y el firewall listos;
- el asistente inicial pide los datos de la empresa, el propietario, la licencia y los backups, o **recupera desde un backup**.

Las **actualizaciones** llegan solas y se instalan en una ventana segura con backup previo. Si algo falla, **vuelven solas a la
versión anterior**.

**Entregable verificable del plan:** instalación limpia en Windows 10 y 11 sin conocimientos técnicos. Además: actualizar de la
versión N a la N+1, y la vuelta atrás automática cuando la N+1 falla.

| Incluido | Excluido (fase) |
|---|---|
| **Instalador** `.exe` único con tres modos: Todo en uno (`SINGLE`), Servidor (`MULTI`) y Caja (agente de caja + acceso directo) | La interfaz de caja y del backoffice (Fase 15; el instalador ya deja el lugar para la UI) |
| **PostgreSQL 18 empaquetado** (binarios, sin su instalador interactivo): `initdb`, servicio propio, solo `localhost`, puerto no estándar, contraseñas aleatorias en DPAPI | Instalación silenciosa masiva por dominio o GPO (no hay MSI, pregunta 1) |
| Servicios `PosSupermercado-DB`, `-Server`, `-Updater` y `-Agent` con inicio y recuperación automáticos | Actualizar las cajas autónomas (no existen aún) |
| Firewall: el 5443 solo en la red **privada** (modo Servidor); certificado TLS autogenerado | |
| **Asistente inicial** mínimo en el navegador (pregunta 3): empresa, sucursal, propietario, licencia o demo, destinos de backup, o **"Recuperar desde backup"** | |
| **Emparejar una caja**: buscar el servidor en la LAN o escribir la IP, más el código de emparejamiento | |
| **Actualizador**: manifiesto firmado, canales, descarga en segundo plano, ventana segura, backup obligatorio, migración, chequeo de salud y **vuelta atrás** | Pruebas largas de actualización con datos reales (Fase 14) |
| Desinstalación que **conserva datos y backups** por defecto | |
| **Paquete de soporte** (registros y estado, sin datos sensibles) | |
| Publicación más liviana (ReadyToRun, compresión) | Recorte de código (*trimming*): rompe EF y Dapper |

Se entrega en **cinco bloques** con una sola aprobación:
- **13.1** Publicación y paquete.
- **13.2** Instalador y PostgreSQL.
- **13.3** Asistente inicial y emparejamiento.
- **13.4** Actualizador con vuelta atrás.
- **13.5** Desinstalación, soporte y documentación.

---

## 1. Qué queda en el equipo

```
C:\Program Files\PosSupermercado\
   app\1.4.0\          ← binarios de cada versión, lado a lado (🔒 D13-03)
   app\1.3.2\          ← la anterior se conserva para volver atrás
   pgsql\18\           ← binarios de PostgreSQL (incluyen pg_dump y pg_restore para los backups)
   current.json        ← qué versión está activa
C:\ProgramData\PosSupermercado\
   data\pg\            ← datos de PostgreSQL
   config\             ← appsettings.Production.json con secretos dpapi:
   backups\  logs\  updates\  certs\
```

## 2. Modos de instalación

| Modo | Instala | Servicios | Red |
|---|---|---|---|
| **Todo en uno** (Caja Única) | PostgreSQL, servidor, agente de caja, actualizador | DB, Server, Agent, Updater | Solo `localhost` |
| **Servidor** (Multicaja) | PostgreSQL, servidor, actualizador (y el agente si también vende) | DB, Server, Updater (+ Agent) | 5443 en la red privada |
| **Caja** (Multicaja) | Agente de caja y acceso directo a la UI | Agent | Sale hacia el servidor. Se actualiza **desde el servidor de la tienda**, no desde Internet |

## 3. Decisiones principales

| # | Decisión | Recomendación |
|---|---|---|
| D13-01 🔒 | Herramienta del instalador | **Inno Setup**: un solo `.exe`, en español, con scripts para los pasos especiales. Es gratis también para uso comercial (se confirma la licencia vigente al implementar). WiX genera MSI, útil solo con administración por dominio, que un supermercado no tiene. Además, desde la v6 exige una cuota de mantenimiento a quien la use comercialmente |
| D13-02 🔒 | PostgreSQL | Binarios oficiales en ZIP de PostgreSQL 18 para Windows (licencia PostgreSQL, permisiva). `initdb` con `--auth=scram-sha-256`, `listen_addresses=localhost`, puerto ⚙️ **5488**, servicio con `pg_ctl register`, cuenta `NT SERVICE\PosSupermercado-DB`. La contraseña de `postgres` es aleatoria y queda cifrada con DPAPI de máquina. Solo la usan el instalador y el actualizador; ninguna persona la conoce (pregunta 4) |
| D13-03 🔒 | Versiones lado a lado | Cada versión en su carpeta; `current.json` dice cuál está activa. Volver atrás = apuntar a la anterior y restaurar el backup previo. Nunca se sobrescriben binarios en uso |
| D13-04 | Secretos | Cadenas de conexión de `pos_app`, `pos_migrator` y `pos_backup` con contraseñas aleatorias de 32 bytes, protegidas con DPAPI de **máquina**. La clave de datos de los backups ya usa DPAPI (Fase 11) |
| D13-05 | Asistente inicial | **Página web mínima** servida por el propio servidor (`/instalacion`, HTML y JS simples, sin framework). Se abre sola al terminar el instalador y usa `POST /api/v1/setup`, la licencia (12-B) y los destinos de backup (11). La Fase 15 la reemplaza con el diseño definitivo (pregunta 3) |
| D13-06 | Recuperar desde backup | En el asistente: elegir el `.posbak` y escribir el código de recuperación. Llama a lo mismo que `Pos.Server.Migrator restore` (Fase 11). Luego se reactiva la licencia (12-B) y se re-emparejan las cajas |
| D13-07 | Emparejar cajas | En el modo Caja, el instalador busca el servidor en la LAN por **difusión UDP** (el servidor responde su nombre, IP y la huella del certificado) o se escribe la IP. Luego se pide el código de emparejamiento que muestra el servidor (Fase 3) |
| D13-08 🔒 | Actualizaciones | El servicio `-Updater` consulta ⚙️ cada 6 h un **manifiesto firmado con Ed25519** (clave de firma de actualizaciones propia, distinta de la de licencias) publicado en tu VPS (`https://<tu dominio>/updates/stable.json`). Descarga el paquete, verifica la firma y el SHA-256, y espera la ventana segura: sin jornadas abiertas, o la hora que programe el propietario ⚙️ (02:00). Luego: backup `PRE_UPDATE` (ya obligatorio), detener, cambiar de versión, migrar, chequeo de salud, arrancar. Si falla, **vuelve atrás sola**: versión anterior + restauración del backup previo si la migración alcanzó a correr |
| D13-09 | Actualizar las cajas | El servidor de la tienda ofrece el paquete de la caja a sus cajas. El agente lo descarga del servidor (LAN, sin Internet), verifica la firma y se actualiza. El servidor declara la **versión mínima de caja** compatible |
| D13-10 | Firma de código (Authenticode) | **Sin certificado en el piloto**: Windows muestra "Editor desconocido" y se documenta cómo continuar. Un certificado de firma de código se compra antes de vender (pregunta 2). El manifiesto y los paquetes **siempre** van firmados con Ed25519, con o sin Authenticode |
| D13-11 | Peso | ReadyToRun + compresión LZMA del instalador. Estimado: 60–80 MB con PostgreSQL. Sin *trimming* |
| D13-12 | Desinstalar | Quita binarios, servicios y la regla de firewall. **Conserva** `ProgramData` (datos, backups, configuración). Borrar los datos exige una casilla explícita, una segunda confirmación y ofrece un backup antes |

## 4. Revisión arquitectónica de las decisiones difíciles de cambiar

| # | Motivo | Alternativas | Riesgos | Impacto en el POS local | Multisucursal | Sincronización | Licenciamiento |
|---|---|---|---|---|---|---|---|
| D13-01 Inno Setup | Simple, un `.exe`, español, gratis | WiX/MSI (cuota comercial, más complejo); MSIX (no admite servicios de Windows ni PostgreSQL fácilmente); Velopack (no instala servicios) | Cambiar de herramienta después obliga a migrar el registro de instalación. Mitigación: el instalador **no guarda estado propio**; todo vive en `current.json` y `ProgramData` | Ninguno | El mismo instalador para cada sucursal | — | El asistente activa la licencia |
| D13-02 PostgreSQL empaquetado | Controlamos la versión, el puerto y la seguridad; no choca con otro PostgreSQL del equipo | Pedir al cliente que instale PostgreSQL (soporte imposible); Docker (no apto para PCs de tienda) | Actualizar la versión mayor de PostgreSQL (18 → 19) requiere `pg_upgrade`: se planea como actualización especial cuando llegue | La BD queda solo en `localhost` | Cada sucursal tiene su BD | Sin cambio | — |
| D13-03 Lado a lado | La vuelta atrás es instantánea y segura | Sobrescribir y guardar un ZIP de respaldo (lento y frágil) | Ocupa disco: se conservan solo ⚙️ 2 versiones | Ninguno | — | — | — |
| D13-08 Actualizador propio | Controla la ventana segura, el backup y la vuelta atrás, que son específicos del POS | Windows Update o winget (sin ventana segura ni vuelta atrás de la BD); instalador manual (el cliente nunca actualiza) | La clave de firma de actualizaciones es crítica: guardarla fuera del VPS, igual que la de licencias. El VPS es el punto de distribución: si cae, simplemente no hay actualizaciones | La tienda nunca se actualiza con jornadas abiertas | Cada sucursal a su ritmo; el portal (12-A) ve la versión de cada una en los check-ins | Versiones distintas por sucursal: la API de sincronización se versiona | El token lleva `UPDATE_AVAILABLE`; ya existe en 12-A |

## 5. Flujos

1. **Instalar Todo en uno:**
   1. aceptar;
   2. modo;
   3. carpeta (por defecto);
   4. se copian los binarios;
   5. `initdb` y servicio de BD;
   6. `Pos.Server.Migrator create-database` y `migrate --no-backup` (BD vacía);
   7. se escribe la configuración con DPAPI;
   8. servicios;
   9. se abre `http://localhost:5480/instalacion`.
2. **Asistente:** empresa y sucursal → propietario → licencia o demo → destino de backup externo (opcional) → generar y confirmar
   el **código de recuperación** → prueba de impresora (agente) → listo.
3. **Recuperar desde backup** (PC nuevo): instalar → asistente → "Recuperar" → archivo y código → restaurar (Fase 11) → reactivar la
   licencia → re-emparejar las cajas.
4. **Caja nueva:** instalar en modo Caja → encontrar el servidor → comparar la huella del certificado → código de emparejamiento
   → servicio del agente → acceso directo.
5. **Actualizar:** manifiesto → descarga → espera de la ventana → backup → cambio → migración → salud. Si falla, se vuelve atrás y
   se audita `UPDATE_FAILED` + `UPDATE_ROLLED_BACK`, con un aviso en el tablero. Si funciona, `UPDATE_APPLIED` y las cajas se
   actualizan desde el servidor.

## 6. Cambios en el código actual (pequeños)

- `Pos.Server.Migrator`:
  - `install-config` escribe la configuración con DPAPI;
  - `support-bundle` genera el ZIP de soporte con registros y estado, sin secretos ni datos de clientes;
  - `rollback` es la vuelta atrás manual;
  - el actualizador reutiliza `backup`, `migrate` y `restore`.
- Servidor:
  - página `/instalacion` en `wwwroot`;
  - respuesta a la difusión UDP del descubrimiento (solo en modo `MULTI`);
  - `GET /health` ya existe y es el chequeo de salud del actualizador.
- Nuevo proyecto `src/Server/Pos.Server.Updater` (Servicio de Windows).
- `installer/` con el script de Inno Setup, más `tools/scripts/build-installer.ps1` para publicar, firmar el manifiesto y compilar el
  instalador.
- VPS: carpeta `updates/` servida por Caddy (se agrega a `deploy/cloud`) y el comando `publish-update` que firma el manifiesto con la
  clave de actualizaciones.

## 7. Permisos y auditoría

| Permiso | Propietario | Administrador |
|---|---|---|
| `system.update.view` (versión, actualización pendiente, historial) | ✅ | ✅ |
| `system.update.manage` (programar la ventana, instalar ahora, posponer) | ✅ | ✅ |

**Auditoría:** `UPDATE_DOWNLOADED`, `UPDATE_APPLIED`, `UPDATE_FAILED`, `UPDATE_ROLLED_BACK` y `TERMINAL_UPDATED`. Se generan con el
catálogo de la Fase 10.

## 8. Validación de la fase (según tu forma de trabajo)

Yo compilo el instalador y hago **una** prueba corta automática: instalación silenciosa en una carpeta temporal, `/health` y
desinstalación. Además, una prueba del actualizador con dos versiones de prueba: N → N+1 correcta, y N+1 que falla en la migración y
vuelve a N.

**Tú** validas lo importante en equipos reales o en *Windows Sandbox*:
- instalación limpia en Windows 10 y 11;
- Todo en uno;
- Servidor + una caja;
- recuperación desde backup;
- desinstalación.

La guía `docs/guia-instalacion.md` tiene la lista de pasos.

## 9. Riesgos

| Riesgo | Mitigación |
|---|---|
| Antivirus o SmartScreen bloquea el instalador sin firma | Guía con capturas; firma Authenticode antes de vender (pregunta 2) |
| El puerto 5488 está ocupado | El instalador prueba y elige el siguiente libre; queda en la configuración |
| Se va la luz durante una actualización | Lado a lado + `current.json` escrito al final de forma atómica + backup previo: al arrancar, si la versión nueva no pasó el chequeo de salud, se vuelve atrás |
| PC con Windows 10 viejo sin actualizaciones | Requisito mínimo: Windows 10 22H2 x64 con 4 GB de RAM. El instalador lo verifica |
| La clave de firma de actualizaciones se filtra | Se guarda fuera del VPS; el POS acepta dos claves (activa y reserva), igual que las licencias |

## 10. Criterios de aceptación

1. Un `.exe` instala en Windows 10 22H2 y 11 los tres modos sin pedir nada técnico; el servidor responde `/health` y el asistente
   se abre solo.
2. PostgreSQL queda solo en `localhost`, con contraseñas aleatorias en DPAPI; `pg_dump` queda disponible para los backups (cierra el
   pendiente de la Fase 11).
3. Una caja encuentra el servidor en la LAN, verifica la huella y se empareja con el código.
4. La recuperación desde backup en un PC nuevo deja la tienda vendiendo en menos de una hora.
5. La actualización N → N+1 respeta la ventana y hace backup; una N+1 defectuosa vuelve sola a N sin perder datos.
6. Desinstalar conserva los datos y los backups; reinstalar los reutiliza.
7. Documentación: `guia-instalacion.md`, doc 10 actualizado, ADRs (Inno Setup, PostgreSQL empaquetado, versiones lado a lado y
   actualizador firmado), informe y lista de verificación manual.

## 11. Preguntas para ti (la opción recomendada va primero)

1. **Tipo de instalador:** ¿**`.exe` con Inno Setup** (**recomendado**), o necesitas MSI (solo sirve si algún cliente administra
   equipos por dominio de Windows)?
2. **Firma de código:** ¿**piloto sin firma** y compramos el certificado antes de vender (**recomendado**; cuesta del orden de cientos
   de dólares al año), o lo compras ya?
3. **Asistente inicial antes de la Fase 15:** ¿una **página web mínima** ahora, que la Fase 15 rediseña (**recomendado**: el
   instalador queda probado de punta a punta), o hacemos la Fase 15 antes y el asistente nace con el diseño final?
4. **Usuario `postgres`:** ¿se deja **sin contraseña conocida por personas** (cifrada con DPAPI, solo para el instalador; **recomendado**, más seguro), o
   quieres una contraseña de soporte que se te muestre al instalar?
5. **Actualizaciones:** ¿**automáticas en la ventana segura** con aviso (**recomendado**), o solo cuando el propietario pulse
   "Actualizar"?
