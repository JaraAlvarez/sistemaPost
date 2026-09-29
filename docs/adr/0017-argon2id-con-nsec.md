# ADR-0017 · Argon2id con NSec (libsodium) y formato PHC

- **Estado:** Aceptada · 2026-09-28 · Fase 3 · Amplía ADR-0006 (se admite la licencia ISC)

## Contexto
RN-SEC-01 exige contraseñas y PIN con Argon2id. .NET 10 no trae Argon2 en la biblioteca base. Las implementaciones
administradas puras son lentas o poco mantenidas; libsodium es la referencia auditada. NSec (MIT) la envuelve e incluye el
binario nativo de libsodium (ISC), licencia equivalente a MIT/BSD-2, aceptada por el propietario.

## Decisión
- `NSec.Cryptography` 26.4.0 detrás de la abstracción `ISecretHasher` (`Argon2idSecretHasher` en `Pos.Infrastructure`).
- Parámetros ⚙️: contraseña 64 MiB / 3 iteraciones; PIN 19 MiB / 2 iteraciones; paralelismo 1; sal aleatoria de 16 bytes.
- Formato **PHC** (`$argon2id$v=19$m=…,t=…,p=1$sal$hash`): el hash lleva sus parámetros, así que se pueden subir en el
  futuro; si un hash quedó con parámetros viejos, se **rehace al iniciar sesión** (`SecretVerification.SucceededRehashNeeded`).
- Comparación en tiempo constante; un semáforo limita a 4 cálculos simultáneos para no agotar la memoria de la caja.
- Cuando el usuario no existe se calcula igual un hash ficticio (mismo tiempo de respuesta: no se revela si existe).

## Consecuencias
- ✅ Algoritmo recomendado por OWASP, con implementación auditada y rápida (< 500 ms en el hardware mínimo).
- ✅ Parámetros ajustables sin migrar datos.
- ⚠️ Dependencia nativa (libsodium) incluida en el paquete para win-x64; se valida en el instalador (Fase 13).
- ⚠️ Nuevas licencias registradas en [licencias-terceros.md](../licencias-terceros.md).
