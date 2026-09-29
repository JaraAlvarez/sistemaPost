# ADR-0029 · Sello de la auditoría en el reporte Z

- **Estado:** Aceptada · 2026-09-29 · Fase 6 · Decisión D6-08

## Contexto
La auditoría se sella por lotes con una cadena por nodo (ADR-0012). Quien controla la base de datos podría reescribir la
bitácora y volver a calcular toda la cadena; hace falta un **ancla externa** que no esté en la base de datos.

## Decisión
- Al confirmar el cierre se fuerza un ciclo del sellador (`IAuditAnchor.SealNowAsync`: sella lo emitido antes del horizonte
  seguro) y el reporte Z imprime el último sello del nodo: `SELLO #1234 · 7F3A-91C2-0B44-E1D8` (16 caracteres del hash).
- El código se comprueba con `GET /api/v1/audit/seals/{n}/check?code=…` o con
  `Pos.Server.Migrator verify-audit --seal N --code XXXX-XXXX-XXXX-XXXX`: si alguien reescribe la bitácora hasta ese sello, el
  hash cambia y deja de coincidir con el papel.
- El ancla cubre lo sellado hasta ese momento (lo ocurrido hasta aproximadamente un minuto antes del cierre); los eventos del
  propio cierre quedan en un sello posterior, protegido por la cadena y por el siguiente Z.

## Consecuencias
- ✅ Cada cierre deja en papel una huella verificable de la auditoría de toda la tienda.
- ⚠️ Si aún no existe ningún sello (instalación recién hecha), el Z imprime "SELLO PENDIENTE".
