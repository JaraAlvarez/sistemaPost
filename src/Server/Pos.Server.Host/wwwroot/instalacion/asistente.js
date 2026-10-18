// Asistente inicial mínimo (Fase 13, D13-05) de /instalacion. Archivo aparte: la política de seguridad de la página no admite scripts
// en línea. Los textos que vienen del servidor se ponen con textContent y elementos del DOM (nunca como HTML).
"use strict";
const api = "/api/v1";
const claveToken = "pos.setup.token";
let token = sessionStorage.getItem(claveToken);
const $ = (id) => document.getElementById(id);

function show(step) {
  for (const s of ["empresa", "ingreso", "licencia", "recuperacion", "listo"]) $(s).classList.toggle("hidden", s !== step);
  const order = ["empresa", "licencia", "recuperacion", "listo"];
  const at = order.indexOf(step === "ingreso" ? "licencia" : step);
  document.querySelectorAll("#steps li").forEach((li, i) => { li.classList.toggle("on", i === at); li.classList.toggle("done", i < at); });
}

function fail(message) { const e = $("error"); e.textContent = message; e.classList.toggle("hidden", !message); }

function olvidarToken() {
  token = null;
  sessionStorage.removeItem(claveToken);
}

async function call(method, path, body) {
  fail("");
  const headers = { "Content-Type": "application/json" };
  if (token) headers.Authorization = "Bearer " + token;
  const response = await fetch(api + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!response.ok) {
    const fields = data && data.errors ? " " + Object.values(data.errors).flat().join(" ") : "";
    const error = new Error(((data && (data.detail || data.title)) || "Error " + response.status) + fields);
    error.status = response.status;
    error.code = data && data.code;
    if (response.status === 401 && token && path !== "/auth/login") {
      // Sesión vencida o de otra instalación (token viejo en esta pestaña): se pide entrar de nuevo en vez de quedar atascado.
      olvidarToken();
      show("ingreso");
      error.message = "Su sesión venció. Entre de nuevo con el usuario y la contraseña del propietario.";
    }
    throw error;
  }
  return data;
}

async function login(username, password) {
  const result = await call("POST", "/auth/login", { username, password });
  token = result.token;
  sessionStorage.setItem(claveToken, token);
}

function opciones(select, items) {
  select.replaceChildren(...items.map((item) => {
    const option = document.createElement("option");
    option.value = item.code;
    option.textContent = item.name;
    return option;
  }));
}

async function loadCatalogs() {
  const [types, regimes] = await Promise.all([call("GET", "/reference/identification-types"), call("GET", "/reference/tax-regimes")]);
  opciones($("idTypes"), types.filter((t) => t.allowsLegal || t.allowsNatural));
  $("idTypes").value = "NIT";
  opciones($("regimes"), regimes);
}

let municipios = [];
$("municipioBuscar").addEventListener("input", async (e) => {
  const q = e.target.value.trim();
  if (q.length < 3) return;
  municipios = await call("GET", "/reference/municipalities?search=" + encodeURIComponent(q));
  $("municipios").replaceChildren(...municipios.slice(0, 30).map((m) => {
    const option = document.createElement("option");
    option.value = `${m.name} (${m.code})`;
    return option;
  }));
});

// Mostrar u ocultar la contraseña (las dos casillas) para revisar que se escribió bien.
$("verContrasena").addEventListener("change", (e) => {
  for (const id of ["password", "passwordConfirm"]) $(id).type = e.target.checked ? "text" : "password";
});

// La configuración se guardó pero no se pudo entrar (p. ej. demasiados intentos seguidos): se pide entrar, no volver a enviar.
function configuradaSinIngreso(usuario, motivo) {
  show("ingreso");
  const form = $("form-ingreso");
  form.elements.username.value = usuario || "";
  form.elements.password.value = "";
  fail(`La empresa ya quedó configurada, pero no pudimos entrar automáticamente (${motivo}). Escriba la contraseña del propietario y pulse "Entrar". Si dice que hay demasiados intentos, espere un minuto.`);
}

$("form-empresa").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = Object.fromEntries(new FormData(e.target));
  const code = (/\((\d{5})\)$/.exec($("municipioBuscar").value) || [])[1];
  if (!code) { fail("Elija el municipio de la lista."); return; }
  if (f.password !== f.passwordConfirm) { fail("Las dos contraseñas no coinciden. Escríbalas de nuevo (puede marcar \"Mostrar la contraseña\")."); return; }
  try {
    await call("POST", "/setup", {
      company: { legalName: f.legalName, tradeName: f.tradeName, personType: f.personType, identificationType: f.identificationType,
        identificationNumber: f.identificationNumber, checkDigit: f.checkDigit || null, taxRegime: f.taxRegime, fiscalResponsibilities: ["R-99-PN"],
        municipalityCode: code, address: f.address, phone: f.phone || null, email: f.email || null },
      branch: { code: f.branchCode, name: f.branchName, municipalityCode: code, address: f.address, phone: f.phone || null },
      owner: { username: f.username, displayName: f.displayName, password: f.password, email: f.ownerEmail || null, posCode: f.posCode || null, pin: f.pin || null },
      terminal: { code: f.terminalCode, name: f.terminalName },
      businessProfile: f.businessProfile || "SUPERMARKET",
    });
  } catch (err) {
    if (err.code === "SETUP.ALREADY_COMPLETED") { configuradaSinIngreso(f.username, "la configuración ya se había guardado"); return; }
    fail(err.message);
    return;
  }

  try {
    await login(f.username, f.password);
    show("licencia");
  } catch (err) { configuradaSinIngreso(f.username, err.message); }
});

$("form-ingreso").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = Object.fromEntries(new FormData(e.target));
  try { await login(f.username, f.password); show("licencia"); } catch (err) { fail(err.message); }
});

$("form-licencia").addEventListener("submit", async (e) => {
  e.preventDefault();
  const key = new FormData(e.target).get("licenseKey");
  try {
    const status = await call("POST", "/license/activate", { licenseKey: key });
    $("estadoLicencia").textContent = `Licencia activada: ${status.organizationName ?? ""} · vigente hasta ${status.validUntil ? status.validUntil.substring(0, 10) : "—"}.`;
    show("recuperacion");
  } catch (err) { fail(err.message); }
});
$("demo").addEventListener("click", () => show("recuperacion"));

$("generar").addEventListener("click", async () => {
  try {
    const result = await call("POST", "/backups/recovery-code");
    $("codigo").textContent = result.code;
    $("codigo").classList.remove("hidden");
    $("form-confirmar").classList.remove("hidden");
    $("generar").classList.add("hidden");
  } catch (err) { fail(err.message); }
});

$("form-confirmar").addEventListener("submit", async (e) => {
  e.preventDefault();
  try {
    await call("POST", "/backups/recovery-code/confirm", { code: new FormData(e.target).get("code") });
    olvidarToken();
    show("listo");
  } catch (err) { fail(err.message); }
});

(async () => {
  try {
    const status = await call("GET", "/setup/status");
    if (!status.isCompleted) { await loadCatalogs(); show("empresa"); }
    else if (token) { show("licencia"); }
    else { show("ingreso"); }
  } catch (err) { fail("El servidor no responde todavía. Espere unos segundos y recargue la página. (" + err.message + ")"); }
})();
