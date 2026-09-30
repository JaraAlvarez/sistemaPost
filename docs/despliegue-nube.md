# Despliegue del servidor de licencias en la nube (Fase 12-A)

> Guía paso a paso para poner en producción el servidor de licencias (API del POS `/v1`, API interna `/admin` y portal web) en un
> VPS con **Ubuntu 24.04 LTS**. Decisión L-11 de la [propuesta](fases/fase-12a-propuesta.md): Docker Compose con la aplicación,
> **PostgreSQL 18** y **Caddy** (HTTPS automático con Let's Encrypt) en `licencias.<su-dominio>`; respaldo diario cifrado **fuera
> del VPS**; la clave privada de firma en un archivo protegido, nunca en el repositorio ni en la BD.

## 0. Piezas

| Pieza | Dónde | Qué hace |
|---|---|---|
| Imagen de la aplicación | `src/Cloud/Pos.Cloud.Host/Dockerfile` | Multi-etapa (`dotnet/sdk:10.0` → `dotnet/aspnet:10.0`), usuario no root (UID 1654), sonda `healthcheck`, sin secretos |
| Compose | `deploy/cloud/docker-compose.yml` | `postgres` (volumen `pgdata`, sin puertos publicados), `db-init` (crea BD/roles y migra; termina), `app` (solo red interna, sistema de archivos de solo lectura), `caddy` (80/443) |
| Proxy | `deploy/cloud/Caddyfile` | `licencias.{$DOMINIO}` → `app:8080`, certificado automático |
| Configuración | `deploy/cloud/.env.example` → `.env` | Dominio, contraseñas, ruta de la clave de firma, respaldo |
| Respaldo | `deploy/cloud/backup.sh` / `restore.sh` | `pg_dump` + llaves de Data Protection cifrados con `age`; copia con `rclone` o `rsync` |
| Prueba local | `deploy/cloud/docker-compose.local.yml` + `Caddyfile.local` | Mismo despliegue en su PC: app en `127.0.0.1:8089`, Caddy con `tls internal` en `127.0.0.1:8443` |

Roles de la BD (los crea `db-init` con `setup-database`, igual que en el POS): `pos_owner` (dueño, sin login), `pos_migrator`
(migraciones; solo lo usa `db-init`), `pos_app` (la aplicación, sin DDL ni `DELETE`), `pos_backup` (solo lectura, respaldos).

Secretos y dónde viven:

| Secreto | Dónde | Respaldo |
|---|---|---|
| Contraseñas de PostgreSQL | `deploy/cloud/.env` (permisos 600) | En su gestor de contraseñas |
| Clave privada de firma Ed25519 **activa** | `deploy/cloud/secrets/signing-key.pem` (dueño 1654, permisos 400), montada en `/run/secrets/signing_key` | Cifrada, fuera del VPS, **una vez** al generarla |
| Clave privada de **reserva** (standby) | **Nunca en el VPS**: solo cifrada fuera de línea | Dos copias cifradas en lugares distintos |
| Llaves de Data Protection (cifran el secreto TOTP) | Volumen `dataprotection` (en claro, solo legible por el UID 1654; por eso el aviso "No XML encryptor configured" del registro es esperado) | Incluidas en cada respaldo diario (cifrado) |
| Clave privada de `age` (descifra respaldos) | **Nunca en el VPS** | En su gestor de contraseñas y en papel/USB |

## 1. Requisitos

- VPS con Ubuntu 24.04 LTS, 2 GB de RAM (1 GB + 2 GB de swap funciona si se compila la imagen en otro equipo), 20 GB de disco.
- Un dominio propio y acceso a su DNS.
- En su equipo: SSH y `age` (Windows: `winget install FiloSottile.age`; Linux: `sudo apt install age`).

## 2. DNS

Cree un registro **A** `licencias.<su-dominio>` → IP pública del VPS (y **AAAA** si el VPS tiene IPv6). Compruebe antes de seguir:

```bash
dig +short licencias.midominio.com     # debe mostrar la IP del VPS
```

Caddy no podrá obtener el certificado mientras el DNS no apunte al VPS.

## 3. Preparar el servidor

```bash
# Usuario administrador (no trabaje como root) y actualizaciones
sudo apt update && sudo apt -y full-upgrade
sudo apt -y install unattended-upgrades age git
sudo dpkg-reconfigure -plow unattended-upgrades

# SSH solo con llave (después de copiar su llave con ssh-copy-id):
sudo sed -i 's/^#\?PasswordAuthentication .*/PasswordAuthentication no/' /etc/ssh/sshd_config
sudo systemctl reload ssh

# Firewall: solo SSH, HTTP (desafío de Let's Encrypt y redirección) y HTTPS (TCP y UDP para HTTP/3)
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw allow 443/udp
sudo ufw enable
```

> Docker publica puertos por encima de `ufw`. Por eso el compose **solo** publica los puertos de Caddy: PostgreSQL y la aplicación
> quedan en redes internas de Docker (la red `backend` ni siquiera tiene salida a internet).

### Docker Engine (repositorio oficial)

```bash
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list
sudo apt update
sudo apt -y install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker $USER   # cierre la sesión SSH y vuelva a entrar
docker compose version
```

Rotación de logs de los contenedores (evita llenar el disco): cree `/etc/docker/daemon.json` y reinicie Docker.

```json
{ "log-driver": "local", "log-opts": { "max-size": "20m", "max-file": "5" } }
```

```bash
sudo systemctl restart docker
```

## 4. Código y configuración

```bash
sudo mkdir -p /opt/pos && sudo chown $USER: /opt/pos
git clone <url-del-repositorio> /opt/pos
cd /opt/pos/deploy/cloud

cp .env.example .env
chmod 600 .env
# Contraseñas: solo letras y dígitos (van dentro de cadenas de conexión)
for v in POSTGRES_SUPERUSER_PASSWORD DB_MIGRATOR_PASSWORD DB_APP_PASSWORD DB_BACKUP_PASSWORD; do
  sed -i "s/^$v=.*/$v=$(openssl rand -hex 24)/" .env
done
nano .env    # DOMINIO=midominio.com, ACME_EMAIL=..., (el respaldo se configura en el paso 8)
```

Guarde las contraseñas de `.env` en su gestor de contraseñas.

## 5. Construir la imagen y generar la clave de firma

```bash
cd /opt/pos/deploy/cloud
docker compose build            # compila la aplicación dentro de la imagen (unos minutos)
```

> Con poca RAM puede compilar en su equipo y copiar la imagen:
> `docker build -f src/Cloud/Pos.Cloud.Host/Dockerfile -t pos-cloud:local .` (en la raíz del repositorio), luego
> `docker save pos-cloud:local | gzip | ssh vps 'gunzip | docker load'`.

Genere la clave privada **activa** (el comando nunca sobrescribe un archivo existente):

```bash
mkdir -p secrets && chmod 700 secrets
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD/secrets:/out" pos-cloud:local \
  generate-signing-key --out /out/signing-key.pem
# Imprime el kid y la clave pública: anótelos (la clave pública se embebe en el POS, Fase 12-B).

# Respaldo cifrado INMEDIATO, fuera del VPS (use una frase de paso larga y guárdela en su gestor):
age -p -o signing-key.pem.age secrets/signing-key.pem
# Desde su equipo: scp vps:/opt/pos/deploy/cloud/signing-key.pem.age .   y luego en el VPS:  rm signing-key.pem.age

# El contenedor corre como UID 1654: debe poder leer la clave; nadie más.
sudo chown 1654:1654 secrets/signing-key.pem
sudo chmod 400 secrets/signing-key.pem
```

**Clave de reserva** (recomendado desde el día 1, L-04): en **su equipo** (no en el VPS) genere otra clave con el mismo comando
(`docker run --rm -v "$PWD:/out" pos-cloud:local generate-signing-key --out /out/reserva.pem`, o
`dotnet run --project src/Cloud/Pos.Cloud.Host -- generate-signing-key --out reserva.pem`), cífrela con `age -p`, guárdela fuera de
línea en dos lugares y borre la copia en claro. Su clave pública se publica en el paso 7.

## 6. Primer arranque

```bash
docker compose up -d
docker compose ps                         # db-init: "exited (0)"; app y postgres: "healthy"; caddy: "running"
docker compose logs db-init               # "Base de datos 'pos_cloud' y roles listos." y la versión de esquema
docker compose logs -f app                # "Clave de firma de licencias cargada (<kid>)" y "Servidor de licencias listo"
curl -s https://licencias.midominio.com/health
```

`/health` debe responder `"status":"Healthy"` con `database` y `signing` en `Healthy`. Si `signing` está `Degraded`, la clave no se
pudo leer (revise dueño 1654 y permisos 400) o está retirada/revocada.

`db-init` se ejecuta en **cada** `docker compose up`: es idempotente (actualiza contraseñas de los roles si cambiaron en `.env` y
aplica las migraciones pendientes) y la aplicación solo arranca si terminó bien.

## 7. Primer superadministrador y clave de reserva

```bash
docker compose exec app dotnet /app/Pos.Cloud.Host.dll create-superadmin --email usted@midominio.com --name "Su nombre"
```

Imprime una **contraseña temporal** (solo funciona si aún no hay superadministrador). Entre a
`https://licencias.midominio.com/cuenta/ingresar`: el portal exige cambiarla y enrolar el doble factor (TOTP) con el código QR.

Publique la clave pública de la reserva (sin su privada):

```bash
docker compose exec app dotnet /app/Pos.Cloud.Host.dll register-standby-key --public-key <clave-publica-de-la-reserva>
curl -s https://licencias.midominio.com/v1/public-keys     # debe listar la activa y la reserva
```

Otros comandos de consola: `docker compose exec app dotnet /app/Pos.Cloud.Host.dll help`.

## 8. Respaldo diario cifrado fuera del VPS

1. En **su equipo** cree el par de `age` (la privada nunca va al VPS):

   ```bash
   age-keygen -o pos-cloud-respaldos.key     # imprime "Public key: age1…"
   ```

   Guarde `pos-cloud-respaldos.key` en su gestor de contraseñas y en una memoria USB. **Sin ella los respaldos no sirven.**
2. En el VPS, en `.env`: `BACKUP_AGE_RECIPIENT=age1…` (la pública), `BACKUP_DIR`, `BACKUP_RETENTION_DAYS` y el destino externo:
   - `rclone` (Backblaze B2, S3, Google Drive…): `sudo apt install rclone`, `rclone config` (como root, que ejecuta el cron) y
     `BACKUP_REMOTE_TOOL=rclone`, `BACKUP_REMOTE=b2:mi-bucket/pos-cloud`. Use una llave de aplicación que solo pueda **escribir**
     (sin borrar) y active la retención de versiones del bucket.
   - `rsync` por SSH a otro servidor: `BACKUP_REMOTE_TOOL=rsync`, `BACKUP_REMOTE=respaldos@otro-servidor:/srv/pos-cloud`.
3. Pruebe y programe:

   ```bash
   chmod +x backup.sh restore.sh
   sudo ./backup.sh
   sudo crontab -e
   # 15 3 * * * cd /opt/pos/deploy/cloud && ./backup.sh >> /var/log/pos-cloud-backup.log 2>&1
   ```

Cada respaldo (`pos-cloud-AAAAMMDDTHHMMSSZ.tar.age`) contiene `pos_cloud.dump` (`pg_dump` formato custom con el rol `pos_backup`),
`dataprotection.tar` (llaves que descifran los secretos TOTP) y `SHA256SUMS`. La clave de firma **no** va en el respaldo diario.

**Pruebe la restauración** al menos cada trimestre en otro equipo (paso 9 con la prueba local de la sección 13).

## 9. Restauración

```bash
cd /opt/pos/deploy/cloud
# Copie el respaldo y, temporalmente, la clave age al servidor (o descifre en su equipo y copie el .tar):
./restore.sh /ruta/pos-cloud-20261001T031500Z.tar.age --identity /ruta/pos-cloud-respaldos.key
shred -u /ruta/pos-cloud-respaldos.key      # no deje la clave age en el VPS
```

El script verifica las sumas, pide escribir `RESTAURAR`, detiene la aplicación, asegura los roles (`db-init`), **recrea** la BD
`pos_cloud` vacía, ejecuta `pg_restore`, repone las llaves de Data Protection, levanta todo (db-init vuelve a aplicar permisos y
migraciones pendientes) y ejecuta `verify-audit` (debe decir "Auditoría íntegra.").

En un VPS **nuevo**: pasos 2 a 5 (con las **mismas** contraseñas de `.env` si las tiene; si no, unas nuevas: `db-init` las
aplica), restaure la clave de firma desde su copia cifrada (`age -d -o secrets/signing-key.pem signing-key.pem.age`, luego
`chown 1654:1654` y `chmod 400`) y ejecute `restore.sh`.

## 10. Actualización de la aplicación

```bash
cd /opt/pos
sudo ./deploy/cloud/backup.sh               # respaldo antes de actualizar
git pull
cd deploy/cloud
docker compose build
docker compose up -d                         # db-init migra; luego se recrea la aplicación
docker compose ps && curl -s https://licencias.midominio.com/health
docker image prune -f
```

Las migraciones son solo hacia adelante: para volver atrás, restaure el respaldo previo con la versión anterior del código
(`git checkout <tag>`, `docker compose build`, `restore.sh`). Actualice PostgreSQL y Caddy con `docker compose pull && docker compose up -d`
(solo versiones menores de PostgreSQL 18; un cambio de versión mayor se hace con volcado y restauración).

## 11. Rotación de la clave de firma

Estados: `STANDBY` (publicada, su privada fuera del servidor) → `ACTIVE` (firma) → `RETIRED` (ya no firma; los tokens emitidos
con ella siguen siendo válidos) · `REVOKED` (comprometida: el POS deja de confiar en ella).

**Rotación planificada** (p. ej. anual):

1. La reserva ya está publicada (paso 7) y embebida en las versiones del POS instaladas.
2. Descifre la reserva en su equipo, cópiela al VPS como archivo nuevo y reemplace la activa:

   ```bash
   sudo install -o 1654 -g 1654 -m 400 reserva.pem secrets/signing-key.pem.nueva
   sudo mv secrets/signing-key.pem.nueva secrets/signing-key.pem
   shred -u reserva.pem
   docker compose up -d --force-recreate app
   docker compose logs app | grep "Clave de firma"     # nuevo kid; la anterior pasa a RETIRED (queda en la auditoría)
   ```

3. Genere una **nueva reserva** fuera de línea y publíquela (`register-standby-key`); inclúyala en la próxima versión del POS.

**Clave comprometida** (se filtró el archivo o el VPS):

1. Active la reserva como en la rotación planificada (paso 2).
2. Revoque la clave filtrada: `docker compose exec app dotnet /app/Pos.Cloud.Host.dll revoke-signing-key --kid <kid-filtrado>`
   (o desde el portal, en *Claves de firma*). Por consola, el servidor en marcha tarda **hasta 1 minuto** en dejar de aceptar los
   tokens de esa clave (caché de las claves de confianza); desde el portal se aplica al instante.
3. Genere y publique una nueva reserva; cambie además las contraseñas de `.env` y revise la auditoría (`verify-audit` y el portal).

## 12. Recuperación de emergencia

| Situación | Qué hacer |
|---|---|
| Un usuario perdió el doble factor o está bloqueado | Otro superadministrador lo resuelve en el portal. Si no hay ninguno disponible: `docker compose exec app dotnet /app/Pos.Cloud.Host.dll recover-user --email <correo> --reason "<motivo>"` (contraseña temporal, reinicia el TOTP y desbloquea; queda en la auditoría) |
| `/health` → `database` Unhealthy | `docker compose logs db-init app postgres`; `docker compose exec app dotnet /app/Pos.Cloud.Host.dll status --connection "Host=postgres;Database=pos_cloud;Username=pos_migrator;Password=<DB_MIGRATOR_PASSWORD>"` muestra la versión y los scripts pendientes o modificados |
| `/health` → `signing` Degraded | Archivo ausente o ilegible (dueño 1654, permisos 400) o clave `RETIRED`/`REVOKED`: configure la activa correcta y recree `app` |
| Caddy no obtiene el certificado | DNS (`dig`), puertos 80/443 abiertos en `ufw` **y** en el firewall del proveedor, `docker compose logs caddy`. Let's Encrypt limita los intentos: corrija antes de reintentar |
| Se perdieron las llaves de Data Protection | Los secretos TOTP no se pueden descifrar: restaure el respaldo, o use `recover-user` con cada usuario para re-enrolar el doble factor |
| La auditoría tiene hallazgos (`verify-audit` ≠ 0) | No borre nada. Conserve el volumen y un respaldo, compare con respaldos anteriores e investigue antes de continuar |
| Se perdió el VPS | VPS nuevo + sección 9. Los POS siguen vendiendo sin conexión hasta `valid_until + gracia` (doc 09) |
| Se perdió la clave privada activa sin respaldo | Active la reserva (sección 11). Sin reserva publicada, los POS instalados no confiarán en una clave nueva hasta actualizarlos |

## 13. Prueba local del despliegue (su PC)

Mismo compose con `docker-compose.local.yml`: la aplicación se publica en `127.0.0.1:8089` (HTTP) y Caddy usa su CA interna en
`https://licencias.localhost:8443`. Los puertos evitan los de otros servicios del equipo.

```bash
cd deploy/cloud
mkdir -p secrets
dotnet run --project ../../src/Cloud/Pos.Cloud.Host -- generate-signing-key --out "$PWD/secrets/signing-key.pem"
cat > .env.local <<'EOF'
DOMINIO=localhost
ACME_EMAIL=admin@example.com
POSTGRES_SUPERUSER_PASSWORD=localsuperuser1234
DB_MIGRATOR_PASSWORD=localmigrator1234
DB_APP_PASSWORD=localapp12345678
DB_BACKUP_PASSWORD=localbackup12345
EOF
docker compose -p pos-cloud-local -f docker-compose.yml -f docker-compose.local.yml --env-file .env.local up -d --build --wait
curl -s http://127.0.0.1:8089/health
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8089/cuenta/ingresar     # 200
curl -sk -o /dev/null -w "%{http_code}\n" https://licencias.localhost:8443/cuenta/ingresar   # 200 (Caddy)
docker compose -p pos-cloud-local -f docker-compose.yml -f docker-compose.local.yml --env-file .env.local down -v
```

`.env.local` y `secrets/` están en `.gitignore`. El ingreso al portal necesita HTTPS (cookie `__Host-`): use la dirección de Caddy.

## 14. Nube de backups de las tiendas (Fase 11, MinIO)

Las tiendas suben sus backups (`.posbak`, **ya cifrados en la tienda**) a un almacenamiento compatible S3. En el VPS se usa **MinIO**
como servicio aparte (perfil `backups`). Ni el VPS ni el portal pueden leer esos archivos: solo se abren con la clave del equipo de la
tienda o con el código de recuperación del propietario.

1. **DNS:** registro A de `backups.<DOMINIO>` → IP del VPS.
2. **Configuración:** en `.env` defina `MINIO_ROOT_PASSWORD` (larga). Descomente el bloque `backups.{$DOMINIO}` del `Caddyfile`.
3. **Arranque:**
   ```bash
   docker compose --profile backups up -d
   ```
4. **Bucket y usuario por tienda** (con el cliente `mc` que trae la imagen):
   ```bash
   docker compose exec minio mc alias set local http://localhost:9000 posadmin "$MINIO_ROOT_PASSWORD"
   docker compose exec minio mc mb local/pos-backups
   docker compose exec minio mc version enable local/pos-backups
   docker compose exec minio mc admin user add local tienda-s01 "<clave-larga-de-la-tienda>"
   docker compose exec minio mc admin policy attach local readwrite --user tienda-s01
   ```
   El versionado del bucket protege contra borrados o un ransomware en la tienda (se puede recuperar la versión anterior).
5. **En la tienda** (`POST /api/v1/backups/destinations`): tipo `S3`, `endpoint` `https://backups.<DOMINIO>`, `bucket` `pos-backups`,
   `accessKey` `tienda-s01`, `secretKey` la clave del paso 4, `onNightly` y `onClosing` en `true`. Pruebe con
   `POST /api/v1/backups/destinations/{id}/test`.
6. **Espacio:** cada tienda guarda según su retención (por defecto 7 diarios, 4 semanales y 12 mensuales). Revise el espacio del VPS con
   `docker system df -v` y `df -h`.

Recuperar un backup desde la nube en un equipo nuevo: descárguelo desde la consola de MinIO (`https://backups.<DOMINIO>` no expone la
consola; use `docker compose exec minio mc cp local/pos-backups/<carpeta>/<archivo>.posbak /tmp/` y cópielo) y siga
[guia-recuperacion.md](guia-recuperacion.md).

## 15. Claves públicas embebidas en el POS (Fase 12-B)

El POS solo confía en las claves públicas **embebidas en su binario** (ADR-0054). Antes de publicar una versión para clientes:

1. En el VPS, liste las claves de firma (portal → "Claves de firma", o el comando `keys` del servidor): anote `kid` y `x` de la
   clave `ACTIVE` y de la `STANDBY`.
2. Escríbalas en `src/Modules/Licensing/Pos.Modules.Licensing.Infrastructure/trusted-keys.json`:

   ```json
   { "keys": [ { "kid": "…", "kty": "OKP", "crv": "Ed25519", "x": "…", "status": "ACTIVE" },
               { "kid": "…", "kty": "OKP", "crv": "Ed25519", "x": "…", "status": "STANDBY" } ] }
   ```
3. Compile y publique. Al rotar (§11), la `STANDBY` pasa a `ACTIVE` y el POS ya la conoce; genere una nueva `STANDBY` y embébala en la
   siguiente versión.
4. Configure en cada tienda `Pos:Licensing:ServerUrl` con la dirección pública del servidor (lo hace el instalador, Fase 13).

La migración de la nube `V2026.10.005` (sello de auditoría en los check-ins) se aplica sola al actualizar el contenedor (§10).

## 16. Actualizaciones del POS (Fase 13)

Caddy sirve la carpeta `deploy/cloud/updates` del VPS en `https://licencias.<DOMINIO>/updates/` (solo lectura).

1. Arme la versión en su PC con `tools/scripts/build-installer.ps1` (ver [guía de instalación](guia-instalacion.md) §2).
2. Copie al VPS `PosSupermercado-<versión>.zip` y el manifiesto firmado `stable.json` (o `beta.json`):

   ```bash
   scp artifacts/releases/PosSupermercado-1.0.1.zip artifacts/releases/stable.json usuario@vps:~/pos/deploy/cloud/updates/
   ```
3. Las tiendas lo descargan en las siguientes 6 horas y lo instalan a las 02:00 sin jornadas abiertas. Para un piloto, publique primero
   en `beta.json` y configure esas tiendas con `Pos:Updates:Channel = beta`.
4. **La clave de firma de actualizaciones nunca va al VPS**: el manifiesto se firma en su PC.

Para retirar una versión defectuosa, vuelva a subir el `stable.json` anterior: las tiendas que ya actualizaron se quedan en la nueva
(la vuelta atrás automática solo actúa si la versión no arranca).
