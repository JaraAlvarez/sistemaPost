-- =====================================================================================================
-- V2026.10.049 · sales · Retorno a cero de la báscula en el servidor y lecturas firmadas usadas (Fase 7-C, segunda revisión, D1 y D3).
-- Diseño: docs/fases/fase-07c-propuesta.md ("Segunda revisión de la báscula").
--
--   · scale_interlocks: una fila por caja con la última lectura aceptada para una línea (hora del RELOJ DEL AGENTE) y si la siguiente
--     pesada exige ver la báscula vacía. Vive por caja: recargar la pantalla o empezar otra venta no la borra. Eliminar la línea de la
--     última pesada la libera.
--   · scale_readings: cada lectura firmada usada en una línea, con el texto y la firma tal como llegaron (auditoría). La clave
--     primaria es el identificador de la lectura: la misma lectura no se acepta dos veces.
-- =====================================================================================================

CREATE TABLE sales.scale_interlocks (
    pos_terminal_id   uuid           NOT NULL,
    company_id        uuid           NOT NULL,
    last_reading_id   uuid,
    last_reading_at   timestamptz,
    last_gross_kg     numeric(12, 3),
    last_sale_id      uuid,
    last_sale_line_id uuid,
    awaiting_empty    boolean        NOT NULL DEFAULT false,
    updated_at        timestamptz    NOT NULL,
    CONSTRAINT pk_scale_interlocks PRIMARY KEY (pos_terminal_id),
    CONSTRAINT fk_scale_interlocks__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_scale_interlocks__terminal FOREIGN KEY (pos_terminal_id) REFERENCES org.pos_terminals (id),
    CONSTRAINT ck_scale_interlocks__awaiting CHECK (NOT awaiting_empty OR (last_reading_at IS NOT NULL AND last_sale_line_id IS NOT NULL))
);
CREATE INDEX ix_scale_interlocks__company ON sales.scale_interlocks (company_id);

CREATE TABLE sales.scale_readings (
    reading_id      uuid           NOT NULL,
    company_id      uuid           NOT NULL,
    pos_terminal_id uuid           NOT NULL,
    sale_id         uuid           NOT NULL,
    sale_line_id    uuid           NOT NULL,
    key_id          uuid           NOT NULL,
    agent_at        timestamptz    NOT NULL,
    gross_kg        numeric(12, 3) NOT NULL,
    tare_kg         numeric(12, 3) NOT NULL,
    net_kg          numeric(12, 3) NOT NULL,
    payload         varchar(400)   NOT NULL,
    signature       varchar(200)   NOT NULL,
    recorded_at     timestamptz    NOT NULL,
    CONSTRAINT pk_scale_readings PRIMARY KEY (reading_id),
    CONSTRAINT fk_scale_readings__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_scale_readings__terminal FOREIGN KEY (pos_terminal_id) REFERENCES org.pos_terminals (id),
    CONSTRAINT ck_scale_readings__weights CHECK (gross_kg > 0 AND tare_kg >= 0 AND net_kg > 0)
);
CREATE INDEX ix_scale_readings__company ON sales.scale_readings (company_id);
CREATE INDEX ix_scale_readings__terminal ON sales.scale_readings (pos_terminal_id, agent_at);
CREATE INDEX ix_scale_readings__sale_line ON sales.scale_readings (sale_line_id);
