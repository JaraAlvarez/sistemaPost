# ADR-0015 · Ediciones Caja Única y Multicaja como única diferencia comercial

- **Estado:** Aceptada · 2026-09-28 · Fase 2 · Reemplaza los planes por módulos y el límite `max_terminals` del doc 09

## Contexto
Decisión del propietario: sin límite de cajas; dos instaladores; ningún módulo se habilita por plan.

## Decisión
- **Caja Única** (`node_role = ALL_IN_ONE`): un equipo con servidor, BD y una caja. Crear una segunda caja responde `409 LICENSE.EDITION_SINGLE_TERMINAL`.
- **Multicaja** (`node_role = STORE_SERVER`): cajas y equipos administrativos ilimitados en la LAN.
- Mismo esquema en ambas: pasar de una a otra solo cambia `node_role`.
- Todas las funcionalidades en ambas ediciones. La licencia (Fase 12) llevará `edition`, vigencia y dispositivos. Se elimina `permissions.requires_feature`.
- El estado `BLOCKED` de una caja es solo por seguridad.

## Consecuencias
- ✅ Modelo comercial y técnico simple; sin estados de caja "bloqueada por licencia".
