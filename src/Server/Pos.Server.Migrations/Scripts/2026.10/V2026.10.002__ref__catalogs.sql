-- =====================================================================================================
-- V2026.10.002 · ref · Catálogos oficiales de país (solo lectura para pos_app).
-- Los datos se cargan con R__ref__seed_colombia.sql (fuentes: ISO 3166, ISO 4217, DANE DIVIPOLA, DIAN).
-- =====================================================================================================

CREATE TABLE ref.countries (
    code          char(2)       NOT NULL,
    iso3          char(3)       NOT NULL,
    numeric_code  char(3)       NOT NULL,
    name          varchar(100)  NOT NULL,
    phone_prefix  varchar(6),
    CONSTRAINT pk_countries PRIMARY KEY (code),
    CONSTRAINT ux_countries__iso3 UNIQUE (iso3),
    CONSTRAINT ck_countries__code CHECK (code ~ '^[A-Z]{2}$'),
    CONSTRAINT ck_countries__iso3 CHECK (iso3 ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_countries__numeric_code CHECK (numeric_code ~ '^[0-9]{3}$')
);

CREATE TABLE ref.currencies (
    code      char(3)      NOT NULL,
    name      varchar(80)  NOT NULL,
    decimals  smallint     NOT NULL,
    symbol    varchar(5)   NOT NULL,
    CONSTRAINT pk_currencies PRIMARY KEY (code),
    CONSTRAINT ck_currencies__code CHECK (code ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_currencies__decimals CHECK (decimals BETWEEN 0 AND 4)
);

CREATE TABLE ref.departments (
    code          varchar(5)    NOT NULL,
    country_code  char(2)       NOT NULL,
    name          varchar(100)  NOT NULL,
    CONSTRAINT pk_departments PRIMARY KEY (code),
    CONSTRAINT fk_departments__country FOREIGN KEY (country_code) REFERENCES ref.countries (code)
);

CREATE INDEX ix_departments__country_code ON ref.departments (country_code);

CREATE TABLE ref.municipalities (
    code             varchar(8)    NOT NULL,
    department_code  varchar(5)    NOT NULL,
    name             varchar(100)  NOT NULL,
    kind             varchar(30)   NOT NULL,
    CONSTRAINT pk_municipalities PRIMARY KEY (code),
    CONSTRAINT fk_municipalities__department FOREIGN KEY (department_code) REFERENCES ref.departments (code),
    CONSTRAINT ck_municipalities__kind CHECK (kind IN ('MUNICIPALITY', 'ISLAND', 'NON_MUNICIPALIZED_AREA'))
);

CREATE INDEX ix_municipalities__department_code ON ref.municipalities (department_code);

-- Tipos de documento de identidad con su código DIAN (anexo técnico de facturación electrónica).
CREATE TABLE ref.identification_types (
    code                  varchar(10)  NOT NULL,
    country_code          char(2)      NOT NULL,
    name                  varchar(80)  NOT NULL,
    fiscal_code           varchar(4)   NOT NULL,
    requires_check_digit  boolean      NOT NULL,
    allows_natural        boolean      NOT NULL,
    allows_legal          boolean      NOT NULL,
    CONSTRAINT pk_identification_types PRIMARY KEY (code),
    CONSTRAINT fk_identification_types__country FOREIGN KEY (country_code) REFERENCES ref.countries (code),
    CONSTRAINT ux_identification_types__fiscal_code UNIQUE (country_code, fiscal_code),
    CONSTRAINT ck_identification_types__person CHECK (allows_natural OR allows_legal)
);

CREATE INDEX ix_identification_types__country_code ON ref.identification_types (country_code);

CREATE TABLE ref.fiscal_responsibilities (
    code  varchar(10)   NOT NULL,
    name  varchar(120)  NOT NULL,
    CONSTRAINT pk_fiscal_responsibilities PRIMARY KEY (code)
);

CREATE TABLE ref.tax_regimes (
    code  varchar(10)   NOT NULL,
    name  varchar(120)  NOT NULL,
    CONSTRAINT pk_tax_regimes PRIMARY KEY (code)
);
