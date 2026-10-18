# Demo por equipo (H9) — Propuesta e implementación

- **Estado:** Implementado en la rama `demo-por-equipo` — pendiente de tu validación · 2026-10-17
- **Decisión del usuario:** si se reinstala borrando los datos, la prueba gratuita NO vuelve a empezar: se reconoce el equipo.
- **Base:** [fase-licencia-endurecimiento.md](fase-licencia-endurecimiento.md) (H9 quedó pendiente allí).
- **Migraciones:** nube `V2026.10.016__licensing__demo_devices.sql` (y un `GRANT DELETE` en `A__system__privileges.sql`, §2.4);
  tienda `V2026.10.110__licensing__demo_token.sql`.

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

Regla:
1. `local = min(creación de la instalación, copia firmada en license_state, archivo protegido, marca del equipo)`;
2. `local = max(local, piso creíble)`, con piso = la compilación MÁS ANTIGUA del programa vista en este equipo (§2.3);
3. `inicio = min(local, inicio firmado por la nube)`; si la nube dice que soporte **reinició** la demo, `inicio = el de la nube`.

Ninguna capa local ni una respuesta vieja pueden ALARGAR la demo; solo el reinicio firmado de soporte la vuelve a empezar.

### 2.1 Marca del equipo (HKLM)
- Guarda el inicio, la compilación más antigua vista y la huella. Si no se puede escribir, se recuerda lo pendiente (vale para leer) y
  se reintenta con espera creciente (1 min → 1 h).
- La ACL (solo SYSTEM y Administradores, sin herencia) se aplica al crear la clave y se **vuelve a aplicar** si la clave ya existía
  (una vez por proceso), para que una clave creada antes por otro programa o con otra ACL quede igual.
- Valor binario `Mark` = DPAPI de **máquina** sobre `{ "start": …, "fp": "fp1.…" }` (mismo mecanismo que `backup.key`). Se eligió
  DPAPI en vez de un HMAC con un secreto derivado de la huella porque la huella no es secreta (se puede recalcular) y DPAPI de máquina
  ya ata la marca a esta instalación de Windows.
- La clave se crea con ACL explícita y sin herencia: control total para SYSTEM y Administradores; nadie más la puede leer ni escribir.
  El servidor corre como LocalSystem, así que puede escribirla.
- Solo vale si la huella guardada coincide con la del equipo (2 de 3). Se escribe cuando el inicio conocido es más antiguo que el de la
  marca (solo baja).
- **Solo en Release.** En Debug (desarrollo y pruebas) la marca vive en memoria: así el equipo del desarrollador no acumula una demo
  vencida y las pruebas no se contaminan entre sí. Las pruebas de integración la inyectan en memoria también en Release.

### 2.3 Fecha mínima creíble (reloj atrasado al instalar)
- La demo no pudo empezar antes de que existiera el programa. La tienda usa el atributo `PosBuildDate` (día UTC de la compilación, lo pone
  el `.csproj` de Licensing.Infrastructure). El piso es la compilación **más antigua** vista (archivo protegido y marca): una actualización
  posterior NO adelanta el piso (si no, cada actualización regalaría días).
- La nube usa `max(Licensing:DemoDevices:Since ⚙️ 2026-10-01, compilación que manda la tienda)` y solo al recibir un inicio: lo ya
  registrado no se recorta.
- Si aun así la demo quedó vencida por un reloj muy atrasado (programa compilado mucho antes de instalarlo), soporte la reinicia (§2.5).

### 2.4 Cupo y limpieza en la nube
- Cupo de ⚙️ 20 equipos NUEVOS por IP y por día (`Licensing:DemoDevices:NewDevicesPerIpPerDay`); los ya conocidos no cuentan.
- Limpieza (proceso de fondo de la nube, con el refresco de suscripciones): se BORRAN los equipos vistos una sola vez hace más de
  ⚙️ 90 días (`StaleAfterDays`), salvo los reiniciados por soporte. Es el único DELETE del esquema `licensing`
  (`GRANT DELETE ON licensing.demo_devices TO pos_app`); se acepta porque esas filas no tienen valor de auditoría y crecerían sin límite
  con registros abusivos. Efecto: un equipo que se vio una sola vez y vuelve después de 90 días tiene una demo nueva.
- Candado por componente de la huella (`pg_advisory_xact_lock(hashtext(hash))`, en orden): el mismo equipo no se registra dos veces y
  equipos distintos no se esperan entre sí.

### 2.5 Reinicio por soporte
- Portal → "Demo por equipo" → buscar por el **código del equipo** (`XXXX-XXXX`, primeros 8 caracteres del hash del disco, o de la placa,
  o del MachineGuid). La tienda lo muestra en el aviso de la demo vencida. "Reiniciar la demo de este equipo" pide motivo, exige el
  permiso `licensing.demo.reset` (solo superadministrador) y queda en la auditoría (`DEMO_DEVICE_RESET`).
- Como `pos_app` no borra (salvo la limpieza), el reinicio ACTUALIZA la fila: `status = RESET`, inicio = ahora.
- El token de la demo lleva `reset: true`; la tienda usa ese inicio aunque sea posterior y **reemplaza su marca local**. Cubre también
  los equipos clonados (misma huella).

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

La huella de la **licencia** NO cambia en esta rama: hash de (UUID de la placa `Win32_ComputerSystemProduct`, serie del disco del
sistema, `MachineGuid`), con tolerancia 2 de 3. Una tienda que paga con una placa genérica y sin serie de disco sigue con su licencia.

Para la **demo** (`DemoDeviceIdentity`, en el contrato compartido; lo aplican la tienda y la nube):
- Los valores genéricos cuentan como no disponibles: UUIDs de placa `FFFFFFFF-…`, `00000000-…`, `03000200-0400-0500-0006-000700080009`…
  y series de disco `0000_0000_0000_0000.`, `To be filled by O.E.M.`, `0`… (se reconocen comparando hashes).
- Con la placa disponible en los dos lados: 2 de 3, como la licencia. Si la placa es genérica: basta que coincida el **disco**; si
  tampoco hay disco útil, el `MachineGuid`.
- **Reinstalar Windows en el mismo hardware** cambia solo `MachineGuid` → mismo equipo. **Cambiar el disco** con placa real → mismo
  equipo; con placa genérica → equipo nuevo. **Cambiar placa y disco** → equipo nuevo (demo nueva; aceptable).

## 4. Interfaz

Con la demo ya vencida en el equipo:
- El asistente de instalación (`/instalacion`, paso Licencia) dice "La prueba gratuita de este equipo ya terminó; active la licencia
  con su clave" y no ofrece "Seguir en demostración".
- La pantalla de ingreso muestra el mismo aviso, y la administración lo lleva en los avisos (código `DEMO_ENDED`) y en el mensaje de
  cada acción bloqueada.
- `GET /api/v1/setup/status` (anónimo) trae `demoEnded`.

## 5. Límites (documentados)

- **Máquinas virtuales**: suelen tener UUID y serie de disco genéricos o regenerados al clonar; queda el `MachineGuid` (o el disco
  virtual). Clonar una VM o recrearla da una demo nueva; se acepta (soporte puede reiniciar y la nube ve el patrón).
- **MachineGuid**: lo cambia reinstalar Windows y lo puede cambiar un administrador; solo decide cuando no hay placa ni disco útiles.
- **Versión anterior del programa**: un POS sin esta versión no consulta la nube ni escribe la marca; reinstalar la versión vieja con
  datos borrados daría una demo nueva hasta que se actualice y se conecte (entonces la nube devuelve el inicio real del equipo).
- **Bloqueo del dominio de la nube** (firewall, DNS): sin respuesta de la nube vale la marca local; con la marca borrada y el dominio
  bloqueado, la demo se reinicia. La nube sigue siendo la protección principal.
- **Equipos clonados** (misma imagen de disco y misma placa genérica): comparten la demo; soporte la reinicia por equipo (§2.5).
  (Opcional pendiente: tratar como genérico en la nube un hash que aparezca con muchos componentes distintos.)
- **ACL de la marca**: se reaplica si la clave ya existía, pero un administrador siempre puede cambiarla o borrar la clave.
- **Limpieza a 90 días**: un equipo visto una sola vez y que vuelve después de 90 días tiene una demo nueva.

- Cambiar de equipo (placa y disco) da una demo nueva. Es aceptable.
- Un administrador de Windows puede borrar la clave del registro y los datos: por eso la **nube es la protección principal**. Sin
  Internet en ningún momento, un equipo así reinstalado tendría una demo nueva hasta que se conecte una vez.
- Alguien que conozca los hashes de la huella de OTRO equipo podría registrarlos antes y acortarle la demo. Los hashes no se muestran en
  ningún lado de la tienda y el endpoint tiene límite por IP; se acepta.
- Reinstalar Windows invalida la marca DPAPI (nueva clave de máquina); la nube cubre ese caso.
