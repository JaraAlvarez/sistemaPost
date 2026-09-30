-- =====================================================================================================
-- R__ref__banks · Bancos y entidades de depósito de Colombia (repetible e idempotente). Tabla: V2026.10.025.
-- code = código de compensación ACH (Superintendencia Financiera / ACH Colombia). Verificar contra el listado vigente
-- antes de cada versión: las entidades nuevas se agregan aquí; nunca se borran filas (pueden estar referenciadas):
-- las que desaparecen quedan is_active = false.
-- =====================================================================================================

INSERT INTO ref.banks (code, country_code, name, is_active, sort_order) VALUES
    ('1007', 'CO', 'Bancolombia', true, 10),
    ('1051', 'CO', 'Davivienda', true, 20),
    ('1001', 'CO', 'Banco de Bogotá', true, 30),
    ('1013', 'CO', 'BBVA Colombia', true, 40),
    ('1023', 'CO', 'Banco de Occidente', true, 50),
    ('1002', 'CO', 'Banco Popular', true, 60),
    ('1052', 'CO', 'Banco AV Villas', true, 70),
    ('1032', 'CO', 'Banco Caja Social', true, 80),
    ('1019', 'CO', 'Scotiabank Colpatria', true, 90),
    ('1040', 'CO', 'Banco Agrario de Colombia', true, 100),
    ('1006', 'CO', 'Banco Itaú', true, 110),
    ('1012', 'CO', 'Banco GNB Sudameris', true, 120),
    ('1009', 'CO', 'Citibank', true, 130),
    ('1060', 'CO', 'Banco Pichincha', true, 140),
    ('1061', 'CO', 'Bancoomeva', true, 150),
    ('1062', 'CO', 'Banco Falabella', true, 160),
    ('1063', 'CO', 'Banco Finandina', true, 170),
    ('1065', 'CO', 'Banco Santander de Negocios Colombia', true, 180),
    ('1066', 'CO', 'Banco Cooperativo Coopcentral', true, 190),
    ('1059', 'CO', 'Bancamía', true, 200),
    ('1053', 'CO', 'Banco W', true, 210),
    ('1047', 'CO', 'Banco Mundo Mujer', true, 220),
    ('1069', 'CO', 'Banco Serfinanza', true, 230),
    ('1070', 'CO', 'Lulo Bank', true, 240),
    ('1507', 'CO', 'Nequi', true, 300),
    ('1551', 'CO', 'Daviplata', true, 310),
    ('1801', 'CO', 'Movii', true, 320),
    ('1809', 'CO', 'Nu Colombia', true, 330)
ON CONFLICT (code) DO UPDATE SET country_code = EXCLUDED.country_code, name = EXCLUDED.name, is_active = EXCLUDED.is_active,
    sort_order = EXCLUDED.sort_order;
