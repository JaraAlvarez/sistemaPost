# ADR-0038 · Token de licencia JWS EdDSA/Ed25519 con rotación por `kid`

- **Estado:** Aceptada · 2026-09-29 · Fase 12-A · Decisiones L-04 y L-05 de la [propuesta](../fases/fase-12a-propuesta.md)

## Contexto
El POS debe verificar su licencia **sin Internet** hasta `valid_until + gracia` (doc 09) y nunca dejar de vender por falta de
conexión. La nube firma; el POS solo verifica con claves públicas embebidas. Si la clave privada se pierde o se filtra, hay que poder
reemplazarla sin reinstalar los POS.

## Decisión
- **Formato**: JWS compacto (RFC 7515) con **EdDSA/Ed25519** (RFC 8037) firmado con NSec (ya usado en el POS, ADR-0017):
  `base64url(cabecera).base64url(contenido).base64url(firma)`, cabecera `{"alg":"EdDSA","kid":"…","typ":"pos-license+jws"}`,
  tamaño máximo 8 KB. Implementación propia en `Pos.Licensing.Contracts` (`LicenseToken.Sign`/`Verify`), sin librerías JWT.
- **`kid`** = `ed25519-` + los primeros 12 hexadecimales del SHA-256 de la clave pública cruda (determinista).
- **Contenido** (`LicenseClaims`): `ver`, `lic`, `org` (NIT con DV), `org_name`, `inst`, `dev` (huella), `role`, `edition`,
  `sub_status`, `iat`, `valid_until`, `grace_days`, `refresh_after`, `msgs`. El token dice el estado; la validez en el tiempo la
  decide quien lo usa, no la firma.
- **Versión del contenido `ver`** (hoy 1): agregar un campo obligatorio o cambiar el significado de uno exige subirla; el verificador
  rechaza versiones que no conoce (`UnsupportedVersion`) aunque la firma sea válida.
- **Verificación con varias claves** (`LicenseKeyRing`): el POS trae varias claves públicas embebidas y acepta un token si su `kid`
  está entre ellas y la firma corresponde. Resultados: `Valid`, `Malformed`, `UnknownKey`, `InvalidSignature`, `UnsupportedVersion`.
  La nube publica sus claves de confianza como JWK OKP en `GET /v1/public-keys`.
- **Estados de la clave de firma** (`licensing.signing_keys`, solo la pública):
  - `STANDBY`: reserva publicada; su privada está **fuera del servidor**, cifrada y fuera de línea; el POS ya la trae embebida.
  - `ACTIVE`: la única que firma hoy.
  - `RETIRED`: ya no firma; sigue verificando los tokens que emitió.
  - `REVOKED`: comprometida; deja de ser de confianza y de publicarse (sus tokens se rechazan en el check-in).
  Transiciones: `STANDBY → ACTIVE → RETIRED`; `REVOKED` desde reserva o retirada (la activa se reemplaza primero).
- **Clave privada en un archivo PEM** (PKCS#8) montado como secreto de solo lectura (`Licensing:Signing:PrivateKeyPath`; en Docker
  `/run/secrets/signing_key`, dueño UID 1654, permisos 400). Nunca en la BD ni en el repositorio. Al arrancar
  (`EnsureActiveSigningKeyCommand`), la clave del archivo **es** la activa: si es nueva se registra, si estaba en reserva se activa y
  la anterior pasa a `RETIRED`; si está retirada o revocada el servidor **no firma** (`LICENSE.SIGNING_UNAVAILABLE`, 503) y lo audita
  como crítico.
- Consola: `generate-signing-key` (nunca sobrescribe), `register-standby-key`, `revoke-signing-key`. Portal: publicar una reserva y
  revocar con el permiso `licensing.signing_key.manage` (solo superadministrador).
- **Caché de las claves de confianza** (`CachedTrustedSigningKeys`, vida de 1 minuto): el check-in y la liberación verifican el token
  contra las claves de confianza leídas de la BD. Un cambio hecho en el mismo proceso (portal o arranque) invalida el caché al
  instante; **una revocación por consola** (`docker compose exec app … revoke-signing-key`, otro proceso) tarda **hasta 1 minuto**
  en aplicarse en el servidor en marcha.
- Probado de punta a punta (`SigningKeyRotationTests`): reserva `STANDBY` publicada → el servidor arranca con ella → `ACTIVE` y la
  anterior `RETIRED` (sus tokens siguen renovándose); una clave revocada → `LICENSE.TOKEN_INVALID` en el check-in; el servidor con su
  clave revocada → `503 LICENSE.SIGNING_UNAVAILABLE`.

### Desviación respecto de la propuesta
La propuesta tenía solo `ACTIVE` y `RETIRED`. Se agregaron **`STANDBY`** (para publicar la reserva desde el día 1 sin que firme) y
**`REVOKED`** (para distinguir una clave comprometida de una retirada por rotación normal, que sigue siendo de confianza).

## Consecuencias
- ✅ Firmas cortas y rápidas; verificación offline; rotación sin reinstalar el POS si la reserva ya estaba embebida.
- ✅ Una clave filtrada se revoca y los tokens que firmó dejan de renovarse.
- ⚠️ Perder la privada activa **y** la reserva obliga a actualizar todos los POS con una clave pública nueva: la reserva debe tener
  dos copias cifradas en lugares distintos ([despliegue-nube.md](../despliegue-nube.md) §5 y §11).
- ⚠️ Un POS sin conexión no se entera de una revocación hasta su siguiente check-in (se acepta: el token caduca por sí solo).
