// Consola de PRUEBA (solo desarrollo): usa la misma API /api/v1 que usará la interfaz definitiva de la Fase 15.
"use strict";

const api = "/api/v1";
const $ = (id) => document.getElementById(id);
const money = new Intl.NumberFormat("es-CO", { style: "currency", currency: "COP", maximumFractionDigits: 2 });
const qty = new Intl.NumberFormat("es-CO", { maximumFractionDigits: 4 });
let token = sessionStorage.getItem("pos.token");
let warehouseId = null;

async function call(path, options = {}) {
  const response = await fetch(api + path, {
    ...options,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: "Bearer " + token } : {}), ...(options.headers || {}) },
  });
  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) {
    if (response.status === 401) { logout(); }
    const message = body?.detail || body?.title || response.statusText;
    throw new Error(`${message}${body?.code ? " (" + body.code + ")" : ""}`);
  }
  return body;
}

function showError(target, error) {
  const box = $(target);
  box.textContent = error ? error.message : "";
  box.classList.toggle("hidden", !error);
}

function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

/** Tabla simple: columns = [[título, (fila) => valor, "num"?]]. */
function table(rows, columns, actions) {
  if (!rows || rows.length === 0) { return '<div class="table-wrap"><div class="empty">Sin datos.</div></div>'; }
  const head = columns.map(([title]) => `<th>${esc(title)}</th>`).join("") + (actions ? "<th></th>" : "");
  const body = rows.map((row, i) => "<tr>" + columns.map(([, get, kind]) => `<td class="${kind || ""}">${esc(get(row))}</td>`).join("")
    + (actions ? `<td>${actions(row, i)}</td>` : "") + "</tr>").join("");
  return `<div class="table-wrap"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

const date = (v) => v ? new Date(v).toLocaleString("es-CO") : "";
const items = (r) => Array.isArray(r) ? r : (r?.items ?? []);

// ───────────── Sesión ─────────────
async function login(event) {
  event.preventDefault();
  showError("loginError", null);
  try {
    const result = await call("/auth/login", { method: "POST", body: JSON.stringify({ username: $("username").value, password: $("password").value }) });
    token = result.token;
    sessionStorage.setItem("pos.token", token);
    await start();
  } catch (e) { showError("loginError", e); }
}

function logout() {
  if (token) { fetch(api + "/auth/logout", { method: "POST", headers: { Authorization: "Bearer " + token } }).catch(() => {}); }
  token = null;
  sessionStorage.removeItem("pos.token");
  $("appView").classList.add("hidden");
  $("user").classList.add("hidden");
  $("loginView").classList.remove("hidden");
}

async function start() {
  const me = await call("/auth/me");
  $("userName").textContent = `${me.displayName} · ${me.permissions.length} permisos`;
  $("loginView").classList.add("hidden");
  $("appView").classList.remove("hidden");
  $("user").classList.remove("hidden");
  openTab("products");
}

// ───────────── Pestañas ─────────────
const loaders = {
  products: loadProducts, scan: () => $("scanCode").focus(), stock: loadStock, purchases: loadPurchases, audit: loadAudit, company: loadCompany, sales: () => {},
};

function openTab(name) {
  document.querySelectorAll("#tabs button").forEach((b) => b.classList.toggle("active", b.dataset.tab === name));
  document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("hidden", t.id !== "tab-" + name));
  showError("error", null);
  Promise.resolve(loaders[name]()).catch((e) => showError("error", e));
}

// ───────────── Productos ─────────────
async function loadProducts(event) {
  event?.preventDefault();
  const search = $("productQuery").value.trim();
  const result = await call(`/catalog/products?pageSize=100${search ? "&search=" + encodeURIComponent(search) : ""}`);
  $("productsTable").innerHTML = table(items(result), [
    ["SKU", (p) => p.sku], ["Producto", (p) => p.name], ["Categoría", (p) => p.categoryName], ["Código de barras", (p) => p.primaryBarcode],
    ["Unidad", (p) => p.baseUnitCode], ["Precio", (p) => p.price == null ? "—" : money.format(p.price), "num"], ["Estado", (p) => p.status],
  ]);
}

// ───────────── Leer código ─────────────
async function scan(event) {
  event.preventDefault();
  showError("error", null);
  try {
    const s = await call("/catalog/scan/" + encodeURIComponent($("scanCode").value.trim()));
    const taxes = (s.taxes || []).map((t) => t.rate != null ? `${t.code} ${t.rate} %` : `${t.code} ${money.format(t.fixedAmount)}`).join(", ");
    $("scanResult").innerHTML = `<div class="card"><div class="grid">
      <div class="kv">Producto<b>${esc(s.name)}</b></div>
      <div class="kv">SKU<b>${esc(s.sku)}</b></div>
      <div class="kv">Presentación<b>${esc(s.packagingName || "Unidad")}</b></div>
      <div class="kv">Leído como<b>${esc(s.source)}</b></div>
      <div class="kv">Cantidad<b>${qty.format(s.quantity)} ${esc(s.baseUnitCode)}</b></div>
      <div class="kv">Precio unitario<b>${s.unitPrice == null ? "—" : money.format(s.unitPrice)}</b></div>
      <div class="kv">Importe<b>${s.amount == null ? "—" : money.format(s.amount)}</b></div>
      <div class="kv">Impuestos<b>${esc(taxes || "—")} ${s.priceIncludesTax ? "(incluidos)" : ""}</b></div>
      <div class="kv">¿Se puede vender?<b class="${s.isSellable ? "ok" : "bad"}">${s.isSellable ? "Sí" : "No: " + esc(s.notSellableReasons.join(" "))}</b></div>
    </div></div>`;
  } catch (e) { $("scanResult").innerHTML = ""; showError("error", e); }
  $("scanCode").select();
}

// ───────────── Existencias y kardex ─────────────
async function loadStock() {
  const rows = await call("/inventory/stock");
  $("stockTable").innerHTML = table(rows, [
    ["Bodega", (r) => r.warehouseCode], ["SKU", (r) => r.sku], ["Producto", (r) => r.productName],
    ["Cantidad", (r) => qty.format(r.quantity), "num"], ["Costo promedio", (r) => r.averageCost == null ? "—" : money.format(r.averageCost), "num"],
    ["Valor", (r) => r.totalValue == null ? "—" : money.format(r.totalValue), "num"], ["Último movimiento", (r) => date(r.lastMovementAt)],
  ], (r, i) => `<button class="small" data-kardex="${i}">Kardex</button>`);
  $("stockTable").querySelectorAll("[data-kardex]").forEach((b) => b.addEventListener("click", () => loadKardex(rows[+b.dataset.kardex])));
}

async function loadKardex(row) {
  try {
    const k = await call(`/inventory/kardex?warehouseId=${row.warehouseId}&productId=${row.productId}`);
    $("kardexTitle").textContent = `Kardex · ${k.sku} · ${k.productName} (${row.warehouseCode})`;
    $("kardexTitle").classList.remove("hidden");
    $("kardexTable").innerHTML = table(k.entries, [
      ["Fecha", (e) => date(e.occurredAt)], ["Documento", (e) => e.sourceNumber], ["Tipo", (e) => e.movementType],
      ["Entra", (e) => e.quantityIn == null ? "" : qty.format(e.quantityIn), "num"], ["Sale", (e) => e.quantityOut == null ? "" : qty.format(e.quantityOut), "num"],
      ["Saldo", (e) => qty.format(e.balanceQuantity), "num"], ["Costo unit.", (e) => e.unitCost == null ? "—" : money.format(e.unitCost), "num"],
      ["Costo prom.", (e) => e.balanceAverageCost == null ? "—" : money.format(e.balanceAverageCost), "num"], ["Usuario", (e) => e.userName],
    ]);
  } catch (e) { showError("error", e); }
}

// ───────────── Compras, auditoría, empresa ─────────────
async function loadPurchases() {
  const rows = await call("/purchasing/purchases");
  $("purchasesTable").innerHTML = table(items(rows), [
    ["Número", (r) => r.number], ["Proveedor", (r) => r.supplierName ?? r.partyName], ["Estado", (r) => r.status],
    ["Total", (r) => r.total == null ? "" : money.format(r.total), "num"], ["Fecha", (r) => r.businessDate ?? date(r.createdAt)],
  ]);
}

async function loadAudit() {
  const result = await call("/audit/logs?pageSize=100");
  $("auditTable").innerHTML = table(items(result), [
    ["Fecha", (r) => date(r.occurredAt)], ["Usuario", (r) => r.userDisplayName], ["Módulo", (r) => r.module], ["Acción", (r) => r.action],
    ["Resumen", (r) => r.summary ?? r.entityLabel],
  ]);
}

async function loadCompany() {
  const company = await call("/organization/company");
  const branches = await call("/organization/branches");
  const detail = branches.length ? await call("/organization/branches/" + branches[0].id) : null;
  warehouseId = detail?.branch?.defaultWarehouseId ?? null;
  $("companyInfo").innerHTML = `<div class="card"><div class="grid">
      <div class="kv">Razón social<b>${esc(company.legalName)}</b></div>
      <div class="kv">Nombre comercial<b>${esc(company.tradeName)}</b></div>
      <div class="kv">NIT<b>${esc(company.identificationNumber)}-${esc(company.checkDigit)}</b></div>
      <div class="kv">Dirección<b>${esc(company.address)}</b></div>
    </div></div>
    <h2>Bodegas</h2>${table(detail?.warehouses ?? [], [["Código", (w) => w.code], ["Nombre", (w) => w.name], ["Tipo", (w) => w.kind], ["Vende", (w) => w.allowsSales ? "Sí" : "No"]])}
    <h2>Cajas</h2>${table(detail?.terminals ?? [], [["Código", (t) => t.code], ["Nombre", (t) => t.name], ["Estado", (t) => t.status]])}`;
}

// ───────────── Arranque ─────────────
$("loginForm").addEventListener("submit", login);
$("logout").addEventListener("click", logout);
$("productSearch").addEventListener("submit", (e) => loadProducts(e).catch((err) => showError("error", err)));
$("scanForm").addEventListener("submit", scan);
$("reloadStock").addEventListener("click", () => loadStock().catch((e) => showError("error", e)));
document.querySelectorAll("#tabs button").forEach((b) => b.addEventListener("click", () => openTab(b.dataset.tab)));
if (token) { start().catch(() => logout()); }
