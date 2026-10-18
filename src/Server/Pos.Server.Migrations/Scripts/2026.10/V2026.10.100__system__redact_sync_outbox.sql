-- =====================================================================================================
-- V2026.10.100 · system · Outbox SYNC sin secretos ni datos personales en claro (revisión R6,
-- docs/fases/fase-sync-pagos-endurecimiento.md §1).
--
-- Hasta ahora cada cambio de un maestro sincronizable dejaba en system.outbox_messages (destino SYNC) TODOS sus campos, incluidos
-- los hashes de contraseña y de PIN y los datos personales. El outbox no se purga y va en cada backup. Desde esta versión el
-- interceptor ya no los escribe ([Sensitive] fuera, [PersonalData] enmascarado); este script limpia lo que ya estaba guardado:
--   · quita password_hash, pin_hash y credential_hash de cualquier mensaje sync.entity_changed.v1;
--   · enmascara email (inicial + dominio), phone (últimos 4 dígitos), address y notes ("(registrado)") de identity.users y
--     parties.parties, igual que la auditoría (D10-07).
-- No borra mensajes: nadie los consume todavía (la nube lee las tablas) y el diseño D4-09 los reserva para los maestros.
-- =====================================================================================================

-- 1. Secretos: fuera, en cualquier entidad.
UPDATE system.outbox_messages
SET payload = jsonb_set(payload, '{changes}', (payload -> 'changes') - 'password_hash' - 'pin_hash' - 'credential_hash')
WHERE type = 'sync.entity_changed.v1'
  AND jsonb_typeof(payload -> 'changes') = 'object'
  AND (payload -> 'changes') ?| ARRAY['password_hash', 'pin_hash', 'credential_hash'];

-- 2. Datos personales de usuarios y terceros: enmascarados como en la auditoría (texto vacío o no texto → null).
UPDATE system.outbox_messages m
SET payload = jsonb_set(m.payload, '{changes}', (m.payload -> 'changes') || masked.masked_values)
FROM (
    SELECT o.id, jsonb_object_agg(f.key,
        CASE
            WHEN jsonb_typeof(f.value) <> 'string' OR btrim(f.value #>> '{}') = '' THEN 'null'::jsonb
            WHEN f.key = 'email' AND position('@' IN (f.value #>> '{}')) > 1
                THEN to_jsonb(left(f.value #>> '{}', 1) || '***' || substr(f.value #>> '{}', position('@' IN (f.value #>> '{}'))))
            WHEN f.key = 'email' THEN '"***"'::jsonb
            WHEN f.key = 'phone' AND length(regexp_replace(f.value #>> '{}', '[^0-9]', '', 'g')) > 4
                THEN to_jsonb('***' || right(regexp_replace(f.value #>> '{}', '[^0-9]', '', 'g'), 4))
            WHEN f.key = 'phone' THEN '"***"'::jsonb
            ELSE '"(registrado)"'::jsonb
        END) AS masked_values
    FROM system.outbox_messages o
    CROSS JOIN LATERAL jsonb_each(o.payload -> 'changes') AS f(key, value)
    WHERE o.type = 'sync.entity_changed.v1'
      AND o.payload ->> 'entity' IN ('identity.users', 'parties.parties')
      AND jsonb_typeof(o.payload -> 'changes') = 'object'
      AND f.key IN ('email', 'phone', 'address', 'notes')
    GROUP BY o.id) AS masked
WHERE m.id = masked.id;
