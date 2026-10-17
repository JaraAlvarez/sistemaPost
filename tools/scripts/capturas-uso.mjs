// Capturas de pantalla de los manuales de uso (docs/imagenes/uso, índice en docs/imagenes/uso/INDICE.md).
//
// Prepara una tienda de demostración realista (Supermercado La Economía SAS) SOLO con el asistente, la interfaz y la API del POS, y toma
// las capturas con Microsoft Edge sin ventana (protocolo de DevTools) a 1280×800 (algunas de caja también a 1366×768).
//
// Requisitos (todo local, nada sale a Internet):
//   1. Una base de datos de desarrollo NUEVA (sin configurar). Para no tocar la de todos los días se usa otro contenedor, p. ej.:
//        docker run -d --name pos-manuales-postgres -e POSTGRES_PASSWORD=pos-dev-superuser -p 127.0.0.1:5489:5432 postgres:18
//        dotnet run --project src/Server/Pos.Server.Migrator -- create-database --superuser "Host=127.0.0.1;Port=5489;Username=postgres;Password=pos-dev-superuser;Database=postgres" --database pos --migrator-password pos-dev-migrator --app-password pos-dev-app-password --backup-password pos-dev-backup
//   2. El servidor en Development, Caja Única, con el proveedor fiscal SIMULADO (FAKE: numera, genera CUFE y QR sin llamar a nadie):
//        ASPNETCORE_ENVIRONMENT=Development  Pos__Server__Port=5486  Pos__Server__LanDiscovery=false  Pos__Database__Edition=SINGLE
//        Pos__Billing__Provider=FAKE  Pos__DataRoot=<carpeta temporal>
//        Pos__Database__ConnectionString / MigratorConnectionString / BackupConnectionString → Port=5489 (mismas claves de desarrollo)
//   3. El agente de la caja en Development (puerto fijo 5490), con salida a archivo y báscula simulada:
//        DOTNET_ENVIRONMENT=Development  Agent__AllowedOrigins__0=http://localhost:5486  Agent__FileOutputDirectory=<carpeta tiquetes>
//        Agent__ScaleConfigFile=<carpeta>\scale.json  Agent__EnableScaleSimulator=true
//   4. Node 22+, Microsoft Edge, Python 3 con Pillow (optimiza los PNG) y el SDK de .NET (dotnet fsi dibuja el QR con QRCoder).
//
// Uso (desde la raíz del repositorio):
//   node tools/scripts/capturas-uso.mjs --tiquetes <carpeta de salida del agente> [--url http://localhost:5486/] [--fases todas]
//   Fases, en este orden: asistente, datos, turno-cerrado, caja (incluye caja-fin), turno-abierto, admin, tiquetes, optimizar
//   ("todas" = todas; "caja-fin" sola repite el final de la caja: báscula sin respuesta, 1366×768 y cierre).
//   Las fases dependen de las anteriores (la base debe estar en el estado que dejó la fase previa).
//
// Las franjas de licencia en demostración y la alerta de backups (propias de una base de desarrollo recién creada) se ocultan, igual que
// la línea técnica "Proveedor: FAKE" de facturación; INDICE.md dice dónde.
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const raiz = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const argumento = (nombre, defecto) => { const i = process.argv.indexOf(nombre); return i > 0 && i + 1 < process.argv.length ? process.argv[i + 1] : defecto; };
const BASE = argumento("--url", "http://localhost:5486/");
const API = BASE + "api/v1/";
const AGENTE = "http://localhost:5490/";
const SALIDA = resolve(raiz, argumento("--salida", "docs/imagenes/uso"));
const CRUDAS = resolve(argumento("--crudas", join(tmpdir(), "capturas-uso-crudas")));
const TIQUETES = argumento("--tiquetes", "");
const EDGE = argumento("--edge", "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe");
const FASES = argumento("--fases", "todas").split(",");
const ORDEN = ["asistente", "datos", "turno-cerrado", "caja", "caja-fin", "turno-abierto", "admin", "tiquetes", "optimizar"];
const fase = (f) => FASES.includes("todas") || FASES.includes(f);
mkdirSync(CRUDAS, { recursive: true });

// Datos de la tienda de demostración (ficticios; los documentos y teléfonos no son de personas reales).
const DUENO = { usuario: "dueno", clave: "Sup3rmercado-Seguro", codigo: "100", pin: "4826" };
const ANA = { codigo: "601", pin: "2580" };
const JORGE = { codigo: "602", pin: "1470" };
const SUPERVISOR = { codigo: "701", pin: "3690" };

// ═════════════════════════════════════ Edge sin ventana (DevTools) ═════════════════════════════════════
const espera = (ms) => new Promise((r) => setTimeout(r, ms));
let ws, secuencia = 0, navegador, perfil;
const pendientes = new Map();
const cdp = (method, params = {}) => new Promise((ok, mal) => { const id = ++secuencia; pendientes.set(id, { ok, mal }); ws.send(JSON.stringify({ id, method, params })); });
async function abrirEdge() {
  perfil = mkdtempSync(join(tmpdir(), "capturas-uso-edge-"));
  const puerto = 9400 + Math.floor(Math.random() * 400);
  navegador = spawn(EDGE, ["--headless=new", `--remote-debugging-port=${puerto}`, `--user-data-dir=${perfil}`, "--window-size=1280,800", "--hide-scrollbars",
    "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--lang=es-CO", "--allow-file-access-from-files", "about:blank"], { stdio: "ignore" });
  let lista;
  for (let i = 0; i < 80 && !lista; i++) { try { lista = await (await fetch(`http://127.0.0.1:${puerto}/json/list`)).json(); } catch { await espera(250); } }
  ws = new WebSocket(lista.find((t) => t.type === "page").webSocketDebuggerUrl);
  await new Promise((r) => ws.addEventListener("open", r));
  ws.addEventListener("message", (e) => { const m = JSON.parse(e.data); const p = m.id && pendientes.get(m.id); if (p) { pendientes.delete(m.id); m.error ? p.mal(new Error(m.error.message)) : p.ok(m.result); } });
  await cdp("Page.enable"); await cdp("Runtime.enable");
  await tamano(1280, 800);
}
async function cerrarEdge() {
  try { ws?.close(); } catch { /* ya cerrado */ }
  navegador?.kill();
  await espera(800);
  try { rmSync(perfil, { recursive: true, force: true }); } catch { /* Edge puede tardar en soltar el perfil */ }
}
const tamano = (ancho, alto) => cdp("Emulation.setDeviceMetricsOverride", { width: ancho, height: alto, deviceScaleFactor: 1, mobile: false });
async function evaluar(expr) {
  const r = await cdp("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error("JS: " + JSON.stringify(r.exceptionDetails).slice(0, 300));
  return r.result.value;
}
const J = JSON.stringify;
async function ir(url, ms = 5000) { await cdp("Page.navigate", { url }); await espera(ms); }
/** Navegación interna de Blazor (sin recargar: la sesión sigue en memoria y en sessionStorage). */
async function ruta(r, ms = 3500) {
  await evaluar(`(() => { history.pushState(null, "", ${J(BASE + r.replace(/^\//, ""))}); window.dispatchEvent(new PopStateEvent("popstate")); })()`);
  await espera(ms);
  await arriba();
}
async function arriba() { await evaluar(`(() => { window.scrollTo(0, 0); document.querySelectorAll(".mud-main-content, .bp-main, main, .caja-lateral, .caja-paso, .caja").forEach(e => e.scrollTop = 0); })()`); await espera(300); }
async function abajo() { await evaluar(`(() => { window.scrollTo(0, 1e6); document.querySelectorAll(".mud-main-content, .bp-main, main, .caja-paso, .caja").forEach(e => e.scrollTop = 1e6); })()`); await espera(400); }
async function clic(texto, sel = "button, .mud-tab, a, [role=tab], .mud-nav-link, .mud-list-item", { exacto = true, ms = 900 } = {}) {
  const ok = await evaluar(`(() => { const t = ${J(texto)}; const el = [...document.querySelectorAll(${J(sel)})].filter(e => e.offsetParent !== null)
    .find(e => ${exacto ? "e.textContent.trim() === t" : "e.textContent.trim().includes(t)"}); if (!el) return false; el.scrollIntoView({ block: "center" }); el.click(); return true; })()`);
  if (!ok) throw new Error("No encontré «" + texto + "»");
  await espera(ms);
}
async function clicFila(texto, ms = 3000) {
  const ok = await evaluar(`(() => { const tr = [...document.querySelectorAll("tbody tr")].filter(e => e.offsetParent !== null).find(e => e.textContent.includes(${J(texto)}));
    if (!tr) return false; const a = tr.querySelector("a"); (a || tr.querySelector("td") || tr).click(); return true; })()`);
  if (!ok) throw new Error("No encontré la fila «" + texto + "»");
  await espera(ms);
}
async function enfocar(etiqueta) {
  const ok = await evaluar(`(() => { const l = [...document.querySelectorAll("label")].find(e => (e.offsetParent !== null || e.closest(".mud-input-control")) && e.textContent.trim().startsWith(${J(etiqueta)}));
    const c = l && ((l.htmlFor && document.getElementById(l.htmlFor)) || l.closest(".mud-input-control")?.querySelector("input,textarea") || l.querySelector("input,textarea,select"));
    if (!c) return false; c.focus(); c.select && c.select(); return true; })()`);
  if (!ok) throw new Error("No encontré el campo «" + etiqueta + "»");
}
async function escribir(etiqueta, texto) { await enfocar(etiqueta); await cdp("Input.insertText", { text: String(texto) }); await espera(250); }
async function teclear(texto) { await cdp("Input.insertText", { text: String(texto) }); await espera(200); }
/** Reemplaza el contenido del campo que tiene el foco. */
async function reemplazar(texto) { await evaluar(`document.activeElement && document.activeElement.select && document.activeElement.select()`); await teclear(texto); await espera(150); }
const VK = { Enter: 13, Escape: 27, Delete: 46, Insert: 45, ArrowUp: 38, ArrowDown: 40, Tab: 9 };
async function tecla(key, ms = 700) {
  const vk = VK[key] ?? (/^F(\d+)$/.test(key) ? 111 + Number(key.slice(1)) : key.toUpperCase().charCodeAt(0));
  await cdp("Input.dispatchKeyEvent", { type: "rawKeyDown", key, code: key, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk });
  if (key === "Enter") await cdp("Input.dispatchKeyEvent", { type: "char", key, text: "\r", windowsVirtualKeyCode: 13 });
  await cdp("Input.dispatchKeyEvent", { type: "keyUp", key, code: key, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk });
  await espera(ms);
}
async function elegir(etiqueta, opcion) {
  const ok = await evaluar(`(() => { const l = [...document.querySelectorAll("label")].filter(e => e.offsetParent !== null).find(e => e.textContent.trim().startsWith(${J(etiqueta)}));
    const c = l && l.closest(".mud-select, .mud-input-control"); if (!c) return false; const t = c.querySelector(".mud-input-slot, input") || c;
    t.dispatchEvent(new MouseEvent("mousedown", { bubbles: true })); t.click(); return true; })()`);
  if (!ok) throw new Error("No encontré la lista «" + etiqueta + "»");
  await espera(700);
  if (opcion === null) return; // solo abrirla (para la captura)
  const ok2 = await evaluar(`(() => { const it = [...document.querySelectorAll(".mud-popover-open .mud-list-item, .mud-popover-open [role=option]")].find(e => e.textContent.trim().startsWith(${J(opcion)})); if (!it) return false; it.click(); return true; })()`);
  if (!ok2) throw new Error("No encontré la opción «" + opcion + "»");
  await espera(600);
}
async function marcar(etiqueta, valor = true) {
  await evaluar(`(() => { const l = [...document.querySelectorAll("label, .mud-checkbox")].find(e => e.textContent.trim().startsWith(${J(etiqueta)}));
    const i = l && (l.querySelector("input[type=checkbox]") || l.closest(".mud-input-control, .mud-checkbox")?.querySelector("input[type=checkbox]")); if (i && i.checked !== ${valor}) i.click(); })()`);
  await espera(500);
}
async function subirArchivo(selector, archivo) {
  const doc = await cdp("DOM.getDocument", { depth: -1, pierce: true });
  const r = await cdp("DOM.querySelector", { nodeId: doc.root.nodeId, selector });
  await cdp("DOM.setFileInputFiles", { nodeId: r.nodeId, files: [archivo] });
  await espera(3000);
}
const sinFoco = () => evaluar(`document.activeElement && document.activeElement.blur && document.activeElement.blur()`);
const errorPantalla = () => evaluar(`(() => { const e = document.getElementById("blazor-error-ui"); return !!e && getComputedStyle(e).display !== "none"; })()`);
const avisosError = [];
/** Si la interfaz mostró "Ocurrió un error inesperado", recarga la página (la venta y la sesión viven en el servidor) y lo anota. */
async function asegurar(donde) {
  if (await errorPantalla()) {
    avisosError.push(donde);
    console.warn(`  ! "Ocurrió un error inesperado en la pantalla" en ${donde}: se recarga la página`);
    await ir(await evaluar("location.href"), 7000);
  }
}
/** Oculta lo que solo existe en una base de desarrollo recién creada (licencia en demostración, alerta de backups, proveedor FAKE). */
async function ocultarDesarrollo() {
  await evaluar(`(() => {
    document.querySelectorAll(".avisos-barra, .avisos-detalle, .caja-avisos .caja-aviso").forEach(e => e.style.display = "none");
    document.querySelectorAll(".caja-barra .caja-estado.alerta, .caja-barra .caja-estado.error").forEach(e => { if (!/conexi/i.test(e.textContent)) e.style.display = "none"; });
    for (const el of document.querySelectorAll("a, .mud-paper, .mud-card, div")) {
      if (el.children.length < 6 && /alerta\\(s\\) de backups/.test(el.textContent) && el.offsetHeight < 140) { el.style.display = "none"; }
    }
    for (const h of document.querySelectorAll("h2, h3, h6, .mud-typography")) {
      if (h.textContent.trim() === "Requiere atención") { const s = h.nextElementSibling; if (!s || s.offsetHeight === 0 || [...s.children].every(c => c.style.display === "none")) h.style.display = "none"; }
    }
    for (const el of document.querySelectorAll("p, .mud-typography, td, tr, span, small")) {
      if (el.children.length === 0 && /adaptador activo|\\bFAKE\\b/.test(el.textContent)) { (el.closest("tr") && /Proveedor/.test(el.closest("tr").textContent) ? el.closest("tr") : el).style.visibility = "hidden"; }
    }
  })()`);
  await espera(250);
}
async function foto(nombre, { ms = 700, desarrollo = false } = {}) {
  await espera(ms);
  if (await errorPantalla()) throw new Error(`La pantalla muestra "Ocurrió un error inesperado" antes de ${nombre}`);
  if (!desarrollo) await ocultarDesarrollo();
  const r = await cdp("Page.captureScreenshot", { format: "png" });
  writeFileSync(join(CRUDAS, nombre + ".png"), Buffer.from(r.data, "base64"));
  console.log("  ·", nombre);
}
let nCaja = 0;
const fotoCaja = (desc, opciones) => foto(`caja-${String(++nCaja).padStart(2, "0")}-${desc}`, opciones);
const texto = () => evaluar("document.body.innerText");
const snack = () => evaluar(`[...document.querySelectorAll(".mud-snackbar")].map(e => e.textContent.trim())`);

async function salirDeLaSesion() {
  await ir(BASE + "ingresar", 3000);
  await evaluar(`(() => { try { sessionStorage.clear(); } catch { } })()`);
  await ir(BASE + "ingresar", 6000);
}
async function entrarAdministracion(capturar) {
  await salirDeLaSesion();
  await clic("Administración");
  await escribir("Usuario", DUENO.usuario);
  await escribir("Contraseña", DUENO.clave);
  if (capturar) await foto("adm-01-ingreso-administracion");
  await clic("Entrar", "button", { ms: 5000 });
}

// ═════════════════════════════════════ API (datos de demostración) ═════════════════════════════════════
class Pos {
  static async login(username = DUENO.usuario, password = DUENO.clave) { const p = new Pos(); p.token = (await p.post("auth/login", { username, password })).token; return p; }
  static async caja({ codigo, pin }) { const p = new Pos(); p.token = (await p.post("auth/pos-login", { posCode: codigo, pin })).token; return p; }
  async req(method, path, body, headers = {}) {
    const h = { ...headers };
    if (this.token) h.Authorization = "Bearer " + this.token;
    if (body !== undefined) h["Content-Type"] = "application/json";
    const r = await fetch(API + path, { method, headers: h, body: body === undefined ? undefined : J(body) });
    const t = await r.text();
    const data = t ? (() => { try { return JSON.parse(t); } catch { return t; } })() : null;
    if (!r.ok) { const e = new Error(`${method} ${path} → ${r.status}: ${typeof data === "string" ? data : J(data)}`.slice(0, 900)); e.status = r.status; e.data = data; throw e; }
    return data;
  }
  get(p) { return this.req("GET", p); }
  post(p, b, h) { return this.req("POST", p, b, h); }
  put(p, b, h) { return this.req("PUT", p, b, h); }
}
function dvNit(nit) {
  const pesos = [3, 7, 13, 17, 19, 23, 29, 37, 41, 43, 47, 53, 59, 67, 71];
  const s = String(nit).split("").reverse().reduce((a, x, i) => a + Number(x) * pesos[i], 0) % 11;
  return String(s > 1 ? 11 - s : s);
}
function ean13(n) {
  const cuerpo = `770999${String(n).padStart(6, "0")}`;
  const s = cuerpo.split("").reduce((a, c, i) => a + Number(c) * (i % 2 === 0 ? 1 : 3), 0);
  return cuerpo + ((10 - (s % 10)) % 10);
}

// Catálogo: 28 productos por unidad y 8 por peso (fruver y carnes), con marcas y categorías de un supermercado de barrio.
const PRODUCTOS = [
  ["ABA-001", "Arroz Diana 1 kg", "Arroz Diana 1kg", "Abarrotes > Granos", "Diana", "IVA_EXCLUIDO", 4900, 1000, "G"],
  ["ABA-002", "Fríjol cargamanto 500 g", "Fríjol 500g", "Abarrotes > Granos", "La Economía", "IVA5", 7800, 500, "G"],
  ["ABA-003", "Lenteja 500 g", "Lenteja 500g", "Abarrotes > Granos", "La Economía", "IVA5", 3900, 500, "G"],
  ["ABA-004", "Aceite de girasol Premier 900 ml", "Aceite Premier 900", "Abarrotes > Aceites", "Premier", "IVA19", 12900, 900, "ML"],
  ["ABA-005", "Azúcar Manuelita 1 kg", "Azúcar 1kg", "Abarrotes > Azúcar y panela", "Manuelita", "IVA5", 4800, 1000, "G"],
  ["ABA-006", "Panela redonda 1 kg", "Panela 1kg", "Abarrotes > Azúcar y panela", "La Economía", "IVA_EXCLUIDO", 4200, 1000, "G"],
  ["ABA-007", "Café Sello Rojo 500 g", "Café Sello Rojo", "Abarrotes > Café y chocolate", "Sello Rojo", "IVA5", 16900, 500, "G"],
  ["ABA-008", "Chocolate Corona 500 g", "Chocolate Corona", "Abarrotes > Café y chocolate", "Corona", "IVA5", 11500, 500, "G"],
  ["ABA-009", "Sal Refisal 1 kg", "Sal 1kg", "Abarrotes > Granos", "Refisal", "IVA_EXCLUIDO", 2300, 1000, "G"],
  ["ABA-010", "Spaghetti Doria 500 g", "Spaghetti Doria", "Abarrotes > Pastas", "Doria", "IVA5", 3600, 500, "G"],
  ["ABA-011", "Atún Van Camp's en aceite 160 g", "Atún 160g", "Abarrotes > Enlatados", "Van Camp's", "IVA19", 6900, 160, "G"],
  ["ABA-012", "Galletas Festival fresa x6", "Galletas Festival", "Abarrotes > Galletas y snacks", "Festival", "IVA19", 4500, null, null],
  ["LAC-001", "Leche entera Alquería 1100 ml", "Leche entera 1100", "Lácteos > Leches", "Alquería", "IVA0_EXENTO", 4980, 1100, "ML"],
  ["LAC-002", "Leche deslactosada Colanta 1 L", "Leche deslactosada", "Lácteos > Leches", "Colanta", "IVA0_EXENTO", 5300, 1000, "ML"],
  ["LAC-003", "Queso campesino 500 g", "Queso campesino", "Lácteos > Quesos", "Colanta", "IVA5", 11900, 500, "G"],
  ["LAC-004", "Yogur Alpina fresa 1 L", "Yogur fresa 1L", "Lácteos > Yogures", "Alpina", "IVA19", 7900, 1000, "ML"],
  ["LAC-005", "Mantequilla 250 g", "Mantequilla 250g", "Lácteos > Quesos", "Alpina", "IVA19", 8500, 250, "G"],
  ["LAC-006", "Huevos AA x30", "Huevos AA x30", "Lácteos > Huevos", "Kikes", "IVA0_EXENTO", 18900, null, null],
  ["ASE-001", "Detergente en polvo Ariel 1 kg", "Detergente Ariel", "Aseo > Ropa", "Ariel", "IVA19", 14900, 1000, "G"],
  ["ASE-002", "Jabón Rey en barra 300 g", "Jabón Rey", "Aseo > Ropa", "Rey", "IVA19", 3500, 300, "G"],
  ["ASE-003", "Papel higiénico Familia x12", "Papel Familia x12", "Aseo > Hogar", "Familia", "IVA19", 21900, null, null],
  ["ASE-004", "Lavaloza Axion 450 g", "Lavaloza Axion", "Aseo > Cocina", "Axion", "IVA19", 6900, 450, "G"],
  ["ASE-005", "Blanqueador Clorox 1 L", "Clorox 1L", "Aseo > Hogar", "Clorox", "IVA19", 4500, 1000, "ML"],
  ["BEB-001", "Gaseosa Coca-Cola 1,5 L", "Coca-Cola 1,5L", "Bebidas > Gaseosas", "Coca-Cola", "IVA19", 5900, 1500, "ML"],
  ["BEB-002", "Gaseosa Postobón manzana 1,5 L", "Postobón manzana", "Bebidas > Gaseosas", "Postobón", "IVA19", 4900, 1500, "ML"],
  ["BEB-003", "Agua Cristal 600 ml", "Agua Cristal", "Bebidas > Aguas y jugos", "Cristal", "IVA19", 1800, 600, "ML"],
  ["BEB-004", "Jugo Hit mango 500 ml", "Jugo Hit mango", "Bebidas > Aguas y jugos", "Hit", "IVA19", 2900, 500, "ML"],
  ["BEB-005", "Cerveza Águila lata 330 ml", "Cerveza Águila", "Bebidas > Cervezas", "Águila", "IVA19", 3200, 330, "ML"],
];
const PESO = [
  ["FRU-101", "Tomate chonto", "Fruver > Verduras", 4980, "101"], ["FRU-102", "Papa pastusa", "Fruver > Verduras", 2900, "102"],
  ["FRU-103", "Cebolla cabezona blanca", "Fruver > Verduras", 3800, "103"], ["FRU-104", "Banano criollo", "Fruver > Frutas", 3200, "104"],
  ["FRU-105", "Aguacate Hass", "Fruver > Frutas", 9900, "105"], ["CAR-201", "Pechuga de pollo", "Carnes > Pollo", 16900, "201"],
  ["CAR-202", "Carne molida de res", "Carnes > Res", 22900, "202"], ["CAR-203", "Costilla de cerdo", "Carnes > Cerdo", 19900, "203"],
];
const miles = (n) => n.toLocaleString("es-CO").replace(/,/g, ".");
const CODIGO = Object.fromEntries(PRODUCTOS.map((p, i) => [p[0], ean13(i + 1)]));
function csvProductos() {
  const filas = ["sku;nombre;nombre_corto;descripcion;categoria;marca;unidad;modo_venta;tipo;iva;codigo_barras;precio;plu;bascula;contenido_neto;unidad_contenido"];
  for (const [sku, n, c, cat, m, iva, p, cn, cu] of PRODUCTOS) filas.push(`${sku};${n};${c};;${cat};${m};UND;UNIDAD;INVENTARIABLE;${iva};${CODIGO[sku]};${miles(p)};;NO;${cn ?? ""};${cu ?? ""}`);
  for (const [sku, n, cat, p, plu] of PESO) filas.push(`${sku};${n};${n};;${cat};;KG;PESO;INVENTARIABLE;IVA_EXCLUIDO;;${miles(p)};${plu};SI;;`);
  return "\uFEFF" + filas.join("\r\n") + "\r\n";
}
function csvSaldoInicial() {
  const cant = [48, 36, 60, 72, 24, 30];
  const filas = ["sku;cantidad;costo"];
  [...PRODUCTOS.map((p) => [p[0], p[6], false]), ...PESO.map((p) => [p[0], p[3], true])].forEach(([sku, precio, peso], i) =>
    filas.push(`${sku};${peso ? [25, 30, 40, 18][i % 4] : cant[i % cant.length]};${Math.round(precio * 0.72 / 10) * 10}`));
  return "\uFEFF" + filas.join("\r\n") + "\r\n";
}

// ═════════════════════════════════════ Fase 1: asistente de configuración inicial ═════════════════════════════════════
async function faseAsistente() {
  await ir(BASE + "instalacion", 3500);
  const poner = (name, v) => evaluar(`(() => { const e = document.querySelector('[name="${name}"]'); e.value = ${J(v)}; e.dispatchEvent(new Event("input")); e.dispatchEvent(new Event("change")); })()`);
  await poner("legalName", "Supermercado La Economía SAS"); await poner("tradeName", "La Economía");
  await poner("identificationNumber", "900123456"); await poner("checkDigit", "8");
  await evaluar(`document.getElementById("municipioBuscar").focus()`); await teclear("Medell"); await espera(1500);
  await evaluar(`(() => { const o = [...document.querySelectorAll("#municipios option")].find(o => o.value.includes("05001")); document.getElementById("municipioBuscar").value = o.value; })()`);
  await poner("address", "Calle 50 # 45-20"); await poner("phone", "604 555 1234"); await poner("email", "contacto@laeconomia.co");
  await poner("branchName", "Centro");
  await poner("username", DUENO.usuario); await poner("displayName", "Dueño de la tienda"); await poner("password", DUENO.clave);
  await poner("posCode", DUENO.codigo); await poner("pin", DUENO.pin);
  await sinFoco();
  await foto("conf-01-asistente-empresa");
  await evaluar(`window.scrollTo(0, 1e6)`); await foto("conf-02-asistente-empresa-continuacion");
  await evaluar(`document.querySelector("#form-empresa button[type=submit]").click()`); await espera(5000);
  await evaluar(`window.scrollTo(0, 0)`); await foto("conf-03-asistente-licencia");
  await evaluar(`document.getElementById("demo").click()`); await espera(800);
  await foto("conf-04-asistente-codigo-recuperacion");
  await evaluar(`document.getElementById("generar").click()`); await espera(2500);
  await foto("conf-05-asistente-codigo-generado");
  await evaluar(`(() => { const e = document.querySelector("#form-confirmar [name=code]"); e.value = document.getElementById("codigo").textContent; document.querySelector("#form-confirmar button").click(); })()`);
  await espera(2500);
  await foto("conf-06-asistente-todo-listo");
}

// ═════════════════════════════════════ Fase 2: datos de la tienda ═════════════════════════════════════
async function faseDatos() {
  // Importar productos y saldo inicial por la interfaz.
  await entrarAdministracion(true);
  const carpeta = mkdtempSync(join(tmpdir(), "capturas-uso-csv-"));
  const productosCsv = join(carpeta, "productos-la-economia.csv"), saldoCsv = join(carpeta, "saldo-inicial.csv");
  writeFileSync(productosCsv, csvProductos(), "utf8"); writeFileSync(saldoCsv, csvSaldoInicial(), "utf8");
  await ruta("admin/importar");
  await foto("adm-30-importar");
  await evaluar(`document.querySelectorAll("input[type=file]")[0].setAttribute("data-captura", "productos")`);
  await subirArchivo("input[data-captura=productos]", productosCsv);
  await foto("adm-31-importar-vista-previa");
  await clic("Aplicar", "button", { ms: 4000 });
  await arriba(); await foto("adm-32-importar-aplicado");
  await evaluar(`[...document.querySelectorAll("input[type=file]")].pop().setAttribute("data-captura", "saldo")`);
  await subirArchivo("input[data-captura=saldo]", saldoCsv);
  await abajo(); await foto("adm-33-importar-saldo-inicial", { ms: 200 });
  await ruta("admin/ajustes");
  await foto("adm-34-ajustes-saldo-inicial-en-borrador");
  await clicFila("S01-000001");
  await foto("adm-35-ajuste-saldo-inicial-borrador");
  await clic("Aplicar ajuste", "button", { ms: 1200 });
  await foto("adm-36-ajuste-aplicar-confirmacion");
  await clic("Aplicar", ".mud-dialog button", { ms: 3000 });
  await foto("adm-37-ajuste-saldo-inicial-aplicado");
  rmSync(carpeta, { recursive: true, force: true });

  // El resto por la API (mismas reglas que la interfaz).
  const pos = await Pos.login();
  const productos = (await pos.get("catalog/products?pageSize=200")).items;
  const P = (sku) => productos.find((p) => p.sku === sku);
  const branch = (await pos.get("organization/branches"))[0];
  const roles = await pos.get("identity/roles");
  const rol = (c) => roles.find((r) => r.code === c).id;
  const metodos = await pos.get("cash/payment-methods");
  const M = (c) => metodos.find((m) => m.code === c).id;
  for (const [username, displayName, posCode, pin, r] of [
    ["ana.cajera", "Ana Cajera", ANA.codigo, ANA.pin, "CASHIER"], ["jorge.cajero", "Jorge Cajero", JORGE.codigo, JORGE.pin, "CASHIER"],
    ["pedro.supervisor", "Pedro Supervisor", SUPERVISOR.codigo, SUPERVISOR.pin, "CASH_SUPERVISOR"], ["laura.admin", "Laura Administradora", null, null, "ADMIN"],
    ["camilo.bodega", "Camilo Bodega", null, null, "INVENTORY"]]) {
    await pos.post("identity/users", { username, displayName, password: "Clave-Inicial-2026", posCode, pin, roles: [{ roleId: rol(r), branchId: null }] });
  }
  await pos.post(`organization/branches/${branch.id}/warehouses`, { code: "BOD", name: "Bodega trasera", kind: "Storage", allowsSales: false });
  const bodegas = (await pos.get(`organization/branches/${branch.id}`)).warehouses;
  const piso = bodegas.find((w) => /SALES/i.test(w.kind)), bodega = bodegas.find((w) => w.code === "BOD");
  for (const [nit, legalName, tradeName, code, days, tel, email] of [
    ["900456789", "Distribuidora Láctea del Valle SAS", "Lácteos del Valle", "LACVAL", 30, "3005550101", "facturas@lacteosdelvalle.example"],
    ["901112223", "Comercializadora Andina de Abarrotes SAS", "Andina Abarrotes", "ANDINA", 15, "3105550202", "cartera@andina.example"],
    ["800987654", "Aseo Total Ltda", "Aseo Total", "ASEOTO", 0, "6045550303", "pedidos@aseototal.example"]]) {
    const party = await pos.post("parties", { personType: "Legal", identificationType: "NIT", identificationNumber: nit, checkDigit: dvNit(nit), legalName, tradeName,
      taxRegime: "48", fiscalResponsibilities: ["O-13"], email, contacts: [{ name: "Asesor comercial", position: "Ventas", phone: tel, isPrimary: true }] });
    await pos.post("purchasing/suppliers", { partyId: party.id, supplier: { code, paymentTermDays: days, issuesInvoices: true } });
  }
  const emp = await pos.post("catalog/price-lists", { code: "EMPLEADOS", name: "Empleados", pricesIncludeTax: true, adjustmentPercent: -5, roundingIncrement: 50, allowsPromotions: true });
  const may = await pos.post("catalog/price-lists", { code: "MAYORISTA", name: "Mayorista (tiendas y restaurantes)", pricesIncludeTax: true, adjustmentPercent: -8, roundingIncrement: 50, allowsPromotions: false });
  const gEmp = await pos.post("customers/groups", { code: "EMPLEADOS", name: "Empleados de la tienda", priceListId: emp.id });
  const gMay = await pos.post("customers/groups", { code: "MAYORISTAS", name: "Clientes mayoristas", priceListId: may.id });
  const politicas = await pos.get("customers/privacy-policies");
  if (politicas[0] && politicas[0].status !== "ACTIVE") await pos.post(`customers/privacy-policies/${politicas[0].id}/activate`);
  for (const c of [
    { personType: "NATURAL", identificationType: "CC", identificationNumber: "1036600111", firstNames: "María Fernanda", lastNames: "Ríos Patiño", phone: "300 555 0111", email: "mariafernanda@example.com" },
    { personType: "NATURAL", identificationType: "CC", identificationNumber: "71600222", firstNames: "Jorge Iván", lastNames: "Castaño Mejía", phone: "301 555 0222", email: null },
    { personType: "NATURAL", identificationType: "CC", identificationNumber: "43600333", firstNames: "Luz Marina", lastNames: "Gómez Arango", phone: "310 555 0333", email: "luzmarina@example.com", grupo: gEmp.id },
    { personType: "LEGAL", identificationType: "NIT", identificationNumber: "901600444", checkDigit: dvNit("901600444"), legalName: "Restaurante El Buen Sabor SAS", phone: "604 555 0444", email: "compras@buensabor.example", grupo: gMay.id },
    { personType: "NATURAL", identificationType: "CC", identificationNumber: "1017600555", firstNames: "Andrés Felipe", lastNames: "Zapata Ruiz", phone: "320 555 0555", email: "andresz@example.com" }]) {
    const { grupo, ...cuerpo } = c;
    const r = await pos.post("customers/quick", { ...cuerpo, consents: [{ purpose: "Service", granted: true }] });
    if (grupo) await pos.put(`customers/${r.customer?.partyId ?? r.partyId}/pricing`, { groupId: grupo });
  }
  const cats = await pos.get("catalog/categories");
  const cat = (n) => cats.find((c) => c.name === n && !c.parentId).id;
  for (const p of [
    { name: "Postobón 3x2", type: "MultiBuy", validFrom: "2026-10-01T00:00:00-05:00", validTo: "2026-11-30T23:59:59-05:00", ticketText: "Lleve 3 pague 2", buyQuantity: 3, payQuantity: 2, items: [{ productId: P("BEB-002").id }] },
    { name: "Lácteos 10 % fin de semana", type: "PercentOff", validFrom: "2026-10-01T00:00:00-05:00", validTo: "2026-12-31T23:59:59-05:00", days: ["Saturday", "Sunday"], percent: 10, ticketText: "Lácteos 10 % fin de semana", items: [{ categoryId: cat("Lácteos") }] },
    { name: "Combo desayuno", type: "Combo", validFrom: "2026-10-01T00:00:00-05:00", validTo: "2026-12-31T23:59:59-05:00", price: 36000, ticketText: "Combo desayuno", items: [{ productId: P("ABA-007").id, quantity: 1 }, { productId: P("ABA-006").id, quantity: 1 }, { productId: P("LAC-006").id, quantity: 1 }] }]) {
    await pos.post(`promotions/${(await pos.post("promotions", p)).id}/activate`);
  }
  await pos.post("promotions", { name: "Aseo 15 % temporada navideña", type: "PercentOff", validFrom: "2026-12-01T00:00:00-05:00", validTo: "2026-12-24T23:59:59-05:00", percent: 15, ticketText: "Aseo 15 % Navidad", items: [{ categoryId: cat("Aseo") }] });
  // Compras: orden aprobada y enviada → factura contra la orden; otra factura a la bodega trasera; una orden en borrador.
  const sup = await pos.get("purchasing/suppliers");
  const S = (code) => sup.find((s) => s.code === code).id;
  const contabilizar = async (cuerpo) => { const r = await pos.post("purchasing/purchases", cuerpo); await pos.put(`purchasing/purchases/${r.id}`, { ...cuerpo, invoiceTotal: r.total }); await pos.post(`purchasing/purchases/${r.id}/post`); };
  const orden = await pos.post("purchasing/orders", { supplierId: S("LACVAL"), warehouseId: piso.id, lines: [
    { productId: P("LAC-001").id, quantity: 48, unitCost: 3550 }, { productId: P("LAC-002").id, quantity: 24, unitCost: 3800 }, { productId: P("LAC-004").id, quantity: 24, unitCost: 5600 }] });
  await pos.post(`purchasing/orders/${orden.id}/approve`); await pos.post(`purchasing/orders/${orden.id}/send`);
  await contabilizar({ supplierId: S("LACVAL"), warehouseId: piso.id, purchaseOrderId: orden.id, supplierInvoiceNumber: "FE-20871", invoiceDate: "2026-10-15", paymentMode: "Credit",
    proration: "Value", chargesTotal: 0, lines: orden.lines.map((l) => ({ productId: l.productId, quantity: l.quantity, unitCost: l.unitCost, orderLineId: l.id })) });
  await contabilizar({ supplierId: S("ANDINA"), warehouseId: bodega.id, supplierInvoiceNumber: "AND-5512", invoiceDate: "2026-10-16", paymentMode: "Credit", proration: "Value", chargesTotal: 0,
    lines: [{ productId: P("ABA-001").id, quantity: 60, unitCost: 3500 }, { productId: P("ABA-005").id, quantity: 48, unitCost: 3450 }, { productId: P("ABA-004").id, quantity: 24, unitCost: 9200 }, { productId: P("ABA-010").id, quantity: 40, unitCost: 2550 }] });
  await pos.post("purchasing/orders", { supplierId: S("ASEOTO"), warehouseId: piso.id, lines: [{ productId: P("ASE-001").id, quantity: 24, unitCost: 10600 }, { productId: P("ASE-003").id, quantity: 20, unitCost: 15600 }] });
  const cuentas = await pos.get(`purchasing/payables?supplierId=${S("LACVAL")}`);
  await pos.post("purchasing/payments", { supplierId: S("LACVAL"), paymentMethodId: M("TRANSFERENCIA"), reference: "TRF-004512", allocations: [{ accountId: cuentas[0].id, amount: 200000 }] });
  // Inventario: traslado recibido, traslado en tránsito, avería y conteo en curso.
  const t1 = await pos.post("inventory/transfers", { originWarehouseId: bodega.id, destinationWarehouseId: piso.id, notes: "Surtido de góndola", lines: [{ productId: P("ABA-001").id, quantity: 24 }, { productId: P("ABA-010").id, quantity: 20 }] });
  await pos.post(`inventory/transfers/${t1.id}/dispatch`);
  await pos.post(`inventory/transfers/${t1.id}/receive`, { received: [{ productId: P("ABA-001").id, quantity: 24 }, { productId: P("ABA-010").id, quantity: 20 }] });
  const t2 = await pos.post("inventory/transfers", { originWarehouseId: bodega.id, destinationWarehouseId: piso.id, lines: [{ productId: P("ABA-005").id, quantity: 12 }, { productId: P("ABA-004").id, quantity: 12 }] });
  await pos.post(`inventory/transfers/${t2.id}/dispatch`);
  const dano = (await pos.get("inventory/reasons")).find((m) => /DAMAGE/i.test(m.code ?? m.kind));
  const averia = await pos.post("inventory/adjustments", { warehouseId: piso.id, reasonId: dano.id, notes: "Empaques rotos al descargar", lines: [{ productId: P("LAC-004").id, quantity: 2 }, { productId: P("ABA-008").id, quantity: 1 }] });
  await pos.post(`inventory/adjustments/${averia.id}/post`);
  for (const [sku, min] of [["LAC-006", 30], ["ASE-003", 36], ["BEB-005", 80], ["ABA-007", 40], ["LAC-001", 30], ["ABA-001", 40], ["BEB-001", 24]]) {
    await pos.put("inventory/policies", { warehouseId: piso.id, productId: P(sku).id, minQuantity: min, maxQuantity: min * 3, reorderPoint: min, reorderQuantity: min * 2 });
  }
  const conteo = await pos.post("inventory/counts", { warehouseId: piso.id, type: "Full", isBlind: true });
  await pos.post(`inventory/counts/${conteo.id}/start`);
  await pos.post(`inventory/counts/${conteo.id}/entries`, [{ code: CODIGO["BEB-001"], quantity: 29, location: "Nevera 1" }, { code: CODIGO["BEB-002"], quantity: 24, location: "Nevera 1" },
    { code: CODIGO["BEB-003"], quantity: 35, location: "Nevera 2" }, { code: "ABA-009", quantity: 48, location: "Góndola 3" }]);
  const gcat = await pos.get("expenses/categories");
  const G = (n) => gcat.find((c) => c.name === n).id;
  for (const g of [[G("Arriendo"), "Arriendo del local, octubre", 2500000, "TRANSFERENCIA", "TRF-889120", "2026-10-05"], [G("Servicios públicos"), "Energía y agua (EPM), septiembre", 486300, "TRANSFERENCIA", "PSE-77120", "2026-10-10"],
    [G("Transporte y fletes"), "Flete de mercancía desde la plaza", 45000, "EFECTIVO", null, "2026-10-16"]]) {
    await pos.post("expenses", { categoryId: g[0], description: g[1], amount: g[2], taxAmount: 0, paymentMethodId: M(g[3]), partyId: null, cashSessionId: null, reference: g[4], businessDate: g[5] });
  }
  await pos.put("settings/documents.receipt_footer_message", { scope: "Company", value: "¡Gracias por comprar en La Economía! Cambios con tiquete dentro de 8 días." });
  console.log("  datos de la tienda listos");

  // Cajas e impresoras: impresora (archivo) y báscula (simulada en desarrollo; la captura muestra una Toledo 8217).
  await ruta("admin/cajas");
  await foto("adm-93-cajas-lista");
  await clic("Impresora", "button", { ms: 1500 });
  await foto("integ-01-impresora-archivo");
  await elegir("Conexión", "Red");
  await escribir("Dirección IP y puerto", "192.168.1.50:9100"); await sinFoco();
  await foto("integ-02-impresora-red");
  await elegir("Conexión", "Archivo");
  await evaluar(`(() => { const l = [...document.querySelectorAll("label")].find(l => l.textContent.trim().startsWith("Carpeta")); const i = l.closest(".mud-input-control").querySelector("input"); i.value = ""; i.dispatchEvent(new Event("input", { bubbles: true })); i.dispatchEvent(new Event("change", { bubbles: true })); })()`);
  await clic("Guardar", ".panel button, .mud-dialog button", { ms: 1500 });
  await clic("Báscula", "button", { ms: 2000 });
  await marcar("Esta caja tiene báscula");
  await escribir("Marca (opcional)", "Mettler Toledo"); await escribir("Modelo (opcional)", "8217");
  await marcar("Si la báscula no responde"); await sinFoco();
  await foto("integ-03-bascula");
  await elegir("Marca o protocolo", "Báscula simulada");
  await clic("Guardar", ".panel button, .mud-dialog button", { ms: 1500 });

  // Configuración (empresa, sucursales y bodegas, medios de pago con Sistecrédito, parámetros).
  await ruta("admin/configuracion");
  await foto("conf-07-empresa");
  await clic("Sucursales y bodegas", ".mud-tab", { ms: 1500 }); await foto("conf-08-sucursales");
  await clic("Bodegas", "button", { ms: 1500 }); await foto("conf-09-bodegas");
  await tecla("Escape"); await evaluar(`[...document.querySelectorAll(".panel button, .mud-dialog button")].filter(b => /Cerrar|Cancelar|Listo/.test(b.textContent)).forEach(b => b.click())`); await espera(800);
  await clic("Medios de pago", ".mud-tab", { ms: 2000 }); await foto("conf-10-medios-de-pago");
  await clic("Nuevo medio de pago", "button", { ms: 1500 }); await foto("conf-11-nuevo-medio-de-pago");
  await evaluar(`[...document.querySelectorAll(".panel button, .mud-dialog button")].find(b => /Cancelar/.test(b.textContent))?.click()`); await espera(800);
  await evaluar(`[...document.querySelectorAll("h6, .mud-typography")].find(e => e.textContent.trim() === "Sistecrédito")?.scrollIntoView({ block: "start" })`); await espera(500);
  await foto("integ-04-sistecredito-sin-conectar");
  await escribir("Llave de suscripción", "demo0000000000000000000000la01"); await escribir("ApplicationKey", "demo-application-key");
  await escribir("ApplicationToken", "demo-application-token"); await sinFoco();
  await evaluar(`[...document.querySelectorAll("h6, .mud-typography")].find(e => e.textContent.trim() === "Sistecrédito")?.scrollIntoView({ block: "start" })`); await espera(500);
  await foto("integ-05-sistecredito-llaves-escritas");
  // "Conectar" probaría las llaves contra Sistecrédito (Internet): se guardan por la API, sin probar, para ver el estado conectado.
  await pos.put("cash/sistecredito/settings", { subscriptionKey: "demo0000000000000000000000la01", applicationKey: "demo-application-key", applicationToken: "demo-application-token", environment: "PRODUCTION", enabled: true });
  await ruta("admin/cajas", 800); await ruta("admin/configuracion");
  await clic("Medios de pago", ".mud-tab", { ms: 2000 });
  await evaluar(`[...document.querySelectorAll("h6, .mud-typography")].find(e => e.textContent.trim() === "Sistecrédito")?.scrollIntoView({ block: "center" })`); await espera(500);
  await foto("integ-06-sistecredito-conectado");
  await clic("Parámetros", ".mud-tab", { ms: 2000 }); await foto("conf-12-parametros");
  await abajo(); await foto("conf-13-parametros-continuacion");
  await arriba();
  await clic("Cambiar", "button", { ms: 1500 }); await foto("conf-14-parametro-cambiar");
  await evaluar(`[...document.querySelectorAll(".panel button, .mud-dialog button")].find(b => /Cancelar/.test(b.textContent))?.click()`); await espera(800);

  // Facturación electrónica: credenciales, modo "en cada venta" en pruebas, rangos de la DIAN y asignación a la sucursal.
  await ruta("admin/facturacion");
  await foto("integ-07-facturacion-sin-configurar");
  await escribir("Usuario (correo de Factus)", "facturacion@laeconomia.co"); await escribir("Contraseña", "clave-de-ejemplo");
  await escribir("Client ID", "cliente-de-ejemplo"); await escribir("Client secret", "secreto-de-ejemplo"); await sinFoco();
  await foto("integ-08-facturacion-credenciales");
  await clic("Guardar credenciales", "button", { ms: 2000 });
  await elegir("Modo de emisión", null); await foto("integ-09-facturacion-modos-de-emision");
  await evaluar(`[...document.querySelectorAll(".mud-popover-open .mud-list-item")].find(e => e.textContent.trim().startsWith("Factura electrónica en cada venta")).click()`); await espera(600);
  await elegir("Ambiente del proveedor", "Pruebas"); await sinFoco();
  await foto("integ-10-facturacion-cada-venta-advertencia");
  await marcar("Tengo la cotización");
  await clic("Guardar modo y ambiente", "button", { ms: 2500 });
  await foto("integ-11-facturacion-configurada");
  await clic("Rangos de numeración", ".mud-tab", { ms: 1500 });
  await clic("Sincronizar desde Factus", "button", { ms: 3000 });
  await foto("integ-12-rangos-sincronizados");
  await evaluar(`[...[...document.querySelectorAll("tbody tr")].find(r => r.textContent.includes("SETP")).querySelectorAll("button")].find(b => b.textContent.trim() === "Asignar").click()`); await espera(1500);
  await elegir("Sucursal", "Centro");
  await foto("integ-13-rango-asignar");
  await clic("Guardar", ".panel button, .mud-dialog button", { ms: 2000 });
  await evaluar(`document.querySelector(".mud-table-container, .mud-table-root")?.closest("div[style*=overflow], .mud-table-container")?.scrollTo?.(1e5, 0)`);
  await foto("integ-14-rango-asignado");
}

// ═════════════════════════════════════ Fases 3 y 5: turnos por la API ═════════════════════════════════════
const CANASTAS = [
  [["ABA-001", 2], ["LAC-001", 3], ["ABA-005", 1], ["FRU-101", 1.25]], [["BEB-001", 2], ["ABA-012", 1]], [["CAR-201", 1.8], ["FRU-102", 2.4], ["FRU-103", 0.9], ["ABA-004", 1]],
  [["ASE-001", 1], ["ASE-002", 3], ["ASE-003", 1]], [["BEB-002", 3], ["BEB-005", 6]], [["LAC-006", 1], ["ABA-007", 1], ["LAC-003", 1]], [["ABA-010", 4], ["ABA-011", 2], ["CAR-202", 1.1]],
  [["FRU-104", 1.6], ["FRU-105", 0.85], ["LAC-004", 2]], [["BEB-003", 4], ["BEB-004", 2]], [["ABA-002", 1], ["ABA-003", 1], ["ABA-009", 1], ["ABA-006", 1]], [["ASE-004", 1], ["ASE-005", 2]],
  [["CAR-203", 1.3], ["LAC-002", 2], ["LAC-005", 1]], [["ABA-008", 1], ["LAC-001", 2]], [["BEB-001", 1], ["BEB-003", 2], ["ABA-012", 2]],
];
const MEDIOS = ["EFECTIVO", "EFECTIVO", "DEBITO", "EFECTIVO", "NEQUI", "CREDITO", "EFECTIVO", "MIXTO", "EFECTIVO", "DAVIPLATA", "EFECTIVO", "TRANSFERENCIA", "EFECTIVO", "EFECTIVO"];
async function turnoApi(cajero, ventas, cerrar) {
  const owner = await Pos.login();
  const caja = await Pos.caja(cajero);
  const productos = (await owner.get("catalog/products?pageSize=200")).items;
  const metodos = await caja.get("cash/payment-methods");
  const M = (c) => metodos.find((m) => m.code === c).id;
  const den = await caja.get("cash/denominations");
  const D = (v) => den.find((d) => d.value === v).id;
  const clientes = (await owner.get("customers?pageSize=20")).items ?? [];
  const base = [[50000, 2], [20000, 4], [10000, 5], [5000, 4], [1000, 10]];
  const sesion = await caja.post("cash/sessions", { openingFloat: base.reduce((a, [v, q]) => a + v * q, 0), openingCount: base.map(([v, q]) => ({ paymentMethodId: M("EFECTIVO"), denominationId: D(v), quantity: q })) });
  for (let i = 0; i < ventas; i++) {
    const venta = await caja.post("sales");
    for (const [sku, q] of CANASTAS[i % CANASTAS.length]) {
      const p = productos.find((x) => x.sku === sku);
      await caja.post(`sales/${venta.id}/lines`, p.baseUnitCode === "KG" ? { productId: p.id, weightKg: q, weightSource: "SCALE" } : { code: p.primaryBarcode, quantity: q });
    }
    if (i % 5 === 2 && clientes.length) await caja.put(`sales/${venta.id}/customer`, { partyId: clientes[i % clientes.length].partyId });
    const total = (await caja.get(`sales/${venta.id}`)).amountDue;
    const medio = MEDIOS[i % MEDIOS.length];
    const payments = medio === "EFECTIVO" ? [{ paymentMethodId: M("EFECTIVO"), amount: Math.ceil(total / 10000) * 10000 }]
      : medio === "MIXTO" ? [{ paymentMethodId: M("NEQUI"), amount: 20000, reference: "NQ-" + (55100 + i) }, { paymentMethodId: M("EFECTIVO"), amount: Math.ceil((total - 20000) / 5000) * 5000 }]
        : [{ paymentMethodId: M(medio), amount: total, reference: (/DEBITO|CREDITO/.test(medio) ? "APR-" : "REF-") + (812300 + i * 7) }];
    await caja.post(`sales/${venta.id}/complete`, { payments }, { "Idempotency-Key": `capturas-${venta.id}` });
  }
  if (!cerrar) { console.log(`  turno ${sesion.number} abierto con ${ventas} ventas`); return; }
  await caja.post(`cash/sessions/${sesion.id}/cash-in`, { amount: 50000, reason: "Cambio traído del banco" });
  await caja.post("expenses/from-cash", { categoryId: (await owner.get("expenses/categories")).find((c) => c.name === "Aseo y cafetería").id, description: "Bolsas de basura y jabón para el baño",
    amount: 18500, taxAmount: 0, paymentMethodId: M("EFECTIVO"), partyId: null, cashSessionId: sesion.id, reference: null, businessDate: null });
  const retiro = { amount: 300000, reason: "Sangría a la caja fuerte" };
  try { await caja.post(`cash/sessions/${sesion.id}/withdrawals`, retiro); } catch (e) {
    if (e.status !== 403) throw e;
    const g = await caja.post("auth/authorizations", { supervisorCode: SUPERVISOR.codigo, supervisorPin: SUPERVISOR.pin, permissionCode: e.data.permission, action: e.data.action, targetId: e.data.targetId, targetType: e.data.targetType ?? "CashSession", reason: "Sangría autorizada" });
    await caja.post(`cash/sessions/${sesion.id}/withdrawals`, retiro, { "X-Authorization-Grant": g.grantId });
  }
  const x = await owner.get(`cash/sessions/${sesion.id}/report-x`);
  await caja.post(`cash/sessions/${sesion.id}/start-closing`);
  const count = x.totals.filter((t) => t.expected > 0 || t.code === "EFECTIVO").map((t) => ({ paymentMethodId: t.paymentMethodId, amount: t.code === "EFECTIVO" ? t.expected - 2000 : t.expected }));
  await caja.post(`cash/sessions/${sesion.id}/close`, { count, differenceNote: "Faltaron $2.000 en monedas; se revisa con el cajero" });
  console.log(`  turno ${sesion.number} cerrado (reporte Z con diferencia de -$2.000)`);
}

// ═════════════════════════════════════ Fase 4: la caja, paso a paso (Ana Cajera) ═════════════════════════════════════
const bascula = (cuerpo) => fetch(AGENTE + "scale/simulator", { method: "POST", headers: { "Content-Type": "application/json" }, body: J(cuerpo) });
async function escanear(codigo, ms = 1300) { await evaluar(`window.pos.focus("escaner")`); await teclear(codigo); await espera(150); await tecla("Enter", ms); }
async function supervisor(motivo) {
  await escribir("Código del supervisor", SUPERVISOR.codigo); await escribir("PIN", SUPERVISOR.pin); await escribir("Motivo (opcional)", motivo);
}
// Después de leer la báscula, F10 a veces no abre el cobro hasta recargar la pantalla (se reporta en INDICE.md): se recarga y se repite.
async function teclaCaja(key, donde, comprobar) {
  await tecla(key, 1500);
  if (comprobar && !(await evaluar(comprobar))) {
    avisosError.push(`${key} sin efecto en ${donde}`);
    console.warn(`  ! ${key} no abrió el panel en ${donde}: se recarga la caja y se repite`);
    await ir(await evaluar("location.href"), 7000);
    await tecla(key, 1500);
    if (!(await evaluar(comprobar))) throw new Error(`${key} no funcionó en ${donde}`);
  }
}
async function faseCaja() {
  const owner = await Pos.login();
  await salirDeLaSesion();
  await fotoCaja("ingreso");
  await escribir("Código de cajero", ANA.codigo); await escribir("PIN", ANA.pin);
  await fotoCaja("ingreso-codigo-y-pin");
  await clic("Entrar a la caja", "button", { ms: 5000 });
  await fotoCaja("aviso-modo-demostracion", { desarrollo: true });
  await fotoCaja("abrir-turno");
  for (const [l, v] of [["En billetes de $ 50.000", "100000"], ["En billetes de $ 20.000", "80000"], ["En billetes de $ 10.000", "50000"], ["En billetes de $ 5.000", "20000"],
    ["En billetes de $ 2.000", "10000"], ["En monedas de $ 1.000", "20000"], ["En monedas de $ 500", "10000"], ["En monedas de $ 200", "5000"], ["En monedas de $ 100", "5000"]]) await escribir(l, v);
  await sinFoco();
  await fotoCaja("abrir-turno-conteo");
  await clic("Abrir turno", "button", { ms: 3000 });
  await fotoCaja("venta-vacia");
  // Venta 1: escáner, promociones, búsqueda por nombre, báscula, consulta de precio, cliente, cantidad y descuento con supervisor.
  await escanear("2*" + CODIGO["ABA-001"]); await escanear("3*" + CODIGO["LAC-001"]); await escanear("3*" + CODIGO["BEB-002"]);
  await escanear(CODIGO["ABA-004"]); await escanear(CODIGO["ASE-002"]);
  await fotoCaja("venta-con-productos-y-promociones");
  await evaluar(`window.pos.focus("escaner")`); await teclear("leche"); await espera(1600);
  await fotoCaja("busqueda-por-nombre");
  await tecla("Escape");
  await evaluar(`(() => { const i = document.getElementById("escaner"); i.value = ""; i.dispatchEvent(new Event("input", { bubbles: true })); })()`); await espera(400);
  await evaluar(`window.pos.focus("escaner")`); await teclear("cafe sello"); await espera(1600); await tecla("ArrowDown"); await tecla("Enter", 1500);
  await bascula({ weightKg: 0 }); await espera(500);
  await escanear("FRU-101", 2500);
  await fotoCaja("bascula-ponga-el-producto");
  await bascula({ weightKg: 1.256, stable: false }); await espera(800);
  await fotoCaja("bascula-esperando-peso-estable");
  await bascula({ weightKg: 1.256, stable: true }); await espera(3500); await bascula({ weightKg: 0 });
  await fotoCaja("venta-con-producto-pesado");
  await tecla("F5", 1000); await teclear("detergente"); await espera(1600);
  await fotoCaja("consulta-de-precio-lista");
  await tecla("ArrowDown"); await tecla("Enter", 1500);
  await fotoCaja("consulta-de-precio");
  await tecla("Escape", 800);
  await tecla("F3", 1000); await teclear("rios"); await espera(1800);
  await fotoCaja("cliente-buscar");
  await clic("María Fernanda Ríos Patiño", ".lista-elegir button", { exacto: false, ms: 1500 });
  await fotoCaja("cliente-asignado");
  await clic("Café Sello Rojo 500 g", ".linea-nombre", { ms: 500 });
  await tecla("F4", 1000); await reemplazar("2");
  await fotoCaja("cambiar-cantidad");
  await tecla("Enter", 1500);
  await clic("Aceite de girasol Premier 900 ml", ".linea-nombre", { ms: 500 });
  await tecla("F6", 1000); await reemplazar("10"); await escribir("Motivo", "Empaque abollado");
  await fotoCaja("descuento");
  await clic("Aplicar descuento", ".panel button", { ms: 1800 });
  await fotoCaja("autorizacion-de-supervisor");
  await supervisor("Producto con empaque abollado");
  await fotoCaja("autorizacion-de-supervisor-datos");
  await clic("Autorizar", ".panel button", { ms: 2000 });
  await fotoCaja("venta-con-cliente-y-descuento");
  await teclaCaja("F10", "cobro de la venta 1", `!!document.querySelector("#t-cobro")`);
  await fotoCaja("cobro");
  await enfocar("Recibido en Efectivo"); await reemplazar("100000"); await espera(600);
  await fotoCaja("cobro-efectivo-con-cambio");
  await tecla("F10", 3500);
  await fotoCaja("venta-cobrada");
  // Venta 2: pago mixto (Nequi + efectivo).
  await escanear(CODIGO["ABA-002"]); await escanear(CODIGO["ABA-003"]); await escanear(CODIGO["ABA-011"]); await escanear("2*" + CODIGO["BEB-004"]);
  await teclaCaja("F10", "cobro de la venta 2", `!!document.querySelector("#t-cobro")`);
  await clic("Nequi", ".cobro-medios button", { ms: 500 });
  await enfocar("Recibido en Nequi"); await reemplazar("15000"); await escribir("Referencia", "NQ-48213");
  await clic("Nequi", ".cobro-medios button", { ms: 800 });
  await fotoCaja("pago-mixto-primer-medio");
  await clic("Efectivo", ".cobro-medios button", { ms: 600 });
  await enfocar("Recibido en Efectivo"); await reemplazar("20000");
  await clic("Efectivo", ".cobro-medios button", { ms: 800 });
  await fotoCaja("pago-mixto-completo");
  await tecla("F10", 3500);
  // Venta 3: tarjeta débito con la referencia del datáfono.
  await escanear(CODIGO["ASE-001"]); await escanear(CODIGO["ASE-004"]); await escanear(CODIGO["LAC-004"]);
  await teclaCaja("F10", "cobro de la venta 3", `!!document.querySelector("#t-cobro")`);
  await clic("Tarjeta débito", ".cobro-medios button", { ms: 600 });
  await escribir("Referencia", "APR-552190");
  await fotoCaja("cobro-tarjeta-con-referencia");
  await tecla("F10", 3500);
  await fotoCaja("venta-cobrada-con-tarjeta");
  // Venta 4: Sistecrédito (se muestra el panel; generar el cobro necesita las llaves reales e Internet) y se cobra en efectivo.
  await escanear(CODIGO["ASE-003"]); await escanear("2*" + CODIGO["ASE-001"]); await escanear("2*" + CODIGO["ABA-004"]); await escanear(CODIGO["ABA-007"]);
  await teclaCaja("F10", "cobro de la venta 4", `!!document.querySelector("#t-cobro")`);
  await clic("Sistecrédito", ".cobro-medios button", { ms: 800 });
  await escribir("Número de documento del cliente", "1036600111"); await sinFoco();
  await fotoCaja("cobro-sistecredito");
  await clic("Efectivo", ".cobro-medios button", { ms: 600 });
  await enfocar("Recibido en Efectivo"); await reemplazar("100000");
  await tecla("F10", 3500);
  // Venta 5: anular línea, suspender, otra venta mientras tanto, recuperar y cancelar (con supervisor).
  await escanear("7*" + CODIGO["BEB-005"]); await escanear(CODIGO["ABA-012"]); await escanear(CODIGO["BEB-003"]);
  await clic("Agua Cristal 600 ml", ".linea-nombre", { ms: 400 });
  await tecla("Delete", 1500);
  await fotoCaja("linea-anulada");
  await tecla("F8", 1800);
  await fotoCaja("venta-suspendida");
  await escanear(CODIGO["LAC-003"]); await escanear("FRU-104", 1500);
  await bascula({ weightKg: 0.84, stable: true }); await espera(3500); await bascula({ weightKg: 0 });
  await teclaCaja("F10", "cobro de la venta 6", `!!document.querySelector("#t-cobro")`);
  await enfocar("Recibido en Efectivo"); await reemplazar("20000"); await tecla("F10", 3500);
  await tecla("F9", 1500);
  await fotoCaja("ventas-suspendidas");
  await evaluar(`document.querySelector(".lista-elegir button").click()`); await espera(1800);
  await fotoCaja("venta-recuperada");
  await tecla("F7", 1200);
  await fotoCaja("cancelar-venta");
  await clic("Sí, cancelar venta", ".panel button", { ms: 1800 });
  await supervisor("El cliente desistió de la compra");
  await clic("Autorizar", ".panel button", { ms: 2000 });
  await asegurar("cancelar venta");
  await tecla("F1", 1200);
  await fotoCaja("ayuda-atajos");
  await tecla("Escape", 800);
}
async function faseCajaFin() {
  nCaja = 34;
  const owner = await Pos.login();
  // Se vuelve a abrir la pantalla de la caja antes de pesar otra vez (la segunda lectura de báscula en la misma pantalla puede terminar en
  // "Ocurrió un error inesperado"; se reporta en INDICE.md). Si la sesión de la caja se perdió, se entra de nuevo con el código.
  await ir(BASE + "caja", 7000);
  if (!(await evaluar(`!!document.getElementById("escaner")`))) {
    await ir(BASE + "ingresar", 6000); await escribir("Código de cajero", ANA.codigo); await escribir("PIN", ANA.pin); await clic("Entrar a la caja", "button", { ms: 6000 });
  }
  // Venta 7: la báscula no responde (se configura una Toledo en un puerto sin báscula) → peso digitado con supervisor; vista a 1366×768.
  const caja1 = (await owner.get("organization/terminals"))[0];
  await owner.put(`organization/terminals/${caja1.id}/scale`, { enabled: true, driver: "TOLEDO_8217", port: "COM7", baudRate: 9600, dataBits: 7, parity: "EVEN", stopBits: 1,
    readMode: "REQUEST", requestCommand: "W", timeoutMs: 1000, decimals: 3, brand: "Mettler Toledo", model: "8217", catalogCode: null, stableWaitSeconds: 3, emptyWaitSeconds: 5, allowManualWeight: true });
  await ir(BASE + "caja", 7000);
  await evaluar(`window.pos.focus("escaner")`); await teclear("papa pastusa"); await espera(1600); await tecla("ArrowDown"); await tecla("Enter", 1000);
  for (let i = 0; i < 15 && (await evaluar(`document.querySelector(".panel h2")?.textContent`)) !== "Digitar el peso"; i++) await espera(1000);
  await reemplazar("2,35");
  await fotoCaja("peso-digitado");
  await tecla("Enter", 2000);
  await supervisor("La báscula no responde");
  await clic("Autorizar", ".panel button", { ms: 2000 });
  await tamano(1366, 768);
  await escanear(CODIGO["LAC-001"]); await escanear("2*" + CODIGO["ABA-001"]); await escanear(CODIGO["ABA-005"]); await escanear(CODIGO["BEB-001"]); await escanear(CODIGO["ASE-002"]);
  await fotoCaja("venta-1366x768");
  await teclaCaja("F10", "cobro de la venta 7", `!!document.querySelector("#t-cobro")`);
  await enfocar("Recibido en Efectivo"); await reemplazar("50000");
  await fotoCaja("cobro-1366x768");
  await tecla("F10", 3500);
  await fotoCaja("venta-cobrada-1366x768");
  await tamano(1280, 800);
  // Cierre: conteo por monto (lo esperado se lee del reporte X para que cuadre) y reporte Z.
  const sesiones = await owner.get("cash/sessions?limit=10");
  const abierta = (sesiones.items ?? sesiones).find((s) => s.status === "OPEN");
  const x = await owner.get(`cash/sessions/${abierta.id}/report-x`);
  let efectivo = x.totals.find((t) => t.code === "EFECTIVO").expected;
  await clic("Cerrar turno", "button", { ms: 2500 });
  await fotoCaja("cerrar-turno");
  for (const [v, l] of [[100000, "billetes"], [50000, "billetes"], [20000, "billetes"], [10000, "billetes"], [5000, "billetes"], [2000, "billetes"], [1000, "monedas"], [500, "monedas"], [200, "monedas"], [100, "monedas"], [50, "monedas"]]) {
    const q = v >= 50000 ? Math.min(Math.floor(efectivo / v), v === 100000 ? 2 : 5) : Math.floor(efectivo / v);
    if (q > 0) { await escribir(`En ${l} de $ ${miles(v)}`, String(q * v)); efectivo -= q * v; }
  }
  for (const t of x.totals.filter((t) => t.code !== "EFECTIVO" && t.expected > 0)) await escribir(t.name, String(t.expected));
  await sinFoco();
  await fotoCaja("cerrar-turno-conteo");
  await abajo(); await fotoCaja("cerrar-turno-conteo-continuacion", { ms: 300 });
  await clic("Confirmar cierre", "button", { ms: 3500 });
  await arriba(); await fotoCaja("reporte-z");
  await abajo(); await fotoCaja("reporte-z-continuacion", { ms: 300 });
  await clic("Terminar", "button", { ms: 3000 });
}

// ═════════════════════════════════════ Fase 6: administración ═════════════════════════════════════
async function pestanas(prefijo, nombres) {
  for (const [pestana, nombre] of nombres) { await clic(pestana, ".mud-tab", { exacto: false, ms: 1800 }); await foto(`${prefijo}-${nombre}`); }
}
const cerrarPanel = () => evaluar(`[...document.querySelectorAll(".panel button, .mud-dialog button")].find(b => /^(Cancelar|Cerrar|Listo)/.test(b.textContent.trim()))?.click()`).then(() => espera(800));
const fallidas = [];
/** Paso de la administración: si falla, se anota, se cierran las ventanas abiertas y se sigue con el siguiente. */
async function paso(fn) {
  try { await fn(); } catch (e) {
    fallidas.push(e.message.slice(0, 160));
    console.warn("  ! paso omitido:", e.message.slice(0, 160));
    try { await tecla("Escape", 400); await cerrarPanel(); } catch { /* nada abierto */ }
  }
}
async function faseAdmin() {
  await entrarAdministracion(false);
  await ruta("admin", 5000);
  await paso(async () => {
    await foto("adm-02-tablero");
  });
  await paso(async () => {
    await abajo(); await foto("adm-03-tablero-continuacion", { ms: 300 });
  });
  await paso(async () => {
    await arriba();
  });
  // (adm-04, el menú del usuario, no abre en Edge sin ventana: ver INDICE.md)
  await paso(async () => {
    await ruta("cambiar-clave"); await foto("adm-05-cambiar-contrasena");
  });
  await paso(async () => {
    await ruta("admin", 4000);
  });
  await paso(async () => {
    await evaluar(`document.querySelector('[aria-label="Cambiar a modo oscuro"]').click()`); await espera(1200);
  });
  await paso(async () => {
    await foto("adm-06-tablero-modo-oscuro");
  });
  await paso(async () => {
    await evaluar(`document.querySelector('[aria-label="Cambiar a modo claro"]').click()`); await espera(1000);
  });
  await paso(async () => {
    await evaluar(`document.querySelector('[aria-label="Contraer el menú"]').click()`); await espera(1000);
  });
  await paso(async () => {
    await foto("adm-07-menu-compacto");
  });
  await paso(async () => {
    await evaluar(`document.querySelector('[aria-label="Expandir el menú"]').click()`); await espera(1000);
  });
  await paso(async () => {
    await ruta("caja", 3500); await foto("adm-08-caja-desde-la-administracion");
  });
  // Inventario
  await paso(async () => {
    await ruta("admin/productos"); await foto("adm-10-productos-lista");
  });
  await paso(async () => {
    await evaluar(`[...document.querySelectorAll("tbody tr")].find(r => r.textContent.includes("Café Sello Rojo")).querySelector("button").click()`); await espera(1200);
  });
  await paso(async () => {
    await enfocar("Nuevo precio"); await reemplazar("17500"); await foto("adm-11-producto-cambiar-precio");
  });
  await paso(async () => {
    await cerrarPanel();
  });
  await paso(async () => {
    await ruta("admin/productos/nuevo"); await foto("adm-12-producto-nuevo");
  });
  await paso(async () => {
    await ruta("admin/productos"); await clicFila("Café Sello Rojo");
  });
  await paso(async () => {
    await pestanas("adm", [["Datos", "13-producto-datos"], ["Precios", "14-producto-precios"], ["Presentaciones", "15-producto-presentaciones"],
      ["Códigos de barras", "16-producto-codigos-de-barras"], ["Impuestos", "17-producto-impuestos"], ["Existencias", "18-producto-existencias"]]);
  });
  await paso(async () => {
    const tomate = (await (await Pos.login()).get("catalog/products?search=tomate")).items[0];
    await ruta("admin/productos/" + tomate.id, 4000); await foto("adm-19-producto-por-peso");
  });
  await paso(async () => {
    await ruta("admin/existencias"); await foto("adm-20-existencias");
    await marcar("Solo bajo el mínimo"); await espera(1500); await foto("adm-21-existencias-bajo-el-minimo");
  });
  await paso(async () => {
    await ruta("admin/conteos"); await foto("adm-22-conteos-lista");
  });
  await paso(async () => {
    await ruta("admin/conteos/nuevo"); await foto("adm-23-conteo-nuevo");
  });
  await paso(async () => {
    await ruta("admin/conteos"); await clicFila("Piso"); await foto("adm-24-conteo-detalle");
  });
  await paso(async () => {
    await ruta("admin/traslados"); await foto("adm-25-traslados-lista");
  });
  await paso(async () => {
    await ruta("admin/traslados/nuevo"); await foto("adm-26-traslado-nuevo");
  });
  await paso(async () => {
    await ruta("admin/traslados"); await clicFila("tránsito").catch(() => clicFila("Despach")); await foto("adm-27-traslado-detalle-en-transito");
  });
  await paso(async () => {
    await ruta("admin/ajustes"); await foto("adm-28-ajustes-lista");
  });
  await paso(async () => {
    await clicFila("S01-000002"); await foto("adm-29-ajuste-detalle-averia");
  });
  await paso(async () => {
    await ruta("admin/ajustes"); await clic("Ajuste rápido", "button", { ms: 1500 }); await foto("adm-38-ajuste-rapido"); await cerrarPanel();
  });
  await paso(async () => {
    await ruta("admin/importar"); // (las capturas 30–36 se toman en la fase de datos)
  });
  // Compras y proveedores
  await paso(async () => {
    await ruta("admin/compras"); await foto("adm-40-compras-facturas");
  });
  await paso(async () => {
    await clic("Órdenes de compra", ".mud-tab", { ms: 1500 }); await foto("adm-41-compras-ordenes");
  });
  await paso(async () => {
    await clicFila("Aseo Total"); await foto("adm-43-orden-detalle-borrador");
  });
  await paso(async () => {
    await ruta("admin/compras"); await clic("Facturas de compra", ".mud-tab", { ms: 1200 }); await clicFila("Distribuidora Láctea"); await foto("adm-42-compra-detalle");
  });
  await paso(async () => {
    await ruta("admin/compras/nueva"); await foto("adm-44-compra-nueva");
  });
  await paso(async () => {
    await ruta("admin/compras/ordenes/nueva"); await foto("adm-45-orden-nueva");
  });
  await paso(async () => {
    await ruta("admin/proveedores"); await foto("adm-46-proveedores-lista");
  });
  await paso(async () => {
    await clicFila("Distribuidora Láctea");
  });
  await paso(async () => {
    await pestanas("adm", [["Datos", "47-proveedor-datos"], ["Estado de cuenta", "48-proveedor-estado-de-cuenta"]]);
  });
  await paso(async () => {
    await ruta("admin/proveedores/nuevo"); await foto("adm-49-proveedor-nuevo");
  });
  await paso(async () => {
    await ruta("admin/cuentas-por-pagar");
  });
  await paso(async () => {
    await pestanas("adm", [["Facturas por pagar", "50-cxp-facturas-por-pagar"], ["Cartera por edades", "51-cxp-cartera-por-edades"], ["Vencimientos", "52-cxp-vencimientos"], ["Pagos", "53-cxp-pagos"]]);
  });
  await paso(async () => {
    await clic("Facturas por pagar", ".mud-tab", { ms: 1200 });
  });
  await paso(async () => {
    await evaluar(`[...[...document.querySelectorAll("tbody tr")].find(r => r.textContent.includes("Andina")).querySelectorAll("a, button")].find(e => e.textContent.trim() === "Pagar").click()`); await espera(3000);
  });
  await paso(async () => {
    await elegir("Medio de pago", "Transferencia"); await escribir("Referencia", "TRF-005120"); await sinFoco();
    await foto("adm-54-registrar-pago-proveedor");
  });
  // Clientes y promociones
  await paso(async () => {
    await ruta("admin/clientes"); await clic("Clientes", ".mud-tab", { ms: 1200 }); await foto("adm-60-clientes-lista");
  });
  await paso(async () => {
    await clic("Grupos", ".mud-tab", { ms: 1500 }); await foto("adm-61-clientes-grupos");
  });
  await paso(async () => {
    await clic("Nuevo grupo", "button", { ms: 1500 }); await foto("adm-62-grupo-nuevo"); await cerrarPanel();
  });
  await paso(async () => {
    await ruta("admin/clientes/nuevo"); await foto("adm-63-cliente-nuevo");
  });
  await paso(async () => {
    await ruta("admin/clientes"); await clic("Clientes", ".mud-tab", { ms: 1200 }); await clicFila("María Fernanda");
  });
  await paso(async () => {
    await pestanas("adm", [["Datos", "64-cliente-datos"], ["Compras", "65-cliente-compras"], ["Historial", "66-cliente-historial"]]);
  });
  await paso(async () => {
    await ruta("admin/promociones"); await foto("adm-67-promociones-lista");
  });
  await paso(async () => {
    await clicFila("Combo desayuno");
  });
  await paso(async () => {
    await pestanas("adm", [["Resumen", "68-promocion-detalle"]]);
    await clic("Simular", ".mud-tab", { ms: 1500 });
    await evaluar(`[...document.querySelectorAll("button.mud-button-root")].find(b => b.textContent.trim() === "Simular").click()`); await espera(2000);
    await foto("adm-69-promocion-simular");
  });
  await paso(async () => {
    await ruta("admin/promociones/nueva"); await foto("adm-70-promocion-nueva");
    await elegir("Tipo de promoción", null); await foto("adm-71-promocion-tipos"); await tecla("Escape", 500);
  });
  // Ventas y caja
  await paso(async () => {
    await ruta("admin/jornadas"); await foto("adm-72-turnos-lista");
  });
  await paso(async () => {
    await clicFila("Jorge Cajero");
  });
  await paso(async () => {
    await pestanas("adm", [["Totales por medio de pago", "73-turno-cerrado-totales"], ["Movimientos", "74-turno-cerrado-movimientos"], ["Reporte Z", "75-turno-cerrado-reporte-z"]]);
  });
  await paso(async () => {
    await ruta("admin/jornadas"); await clicFila("Abierta");
  });
  await paso(async () => {
    await foto("adm-76-turno-abierto-totales");
  });
  await paso(async () => {
    await clic("Reporte X", ".mud-tab", { exacto: false, ms: 1800 }); await foto("adm-77-turno-abierto-reporte-x");
  });
  await paso(async () => {
    await clic("Cerrar como supervisor", "button", { ms: 1500 }); await foto("adm-78-turno-cerrar-como-supervisor"); await cerrarPanel();
  });
  await paso(async () => {
    await ruta("admin/gastos"); await foto("adm-79-gastos-lista");
  });
  await paso(async () => {
    await clic("Registrar gasto", "button", { ms: 1500 }); await foto("adm-80-gasto-registrar"); await cerrarPanel();
  });
  await paso(async () => {
    await clic("Categorías", ".mud-tab", { ms: 1500 }); await foto("adm-81-gastos-categorias");
  });
  await paso(async () => {
    await ruta("admin/reportes"); await foto("adm-82-reportes");
  });
  await paso(async () => {
    for (const [rep, n] of [["Resumen diario de ventas", "83-reporte-resumen-diario"], ["Ventas por medio de pago", "84-reporte-ventas-por-medio-de-pago"], ["Ventas por producto", "85-reporte-ventas-por-producto"],
      ["Utilidad por producto", "86-reporte-utilidad-por-producto"], ["Inventario valorizado", "87-reporte-inventario-valorizado"], ["Cierres de caja", "88-reporte-cierres-de-caja"]]) {
      await clic(rep, ".mud-list-item", { ms: 1000 }); await clic("Ver", "button", { ms: 2500 }); await foto("adm-" + n);
    }
  });
  // Facturación electrónica: documentos aceptados y su detalle.
  await paso(async () => {
    await ruta("admin/facturacion");
  });
  await paso(async () => {
    await clic("Documentos", ".mud-tab", { ms: 1500 }); await clic("Consultar", "button", { ms: 2500 }); await foto("integ-15-facturacion-documentos");
  });
  await paso(async () => {
    await clic("Ver", "button", { ms: 2000 }); await foto("integ-16-facturacion-documento-detalle"); await cerrarPanel();
  });
  await paso(async () => {
    await clic("Alertas y conciliación", ".mud-tab", { ms: 1500 }); await clic("Consultar", "button", { ms: 2500 }); await foto("integ-17-facturacion-alertas-y-conciliacion");
  });
  await paso(async () => {
    await ruta("admin/sincronizacion"); await foto("integ-18-sincronizacion");
  });
  // Administración
  await paso(async () => {
    await ruta("admin/usuarios"); await foto("adm-90-usuarios-lista");
  });
  await paso(async () => {
    await clic("Nuevo usuario", "button", { ms: 1500 });
  });
  await paso(async () => {
    await escribir("Usuario", "maria.caja"); await escribir("Nombre completo", "María Gómez"); await escribir("Código de caja", "603");
  });
  await paso(async () => {
    await sinFoco(); await foto("adm-91-usuario-nuevo");
  });
  await paso(async () => {
    await elegir("Rol", null); await foto("adm-92-usuario-nuevo-roles");
  });
  await paso(async () => {
    await tecla("Escape", 500); await cerrarPanel();
  });
  await paso(async () => {
    await ruta("admin/auditoria"); await foto("adm-94-auditoria");
  });
  await paso(async () => {
    await clic("Verificar la integridad", "button", { ms: 4000 }); await foto("adm-95-auditoria-verificada");
  });
  await paso(async () => {
    await ruta("admin/licencia"); await foto("adm-96-licencia", { desarrollo: true });
  });
  await paso(async () => {
    await ruta("admin/backups"); await foto("adm-97-backups", { desarrollo: true });
  });
  await paso(async () => {
    await ruta("admin/actualizaciones"); await foto("adm-98-actualizaciones");
  });
}

// ═════════════════════════════════════ Fase 7: tiquetes impresos (ESC/POS del agente → imagen) ═════════════════════════════════════
const CP850 = "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜø£Ø×ƒáíóúñÑªº¿®¬½¼¡«»░▒▓│┤ÁÂÀ©╣║╗╝¢¥┐└┴┬├─┼ãÃ╚╔╩╦╠═╬¤ðÐÊËÈıÍÎÏ┘┌█▄¦Ì▀ÓßÔÒõÕµþÞÚÛÙýÝ¯´\u00AD±‗¾¶§÷¸°¨·¹³²■\u00A0";
const C128 = "212222 222122 222221 121223 121322 131222 122213 122312 132212 221213 221312 231212 112232 122132 122231 113222 123122 123221 223211 221132 221231 213212 223112 312131 311222 321122 321221 312212 322112 322211 212123 212321 232121 111323 131123 131321 112313 132113 132311 211313 231113 231311 112133 112331 132131 113123 113321 133121 313121 211331 231131 213113 213311 213131 311123 311321 331121 312113 312311 332111 314111 221411 431111 111224 111422 121124 121421 141122 141221 112214 112412 122114 122411 142112 142211 241211 221114 413111 241112 134111 111242 121142 121241 114212 124112 124211 411212 421112 421211 212141 214121 412121 111143 111341 131141 114113 114311 411113 411311 113141 114131 311141 411131 211412 211214 211232 2331112".split(" ");
function code128(datos, alto) {
  const valores = [104, ...[...datos].map((c) => c.charCodeAt(0) - 32)];
  valores.push(valores.reduce((a, v, i) => a + v * Math.max(i, 1), 0) % 103, 106);
  let x = 20, barras = "";
  for (const v of valores) [...C128[v]].forEach((w, i) => { if (i % 2 === 0) barras += `<rect x="${x}" y="0" width="${2 * w}" height="${alto}"/>`; x += 2 * w; });
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${x + 20}" height="${alto}">${barras}</svg>`;
}
function qrSvg(datos) {
  const fsx = join(mkdtempSync(join(tmpdir(), "capturas-uso-qr-")), "qr.fsx");
  writeFileSync(fsx, `#r @"${join(raiz, "src/Server/Pos.Server.Host/bin/Debug/net10.0/QRCoder.dll")}"\nopen QRCoder\nlet d = (new QRCodeGenerator()).CreateQrCode(System.IO.File.ReadAllText(fsi.CommandLineArgs.[1]), QRCodeGenerator.ECCLevel.M)\nfor row in d.ModuleMatrix do printfn "%s" (System.String([| for b in row -> if b then '1' else '0' |]))\n`);
  writeFileSync(fsx + ".txt", datos);
  const r = spawnSync("dotnet", ["fsi", fsx, fsx + ".txt"], { encoding: "utf8" });
  const filas = (r.stdout || "").trim().split(/\r?\n/).filter((l) => /^[01]+$/.test(l));
  if (!filas.length) return `<div style="border:2px solid #000;width:160px;height:160px;margin:auto;display:grid;place-items:center">QR</div>`;
  const m = 4, n = filas.length;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${n * m}" height="${n * m}" shape-rendering="crispEdges">${filas.map((f, y) => [...f].map((b, x) => b === "1" ? `<rect x="${x * m}" y="${y * m}" width="${m}" height="${m}"/>` : "").join("")).join("")}</svg>`;
}
function escposAHtml(bytes) {
  const lineas = []; let actual = [], alin = 0, negrita = false, ancho = 1, alto = 1, codigo = { alto: 80 }, qr = "";
  const cerrar = () => { lineas.push({ alin, partes: actual }); actual = []; };
  const texto = (t) => { const ult = actual[actual.length - 1]; if (ult && ult.t !== undefined && ult.negrita === negrita && ult.ancho === ancho && ult.alto === alto) ult.t += t; else actual.push({ t, negrita, ancho, alto }); };
  for (let i = 0; i < bytes.length; i++) {
    const b = bytes[i];
    if (b === 0x1b) {
      const c = bytes[++i];
      if (c === 0x40) { alin = 0; negrita = false; ancho = alto = 1; } else if (c === 0x61) alin = bytes[++i] % 48; else if (c === 0x45) negrita = (bytes[++i] & 1) === 1;
      else if (c === 0x64) { i++; cerrar(); } else if (c === 0x70) i += 3; else if (c === 0x74) i++; else i++;
    } else if (b === 0x1d) {
      const c = bytes[++i];
      if (c === 0x21) { const n = bytes[++i]; ancho = (n >> 4) + 1; alto = (n & 15) + 1; } else if (c === 0x68) codigo.alto = bytes[++i]; else if (c === 0x77 || c === 0x48 || c === 0x66) i++;
      else if (c === 0x56) { const m = bytes[++i]; if (m === 0x41 || m === 0x42) i++; }
      else if (c === 0x6b) { const m = bytes[++i]; let d = ""; if (m >= 65) { const n = bytes[++i]; for (let k = 0; k < n; k++) d += String.fromCharCode(bytes[++i]); } else { while (bytes[i + 1] !== 0) d += String.fromCharCode(bytes[++i]); i++; }
        actual.push({ html: code128(d.replace(/^\{B/, ""), codigo.alto) }); }
      else if (c === 0x28 && bytes[i + 1] === 0x6b) { const pl = bytes[i + 2], ph = bytes[i + 3], len = pl + ph * 256, cn = bytes[i + 4], fn = bytes[i + 5];
        if (cn === 49 && fn === 80) qr = Buffer.from(bytes.slice(i + 7, i + 3 + len + 1)).toString("latin1"); if (cn === 49 && fn === 81 && qr) actual.push({ html: qrSvg(qr) }); i += 3 + len; }
    } else if (b === 0x0a) cerrar();
    else if (b >= 0x20) texto(b < 0x80 ? String.fromCharCode(b) : CP850[b - 0x80]);
  }
  if (actual.length) cerrar();
  const esc = (t) => t.replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
  return lineas.map(({ alin, partes }) => `<div class="l a${alin}">${partes.map((p) => p.html ?? `<span style="font-size:${p.alto}em;${p.ancho !== p.alto ? `display:inline-block;transform:scaleX(${p.ancho / p.alto});transform-origin:left` : ""}${p.negrita ? ";font-weight:700" : ""}">${esc(p.t)}</span>`).join("") || "&nbsp;"}</div>`).join("");
}
async function faseTiquetes() {
  if (!TIQUETES || !existsSync(TIQUETES)) { console.warn("  (sin --tiquetes: se omiten las imágenes de los tiquetes)"); return; }
  const archivos = readdirSync(TIQUETES).filter((f) => f.endsWith(".escpos")).sort().map((f) => ({ f, b: readFileSync(join(TIQUETES, f)) }));
  const contiene = (b, t) => [...b].map((c) => (c >= 0x80 ? CP850[c - 0x80] : String.fromCharCode(c))).join("").includes(t);
  const venta = archivos.find((a) => contiene(a.b, "Ríos Patiño")) ?? archivos.find((a) => !contiene(a.b, "REPORTE Z"));
  const z = [...archivos].reverse().find((a) => contiene(a.b, "REPORTE Z"));
  const fuente = (archivo) => "data:font/woff2;base64," + readFileSync(join(raiz, "src", "Client", "Pos.Client", "wwwroot", "fonts", archivo)).toString("base64");
  for (const [a, nombre, pie] of [[venta, "integ-19-tiquete-de-venta", "Tiquete de venta (impresora de 80 mm)"], [z, "integ-20-tiquete-reporte-z", "Reporte Z impreso al cerrar el turno"]]) {
    if (!a) continue;
    const html = `<!doctype html><html><head><meta charset="utf-8"><style>
      @font-face { font-family: "Inter"; src: url("${fuente("inter-latin.woff2")}") format("woff2"); }
      body { margin: 0; background: #E9E7F3; font-family: "Inter", "Segoe UI", sans-serif; }
      .marco { padding: 28px 40px 22px; display: inline-block; }
      .papel { background: #fff; width: 42ch; padding: 22px 20px 18px; box-shadow: 0 6px 22px rgba(20, 20, 60, .18); font-family: Consolas, "Cascadia Mono", monospace; font-size: 18px; line-height: 1.3; color: #111; }
      .l { white-space: pre; min-height: 1.3em; } .a1 { text-align: center; } .a2 { text-align: right; } svg { fill: #111; display: inline-block; vertical-align: top; margin: 6px 0; }
      .pie { text-align: center; color: #5A6075; font-size: 13px; margin-top: 12px; }
    </style></head><body><div class="marco"><div class="papel">${escposAHtml(a.b)}</div><div class="pie">${pie} · ${a.f}</div></div></body></html>`;
    const archivoHtml = join(CRUDAS, nombre + ".html");
    writeFileSync(archivoHtml, html, "utf8");
    await tamano(560, 800);
    await ir(pathToFileURL(archivoHtml).href, 1500);
    const caja = await evaluar(`(() => { const r = document.querySelector(".marco").getBoundingClientRect(); return { x: 0, y: 0, width: Math.ceil(r.width), height: Math.ceil(r.height) }; })()`);
    await cdp("Emulation.setDeviceMetricsOverride", { width: caja.width, height: caja.height, deviceScaleFactor: 1, mobile: false });
    await espera(400);
    const r = await cdp("Page.captureScreenshot", { format: "png", clip: { ...caja, scale: 1 } });
    writeFileSync(join(CRUDAS, nombre + ".png"), Buffer.from(r.data, "base64"));
    rmSync(archivoHtml);
    console.log("  ·", nombre, "←", a.f);
  }
  await tamano(1280, 800);
}

// ═════════════════════════════════════ Fase 8: PNG optimizados ═════════════════════════════════════
function faseOptimizar() {
  mkdirSync(SALIDA, { recursive: true });
  const py = `import os, sys
from PIL import Image
src, dst = sys.argv[1], sys.argv[2]
total = 0
for f in sorted(os.listdir(src)):
    if not f.endswith(".png") or f.startswith("x-"): continue
    im = Image.open(os.path.join(src, f)).convert("RGB")
    q = im.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    q.save(os.path.join(dst, f), optimize=True)
    total += os.path.getsize(os.path.join(dst, f))
print(f"{len([f for f in os.listdir(dst) if f.endswith('.png')])} imágenes, {total/1024/1024:.2f} MB")`;
  const r = spawnSync("python", ["-c", py, CRUDAS, SALIDA], { encoding: "utf8" });
  if (r.status !== 0) throw new Error("No se pudieron optimizar los PNG (¿Python con Pillow?): " + r.stderr);
  console.log("  " + r.stdout.trim());
}

// ═════════════════════════════════════ Orden de las fases ═════════════════════════════════════
try {
  const navegadorNecesario = ORDEN.slice(0, 8).some((f) => fase(f) && f !== "turno-cerrado" && f !== "turno-abierto");
  if (navegadorNecesario) await abrirEdge();
  if (fase("asistente")) { console.log("Fase asistente"); await faseAsistente(); }
  if (fase("datos")) { console.log("Fase datos"); await faseDatos(); }
  if (fase("turno-cerrado")) { console.log("Fase turno cerrado (Jorge Cajero)"); await turnoApi(JORGE, 14, true); }
  if (fase("caja")) { console.log("Fase caja (Ana Cajera)"); await faseCaja(); }
  if (fase("caja") || FASES.includes("caja-fin")) { console.log("Fase caja: báscula sin respuesta, 1366×768 y cierre"); await faseCajaFin(); }
  if (fase("turno-abierto")) { console.log("Fase turno abierto (Ana Cajera)"); await turnoApi(ANA, 6, false); }
  if (fase("admin")) { console.log("Fase administración"); await faseAdmin(); }
  if (fase("tiquetes")) { console.log("Fase tiquetes"); await faseTiquetes(); }
  if (fase("optimizar")) { console.log("Fase optimizar"); faseOptimizar(); }
  if (fallidas.length) console.warn(`${fallidas.length} paso(s) de la administración no se capturaron: ${fallidas.join(" | ")}`);
  if (avisosError.length) console.warn(`La interfaz mostró "Ocurrió un error inesperado" ${avisosError.length} vez/veces (se recargó): ${avisosError.join("; ")}`);
} catch (error) {
  console.error("ERROR:", error.message);
  try { const r = await cdp("Page.captureScreenshot", { format: "png" }); writeFileSync(join(CRUDAS, "x-error.png"), Buffer.from(r.data, "base64")); console.error("Pantalla del error:", join(CRUDAS, "x-error.png")); } catch { /* sin navegador */ }
  process.exitCode = 1;
} finally {
  await cerrarEdge();
}
