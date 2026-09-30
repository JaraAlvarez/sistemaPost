// Ayudas del navegador para la interfaz (Fase 15): foco del escáner, teclas de función de la caja y descarga de archivos.
window.pos = {
    focus: function (id) {
        const element = document.getElementById(id);
        if (element) { element.focus(); element.select && element.select(); }
    },
    // Teclas de la caja (D15-06): se envían a .NET y se evita la acción del navegador (F5 recargar, F10 menú…).
    registerKeys: function (dotnet) {
        window.pos.unregisterKeys();
        window.pos._keys = function (e) {
            const keys = ["F2", "F4", "F8", "F9", "F10", "Escape", "Delete"];
            if (keys.includes(e.key)) {
                e.preventDefault();
                dotnet.invokeMethodAsync("OnKey", e.key);
            }
        };
        document.addEventListener("keydown", window.pos._keys);
    },
    unregisterKeys: function () {
        if (window.pos._keys) { document.removeEventListener("keydown", window.pos._keys); window.pos._keys = null; }
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
    userAgent: function () { return navigator.userAgent; }
};
