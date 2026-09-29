-- =====================================================================================================
-- R__ref__units_of_measure · Unidades de medida y conversiones (repetible e idempotente).
-- dian_code: código UN/ECE Recomendación 20 que usa la factura electrónica (se verificará contra el anexo técnico
-- vigente con Factus en la Fase 11-B). Nunca borra filas: los códigos pueden estar referenciados.
-- =====================================================================================================

INSERT INTO ref.units_of_measure (code, name, dimension, dian_code, decimals_allowed, sort_order) VALUES
    ('UND', 'Unidad', 'UNIT', '94', 0, 1),
    ('KG', 'Kilogramo', 'WEIGHT', 'KGM', 3, 10),
    ('G', 'Gramo', 'WEIGHT', 'GRM', 0, 11),
    ('LB', 'Libra', 'WEIGHT', 'LBR', 3, 12),
    ('L', 'Litro', 'VOLUME', 'LTR', 3, 20),
    ('ML', 'Mililitro', 'VOLUME', 'MLT', 0, 21),
    ('GAL', 'Galón', 'VOLUME', 'GLL', 3, 22),
    ('M', 'Metro', 'LENGTH', 'MTR', 3, 30),
    ('CM', 'Centímetro', 'LENGTH', 'CMT', 1, 31)
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, dimension = EXCLUDED.dimension, dian_code = EXCLUDED.dian_code,
    decimals_allowed = EXCLUDED.decimals_allowed, sort_order = EXCLUDED.sort_order;

INSERT INTO ref.unit_conversions (from_unit, to_unit, factor) VALUES
    ('KG', 'G', 1000),
    ('G', 'KG', 0.001),
    ('LB', 'G', 453.59237),
    ('G', 'LB', 0.00220462),
    ('LB', 'KG', 0.45359237),
    ('KG', 'LB', 2.20462262),
    ('L', 'ML', 1000),
    ('ML', 'L', 0.001),
    ('GAL', 'L', 3.78541178),
    ('L', 'GAL', 0.26417205),
    ('M', 'CM', 100),
    ('CM', 'M', 0.01)
ON CONFLICT (from_unit, to_unit) DO UPDATE SET factor = EXCLUDED.factor;
