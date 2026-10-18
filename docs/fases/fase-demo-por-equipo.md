# Demo por equipo (H9) — Propuesta e implementación

- **Estado:** Implementado en la rama `demo-por-equipo` — pendiente de tu validación · 2026-10-17
- **Decisión del usuario:** si se reinstala borrando los datos, la prueba gratuita NO vuelve a empezar: se reconoce el equipo.
- **Base:** [fase-licencia-endurecimiento.md](fase-licencia-endurecimiento.md) (H9 quedó pendiente allí).
- **Migraciones:** nube `V2026.10.016__licensing__demo_devices.sql`. La tienda no necesita migración nueva (usa
  `license_state.demo_started_at`, que ya existe desde la 090).

## 1. Problema

Hoy el inicio de la demo sale de la BD (`system.installation.created_at`, inmutable desde la 090) y del archivo protegido
`{DataRoot}\config\license.vault`. Los dos viven en `C:\ProgramData\BusinessPost`: si al desinstalar se piden borrar los datos,
desaparecen y la reinstalación empieza una demo nueva de 30 días.

## 2. Diseño (tres capas; vale el inicio MÁS ANTIGUO)

| Capa | Dónde | Sobrevive a | Quién la puede borrar |
|---|---|---|---|
| BD + archivo protegido (ya existían) | `ProgramData\BusinessPost` | reinicios, actualizaciones, restaurar backups | desinstalar con "borrar datos" |
| **Marca del equipo** (nueva) | `HKLM\SOFTWARE\BusinessPost\Demo`, valor `Mark` | desinstalar (el desinstalador no la toca), borrar `ProgramData` | un administrador de Windows; reinstalar Windows |
| **Registro en la nube por huella** (nuevo, protección principal) | nube, `licensing.demo_devices` | todo lo local, incluso reinstalar Windows (ver §3) | nadie desde la tienda |

Regla única: `inicio de la demo = min(creación de la instalación, copia firmada en license_state, archivo protegido, marca del
equipo, inicio firmado por la nube)`. Ninguna capa puede ALARGAR la demo, solo adelantar su inicio; por eso una respuesta falsa o una
marca alterada no sirven para ganar días.

### 2.1 Marca del equipo (HKLM)
- Valor binario `Mark` = DPAPI de **máquina** sobre `{ "start": …, "fp": "fp1.…" }` (mismo mecanismo que `backup.key`). Se eligió
  DPAPI en vez de un HMAC con un secreto derivado de la huella porque la huella no es secreta (se puede recalcular) y DPAPI de máquina
  ya ata la marca a esta instalación de Windows.
- La clave se crea con ACL explícita y sin herencia: control total para SYSTEM y Administradores; nadie más la puede leer ni escribir.
  El servidor corre como LocalSystem, así que puede escribirla.
- Solo vale si la huella guardada coincide con la del equipo (2 de 3). Se escribe cuando el inicio conocido es más antiguo que el de la
  marca (solo baja).
- **Solo en Release.** En Debug (desarrollo y pruebas) la marca vive en memoria: así el equipo del desarrollador no acumula una demo
  vencida y las pruebas no se contaminan entre sí. Las pruebas de integración la inyectan en memoria también en Release.

### 2.2 Registro en la nube
- `POST /v1/demo-devices` (anónimo, con el límite por IP de la API del POS): `{ fingerprint, localStartedAt }`. Solo viajan los hashes
  de la huella (igual que en la activación).
- La nube busca registros que coincidan en 2 de 3 componentes; si hay, devuelve el inicio más antiguo y completa los componentes
  faltantes; si no, guarda uno nuevo con `min(localStartedAt, ahora)` (un inicio local futuro o absurdo no cuenta). Un candado de la
  transacción evita dos registros a la vez para el mismo equipo.
- Responde un **token firmado** `pos-demo+jws` (misma clave Ed25519 de la licencia, tipo propio: no sirve como token de licencia ni al
  revés) con la huella y el inicio. La tienda lo verifica con sus claves de confianza y la huella, y adopta el inicio si es más antiguo.
- La tienda lo consulta al arrancar y cada 24 h mientras no tenga licencia (reintento a la hora si falla). Sin Internet usa la marca.

## 3. Huella del equipo

Hoy: hash de (UUID de la placa `Win32_ComputerSystemProduct`, serie del disco del sistema, `MachineGuid`), con tolerancia 2 de 3.
- **Reinstalar Windows en el mismo hardware** cambia solo `MachineGuid`: placa y disco siguen coincidiendo → mismo equipo. Estable.
- **Cambiar el disco** cambia un componente → mismo equipo. **Cambiar placa y disco** → equipo nuevo (demo nueva; aceptable).
- Ajuste: muchos fabricantes dejan UUIDs genéricos (`FFFFFFFF-…`, `00000000-…`, `03000200-0400-0500-0006-000700080009`). Dos PCs
  distintos tendrían la "misma placa", y con eso y un disco clonado podrían confundirse. Esos UUIDs se tratan como "no disponible". Un
  equipo con UUID genérico ya activado sigue valiendo: comparar 2 de 3 con disco y `MachineGuid` iguales sigue coincidiendo.

## 4. Interfaz

Con la demo ya vencida en el equipo:
- El asistente de instalación (`/instalacion`, paso Licencia) dice "La prueba gratuita de este equipo ya terminó; active la licencia
  con su clave" y no ofrece "Seguir en demostración".
- La pantalla de ingreso muestra el mismo aviso, y la administración lo lleva en los avisos (código `DEMO_ENDED`) y en el mensaje de
  cada acción bloqueada.
- `GET /api/v1/setup/status` (anónimo) trae `demoEnded`.

## 5. Límites (documentados)

- Cambiar de equipo (placa y disco) da una demo nueva. Es aceptable.
- Un administrador de Windows puede borrar la clave del registro y los datos: por eso la **nube es la protección principal**. Sin
  Internet en ningún momento, un equipo así reinstalado tendría una demo nueva hasta que se conecte una vez.
- Alguien que conozca los hashes de la huella de OTRO equipo podría registrarlos antes y acortarle la demo. Los hashes no se muestran en
  ningún lado de la tienda y el endpoint tiene límite por IP; se acepta.
- Reinstalar Windows invalida la marca DPAPI (nueva clave de máquina); la nube cubre ese caso.
