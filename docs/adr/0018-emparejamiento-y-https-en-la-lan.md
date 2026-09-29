# ADR-0018 · Emparejamiento de equipos y HTTPS en la LAN con certificado fijado

- **Estado:** Aceptada · 2026-09-28 · Fase 3

## Contexto
En Multicaja, las cajas y los equipos administrativos se conectan al servidor de la tienda por la red local. Una red de
supermercado no es de confianza (Wi-Fi de clientes, equipos compartidos). No hay una autoridad certificadora ni un dominio.

## Decisión
- **Caja Única**: el servidor escucha solo en `localhost:5480` (HTTP); no hay red.
- **Multicaja**: además escucha en `0.0.0.0:5443` **solo HTTPS**, con un certificado ECDSA P-256 autofirmado generado por
  la instalación (`{DataRoot}/config/server-tls.pfx`, clave protegida con DPAPI). El puerto HTTP sigue solo en localhost.
- **Emparejamiento**: un administrador genera un código de **6 dígitos, un solo uso, 10 min** ⚙️ (para una caja concreta
  o para un equipo administrativo). El equipo lo presenta en `/devices/pair` (limitado por IP) y recibe:
  - su `deviceId` y un **secreto** de 256 bits (en la BD solo el hash SHA-256);
  - la **huella del certificado** del servidor, que el equipo fija (*pinning*) y verifica en cada conexión.
- Toda petición remota exige HTTPS + `X-Device-Id` / `X-Device-Secret` de un equipo activo; si no, `403`
  (`SECURITY.REMOTE_NOT_ALLOWED`, `SECURITY.HTTPS_REQUIRED`, `DEVICE.NOT_PAIRED`, `DEVICE.INVALID_CREDENTIALS`).
  Solo `/devices/pair` y `/system` quedan abiertos a equipos sin emparejar.
- El PIN solo vale desde una caja emparejada; el backoffice, desde el servidor o un equipo administrativo emparejado.
- Revocar un equipo invalida su credencial y cierra sus sesiones de inmediato.

## Consecuencias
- ✅ Tráfico cifrado y servidor autenticado sin depender de una CA ni de Internet.
- ✅ Un equipo desconocido en la red no puede ni intentar un login.
- ⚠️ Cambiar el certificado obliga a volver a emparejar los equipos (o a distribuir la nueva huella; Fase 13).
- ⚠️ El instalador (Fase 13) debe abrir el puerto 5443 en el firewall solo para la red privada.
