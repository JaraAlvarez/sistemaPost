-- =====================================================================================================
-- V2026.10.015 · sync · Datos que la nube ya no recibe y no usa (revisión S1 y hallazgo bajo, ronda 2).
--
-- Desde esta versión las tiendas no suben la cédula/NIT del cliente de cada venta ni el costo promedio de las existencias. Se quitan
-- también de lo ya recibido: el portal no los muestra y no hay por qué guardarlos.
-- =====================================================================================================

UPDATE sync.documents
SET data = data - 'customerIdentification'
WHERE kind = 'SALE' AND data ? 'customerIdentification';

UPDATE sync.documents
SET data = data - 'averageCost'
WHERE kind = 'STOCK' AND data ? 'averageCost';
