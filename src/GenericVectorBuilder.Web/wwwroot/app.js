// GenericVectorBuilder page logic: pick data (a folder, a database or a git repository) -> review -> Go -> watch -> search.
// Plain JS, no build step. Every server call goes through api() so errors surface the same way,
// and every failure ends up as visible text on the page (never only in the console).
"use strict";

const UPLOAD_BATCH_FILES = 40;
// Kestrel caps one request at 1 GiB, so a batch is cut by bytes as well as by file count.
const UPLOAD_BATCH_BYTES = 200 * 1024 * 1024;
const MAX_UPLOAD_FILE_BYTES = 1000 * 1024 * 1024;
const MAX_NAME_LENGTH = 60;
const RUN_POLL_MS = 3000;
const RESET_CONFIRM_MS = 5000;
// The connection dropdown's last choice is a connection string typed by hand. Its value is empty
// because no configured connection or DSN can have an empty name.
const TYPED_CHOICE = "";
// How many table previews are fetched at once, so ticking 70 tables does not open 70 connections.
const PREVIEW_PARALLEL = 3;
// The three kinds of data step 1 can read; each has a switch button "mode-<kind>" and a panel "source-<kind>".
const MODES = ["folder", "odbc", "git"];
// How often the page asks what a Fetch is doing, and after how many silent seconds it says git has gone quiet.
const GIT_POLL_MS = 1000;
const GIT_QUIET_SECONDS = 15;
const $ = (id) => document.getElementById(id);
const state = {
  scan: null, path: null, config: null,
  // Step 1 source: "folder", "odbc" or "git". names remembers the pipeline name typed for each, so
  // switching back and forth does not lose it. odbc is the live database session (see
  // startOdbcSession); its password stays in this object, in memory, and nowhere else.
  // git is the fetched repository ({ request, preview }) that Go builds, or null; gitFetching is
  // true while a Fetch is under way.
  mode: "folder", names: {}, odbc: null, odbcConnections: [], odbcLoaded: false, forceLogin: false,
  git: null, gitFetching: false,
  runId: null, events: null,
  active: [], pipelines: [], starting: false, uploading: false,
  // Reset is two clicks: the first arms one pipeline (armed = its name) for RESET_CONFIRM_MS,
  // the second deletes it. resetting is the name being deleted right now, or null.
  armed: null, armTimer: null, resetting: null,
};
// Reset buttons by pipeline name, so arming and disarming change the button in place (a
// rebuilt button would drop keyboard focus).
const resetButtons = new Map();

async function api(url, options = {}) {
  let res;
  try {
    res = await fetch(url, options);
  } catch {
    throw new Error("Could not reach the server. Check that the service is running, then try again.");
  }
  const body = res.headers.get("content-type")?.includes("json") ? await res.json().catch(() => null) : null;
  if (!res.ok) throw new Error(body?.error || `${res.status} ${res.statusText}`);
  return body;
}

function postJson(url, data) {
  return api(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(data) });
}

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") node.className = v; else if (k === "text") node.textContent = v; else node.setAttribute(k, v);
  }
  for (const c of children) node.append(c);
  return node;
}

function showError(message) { $("page-error").textContent = message || ""; }

// Same rules as PipelineNames.Sanitize on the server, so the name shown is the name used.
function sanitizeName(name) {
  const clean = (name || "").trim().toLowerCase().replace(/[^a-z0-9_]+/g, "_").replace(/^_+|_+$/g, "");
  return clean.slice(0, MAX_NAME_LENGTH).replace(/_+$/, "") || "pipeline";
}

// ---- startup -------------------------------------------------------------------------------

async function init() {
  state.config = await api("/api/config");
  const roots = $("roots");
  state.config.roots.forEach((r) => roots.append(el("option", { value: r })));
  $("server-path").value = state.config.roots[0] || "";
  renderDestinations("sinks");
  renderDestinations("search-sinks");
  state.config.models.forEach((m) => $("model").append(el("option", { value: m.key, text: m.key })));
  $("model").value = state.config.defaultModel;
  showModelNote();
  refreshHealth();
  await refreshPipelines();
  await refreshRuns();
  setInterval(() => { if (!document.hidden) refreshRuns(); }, RUN_POLL_MS);
}

// One tick box per destination (SQL Server, Qdrant, then every engine), SQL Server and Qdrant
// ticked. Engines start greyed out until the health check says they are running, so a stopped
// one can never be ticked.
function renderDestinations(listId) {
  $(listId).replaceChildren(...state.config.destinations.map((d) => {
    const input = el("input", { type: "checkbox", value: d.name });
    input.checked = d.defaultOn;
    input.disabled = d.optional;
    const label = el("label", { class: d.optional ? "dest off" : "dest", title: d.name }, input,
      el("span", { class: "dest-name", text: d.displayName }), el("span", { class: "dest-note muted", text: d.optional ? "checking..." : "" }));
    return label;
  }));
}

// Greys out every engine the health check did not find running, and unticks it. SQL Server and
// Qdrant stay usable even when down (a run then finishes the other one), but say so.
function applyDestinationHealth(health) {
  for (const input of document.querySelectorAll(".dest input")) {
    const d = state.config.destinations.find((x) => x.name === input.value);
    const status = health[d.name];
    const up = status === "ok";
    const label = input.closest("label");
    const note = label.querySelector(".dest-note");
    label.title = status && !up ? `${d.name}: ${status}` : d.name;
    if (!d.optional) {
      note.textContent = status === undefined || up ? "" : "not answering";
      continue;
    }
    input.disabled = !up;
    if (!up) input.checked = false;
    label.classList.toggle("off", !up);
    note.textContent = up ? "running" : status === undefined ? "could not check" : "not running";
  }
}

// Asks the server what is up: the top bar shows the embedding service, SQL Server, Qdrant and how
// many engines are running; the destination lists follow (see applyDestinationHealth).
async function refreshHealth() {
  const box = $("health");
  let h;
  try {
    h = await api("/api/health");
  } catch (e) {
    box.textContent = `health check failed: ${e.message}`;
    applyDestinationHealth({});
    return;
  }
  const optional = new Set(state.config.destinations.filter((d) => d.optional).map((d) => d.name));
  const core = Object.entries(h).filter(([k]) => !optional.has(k));
  const engines = Object.entries(h).filter(([k]) => optional.has(k));
  const running = engines.filter(([, v]) => v === "ok").map(([k]) => k);
  box.replaceChildren(...core.map(([k, v]) => el("span", { class: v === "ok" ? "ok" : "down", title: v }, `${k} ${v === "ok" ? "✓" : "✗"}  `)),
    el("span", { title: running.length ? `running: ${running.join(", ")}` : "no engine is running" }, `engines ${running.length} of ${engines.length} running`));
  applyDestinationHealth(h);
}

// Refills the search dropdown. `prefer` is a pipeline to select (the one just built); with no
// pipelines at all the dropdown says so and Search is disabled instead of querying "".
async function refreshPipelines(prefer) {
  let names;
  try {
    names = await api("/api/pipelines");
  } catch (e) {
    showError(`Could not load the pipeline list: ${e.message}`);
    return;
  }
  state.pipelines = names;
  const select = $("search-pipeline");
  const keep = select.value;
  if (names.length === 0) {
    select.replaceChildren(el("option", { value: "", text: "No pipelines yet. Build one first." }));
  } else {
    select.replaceChildren(...names.map((n) => el("option", { value: n, text: n })));
    select.value = names.includes(prefer) ? prefer : names.includes(keep) ? keep : names[0];
  }
  $("search").disabled = names.length === 0;
  renderPipelineList(names);
  showNameNote();
}

function showModelNote() {
  const m = state.config.models.find((x) => x.key === $("model").value);
  $("model-note").textContent = m ? `${m.key}: ${m.measured}` : "";
}

// Tells the user when the name typed will update a pipeline that already exists.
function showNameNote() {
  const name = sanitizeName($("pipeline").value);
  $("name-note").textContent = state.pipelines.includes(name)
    ? `A pipeline named "${name}" already exists. Go will update it and only embed rows that changed. Type another name to keep it separate.`
    : "";
}

// ---- step 1: getting files ---------------------------------------------------------------

// Walks a dropped folder (DataTransferItem.webkitGetAsEntry) and returns [File, relativePath].
async function filesFromEntry(entry, prefix = "") {
  if (entry.isFile) {
    const file = await new Promise((ok, bad) => entry.file(ok, bad));
    return [[file, prefix + file.name]];
  }
  const reader = entry.createReader();
  const out = [];
  // readEntries returns results in pages; keep reading until it returns none.
  for (;;) {
    const page = await new Promise((ok, bad) => reader.readEntries(ok, bad));
    if (page.length === 0) break;
    for (const child of page) out.push(...(await filesFromEntry(child, `${prefix}${entry.name}/`)));
  }
  return out;
}

function showUploadStatus(message, isError = false) {
  const status = $("upload-status");
  status.textContent = message;
  status.classList.toggle("errors", isError);
  status.classList.toggle("muted", !isError);
}

// Cuts the files into upload requests: at most UPLOAD_BATCH_FILES files and UPLOAD_BATCH_BYTES
// bytes each (one bigger file travels alone).
function planBatches(pairs) {
  const batches = [];
  let current = [];
  let bytes = 0;
  for (const pair of pairs) {
    const size = pair[0].size;
    if (current.length && (current.length >= UPLOAD_BATCH_FILES || bytes + size > UPLOAD_BATCH_BYTES)) {
      batches.push(current);
      current = [];
      bytes = 0;
    }
    current.push(pair);
    bytes += size;
  }
  if (current.length) batches.push(current);
  return batches;
}

// A name for loose files that stays the same when the same files are dropped again, so a
// second drop updates the pipeline instead of creating a new one: the file name for one file,
// the shared start of the names for several (without trailing digits or separators, so a
// numbered series like sales_2024_01, sales_2024_02 becomes "sales"), else "files".
function looseFilesName(pairs) {
  const stems = pairs.map(([, rel]) => rel.split("/").pop().replace(/\.[^.]*$/, ""));
  if (stems.length === 1) return stems[0];
  let prefix = stems[0];
  for (const stem of stems) while (!stem.startsWith(prefix)) prefix = prefix.slice(0, -1);
  prefix = prefix.replace(/[^a-zA-Z]+$/, "");
  return prefix.length >= 3 ? prefix : "files";
}

async function uploadAndScan(pairs, label) {
  if (state.uploading) {
    showUploadStatus("An upload is already running. Wait for it to finish.", true);
    return;
  }
  if (pairs.length === 0) {
    showUploadStatus("Nothing to upload. Drop a folder or some files.", true);
    return;
  }
  const tooBig = pairs.find(([file]) => file.size > MAX_UPLOAD_FILE_BYTES);
  if (tooBig) {
    showUploadStatus(`"${tooBig[1]}" is larger than 1 GB, which is the most one upload can carry. Put that file in the server folder instead and scan it from there.`, true);
    return;
  }
  state.uploading = true;
  let sent = 0;
  try {
    const upload = await postJson("/api/uploads", {});
    for (const batch of planBatches(pairs)) {
      showUploadStatus(`uploaded ${sent} of ${pairs.length} files, sending ${batch.length} more...`);
      const form = new FormData();
      batch.forEach(([file, rel]) => form.append("files", file, rel));
      await api(`/api/uploads/${upload.id}/files`, { method: "POST", body: form });
      sent += batch.length;
    }
    showUploadStatus(`uploaded ${pairs.length} files`);
    await scan(upload.path, label || looseFilesName(pairs));
  } catch (e) {
    showUploadStatus(`Upload stopped after ${sent} of ${pairs.length} files: ${e.message} Nothing was built. Try again.`, true);
  } finally {
    state.uploading = false;
  }
}

async function scan(path, label) {
  showUploadStatus("scanning...");
  try {
    state.scan = await postJson("/api/scan", { path });
    state.path = path;
    const name = sanitizeName(label || path.split("/").filter(Boolean).pop());
    state.names.folder = name;
    if (state.mode === "folder") {
      $("pipeline").value = name;
      renderScan();
      showNameNote();
    }
    showUploadStatus("");
  } catch (e) {
    showUploadStatus(`scan failed: ${e.message}`, true);
  }
}

// ---- step 1: a database through ODBC ---------------------------------------------------

function showOdbcStatus(message, isError = false) {
  const status = $("odbc-status");
  status.textContent = message;
  status.classList.toggle("errors", isError);
  status.classList.toggle("muted", !isError);
}

// Switches step 1 between a folder, a database and a git repository. The review (step 2) and Build
// (step 3) follow the mode, and each mode keeps its own pipeline name.
function setMode(mode) {
  if (mode === state.mode) return;
  state.names[state.mode] = $("pipeline").value;
  state.mode = mode;
  for (const m of MODES) {
    $(`source-${m}`).classList.toggle("hidden", m !== mode);
    $(`mode-${m}`).classList.toggle("active", m === mode);
    $(`mode-${m}`).setAttribute("aria-pressed", String(m === mode));
  }
  $("pipeline").value = state.names[mode] || "";
  showNameNote();
  refreshReview();
  updateGo();
  if (mode === "odbc" && !state.odbcLoaded) loadOdbcConnections();
}

// Shows what the chosen mode has to review: the scanned folder, the ticked tables, the fetched
// repository, or nothing.
function refreshReview() {
  if (state.mode === "odbc") {
    renderOdbcReview();
  } else if (state.mode === "git") {
    renderGitReview();
  } else if (state.scan) {
    renderScan();
  } else {
    $("tables").replaceChildren();
    $("step-tables").classList.add("hidden");
    $("step-run").classList.add("hidden");
  }
}

// Fills the connection dropdown: configured connections and DSNs labelled by kind, then the
// choice to type a connection string. If the list cannot be loaded the typed choice still works.
async function loadOdbcConnections() {
  let found = [];
  try {
    const r = await api("/api/odbc/connections");
    found = r.connections;
    const driver = (r.drivers || [])[0];
    if (driver) $("odbc-string").setAttribute("placeholder", `Driver={${driver}};Server=localhost;Database=Sales`);
    state.odbcLoaded = true;
  } catch (e) {
    showOdbcStatus(`Could not list the ODBC connections: ${e.message} You can still type a connection string.`, true);
  }
  state.odbcConnections = found;
  const select = $("odbc-connection");
  select.replaceChildren(
    ...found.map((c) => el("option", { value: c.name, text: `${c.name} (${c.kind}${c.driver ? `, ${c.driver}` : ""})` })),
    el("option", { value: TYPED_CHOICE, text: "Type a connection string..." }));
  select.value = found.length ? found[0].name : TYPED_CHOICE;
  syncOdbcChoice();
}

// Shows the string box for a typed connection, and the user and password boxes only when they
// are needed: the connection has no stored login, the string is typed, or the last try failed.
function syncOdbcChoice() {
  const typed = $("odbc-connection").value === TYPED_CHOICE;
  const info = state.odbcConnections.find((c) => c.name === $("odbc-connection").value);
  const needsLogin = typed || state.forceLogin || (info ? info.needsCredentials : false);
  $("odbc-string-box").classList.toggle("hidden", !typed);
  $("odbc-login").classList.toggle("hidden", !needsLogin);
  const note = $("odbc-login-note");
  note.classList.toggle("hidden", !needsLogin);
  note.textContent = typed
    ? "Put the login in these boxes, not in the string. Leave them empty if the string already has one."
    : info && info.needsCredentials ? "This connection has no stored login. Type one." : "Type a login to try again.";
}

// Opens the chosen connection and lists its tables and views. The user and password are read
// only while their boxes are showing, so a login typed for another connection is never sent.
async function connectOdbc() {
  const typed = $("odbc-connection").value === TYPED_CHOICE;
  const connection = typed ? $("odbc-string").value.trim() : $("odbc-connection").value;
  if (!connection) {
    showOdbcStatus("Type a connection string first.", true);
    return;
  }
  const loginShown = !$("odbc-login").classList.contains("hidden");
  const user = loginShown && $("odbc-user").value.trim() ? $("odbc-user").value.trim() : null;
  const password = loginShown && $("odbc-password").value !== "" ? $("odbc-password").value : null;
  $("odbc-connect").disabled = true;
  showOdbcStatus("Connecting...");
  try {
    const r = await postJson("/api/odbc/tables", { connection, user, password });
    startOdbcSession(connection, user, password, r);
  } catch (e) {
    state.forceLogin = true;
    syncOdbcChoice();
    showOdbcStatus(e.message, true);
  } finally {
    $("odbc-connect").disabled = false;
  }
}

// Starts a fresh database session from the tables the server listed. A session is replaced by
// the next Connect; late preview answers for an old one are dropped (see loadOdbcPreview).
function startOdbcSession(connection, user, password, listing) {
  state.odbc = {
    connection, user, password, tables: listing.tables, filter: "",
    selected: new Set(), previews: new Map(), cards: new Map(), queue: [], running: 0,
  };
  state.forceLogin = false;
  syncOdbcChoice();
  $("odbc-filter").value = "";
  $("pipeline").value = listing.suggestedPipeline;
  showNameNote();
  const views = listing.tables.filter((t) => t.type === "VIEW").length;
  showOdbcStatus(listing.tables.length
    ? `Connected. ${listing.tables.length - views} table(s) and ${views} view(s) found.`
    : "Connected, but this login can see no tables or views.");
  $("odbc-picker").classList.remove("hidden");
  afterOdbcSelection();
}

function odbcLabel(t) { return t.schema ? `${t.schema}.${t.name}` : t.name; }

// The listed tables that pass the filter box, each with its position in the full list.
function visibleOdbcTables() {
  const o = state.odbc;
  const needle = o.filter.trim().toLowerCase();
  return o.tables.map((t, i) => ({ t, i })).filter(({ t }) => !needle || odbcLabel(t).toLowerCase().includes(needle));
}

function pickRow(t, i) {
  const box = el("input", { type: "checkbox" });
  box.checked = state.odbc.selected.has(i);
  box.addEventListener("change", () => toggleOdbcTable(i, box.checked));
  return el("label", { class: "pick" }, box,
    el("span", { class: "pick-name", text: odbcLabel(t) }),
    el("span", { class: "pill", text: t.type }),
    el("span", { class: "muted", text: t.rowCount == null ? "" : `${t.rowCount.toLocaleString()} rows` }));
}

// Redraws the checklist, the select-all buttons (which act on what the filter shows) and the count.
function renderOdbcPicker() {
  const o = state.odbc;
  const shown = visibleOdbcTables();
  const none = o.tables.length ? "No table matches that filter." : "Nothing to pick here.";
  $("odbc-list").replaceChildren(...(shown.length ? shown.map(({ t, i }) => pickRow(t, i)) : [el("p", { class: "muted pick-empty", text: none })]));
  for (const [id, type, word] of [["odbc-select-tables", "TABLE", "tables"], ["odbc-select-views", "VIEW", "views"]]) {
    const count = shown.filter(({ t }) => t.type === type).length;
    $(id).textContent = `Select all ${word} (${count})`;
    $(id).disabled = count === 0;
  }
  $("odbc-clear").disabled = o.selected.size === 0;
  $("odbc-picked").textContent = `${o.selected.size} of ${o.tables.length} selected.`;
}

function toggleOdbcTable(i, on) {
  if (on) queueOdbcPreview(i); else state.odbc.selected.delete(i);
  afterOdbcSelection();
}

function queueOdbcPreview(i) {
  const o = state.odbc;
  o.selected.add(i);
  if (!o.previews.has(i) && !o.queue.includes(i)) o.queue.push(i);
}

function selectAllOdbc(type) {
  for (const { t, i } of visibleOdbcTables()) if (t.type === type) queueOdbcPreview(i);
  afterOdbcSelection();
}

function clearOdbcSelection() {
  state.odbc.selected.clear();
  state.odbc.queue = [];
  afterOdbcSelection();
}

function afterOdbcSelection() {
  state.odbc.queue = state.odbc.queue.filter((i) => state.odbc.selected.has(i));
  renderOdbcPicker();
  renderOdbcReview();
  pumpOdbcPreviews();
  updateGo();
}

// Starts previews for ticked tables, PREVIEW_PARALLEL at a time.
function pumpOdbcPreviews() {
  const o = state.odbc;
  while (o.running < PREVIEW_PARALLEL && o.queue.length) {
    const i = o.queue.shift();
    if (o.selected.has(i) && !o.previews.has(i)) {
      o.running++;
      loadOdbcPreview(o, i);
    }
  }
}

// Fetches one table's preview. A failure becomes that table's card, so one unreadable table
// does not stop the others. An answer for a session that Connect has since replaced is dropped.
async function loadOdbcPreview(o, i) {
  const t = o.tables[i];
  let result;
  try {
    result = { preview: await postJson("/api/odbc/preview", { connection: o.connection, user: o.user, password: o.password, schema: t.schema, name: t.name }) };
  } catch (e) {
    result = { error: e.message };
  }
  o.running--;
  if (state.odbc !== o) return;
  o.previews.set(i, result);
  renderOdbcReview();
  pumpOdbcPreviews();
  updateGo();
}

// Step 2 for a database: one card per ticked table, in list order. Cards are kept between
// redraws so a row id the user picked is not lost when another preview arrives or a table is
// unticked and ticked again.
function renderOdbcReview() {
  const o = state.odbc;
  const picked = o ? [...o.selected].sort((a, b) => a - b) : [];
  $("step-tables").classList.toggle("hidden", picked.length === 0);
  $("step-run").classList.toggle("hidden", picked.length === 0);
  $("rejects-box").classList.add("hidden");
  if (picked.length === 0) {
    $("tables").replaceChildren();
    return;
  }
  const views = picked.filter((i) => o.tables[i].type === "VIEW").length;
  const counted = picked.filter((i) => o.tables[i].rowCount != null);
  const rows = counted.reduce((n, i) => n + o.tables[i].rowCount, 0);
  $("scan-summary").textContent = `${picked.length - views} table(s) and ${views} view(s) chosen` +
    (counted.length ? `, ${rows.toLocaleString()} rows${counted.length < picked.length ? " in the tables" : ""}.` : ".");
  $("tables").replaceChildren(...picked.map((i) => odbcCard(o, i)));
}

function odbcCard(o, i) {
  const result = o.previews.get(i);
  const kind = result ? "done" : "loading";
  if (o.cards.get(i)?.kind !== kind) o.cards.set(i, { kind, node: buildOdbcCard(o.tables[i], result) });
  return o.cards.get(i).node;
}

// A table card for a database table: the same preview and row id picker as a folder table,
// keyed by the stable table id the server sent. While the preview loads, or if it failed, the
// card says so instead.
function buildOdbcCard(t, result) {
  const noun = t.type === "VIEW" ? "view" : "table";
  const title = el("h3", { text: odbcLabel(t) });
  if (!result) {
    return el("div", { class: "table-card" }, el("div", { class: "table-head" }, title, el("span", { class: "muted", text: `loading preview of this ${noun}...` })));
  }
  if (result.error) {
    return el("div", { class: "table-card" }, el("div", { class: "table-head" }, title),
      el("p", { class: "errors table-note", text: `Could not preview this ${noun}: ${result.error} It is still read when you press Go, and a ${noun} that cannot be read keeps its stored rows.` }));
  }
  const p = result.preview;
  const meta = `${noun}, ${t.rowCount == null ? "row count not known" : `${t.rowCount.toLocaleString()} rows`}, ${p.columns.length} columns`;
  const head = el("div", { class: "table-head" }, title, el("span", { class: "muted", text: meta }),
    el("label", { class: "key" }, "Row id:", keySelect(p.tableId, p.columns, p.suggestedKey)));
  const card = el("div", { class: "table-card" }, head, el("div", { class: "preview" }, previewTable(p.columns, p.sample)));
  if (p.skippedColumns.length) {
    card.append(el("p", { class: "muted table-note", text: `Not read (binary data or a type that cannot become text): ${p.skippedColumns.join(", ")}.` }));
  }
  return card;
}

// ---- step 1: a git repository -----------------------------------------------------------

function showGitStatus(message, isError = false) {
  const status = $("git-status");
  status.textContent = message;
  status.classList.toggle("errors", isError);
  status.classList.toggle("muted", !isError);
}

// "cs, .ts  razor" -> ["cs", ".ts", "razor"]; the server adds the dots and checks each one.
function parseExtensions(text) {
  return (text || "").split(/[\s,;]+/).filter(Boolean);
}

// What Fetch sends. An empty address box means the address shown in grey, which is then put in
// the box so the page shows what was fetched.
function gitRequest() {
  const box = $("git-url");
  if (!box.value.trim()) box.value = box.getAttribute("placeholder");
  const branch = $("git-branch").value.trim();
  return { url: box.value.trim(), branch: branch || null, extensions: parseExtensions($("git-extensions").value) };
}

// Fetches the repository's latest commit on the server and shows what Go would read. While git
// works, the server's progress for that repository is polled and shown, so a slow clone and a
// stuck one look different. Go builds exactly what was fetched (see runSource).
async function fetchGit() {
  if (state.gitFetching) return;
  const request = gitRequest();
  state.git = null;
  state.gitFetching = true;
  refreshReview();
  updateGo();
  $("git-fetch").disabled = true;
  showGitStatus(`Fetching ${request.url}...`);
  const ticker = setInterval(() => pollGitProgress(request), GIT_POLL_MS);
  try {
    const preview = await postJson("/api/git/preview", request);
    state.git = { request: { url: preview.url, branch: preview.branch, extensions: preview.extensions }, preview };
    state.names.git = preview.suggestedPipeline;
    if (state.mode === "git") {
      $("pipeline").value = preview.suggestedPipeline;
      showNameNote();
    }
    showGitStatus(`Fetched commit ${preview.commit.slice(0, 12)} of ${preview.url}.`);
  } catch (e) {
    showGitStatus(`Fetch failed: ${e.message}`, true);
  } finally {
    clearInterval(ticker);
    state.gitFetching = false;
    $("git-fetch").disabled = false;
    refreshReview();
    updateGo();
  }
}

// One look at what the server's fetch is doing. Failures here are not shown: the Fetch call
// itself reports anything that really went wrong.
async function pollGitProgress(request) {
  const query = `url=${encodeURIComponent(request.url)}${request.branch ? `&branch=${encodeURIComponent(request.branch)}` : ""}`;
  let p;
  try {
    p = await api(`/api/git/progress?${query}`);
  } catch {
    return;
  }
  if (!state.gitFetching || !p.active) return;
  const quiet = p.quietSeconds >= GIT_QUIET_SECONDS ? `, nothing new from git for ${p.quietSeconds} s` : "";
  showGitStatus(`Fetching ${request.url}: ${p.text} (${p.seconds} s${quiet})`);
}

// Editing the address, branch or file types after a Fetch drops the fetched result, so Go can
// never build something other than what the boxes say.
function onGitInput() {
  if (!state.git) return;
  state.git = null;
  refreshReview();
  updateGo();
  showGitStatus("The address, branch or file types changed. Press Fetch again.");
}

// Step 2 for a git repository: the commit, how many files will be read and where, and the chunk
// estimate. Nothing to pick here; every matching file is read.
function renderGitReview() {
  const g = state.git;
  $("step-tables").classList.toggle("hidden", !g);
  $("step-run").classList.toggle("hidden", !g || g.preview.fileCount === 0);
  $("rejects-box").classList.add("hidden");
  if (!g) {
    $("tables").replaceChildren();
    return;
  }
  const p = g.preview;
  const chunks = p.chunks.extrapolated
    ? `about ${p.chunks.chunks.toLocaleString()} chunks (worked out from ${p.chunks.filesMeasured.toLocaleString()} of the files)`
    : `${p.chunks.chunks.toLocaleString()} chunks`;
  $("scan-summary").textContent = p.fileCount === 0
    ? `No ${p.extensions.join(", ")} files at commit ${p.commit.slice(0, 12)}. Change the file types and Fetch again.`
    : `${p.fileCount.toLocaleString()} file(s) (${p.extensions.join(", ")}) at commit ${p.commit.slice(0, 12)}${p.branch ? ` on ${p.branch}` : ""}, ${chunks} to embed on the first run.`;
  $("tables").replaceChildren(gitCard(p));
}

function gitCard(p) {
  const folders = el("table", {},
    el("thead", {}, el("tr", {}, el("th", { text: "Folder" }), el("th", { text: "Files" }))),
    el("tbody", {}, ...p.folders.map((f) => el("tr", {}, el("td", { text: f.folder }), el("td", { text: f.files.toLocaleString() })))));
  const card = el("div", { class: "table-card git-card" },
    el("div", { class: "table-head" }, el("h3", { text: p.url }), el("span", { class: "muted", text: `${(p.totalBytes / 1e6).toFixed(1)} MB in ${p.localPath}` })),
    el("div", { class: "preview" }, folders));
  if (p.moreFolders) card.append(el("p", { class: "muted table-note", text: `and ${p.moreFolders} more folder(s)` }));
  card.append(el("details", {}, el("summary", { class: "muted", text: `first ${p.firstFiles.length} file(s)` }),
    el("ul", {}, ...p.firstFiles.map((f) => el("li", { text: f })))));
  if (p.skippedCount) {
    card.append(el("details", {}, el("summary", { class: "muted", text: `${p.skippedCount} skipped (other file types, build output, too large)` }),
      el("ul", {}, ...p.skipped.map((r) => el("li", { text: `${r.origin}: ${r.reason}` })))));
  }
  return card;
}

// ---- step 2: review ---------------------------------------------------------------------

function renderScan() {
  const s = state.scan;
  const files = s.tables.reduce((n, t) => n + t.files.length, 0);
  $("scan-summary").textContent =
    `${files} file(s) grouped into ${s.tables.length} table(s), ${s.totalRows.toLocaleString()} rows.` +
    (s.rejected.length ? ` ${s.rejected.length} file(s) could not be read.` : "");
  $("tables").replaceChildren(...s.tables.map(renderTable));
  renderRejects(s);
  $("step-tables").classList.remove("hidden");
  $("step-run").classList.toggle("hidden", s.tables.length === 0);
}

// The "Row id" dropdown of a table card. tableKey is what Go sends the choice under: the table
// name for a folder, the stable table id for a database.
function keySelect(tableKey, columns, suggested) {
  const key = el("select", { "data-table": tableKey, class: "key-select" });
  key.append(el("option", { value: "", text: "none (use row content)" }));
  columns.forEach((c) => key.append(el("option", { value: c, text: c })));
  key.value = suggested || "";
  return key;
}

function previewTable(columns, sample) {
  return el("table", {},
    el("thead", {}, el("tr", {}, ...columns.map((c) => el("th", { text: c })))),
    el("tbody", {}, ...sample.map((r) => el("tr", {}, ...r.map((v) => el("td", { text: v ?? "", title: v ?? "" }))))));
}

function renderTable(t) {
  const key = keySelect(t.name, t.columns, t.suggestedKey);
  const head = el("div", { class: "table-head" },
    el("h3", { text: t.name }),
    el("span", { class: "muted", text: `${t.rows.toLocaleString()} rows, ${t.files.length} file(s), ${t.columns.length} columns` }),
    el("label", { class: "key" }, "Row id:", key));
  const files = el("details", {}, el("summary", { class: "muted", text: "files" }),
    el("ul", {}, ...t.files.map((f) => el("li", { text: `${f.origin}: ${f.rows} rows, ${f.delimiter}, ${f.encoding}${f.badRows ? `, ${f.badRows} bad rows skipped` : ""}` }))));
  return el("div", { class: "table-card" }, head, el("div", { class: "preview" }, previewTable(t.columns, t.sample)), files);
}

function renderRejects(s) {
  const all = [...s.rejected.map((r) => ["rejected", r]), ...s.ignored.map((r) => ["skipped", r])];
  $("rejects-box").classList.toggle("hidden", all.length === 0);
  $("rejects-summary").textContent = `${s.rejected.length} rejected, ${s.ignored.length} skipped`;
  $("rejects").replaceChildren(...all.map(([kind, r]) => el("li", { text: `${kind}: ${r.origin} (${r.reason})` })));
}

// ---- step 3 and 4: run and watch --------------------------------------------------------

// Go is off while any run is queued or running, so a second press cannot bury the running job
// behind a queued one that the page would then watch instead.
function updateGo() {
  const waiting = state.mode === "odbc" && state.odbc !== null && (state.odbc.running > 0 || state.odbc.queue.length > 0);
  const fetching = state.mode === "git" && state.gitFetching;
  const busy = state.starting || state.active.length > 0 || waiting || fetching;
  $("go").disabled = busy;
  $("go-note").textContent = state.active.length ? "A run is in progress. Go works again when it finishes."
    : waiting ? "Loading table previews. Go works when they are done."
    : fetching ? "Fetching the repository. Go works when it is done." : "";
}

// What the run reads: the scanned folder, the connected database with the ticked tables, or the
// fetched repository exactly as it was fetched. The login is sent only in the request body and is
// never written to storage.
function runSource() {
  if (state.mode === "git") return { git: state.git.request };
  if (state.mode !== "odbc") return { path: state.path };
  const o = state.odbc;
  const tables = [...o.selected].sort((a, b) => a - b).map((i) => ({ schema: o.tables[i].schema, name: o.tables[i].name }));
  return { odbc: { connection: o.connection, user: o.user, password: o.password, tables } };
}

async function go() {
  $("go-error").textContent = "";
  const sinks = [...document.querySelectorAll("#sinks input:checked")].map((i) => i.value);
  if (sinks.length === 0) {
    $("go-error").textContent = "Pick at least one destination.";
    return;
  }
  if (state.mode === "odbc" && (!state.odbc || state.odbc.selected.size === 0)) {
    $("go-error").textContent = "Pick at least one table or view.";
    return;
  }
  if (state.mode === "git" && !state.git) {
    $("go-error").textContent = "Press Fetch first.";
    return;
  }
  const tables = {};
  document.querySelectorAll(".key-select").forEach((s) => { tables[s.dataset.table] = { keyColumn: s.value || null, template: null }; });
  const source = runSource();
  state.starting = true;
  updateGo();
  try {
    const allowLargeDeletes = $("allow-large-deletes").checked === true;
    const run = await postJson("/api/runs", { pipeline: $("pipeline").value, ...source, sinks, model: $("model").value, tables, allowLargeDeletes });
    $("allow-large-deletes").checked = false; // a tick applies to one run only
    watch(run.runId);
  } catch (e) {
    $("go-error").textContent = e.message;
  } finally {
    state.starting = false;
    await refreshRuns();
  }
}

function showRunNote(message) { $("run-note").textContent = message || ""; }

function watch(runId) {
  state.runId = runId;
  state.events?.close();
  showRunNote("");
  $("cancel").classList.remove("hidden");
  $("step-progress").classList.remove("hidden");
  const events = new EventSource(`/api/runs/${runId}/events`);
  state.events = events;
  events.onmessage = (msg) => {
    const snap = JSON.parse(msg.data);
    showRunNote("");
    renderProgress(snap);
    if (snap.finishedUtc) {
      events.close();
      state.events = null;
      refreshPipelines(snap.pipeline);
      refreshRuns();
      showFinalState(runId);
    }
  };
  events.onerror = () => onStreamError(events, runId);
}

// The last stream frame can be a moment ahead of the run's final bookkeeping (for example the
// list of rejected files), so the settled snapshot is fetched once more and shown.
async function showFinalState(runId) {
  try {
    const snap = await api(`/api/runs/${runId}`);
    if (snap.finishedUtc && state.runId === runId) renderProgress(snap);
  } catch {
    // The frame already on screen is good enough; nothing to report.
  }
}

// The browser retries a dropped stream by itself (readyState CONNECTING); once it gives up
// (CLOSED) the server either refused it (the run is gone, usually after a restart) or the
// network is down. Say which, instead of freezing on the last snapshot with Cancel showing.
// A run that is still there is picked up again by the next poll in refreshRuns.
async function onStreamError(events, runId) {
  if (state.events !== events) return;
  if (events.readyState === EventSource.CONNECTING) {
    showRunNote("Lost contact with the server. Trying again...");
    return;
  }
  events.close();
  state.events = null;
  try {
    const snap = await api(`/api/runs/${runId}`);
    renderProgress(snap);
    if (snap.finishedUtc) refreshPipelines(snap.pipeline); else showRunNote("The progress stream stopped. Reconnecting...");
  } catch (e) {
    $("cancel").classList.add("hidden");
    showRunNote(`${e.message} Check the pipeline list to see what was written.`);
  }
}

function renderProgress(s) {
  const pct = s.totalRows ? Math.min(100, (100 * s.recordsRead) / s.totalRows) : (s.finishedUtc ? 100 : 0);
  $("bar-fill").style.width = `${pct}%`;
  $("status-line").replaceChildren(el("strong", { class: `status-${s.status}`, text: s.status }), ` ${s.pipeline}: ${s.message || ""}`);
  const written = Object.entries(s.chunksWritten).map(([k, v]) => [`written to ${k}`, v]);
  const items = [["rows read", s.totalRows ? `${s.recordsRead.toLocaleString()} / ${s.totalRows.toLocaleString()}` : s.recordsRead.toLocaleString()], ["unchanged", s.docsUnchanged],
    ["embedded", s.docsEmbedded], ...written, ["deleted", s.docsDeleted], ["empty rows", s.emptyRows],
    ["skipped rows", s.skippedRows ?? 0], ["rows without an id", s.missingKeyRows ?? 0], ["kept (file not fully read)", s.docsKept ?? 0],
    ["rejected files", s.rejected.length]];
  $("counters").replaceChildren(...items.map(([k, v]) => el("div", {}, el("dt", { text: k }), el("dd", { text: String(v) }))));
  // A table or file the run could not read is kept out of the run, not failed, so its reason
  // (for a database: a refused login, a table that is gone) is listed with the errors.
  const rejects = s.rejected.map((r) => `${r.origin}: ${r.reason}`);
  $("errors").replaceChildren(...[...s.errors, ...rejects].map((e) => el("li", { text: e })));
  $("cancel").classList.toggle("hidden", !!s.finishedUtc);
}

async function cancelRun(runId) {
  if (!runId) return;
  showRunNote("");
  try {
    await api(`/api/runs/${runId}/cancel`, { method: "POST" });
  } catch (e) {
    showRunNote(`Could not cancel: ${e.message}`);
  }
  await refreshRuns();
}

// Asks the server which runs are queued or running. Feeds the Go button and the list of active
// runs (shown only when more than one is active, e.g. started from another tab), and makes the
// page follow an active run when it is not streaming one (after a reload, or a run from elsewhere).
async function refreshRuns() {
  let runs;
  try {
    runs = await api("/api/runs");
  } catch {
    return;
  }
  state.active = runs.filter((r) => !r.finishedUtc).sort((a, b) => a.startedUtc.localeCompare(b.startedUtc));
  renderActiveRuns();
  updateGo();
  const target = state.active.find((r) => r.status !== "Queued") || state.active[0];
  if (target && !state.events) watch(target.runId);
}

function renderActiveRuns() {
  $("active-runs-box").classList.toggle("hidden", state.active.length < 2);
  $("active-runs").replaceChildren(...state.active.map((r) => el("li", {},
    el("span", { class: "run-name", text: `${r.pipeline}: ${r.status}` }),
    watchButton(r), cancelButton(r))));
}

function watchButton(r) {
  const b = el("button", { type: "button", class: "btn btn-quiet", text: "Watch" });
  b.disabled = r.runId === state.runId;
  b.addEventListener("click", () => watch(r.runId));
  return b;
}

function cancelButton(r) {
  const b = el("button", { type: "button", class: "btn btn-quiet", text: "Cancel" });
  b.addEventListener("click", () => cancelRun(r.runId));
  return b;
}

// ---- step 5: search ---------------------------------------------------------------------

async function search() {
  const box = $("results");
  const pipeline = $("search-pipeline").value;
  if (!pipeline) {
    box.textContent = "Build a pipeline first, then search it.";
    return;
  }
  if (!$("query").value.trim()) {
    box.textContent = "Type a question first.";
    return;
  }
  const sinks = [...document.querySelectorAll("#search-sinks input:checked")].map((i) => i.value);
  if (sinks.length === 0) {
    box.textContent = "Pick at least one destination to search.";
    return;
  }
  box.textContent = `searching ${pipeline}...`;
  try {
    const r = await postJson("/api/search", { pipeline, query: $("query").value, sinks, top: 5 });
    box.replaceChildren(...Object.entries(r).map(([sink, hits]) => el("div", {}, el("h3", { text: sink }),
      ...(hits.error ? [el("p", { class: "errors", text: hits.error })] : hits.map((h) =>
        el("div", { class: "hit" }, el("span", { class: "score", text: h.score.toFixed(3) }), el("strong", { text: h.table }), el("pre", { text: h.text })))))));
  } catch (e) {
    box.textContent = e.message;
  }
}

// ---- reset a pipeline -------------------------------------------------------------------

// Text for a pipeline's Reset button in its current state.
function resetLabel(name) {
  if (state.resetting === name) return `Deleting ${name}...`;
  return state.armed === name ? `Click again to delete ${name} from SQL and Qdrant and any engine that holds it` : "Reset";
}

// Brings every Reset button in line with state.armed and state.resetting.
function syncResetButtons() {
  for (const [name, button] of resetButtons) {
    button.textContent = resetLabel(name);
    button.classList.toggle("armed", state.armed === name);
    button.disabled = state.resetting !== null;
  }
}

function showResetStatus(message, isError = false) {
  const status = $("reset-status");
  status.textContent = message;
  status.classList.toggle("errors", isError);
  status.classList.toggle("muted", !isError);
}

function disarmReset() {
  clearTimeout(state.armTimer);
  state.armTimer = null;
  state.armed = null;
  syncResetButtons();
}

// First click arms the button for RESET_CONFIRM_MS; a click on the armed button deletes. The
// page never opens a browser dialog for this, because a dialog blocks the page and nothing
// but a person at the keyboard can answer it.
function onResetClick(name) {
  if (state.resetting !== null) return;
  if (state.armed === name) {
    disarmReset();
    resetPipeline(name);
    return;
  }
  clearTimeout(state.armTimer);
  state.armed = name;
  state.armTimer = setTimeout(disarmReset, RESET_CONFIRM_MS);
  showResetStatus("");
  syncResetButtons();
}

// Deletes one pipeline from every destination and shows the outcome next to the list: the
// success line, or the server's reason (a run is active, a destination is down).
async function resetPipeline(name) {
  state.resetting = name;
  syncResetButtons();
  showResetStatus(`Deleting ${name} from every destination that holds it...`);
  try {
    await api(`/api/pipelines/${encodeURIComponent(name)}/reset`, { method: "POST" });
    showResetStatus(`Deleted ${name} from every destination that held it.`);
  } catch (e) {
    showResetStatus(`${name} was not deleted: ${e.message}`, true);
  } finally {
    state.resetting = null;
    syncResetButtons();
  }
  await refreshPipelines();
}

// Lists each pipeline with its Reset button. The rows are only rebuilt when the set of names
// changes, so a refresh that finds the same pipelines leaves a focused button where it is.
function renderPipelineList(names) {
  $("pipelines-box").classList.toggle("hidden", names.length === 0);
  if (names.join("\n") === [...resetButtons.keys()].join("\n")) return;
  if (state.armed !== null && !names.includes(state.armed)) disarmReset();
  resetButtons.clear();
  $("pipeline-list").replaceChildren(...names.map((name) => {
    const button = el("button", { type: "button", class: "btn btn-danger", text: "Reset" });
    button.addEventListener("click", () => onResetClick(name));
    resetButtons.set(name, button);
    return el("li", {}, el("span", { class: "run-name", text: name }), button);
  }));
  syncResetButtons();
}

// ---- wiring ----------------------------------------------------------------------------

const drop = $("drop");
drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("over"); });
drop.addEventListener("dragleave", () => drop.classList.remove("over"));
drop.addEventListener("drop", async (e) => {
  e.preventDefault();
  drop.classList.remove("over");
  try {
    const entries = [...e.dataTransfer.items].map((i) => i.webkitGetAsEntry()).filter(Boolean);
    const pairs = (await Promise.all(entries.map((en) => filesFromEntry(en)))).flat();
    await uploadAndScan(pairs, entries.length === 1 && entries[0].isDirectory ? entries[0].name : null);
  } catch (err) {
    showUploadStatus(`Could not read what was dropped: ${err.message}`, true);
  }
});
// A real button opens the folder picker, so the keyboard reaches it (Tab, then Enter or Space).
$("choose-folder").addEventListener("click", () => $("folder-input").click());
$("folder-input").addEventListener("change", async (e) => {
  const files = [...e.target.files];
  e.target.value = ""; // so picking the same folder again still fires a change
  const label = files[0]?.webkitRelativePath.split("/")[0];
  await uploadAndScan(files.map((f) => [f, f.webkitRelativePath || f.name]), label);
});
$("scan-server").addEventListener("click", () => {
  const path = $("server-path").value.trim();
  if (path) scan(path); else showUploadStatus("Type or pick a folder path first.", true);
});
$("mode-folder").addEventListener("click", () => setMode("folder"));
$("mode-odbc").addEventListener("click", () => setMode("odbc"));
$("mode-git").addEventListener("click", () => setMode("git"));
$("git-fetch").addEventListener("click", fetchGit);
["git-url", "git-branch", "git-extensions"].forEach((id) => {
  $(id).addEventListener("input", onGitInput);
  $(id).addEventListener("keydown", (e) => { if (e.key === "Enter") fetchGit(); });
});
$("odbc-connection").addEventListener("change", () => { state.forceLogin = false; syncOdbcChoice(); });
$("odbc-connect").addEventListener("click", connectOdbc);
["odbc-string", "odbc-user", "odbc-password"].forEach((id) => $(id).addEventListener("keydown", (e) => { if (e.key === "Enter") connectOdbc(); }));
$("odbc-filter").addEventListener("input", () => { if (state.odbc) { state.odbc.filter = $("odbc-filter").value; renderOdbcPicker(); } });
$("odbc-select-tables").addEventListener("click", () => selectAllOdbc("TABLE"));
$("odbc-select-views").addEventListener("click", () => selectAllOdbc("VIEW"));
$("odbc-clear").addEventListener("click", clearOdbcSelection);
$("model").addEventListener("change", showModelNote);
$("pipeline").addEventListener("input", showNameNote);
$("go").addEventListener("click", go);
$("cancel").addEventListener("click", () => cancelRun(state.runId));
$("search").addEventListener("click", search);
$("check-destinations").addEventListener("click", refreshHealth);
$("check-search-destinations").addEventListener("click", refreshHealth);
$("query").addEventListener("keydown", (e) => { if (e.key === "Enter") search(); });
window.addEventListener("unhandledrejection", (e) => showError(`Something went wrong in the page: ${e.reason?.message || e.reason}`));

init().catch((e) => { $("health").textContent = `startup failed: ${e.message}`; });
