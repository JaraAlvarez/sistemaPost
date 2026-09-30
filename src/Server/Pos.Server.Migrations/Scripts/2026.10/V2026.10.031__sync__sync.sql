-- =====================================================================================================
-- V2026.10.031 · sync · Sincronización tienda → nube (Fase 16). Diseño: docs/fases/fase-16-propuesta.md D16-01/D16-02.
--
--   · cursors: por tipo de dato, hasta dónde confirmó la nube (acked_*) y hasta dónde se exportó en paquetes (exported_*).
--     El cursor es (marca de tiempo, id) para desempatar filas del mismo instante.
--   · batches: cada lote enviado o exportado, con el acuse de la nube. Solo inserción y cambio de estado.
-- No se sincronizan (son del nodo).
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS sync;

CREATE TABLE sync.cursors (
    kind          varchar(20)   NOT NULL,
    acked_ts      timestamptz   NOT NULL DEFAULT '-infinity',
    acked_id      uuid          NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    exported_ts   timestamptz   NOT NULL DEFAULT '-infinity',
    exported_id   uuid          NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    updated_at    timestamptz   NOT NULL,
    CONSTRAINT pk_cursors PRIMARY KEY (kind),
    CONSTRAINT ck_cursors__kind CHECK (kind IN ('SALE', 'CASH_SESSION', 'STOCK', 'PRODUCT'))
);

COMMENT ON TABLE sync.cursors IS 'Hasta dónde subió cada tipo de dato a la nube (acuse) y hasta dónde se exportó en paquetes .possync.';

CREATE TABLE sync.batches (
    id          uuid          NOT NULL,
    created_at  timestamptz   NOT NULL,
    via         varchar(10)   NOT NULL,
    items       integer       NOT NULL,
    status      varchar(10)   NOT NULL,
    applied     integer,
    ignored     integer,
    error       varchar(500),
    acked_at    timestamptz,
    CONSTRAINT pk_batches PRIMARY KEY (id),
    CONSTRAINT ck_batches__via CHECK (via IN ('ONLINE', 'FILE')),
    CONSTRAINT ck_batches__status CHECK (status IN ('ACKED', 'FAILED', 'EXPORTED'))
);

CREATE INDEX ix_batches__created ON sync.batches (created_at DESC);

COMMENT ON TABLE sync.batches IS 'Lotes enviados a la nube o exportados en paquetes, con el acuse.';
