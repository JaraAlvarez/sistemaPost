-- =====================================================================================================
-- V2026.10.010 · parties · Terceros: personas naturales y jurídicas con su identificación y datos fiscales, y sus
-- contactos. Proveedor (Fase 5) y cliente (Fase 8) son ROLES de un tercero (D5-01): los datos fiscales se guardan
-- una sola vez. Diseño: docs/fases/fase-05-propuesta.md §4.1.
--
-- Identificación única por empresa (tipo + número) entre los terceros vivos; el mismo tercero creado sin conexión en
-- dos tiendas se fusiona en la nube (merged_into_id apunta al que se conserva, D5-02).
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS parties;

CREATE TABLE parties.parties (
    id                       uuid          NOT NULL,
    company_id               uuid          NOT NULL,
    person_type              varchar(10)   NOT NULL,
    identification_type      varchar(10)   NOT NULL,
    identification_number    varchar(30)   NOT NULL,
    check_digit              varchar(1),
    legal_name               varchar(200),
    first_names              varchar(100),
    last_names               varchar(100),
    trade_name               varchar(200),
    tax_regime               varchar(10)   NOT NULL,
    fiscal_responsibilities  varchar(100)  NOT NULL,
    email                    varchar(200),
    phone                    varchar(30),
    address                  varchar(250),
    municipality_code        varchar(8),
    notes                    varchar(500),
    search_text              varchar(500)  NOT NULL,
    is_system                boolean       NOT NULL DEFAULT false,
    merged_into_id           uuid,
    status                   varchar(10)   NOT NULL,
    row_version              bigint        NOT NULL DEFAULT 1,
    created_at               timestamptz   NOT NULL,
    created_by               uuid          NOT NULL,
    updated_at               timestamptz,
    updated_by               uuid,
    deleted_at               timestamptz,
    deleted_by               uuid,
    CONSTRAINT pk_parties PRIMARY KEY (id),
    CONSTRAINT ux_parties__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_parties__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_parties__identification_type FOREIGN KEY (identification_type) REFERENCES ref.identification_types (code),
    CONSTRAINT fk_parties__tax_regime FOREIGN KEY (tax_regime) REFERENCES ref.tax_regimes (code),
    CONSTRAINT fk_parties__municipality FOREIGN KEY (municipality_code) REFERENCES ref.municipalities (code),
    CONSTRAINT fk_parties__merged_into FOREIGN KEY (merged_into_id, company_id) REFERENCES parties.parties (id, company_id),
    CONSTRAINT ck_parties__person_type CHECK (person_type IN ('LEGAL', 'NATURAL')),
    CONSTRAINT ck_parties__identification_number CHECK (identification_number ~ '^[0-9A-Za-z-]{1,30}$'),
    CONSTRAINT ck_parties__check_digit CHECK (check_digit ~ '^[0-9]$'),
    CONSTRAINT ck_parties__nit_check_digit CHECK (identification_type <> 'NIT' OR check_digit IS NOT NULL),
    CONSTRAINT ck_parties__names CHECK (
        (person_type = 'LEGAL' AND legal_name IS NOT NULL AND btrim(legal_name) <> '')
        OR (person_type = 'NATURAL' AND first_names IS NOT NULL AND btrim(first_names) <> '' AND last_names IS NOT NULL)),
    CONSTRAINT ck_parties__status CHECK (status IN ('ACTIVE', 'INACTIVE', 'MERGED')),
    CONSTRAINT ck_parties__merged CHECK ((status = 'MERGED') = (merged_into_id IS NOT NULL)),
    CONSTRAINT ck_parties__not_self CHECK (merged_into_id <> id),
    CONSTRAINT ck_parties__row_version CHECK (row_version > 0),
    CONSTRAINT ck_parties__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_parties__identification ON parties.parties (company_id, identification_type, identification_number)
    WHERE deleted_at IS NULL AND merged_into_id IS NULL;
CREATE INDEX ix_parties__identification_type ON parties.parties (identification_type);
CREATE INDEX ix_parties__tax_regime ON parties.parties (tax_regime);
CREATE INDEX ix_parties__municipality_code ON parties.parties (municipality_code);
CREATE INDEX ix_parties__merged_into_id_company_id ON parties.parties (merged_into_id, company_id);
CREATE INDEX ix_parties__search_text ON parties.parties USING gin (search_text gin_trgm_ops);

-- Contactos del tercero (vendedor, cartera…). Hijos del tercero: se reemplazan con él.
CREATE TABLE parties.party_contacts (
    id          uuid          NOT NULL,
    party_id    uuid          NOT NULL,
    name        varchar(120)  NOT NULL,
    position    varchar(80),
    phone       varchar(30),
    email       varchar(200),
    is_primary  boolean       NOT NULL DEFAULT false,
    CONSTRAINT pk_party_contacts PRIMARY KEY (id),
    CONSTRAINT fk_party_contacts__party FOREIGN KEY (party_id) REFERENCES parties.parties (id) ON DELETE CASCADE,
    CONSTRAINT ck_party_contacts__name CHECK (btrim(name) <> '')
);

CREATE INDEX ix_party_contacts__party_id ON parties.party_contacts (party_id);
CREATE UNIQUE INDEX ux_party_contacts__primary ON parties.party_contacts (party_id) WHERE is_primary;
