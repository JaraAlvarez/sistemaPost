// Copia un texto al portapapeles (enlace de pago, Fase 12-C). Módulo ES del mismo origen: lo permite la CSP (script-src 'self').
export async function copiar(texto) {
    try {
        await navigator.clipboard.writeText(texto);
        return true;
    } catch {
        return false;
    }
}
