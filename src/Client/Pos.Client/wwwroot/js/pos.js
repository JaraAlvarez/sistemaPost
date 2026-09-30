// Ayudas del navegador para la interfaz (Fase 15): foco del escáner, teclas de función de la caja, descarga de archivos y preferencias
// de apariencia del equipo (docs/guia-diseno.md).
(function () {
    // Aplica el modo claro u oscuro guardado antes de que arranque Blazor (evita el destello blanco en la pantalla de carga).
    try {
        const tema = localStorage.getItem("pos.tema");
        const oscuro = tema === "oscuro" || (tema !== "claro" && window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
        document.documentElement.setAttribute("data-tema", oscuro ? "oscuro" : "claro");
    } catch (e) { /* sin almacenamiento */ }
})();

window.pos = {
    focus: function (id) {
        const element = document.getElementById(id);
        if (element) { element.focus(); element.select && element.select(); }
    },

    // Hay una ventana abierta (panel de la caja, diálogo o menú de MudBlazor): el foco no se le quita.
    _hayVentana: function () {
        return !!document.querySelector(".panel-fondo, .mud-overlay-dialog, .mud-dialog-container, .mud-popover-open");
    },

    _esCampo: function (element) {
        if (!element) { return false; }
        const tag = element.tagName;
        return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || element.isContentEditable;
    },

    // Teclas de la caja (D15-06): se envían a .NET y se evita la acción del navegador (F5 recargar cerraría la sesión, F10 menú…).
    // Con scannerId, el campo del escáner recupera el foco al tocar fuera de un campo y al teclear sin foco (el lector no pierde lecturas).
    registerKeys: function (dotnet, scannerId) {
        window.pos.unregisterKeys();
        const keys = ["F1", "F2", "F3", "F4", "F6", "F7", "F8", "F9", "F10", "Escape", "Delete"];
        window.pos._keys = function (e) {
            if (e.key === "F5" && !e.ctrlKey) { e.preventDefault(); return; }
            if (keys.includes(e.key)) {
                e.preventDefault();
                dotnet.invokeMethodAsync("OnKey", e.key);
                return;
            }
            if (scannerId && e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey
                && !window.pos._esCampo(document.activeElement) && !window.pos._hayVentana()) {
                window.pos.focus(scannerId);
            }
        };
        document.addEventListener("keydown", window.pos._keys);
        if (scannerId) {
            window.pos._click = function (e) {
                if (window.pos._esCampo(e.target)) { return; }
                setTimeout(function () {
                    if (!window.pos._esCampo(document.activeElement) && !window.pos._hayVentana()) { window.pos.focus(scannerId); }
                }, 0);
            };
            document.addEventListener("click", window.pos._click);
        }
    },
    unregisterKeys: function () {
        if (window.pos._keys) { document.removeEventListener("keydown", window.pos._keys); window.pos._keys = null; }
        if (window.pos._click) { document.removeEventListener("click", window.pos._click); window.pos._click = null; }
    },

    // Lleva a la vista la línea seleccionada de la venta.
    scrollIntoView: function (id) {
        const element = document.getElementById(id);
        if (element) { element.scrollIntoView({ block: "nearest" }); }
    },

    download: function (fileName, contentType, base64) {
        const link = document.createElement("a");
        link.href = "data:" + contentType + ";base64," + base64;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
    },
    hostname: function () { return window.location.hostname; },
    userAgent: function () { return navigator.userAgent; },

    // Preferencias del equipo (tema y menú).
    pref: {
        get: function (key) { try { return localStorage.getItem(key); } catch (e) { return null; } },
        set: function (key, value) { try { localStorage.setItem(key, value); } catch (e) { /* sin almacenamiento */ } },
        aplicarTema: function (oscuro) { document.documentElement.setAttribute("data-tema", oscuro ? "oscuro" : "claro"); }
    }
};
