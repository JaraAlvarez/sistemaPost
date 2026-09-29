-- =====================================================================================================
-- V2026.10.008 · catalog · Catálogo de productos: unidades, categorías, marcas, impuestos con vigencia, productos,
-- presentaciones, códigos de barras, reglas de báscula, listas de precios y precios con vigencia.
-- Diseño: docs/04-base-de-datos.md §H.5 con los cambios de docs/fases/fase-04-propuesta.md §4.1.
--
-- Todos los maestros son sincronizables (row_version) y se borran de forma lógica: la sincronización necesita
-- propagar también las eliminaciones. Las restricciones únicas son parciales (solo filas no borradas).
-- Las FK compuestas (x_id, company_id) garantizan que nada apunte a otra empresa.
-- =====================================================================================================

-- -----------------------------------------------------------------------------------------------------
-- Unidades de medida (datos globales de referencia). dian_code: código UN/ECE Rec. 20 que exige la factura
-- electrónica (94 = unidad, KGM = kilogramo…).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE ref.units_of_measure (
    code              varchar(10)  NOT NULL,
    name              varchar(40)  NOT NULL,
    dimension         varchar(10)  NOT NULL,
    dian_code         varchar(10)  NOT NULL,
    decimals_allowed  smallint     NOT NULL,
    sort_order        smallint     NOT NULL,
    CONSTRAINT pk_units_of_measure PRIMARY KEY (code),
    CONSTRAINT ck_units_of_measure__code CHECK (code ~ '^[A-Z0-9]{1,10}$'),
    CONSTRAINT ck_units_of_measure__dimension CHECK (dimension IN ('UNIT', 'WEIGHT', 'VOLUME', 'LENGTH')),
    CONSTRAINT ck_units_of_measure__decimals CHECK (decimals_allowed BETWEEN 0 AND 4)
);

CREATE TABLE ref.unit_conversions (
    from_unit  varchar(10)    NOT NULL,
    to_unit    varchar(10)    NOT NULL,
    factor     numeric(18,8)  NOT NULL,
    CONSTRAINT pk_unit_conversions PRIMARY KEY (from_unit, to_unit),
    CONSTRAINT fk_unit_conversions__from FOREIGN KEY (from_unit) REFERENCES ref.units_of_measure (code),
    CONSTRAINT fk_unit_conversions__to FOREIGN KEY (to_unit) REFERENCES ref.units_of_measure (code),
    CONSTRAINT ck_unit_conversions__factor CHECK (factor > 0),
    CONSTRAINT ck_unit_conversions__distinct CHECK (from_unit <> to_unit)
);

CREATE INDEX ix_unit_conversions__to_unit ON ref.unit_conversions (to_unit);

CREATE SCHEMA IF NOT EXISTS catalog;

-- -----------------------------------------------------------------------------------------------------
-- Categorías: árbol de hasta 4 niveles. path = "/id1/id2/…/" (ruta materializada para consultar subárboles).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.categories (
    id           uuid          NOT NULL,
    company_id   uuid          NOT NULL,
    parent_id    uuid,
    name         varchar(80)   NOT NULL,
    level        smallint      NOT NULL,
    path         varchar(200)  NOT NULL,
    sort_order   integer       NOT NULL DEFAULT 0,
    status       varchar(10)   NOT NULL,
    row_version  bigint        NOT NULL DEFAULT 1,
    created_at   timestamptz   NOT NULL,
    created_by   uuid          NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    deleted_at   timestamptz,
    deleted_by   uuid,
    CONSTRAINT pk_categories PRIMARY KEY (id),
    CONSTRAINT ux_categories__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_categories__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_categories__parent FOREIGN KEY (parent_id, company_id) REFERENCES catalog.categories (id, company_id),
    CONSTRAINT ck_categories__level CHECK (level BETWEEN 1 AND 4),
    CONSTRAINT ck_categories__root CHECK ((parent_id IS NULL) = (level = 1)),
    CONSTRAINT ck_categories__not_self CHECK (parent_id <> id),
    CONSTRAINT ck_categories__name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_categories__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_categories__row_version CHECK (row_version > 0),
    CONSTRAINT ck_categories__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_categories__parent_name ON catalog.categories (company_id, parent_id, lower(name)) NULLS NOT DISTINCT
    WHERE deleted_at IS NULL;
CREATE INDEX ix_categories__parent_id_company_id ON catalog.categories (parent_id, company_id);
CREATE INDEX ix_categories__path ON catalog.categories (path text_pattern_ops);

-- -----------------------------------------------------------------------------------------------------
-- Marcas
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.brands (
    id           uuid          NOT NULL,
    company_id   uuid          NOT NULL,
    name         varchar(80)   NOT NULL,
    status       varchar(10)   NOT NULL,
    row_version  bigint        NOT NULL DEFAULT 1,
    created_at   timestamptz   NOT NULL,
    created_by   uuid          NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    deleted_at   timestamptz,
    deleted_by   uuid,
    CONSTRAINT pk_brands PRIMARY KEY (id),
    CONSTRAINT ux_brands__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_brands__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_brands__name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_brands__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_brands__row_version CHECK (row_version > 0),
    CONSTRAINT ck_brands__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_brands__company_name ON catalog.brands (company_id, lower(name)) WHERE deleted_at IS NULL;

-- -----------------------------------------------------------------------------------------------------
-- Impuestos: la identidad del impuesto (IVA 19 %, INC bolsa…) y sus tarifas con vigencia en tax_rates.
-- Exento = tarifa 0 con derecho a descuento; excluido = no causa IVA (distinción que exige la DIAN).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.taxes (
    id           uuid          NOT NULL,
    company_id   uuid          NOT NULL,
    code         varchar(20)   NOT NULL,
    name         varchar(80)   NOT NULL,
    kind         varchar(30)   NOT NULL,
    calculation  varchar(20)   NOT NULL,
    is_exempt    boolean       NOT NULL DEFAULT false,
    is_excluded  boolean       NOT NULL DEFAULT false,
    dian_code    varchar(10),
    is_system    boolean       NOT NULL DEFAULT false,
    status       varchar(10)   NOT NULL,
    row_version  bigint        NOT NULL DEFAULT 1,
    created_at   timestamptz   NOT NULL,
    created_by   uuid          NOT NULL,
    updated_at   timestamptz,
    updated_by   uuid,
    CONSTRAINT pk_taxes PRIMARY KEY (id),
    CONSTRAINT ux_taxes__id_company UNIQUE (id, company_id),
    CONSTRAINT ux_taxes__company_code UNIQUE (company_id, code),
    CONSTRAINT fk_taxes__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_taxes__code CHECK (code ~ '^[A-Z0-9_]{2,20}$'),
    CONSTRAINT ck_taxes__kind CHECK (kind IN ('VAT', 'CONSUMPTION', 'BAG_CONSUMPTION', 'SUGARY_DRINKS', 'ULTRA_PROCESSED_FOOD', 'OTHER')),
    CONSTRAINT ck_taxes__calculation CHECK (calculation IN ('PERCENTAGE', 'FIXED_PER_UNIT')),
    CONSTRAINT ck_taxes__exempt_excluded CHECK (NOT (is_exempt AND is_excluded)),
    CONSTRAINT ck_taxes__zero_rated_vat CHECK (NOT (is_exempt OR is_excluded) OR (kind = 'VAT' AND calculation = 'PERCENTAGE')),
    CONSTRAINT ck_taxes__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_taxes__row_version CHECK (row_version > 0)
);

CREATE TABLE catalog.tax_rates (
    id            uuid           NOT NULL,
    company_id    uuid           NOT NULL,
    tax_id        uuid           NOT NULL,
    rate          numeric(9,4),
    fixed_amount  numeric(19,4),
    valid_from    date           NOT NULL,
    valid_to      date,
    row_version   bigint         NOT NULL DEFAULT 1,
    created_at    timestamptz    NOT NULL,
    created_by    uuid           NOT NULL,
    updated_at    timestamptz,
    updated_by    uuid,
    deleted_at    timestamptz,
    deleted_by    uuid,
    CONSTRAINT pk_tax_rates PRIMARY KEY (id),
    CONSTRAINT fk_tax_rates__tax FOREIGN KEY (tax_id, company_id) REFERENCES catalog.taxes (id, company_id),
    CONSTRAINT ck_tax_rates__value CHECK ((rate IS NULL) <> (fixed_amount IS NULL)),
    CONSTRAINT ck_tax_rates__rate CHECK (rate BETWEEN 0 AND 100),
    CONSTRAINT ck_tax_rates__fixed_amount CHECK (fixed_amount >= 0),
    CONSTRAINT ck_tax_rates__period CHECK (valid_to IS NULL OR valid_to > valid_from),
    CONSTRAINT ck_tax_rates__row_version CHECK (row_version > 0),
    CONSTRAINT ck_tax_rates__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL)),
    CONSTRAINT ex_tax_rates__no_overlap EXCLUDE USING gist (
        tax_id WITH =, daterange(valid_from, valid_to, '[)') WITH &&) WHERE (deleted_at IS NULL)
);

CREATE INDEX ix_tax_rates__tax_id_company_id ON catalog.tax_rates (tax_id, company_id);

-- -----------------------------------------------------------------------------------------------------
-- Productos. search_text: nombre, nombre corto y SKU en minúsculas y sin tildes (lo calcula el dominio);
-- índice de trigramas para buscar "cafe" y encontrar "Café Águila Roja".
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.products (
    id                       uuid           NOT NULL,
    company_id               uuid           NOT NULL,
    sku                      varchar(40)    NOT NULL,
    name                     varchar(200)   NOT NULL,
    short_name               varchar(40)    NOT NULL,
    description              varchar(1000),
    category_id              uuid           NOT NULL,
    brand_id                 uuid,
    base_unit_code           varchar(10)    NOT NULL,
    sale_mode                varchar(10)    NOT NULL,
    product_type             varchar(20)    NOT NULL,
    is_sold_by_scale         boolean        NOT NULL DEFAULT false,
    allows_decimal_quantity  boolean        NOT NULL DEFAULT false,
    allows_open_price        boolean        NOT NULL DEFAULT false,
    tracks_lots              boolean        NOT NULL DEFAULT false,
    tracks_expiry            boolean        NOT NULL DEFAULT false,
    plu_code                 varchar(6),
    net_content              numeric(12,4),
    net_content_unit         varchar(10),
    search_text              varchar(400)   NOT NULL,
    status                   varchar(15)    NOT NULL,
    row_version              bigint         NOT NULL DEFAULT 1,
    created_at               timestamptz    NOT NULL,
    created_by               uuid           NOT NULL,
    updated_at               timestamptz,
    updated_by               uuid,
    deleted_at               timestamptz,
    deleted_by               uuid,
    CONSTRAINT pk_products PRIMARY KEY (id),
    CONSTRAINT ux_products__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_products__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT fk_products__category FOREIGN KEY (category_id, company_id) REFERENCES catalog.categories (id, company_id),
    CONSTRAINT fk_products__brand FOREIGN KEY (brand_id, company_id) REFERENCES catalog.brands (id, company_id),
    CONSTRAINT fk_products__base_unit FOREIGN KEY (base_unit_code) REFERENCES ref.units_of_measure (code),
    CONSTRAINT fk_products__net_content_unit FOREIGN KEY (net_content_unit) REFERENCES ref.units_of_measure (code),
    CONSTRAINT ck_products__sku CHECK (sku ~ '^[A-Z0-9][A-Z0-9._/-]{0,39}$'),
    CONSTRAINT ck_products__name CHECK (btrim(name) <> '' AND btrim(short_name) <> ''),
    CONSTRAINT ck_products__sale_mode CHECK (sale_mode IN ('UNIT', 'WEIGHT', 'VOLUME')),
    CONSTRAINT ck_products__product_type CHECK (product_type IN ('STOCKABLE', 'SERVICE')),
    CONSTRAINT ck_products__decimals CHECK (sale_mode = 'UNIT' OR allows_decimal_quantity),
    CONSTRAINT ck_products__scale CHECK (NOT is_sold_by_scale OR (sale_mode = 'WEIGHT' AND plu_code IS NOT NULL)),
    CONSTRAINT ck_products__plu CHECK (plu_code ~ '^[0-9]{1,6}$'),
    CONSTRAINT ck_products__lots CHECK (product_type = 'STOCKABLE' OR NOT (tracks_lots OR tracks_expiry)),
    CONSTRAINT ck_products__net_content CHECK ((net_content IS NULL) = (net_content_unit IS NULL) AND (net_content IS NULL OR net_content > 0)),
    CONSTRAINT ck_products__status CHECK (status IN ('ACTIVE', 'INACTIVE', 'DISCONTINUED')),
    CONSTRAINT ck_products__row_version CHECK (row_version > 0),
    CONSTRAINT ck_products__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_products__company_sku ON catalog.products (company_id, sku) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_products__company_plu ON catalog.products (company_id, plu_code)
    WHERE plu_code IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX ix_products__category_id_company_id ON catalog.products (category_id, company_id);
CREATE INDEX ix_products__brand_id_company_id ON catalog.products (brand_id, company_id);
CREATE INDEX ix_products__base_unit_code ON catalog.products (base_unit_code);
CREATE INDEX ix_products__net_content_unit ON catalog.products (net_content_unit);
CREATE INDEX ix_products__search_text ON catalog.products USING gin (search_text gin_trgm_ops);

-- -----------------------------------------------------------------------------------------------------
-- Presentaciones: paquete, caja… con su factor en la unidad base del producto.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.product_packagings (
    id              uuid           NOT NULL,
    company_id      uuid           NOT NULL,
    product_id      uuid           NOT NULL,
    name            varchar(60)    NOT NULL,
    factor          numeric(18,4)  NOT NULL,
    is_sellable     boolean        NOT NULL DEFAULT true,
    is_purchasable  boolean        NOT NULL DEFAULT true,
    row_version     bigint         NOT NULL DEFAULT 1,
    created_at      timestamptz    NOT NULL,
    created_by      uuid           NOT NULL,
    updated_at      timestamptz,
    updated_by      uuid,
    deleted_at      timestamptz,
    deleted_by      uuid,
    CONSTRAINT pk_product_packagings PRIMARY KEY (id),
    CONSTRAINT ux_product_packagings__id_product UNIQUE (id, product_id),
    CONSTRAINT fk_product_packagings__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT ck_product_packagings__name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_product_packagings__factor CHECK (factor > 0),
    CONSTRAINT ck_product_packagings__row_version CHECK (row_version > 0),
    CONSTRAINT ck_product_packagings__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_product_packagings__product_name ON catalog.product_packagings (product_id, lower(name)) WHERE deleted_at IS NULL;
CREATE INDEX ix_product_packagings__product_id_company_id ON catalog.product_packagings (product_id, company_id);

-- -----------------------------------------------------------------------------------------------------
-- Códigos de barras. normalized_code: UPC-A se guarda como EAN-13 (con 0 inicial) para que el escáner encuentre el
-- producto se lea con o sin ese 0. Un código identifica UNA sola cosa en la empresa (D4-06).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.product_barcodes (
    id               uuid          NOT NULL,
    company_id       uuid          NOT NULL,
    product_id       uuid          NOT NULL,
    packaging_id     uuid,
    code             varchar(50)   NOT NULL,
    normalized_code  varchar(50)   NOT NULL,
    code_type        varchar(10)   NOT NULL,
    is_primary       boolean       NOT NULL DEFAULT false,
    row_version      bigint        NOT NULL DEFAULT 1,
    created_at       timestamptz   NOT NULL,
    created_by       uuid          NOT NULL,
    updated_at       timestamptz,
    updated_by       uuid,
    deleted_at       timestamptz,
    deleted_by       uuid,
    CONSTRAINT pk_product_barcodes PRIMARY KEY (id),
    CONSTRAINT fk_product_barcodes__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT fk_product_barcodes__packaging FOREIGN KEY (packaging_id, product_id) REFERENCES catalog.product_packagings (id, product_id),
    CONSTRAINT ck_product_barcodes__code CHECK (normalized_code ~ '^[A-Z0-9._/-]{1,50}$'),
    CONSTRAINT ck_product_barcodes__code_type CHECK (code_type IN ('EAN13', 'EAN8', 'UPCA', 'CODE128', 'INTERNAL')),
    CONSTRAINT ck_product_barcodes__row_version CHECK (row_version > 0),
    CONSTRAINT ck_product_barcodes__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_product_barcodes__company_code ON catalog.product_barcodes (company_id, normalized_code) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_product_barcodes__product_primary ON catalog.product_barcodes (product_id)
    WHERE is_primary AND deleted_at IS NULL;
CREATE INDEX ix_product_barcodes__product_id_company_id ON catalog.product_barcodes (product_id, company_id);
CREATE INDEX ix_product_barcodes__packaging_id_product_id ON catalog.product_barcodes (packaging_id, product_id);

-- -----------------------------------------------------------------------------------------------------
-- Impuestos del producto. fixed_amount: valor por unidad propio del producto (p. ej. bebidas azucaradas, que
-- dependen de su contenido y su azúcar); si es NULL se usa la tarifa vigente del impuesto.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.product_taxes (
    id            uuid           NOT NULL,
    company_id    uuid           NOT NULL,
    product_id    uuid           NOT NULL,
    tax_id        uuid           NOT NULL,
    fixed_amount  numeric(19,4),
    row_version   bigint         NOT NULL DEFAULT 1,
    created_at    timestamptz    NOT NULL,
    created_by    uuid           NOT NULL,
    updated_at    timestamptz,
    updated_by    uuid,
    deleted_at    timestamptz,
    deleted_by    uuid,
    CONSTRAINT pk_product_taxes PRIMARY KEY (id),
    CONSTRAINT fk_product_taxes__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT fk_product_taxes__tax FOREIGN KEY (tax_id, company_id) REFERENCES catalog.taxes (id, company_id),
    CONSTRAINT ck_product_taxes__fixed_amount CHECK (fixed_amount >= 0),
    CONSTRAINT ck_product_taxes__row_version CHECK (row_version > 0),
    CONSTRAINT ck_product_taxes__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_product_taxes__product_tax ON catalog.product_taxes (product_id, tax_id) WHERE deleted_at IS NULL;
CREATE INDEX ix_product_taxes__product_id_company_id ON catalog.product_taxes (product_id, company_id);
CREATE INDEX ix_product_taxes__tax_id_company_id ON catalog.product_taxes (tax_id, company_id);

-- -----------------------------------------------------------------------------------------------------
-- Reglas de códigos de peso/precio variable (etiquetas de báscula). Posiciones 1-based dentro del EAN-13;
-- el prefijo 29 queda reservado para los códigos internos generados por el sistema.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.variable_barcode_rules (
    id              uuid          NOT NULL,
    company_id      uuid          NOT NULL,
    prefix          varchar(2)    NOT NULL,
    content         varchar(10)   NOT NULL,
    plu_start       smallint      NOT NULL,
    plu_length      smallint      NOT NULL,
    value_start     smallint      NOT NULL,
    value_length    smallint      NOT NULL,
    value_decimals  smallint      NOT NULL,
    status          varchar(10)   NOT NULL,
    row_version     bigint        NOT NULL DEFAULT 1,
    created_at      timestamptz   NOT NULL,
    created_by      uuid          NOT NULL,
    updated_at      timestamptz,
    updated_by      uuid,
    deleted_at      timestamptz,
    deleted_by      uuid,
    CONSTRAINT pk_variable_barcode_rules PRIMARY KEY (id),
    CONSTRAINT fk_variable_barcode_rules__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_variable_barcode_rules__prefix CHECK (prefix ~ '^2[0-8]$'),
    CONSTRAINT ck_variable_barcode_rules__content CHECK (content IN ('WEIGHT', 'PRICE')),
    CONSTRAINT ck_variable_barcode_rules__positions CHECK (
        plu_start >= 3 AND plu_length BETWEEN 1 AND 6 AND plu_start + plu_length - 1 <= 12
        AND value_start >= 3 AND value_length BETWEEN 1 AND 7 AND value_start + value_length - 1 <= 12
        AND (plu_start + plu_length <= value_start OR value_start + value_length <= plu_start)),
    CONSTRAINT ck_variable_barcode_rules__decimals CHECK (value_decimals BETWEEN 0 AND 3 AND value_decimals < value_length),
    CONSTRAINT ck_variable_barcode_rules__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_variable_barcode_rules__row_version CHECK (row_version > 0),
    CONSTRAINT ck_variable_barcode_rules__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_variable_barcode_rules__company_prefix ON catalog.variable_barcode_rules (company_id, prefix)
    WHERE deleted_at IS NULL;

-- -----------------------------------------------------------------------------------------------------
-- Listas de precios y precios con vigencia (RN-CAT-05). Un precio nunca se sobrescribe: se cierra su vigencia y
-- se crea otro. branch_id NULL = todas las sucursales; packaging_id NULL = unidad base.
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.price_lists (
    id                  uuid          NOT NULL,
    company_id          uuid          NOT NULL,
    code                varchar(20)   NOT NULL,
    name                varchar(80)   NOT NULL,
    is_default          boolean       NOT NULL DEFAULT false,
    prices_include_tax  boolean       NOT NULL DEFAULT true,
    status              varchar(10)   NOT NULL,
    row_version         bigint        NOT NULL DEFAULT 1,
    created_at          timestamptz   NOT NULL,
    created_by          uuid          NOT NULL,
    updated_at          timestamptz,
    updated_by          uuid,
    deleted_at          timestamptz,
    deleted_by          uuid,
    CONSTRAINT pk_price_lists PRIMARY KEY (id),
    CONSTRAINT ux_price_lists__id_company UNIQUE (id, company_id),
    CONSTRAINT fk_price_lists__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_price_lists__code CHECK (code ~ '^[A-Z0-9_]{2,20}$'),
    CONSTRAINT ck_price_lists__default_active CHECK (NOT is_default OR status = 'ACTIVE'),
    CONSTRAINT ck_price_lists__status CHECK (status IN ('ACTIVE', 'INACTIVE')),
    CONSTRAINT ck_price_lists__row_version CHECK (row_version > 0),
    CONSTRAINT ck_price_lists__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL))
);

CREATE UNIQUE INDEX ux_price_lists__company_code ON catalog.price_lists (company_id, code) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_price_lists__company_default ON catalog.price_lists (company_id) WHERE is_default AND deleted_at IS NULL;

CREATE TABLE catalog.product_prices (
    id             uuid           NOT NULL,
    company_id     uuid           NOT NULL,
    price_list_id  uuid           NOT NULL,
    product_id     uuid           NOT NULL,
    packaging_id   uuid,
    branch_id      uuid,
    price          numeric(19,4)  NOT NULL,
    valid_from     timestamptz    NOT NULL,
    valid_to       timestamptz,
    row_version    bigint         NOT NULL DEFAULT 1,
    created_at     timestamptz    NOT NULL,
    created_by     uuid           NOT NULL,
    updated_at     timestamptz,
    updated_by     uuid,
    deleted_at     timestamptz,
    deleted_by     uuid,
    CONSTRAINT pk_product_prices PRIMARY KEY (id),
    CONSTRAINT fk_product_prices__price_list FOREIGN KEY (price_list_id, company_id) REFERENCES catalog.price_lists (id, company_id),
    CONSTRAINT fk_product_prices__product FOREIGN KEY (product_id, company_id) REFERENCES catalog.products (id, company_id),
    CONSTRAINT fk_product_prices__packaging FOREIGN KEY (packaging_id, product_id) REFERENCES catalog.product_packagings (id, product_id),
    CONSTRAINT fk_product_prices__branch FOREIGN KEY (branch_id, company_id) REFERENCES org.branches (id, company_id),
    CONSTRAINT ck_product_prices__price CHECK (price >= 0),
    CONSTRAINT ck_product_prices__period CHECK (valid_to IS NULL OR valid_to > valid_from),
    CONSTRAINT ck_product_prices__row_version CHECK (row_version > 0),
    CONSTRAINT ck_product_prices__deleted CHECK ((deleted_at IS NULL) = (deleted_by IS NULL)),
    CONSTRAINT ex_product_prices__no_overlap EXCLUDE USING gist (
        price_list_id WITH =,
        product_id WITH =,
        (COALESCE(packaging_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
        (COALESCE(branch_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
        tstzrange(valid_from, valid_to, '[)') WITH &&) WHERE (deleted_at IS NULL)
);

CREATE INDEX ix_product_prices__price_list_id_company_id ON catalog.product_prices (price_list_id, company_id);
CREATE INDEX ix_product_prices__product_id_company_id ON catalog.product_prices (product_id, company_id);
CREATE INDEX ix_product_prices__packaging_id_product_id ON catalog.product_prices (packaging_id, product_id);
CREATE INDEX ix_product_prices__branch_id_company_id ON catalog.product_prices (branch_id, company_id);
CREATE INDEX ix_product_prices__lookup ON catalog.product_prices (product_id, price_list_id, valid_from DESC) WHERE deleted_at IS NULL;

-- -----------------------------------------------------------------------------------------------------
-- Consecutivos de códigos internos por nodo (estado local, no se sincroniza): SKU "001-000123" y EAN-13 "29…".
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.code_sequences (
    node_id     uuid         NOT NULL,
    kind        varchar(10)  NOT NULL,
    last_value  bigint       NOT NULL DEFAULT 0,
    CONSTRAINT pk_code_sequences PRIMARY KEY (node_id, kind),
    CONSTRAINT fk_code_sequences__node FOREIGN KEY (node_id) REFERENCES org.nodes (id),
    CONSTRAINT ck_code_sequences__kind CHECK (kind IN ('SKU', 'BARCODE')),
    CONSTRAINT ck_code_sequences__last_value CHECK (last_value BETWEEN 0 AND 9999999)
);

-- -----------------------------------------------------------------------------------------------------
-- Importaciones: vista previa por fila y resultado (estado local del nodo; lo que se aplica viaja como cambios de
-- maestros).
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE catalog.import_batches (
    id              uuid          NOT NULL,
    company_id      uuid          NOT NULL,
    kind            varchar(10)   NOT NULL,
    file_name       varchar(200)  NOT NULL,
    status          varchar(10)   NOT NULL,
    total_rows      integer       NOT NULL,
    create_rows     integer       NOT NULL,
    update_rows     integer       NOT NULL,
    unchanged_rows  integer       NOT NULL,
    error_rows      integer       NOT NULL,
    created_at      timestamptz   NOT NULL,
    created_by      uuid          NOT NULL,
    applied_at      timestamptz,
    applied_by      uuid,
    CONSTRAINT pk_import_batches PRIMARY KEY (id),
    CONSTRAINT fk_import_batches__company FOREIGN KEY (company_id) REFERENCES org.companies (id),
    CONSTRAINT ck_import_batches__kind CHECK (kind IN ('PRODUCTS', 'PRICES')),
    CONSTRAINT ck_import_batches__status CHECK (status IN ('PREVIEW', 'APPLIED', 'DISCARDED')),
    CONSTRAINT ck_import_batches__applied CHECK ((status = 'APPLIED') = (applied_at IS NOT NULL))
);

CREATE INDEX ix_import_batches__company_id ON catalog.import_batches (company_id);

CREATE TABLE catalog.import_rows (
    id          uuid         NOT NULL,
    batch_id    uuid         NOT NULL,
    row_number  integer      NOT NULL,
    action      varchar(10)  NOT NULL,
    data        jsonb        NOT NULL,
    errors      jsonb        NOT NULL,
    CONSTRAINT pk_import_rows PRIMARY KEY (id),
    CONSTRAINT fk_import_rows__batch FOREIGN KEY (batch_id) REFERENCES catalog.import_batches (id) ON DELETE CASCADE,
    CONSTRAINT ck_import_rows__action CHECK (action IN ('CREATE', 'UPDATE', 'UNCHANGED', 'ERROR'))
);

CREATE INDEX ix_import_rows__batch_id ON catalog.import_rows (batch_id, row_number);
