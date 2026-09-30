# ADR-0043 · Crédito (fiado) y puntos reservados para la Fase 8-B

- **Estado:** Aceptada · 2026-09-29 · Fase 8 · Decisiones D8-15 y D8-16 (preguntas 1 y 2 del propietario)

## Decisión
- **Diseño congelado como libros** (igual que la cartera por pagar, ADR-0026): cuenta por cobrar por venta a crédito con libro de
  solo inserción (`CHARGE`, `PAYMENT`, `CREDIT_NOTE`, `WRITE_OFF`, `VOID`), abonos en caja como movimiento que sí afecta el cajón,
  cupo y plazo por cliente; libro de puntos (`EARN`, `REDEEM`, `EXPIRE`, `ADJUST`, `REVERSAL`) con acumulación por el evento
  `sales.sale_completed.v1` y redención como medio de pago. Los puntos no son retroactivos.
- **Enganches listos hoy:** columnas `credit_status`, `credit_limit`, `credit_term_days` y `loyalty_status` del cliente (CHECK: solo
  `NONE`); tipos de medio de pago `CUSTOMER_CREDIT` y `LOYALTY_POINTS` en el CHECK (migración 026) pero la API rechaza crearlos
  (`CASH.PAYMENT_KIND_NOT_AVAILABLE`); contratos `ICustomerCreditGate` e `ILoyaltyProgram` con implementación nula que la venta ya
  consulta; finalidad `LOYALTY` en la autorización de datos.

## Consecuencias
- Activarlos en la 8-B no cambia el esquema de ventas ni de caja. Antes hay que cerrar con el contador la forma de pago crédito en la
  factura electrónica, el deterioro de cartera, los intereses y el tratamiento tributario del canje de puntos.
