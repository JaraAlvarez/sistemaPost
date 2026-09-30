# ADR-0055 · Instalador con Inno Setup y PostgreSQL empaquetado

- **Estado:** Aceptada · 2026-09-30 · Fase 13 · Decisiones D13-01, D13-02, D13-04, D13-05, D13-07, D13-10 a D13-12 de la [propuesta](../fases/fase-13-propuesta.md)
- **Cierra pendientes de:** ADR-0002 (peso de la publicación), ADR-0017 (libsodium en el paquete), ADR-0018 (firewall y huella del
  certificado), ADR-0051 (binarios de PostgreSQL para los backups).

## Decisión
- **Un `.exe` con Inno Setup 6** (`installer/PosSupermercado.iss`, compilado por `tools/scripts/build-installer.ps1`), en español, con tres
  modos: Todo en uno, Servidor (Multicaja) y Caja. Requiere Windows 10 22H2 x64 o posterior y permisos de administrador.
- **PostgreSQL 18 empaquetado** (binarios oficiales en ZIP, sin pgAdmin ni documentación) en `{app}\pgsql`. El comando
  `Pos.Server.Migrator install` hace `initdb` (UTF-8, `scram-sha-256`, solo `localhost`, puerto ⚙️ 5488 o el siguiente libre), registra el
  servicio `PosSupermercado-DB` con la cuenta NETWORK SERVICE, crea la BD y los roles con **contraseñas aleatorias de 32 bytes**, migra y
  escribe `server.json` con todas las cadenas cifradas con **DPAPI de la máquina** (incluida la del superusuario, que solo usan el
  instalador y el actualizador). Es idempotente: reinstalar solo migra.
- **Servicios** `PosSupermercado-Server`, `-TerminalAgent`, `-Updater` (y `-DB`) con inicio automático y reinicio ante fallos.
- **Firewall**: en modo Servidor, TCP 5443 (HTTPS) y UDP 5444 (descubrimiento) solo en el perfil de red **privada**.
- **Asistente inicial** mínimo en `http://localhost:5480/instalacion` (HTML y JS sin librerías): empresa, propietario, licencia o
  demostración y código de recuperación. La Fase 15 lo reemplaza. **Recuperar desde backup** se hace en el instalador (archivo `.posbak` y
  código) con `migrator restore`, coherente con ADR-0052 (restaurar solo desde la consola, nunca desde el navegador).
- **Caja**: el instalador busca el servidor con una difusión UDP (`Pos.Server.Updater discover`) y guarda la dirección y la huella del
  certificado (se fija, no se confía en cualquier certificado).
- **Desinstalar** conserva `C:\ProgramData\PosSupermercado`; borrarlo exige dos confirmaciones.
- **Paquete de soporte**: `Pos.Server.Migrator support-bundle` (registros, versiones, servicios, configuración sin secretos).
- Publicación autocontenida con **ReadyToRun** (sin recorte: rompe EF Core y Dapper); el actualizador se publica como un solo archivo.
- **Sin firma Authenticode en el piloto** (Windows muestra "Editor desconocido"); se compra el certificado antes de vender.

## Consecuencias
- Ningún paso pide datos técnicos; nadie conoce las contraseñas de la BD.
- El `.exe` solo se compila en un equipo con Inno Setup 6 (`winget install JRSoftware.InnoSetup`).
- Una versión mayor de PostgreSQL (18 → 19) exigirá una actualización especial con `pg_upgrade`.
