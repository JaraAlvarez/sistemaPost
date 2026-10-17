# Fase 12-C · Pago en línea de las suscripciones (Wompi) — Informe

> Estado: **Implementada (apagada hasta configurar Wompi) — pendiente de tu prueba en el sandbox** · 2026-10-17 ·
> [propuesta](fase-12c-propuesta.md) · [ADR-0063](../adr/0063-pago-en-linea-de-suscripciones-con-wompi.md)

## Qué quedó

**En la nube (portal):**
- **Precios** (Pagos en línea → Precios): valor con IVA incluido por plan (Caja Única / Multicaja, mensual / anual) y fecha desde la que
  rige. Nace vacía: mientras un plan no tenga precio, el cliente no puede pagarlo en línea.
- **Enlace de pago** por suscripción (empresa → pestaña "Pagos en línea"): "Copiar enlace de pago" y QR para mandarlo por WhatsApp.
- **Página pública `/pagar/<código>`**: muestra solo el nombre del negocio, el plan, el vencimiento y el valor. El cliente elige 1, 3 o 6
  meses (o 1 año) y paga en la página de Wompi (Nequi, PSE, tarjeta, Bancolombia…). Al volver, `/pagar/resultado` muestra el estado.
- **Renovación automática**: solo con el aviso firmado de Wompi, confirmado consultando la transacción. Se verifica valor y moneda, y se
  usa la misma renovación que la manual (una orden renueva una sola vez). Valor distinto o suscripción suspendida/cancelada →
  **"Requiere revisión"** con alerta en el tablero.
- **Pagos** (lista y detalle con los avisos recibidos), "Consultar ahora", "Marcar revisado"; revisión automática de pendientes cada
  15 minutos; las órdenes sin respuesta en 24 h vencen.
- **Mi suscripción** para el rol Cliente: vigencia, historial y botón "Pagar".
- Todo queda en la auditoría.

**En la tienda (POS):**
- El reporte diario trae el enlace de pago cuando faltan 15 días o menos, o la licencia está en gracia o restringida.
- **Administración → Licencia**: tarjeta "Pagar mensualidad" con el botón, el QR para pagar desde el celular y "Ya pagué, verificar".
  La franja de avisos muestra un enlace "Pagar". La caja no cambia.

**Ingreso al portal:** nuevo diseño igual al de tutiendanueva.com (marco con degradado, tarjeta clara, campos en píldora, "Continuar con
Google" debajo), también en celular.

**Apagado por defecto:** sin las cuatro llaves de Wompi el portal dice "Pago en línea no configurado" y todo funciona como antes
(renovación manual).

## Validación

| Prueba | Resultado |
|---|---|
| Nube: pruebas unitarias de licencias (36 nuevas de pagos: firmas, estados, evento repetido, valor alterado, suspendida) | 120/120 |
| Nube: identidad del portal | 62/62 |
| Nube: base de datos (4 nuevas del esquema de pagos) | 30/30 |
| Nube: integración (5 nuevas: pago por enlace renueva una vez aunque el aviso llegue dos veces; firma inválida → 401; valor alterado o suspendida → revisión; "Consultar ahora" recupera un aviso perdido; el reporte trae el enlace a 10 días y no a 30 ni si está suspendida) | 54/54 |
| POS: infraestructura (enlace de pago: guardar, borrar, rechazar direcciones no http/https) | 100/100 |
| POS: integración de la licencia (enlace en el estado y en `/me` del dueño, no en el del cajero; QR en SVG; 403 cajero; 404 sin enlace) | 1/1 |
| POS: base de datos | 78/78 |
| Arquitectura | 23/23 |
| Navegador | Ingreso nuevo (escritorio y celular, con error de Google), Precios, Pagos y la pestaña de la empresa en modo "no configurado" |

## Lo que tienes que hacer tú

1. Abrir la cuenta de comercio en Wompi y cargar los **precios** en el portal.
2. Probar en el **sandbox**: llaves de prueba en `dotnet user-secrets` o en el `.env` del VPS (`WOMPI_*`, ver
   [despliegue-nube.md §20](../despliegue-nube.md)) y registrar la URL de eventos
   `https://businesspost.tutiendanueva.com/v1/payments/wompi/events` en el panel de Wompi.
3. Pagar con las tarjetas y el Nequi de prueba de Wompi (aprobado, rechazado, pendiente) y comprobar la renovación.

## Riesgos conocidos

- El ejemplo de la documentación de Wompi para la firma de los avisos no coincide con su propia cadena; se implementó el algoritmo
  documentado. **Confírmalo con un pago real del sandbox** antes de producción (si la firma no cuadra, el aviso se rechaza con 401 y la
  revisión cada 15 minutos aplica igual el pago consultando a Wompi).
- El formulario de `/pagar` limita a 10 órdenes pendientes por suscripción en 24 h (no por IP).
