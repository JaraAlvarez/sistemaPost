-- =====================================================================================================
-- V2026.10.019 · org · Periféricos de la caja (Fase 7, bloque 7.3).
-- Diseño: docs/fases/fase-07-propuesta.md §4.4 y D7-14.
--
-- La impresora de tiquetes de cada caja (el cajón va conectado a ella). Sin fila, la caja usa los valores por defecto
-- (transporte "archivo", 80 mm, PC850, corte automático, cajón en el pin 2). El agente de caja recibe esta configuración
-- de la interfaz de caja; la báscula y el visor se agregan en fases posteriores con otro kind.
-- =====================================================================================================

CREATE TABLE org.terminal_devices (
    id                uuid          NOT NULL,
    company_id        uuid          NOT NULL,
    pos_terminal_id   uuid          NOT NULL,
    kind              varchar(20)   NOT NULL,
    connection        varchar(20)   NOT NULL,
    address           varchar(200),
    paper_width_mm    integer       NOT NULL,
    code_page         varchar(10)   NOT NULL,
    auto_cut          boolean       NOT NULL,
    drawer_connected  boolean       NOT NULL,
    drawer_pin        varchar(5)    NOT NULL,
    created_at        timestamptz   NOT NULL,
    created_by        uuid          NOT NULL,
    updated_at        timestamptz,
    updated_by        uuid,
    CONSTRAINT pk_terminal_devices PRIMARY KEY (id),
    CONSTRAINT ux_terminal_devices__kind UNIQUE (pos_terminal_id, kind),
    CONSTRAINT fk_terminal_devices__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_terminal_devices__terminal FOREIGN KEY (pos_terminal_id) REFERENCES org.pos_terminals (id),
    CONSTRAINT ck_terminal_devices__kind CHECK (kind IN ('RECEIPT_PRINTER')),
    CONSTRAINT ck_terminal_devices__connection CHECK (connection IN ('FILE', 'NETWORK', 'WINDOWS_SPOOLER', 'SERIAL')),
    CONSTRAINT ck_terminal_devices__address CHECK (connection = 'FILE' OR address IS NOT NULL),
    CONSTRAINT ck_terminal_devices__paper CHECK (paper_width_mm IN (58, 80)),
    CONSTRAINT ck_terminal_devices__code_page CHECK (code_page IN ('PC850', 'ASCII')),
    CONSTRAINT ck_terminal_devices__drawer_pin CHECK (drawer_pin IN ('PIN2', 'PIN5'))
);

CREATE INDEX ix_terminal_devices__company_id ON org.terminal_devices (company_id);
