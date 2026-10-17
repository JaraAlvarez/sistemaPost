-- =====================================================================================================
-- V2026.10.041 · org · Báscula conectada a la caja (Fase 7-C parte B).
-- Diseño: docs/fases/fase-07c-propuesta.md.
--
-- Una báscula por caja. La interfaz de caja la lee y se la entrega al agente de la caja (PUT /scale/config), como la impresora.
-- Los campos del puerto serie vacíos toman el valor de fábrica del driver (Toledo 8217: 9600 7E1 por pedido "W"; CAS: 9600 8N1
-- continuo). brand, model y catalog_code quedan listos para el catálogo de básculas compatibles (parte D). Los tiempos de espera
-- de la caja y el permiso de digitar el peso son configurables (decisión del propietario).
-- =====================================================================================================

CREATE TABLE org.terminal_scales (
    id                   uuid          NOT NULL,
    company_id           uuid          NOT NULL,
    pos_terminal_id      uuid          NOT NULL,
    enabled              boolean       NOT NULL,
    driver               varchar(20)   NOT NULL,
    port                 varchar(10),
    baud_rate            integer,
    data_bits            integer,
    parity               varchar(4),
    stop_bits            integer,
    read_mode            varchar(10),
    request_command      varchar(40),
    timeout_ms           integer,
    decimals             integer,
    brand                varchar(60),
    model                varchar(60),
    catalog_code         varchar(40),
    stable_wait_seconds  integer       NOT NULL DEFAULT 3,
    empty_wait_seconds   integer       NOT NULL DEFAULT 5,
    allow_manual_weight  boolean       NOT NULL DEFAULT true,
    created_at           timestamptz   NOT NULL,
    created_by           uuid          NOT NULL,
    updated_at           timestamptz,
    updated_by           uuid,
    CONSTRAINT pk_terminal_scales PRIMARY KEY (id),
    CONSTRAINT ux_terminal_scales__terminal UNIQUE (pos_terminal_id),
    CONSTRAINT fk_terminal_scales__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_terminal_scales__terminal FOREIGN KEY (pos_terminal_id) REFERENCES org.pos_terminals (id),
    CONSTRAINT ck_terminal_scales__driver CHECK (driver IN ('TOLEDO_8217', 'CAS', 'GENERIC', 'SIMULATED')),
    CONSTRAINT ck_terminal_scales__port CHECK (CASE WHEN port IS NULL THEN driver = 'SIMULATED' OR NOT enabled ELSE port ~ '^COM[0-9]{1,3}$' END),
    CONSTRAINT ck_terminal_scales__serial CHECK (
        (baud_rate IS NULL OR baud_rate IN (1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200))
        AND (data_bits IS NULL OR data_bits IN (7, 8))
        AND (parity IS NULL OR parity IN ('NONE', 'EVEN', 'ODD'))
        AND (stop_bits IS NULL OR stop_bits IN (1, 2))
        AND (read_mode IS NULL OR read_mode IN ('REQUEST', 'CONTINUOUS'))
        AND (timeout_ms IS NULL OR timeout_ms BETWEEN 200 AND 5000)
        AND (decimals IS NULL OR decimals BETWEEN 0 AND 4)),
    CONSTRAINT ck_terminal_scales__waits CHECK (stable_wait_seconds BETWEEN 1 AND 15 AND empty_wait_seconds BETWEEN 1 AND 30)
);

CREATE INDEX ix_terminal_scales__company_id ON org.terminal_scales (company_id);
