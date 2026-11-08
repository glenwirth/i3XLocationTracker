"use strict";

const $ = id => document.getElementById(id);

// Series colors follow discovery order. Objects past the eighth share a neutral gray rather than
// repeating a color that already identifies another object.
const PALETTE = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e34948"];
const OVERFLOW_COLOR = "#9aa0a6";

const ROLLING_WINDOW_MS = 10 * 60 * 1000;
const SAVE_DEBOUNCE_MS = 400;

const SETTINGS_FIELDS = ["baseUrl", "authScheme", "apiKeyHeader", "typeFilter", "writeElementId", "writeCommand", "writeColor"];

const state = {
  connected: false,
  tracking: false,
  objects: [],
  byId: new Map(),
  source: null,
  saveTimer: null,
  selectedRow: null,
};

// ----- Server calls -----

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error ?? `HTTP ${response.status}`);
  return data;
}

// ----- Settings -----

function showTokenState(hasToken) {
  $("token").placeholder = hasToken ? "Saved. Type to replace" : "";
}

async function loadSettings() {
  const saved = await api("GET", "/api/settings");
  for (const field of SETTINGS_FIELDS) $(field).value = saved[field];
  if (!$("writeCommand").value) $("writeCommand").value = "STOP";
  if (!$("writeColor").value) $("writeColor").value = "NONE";
  showTokenState(saved.hasToken);
  rebuildPayload();
  updateButtons();
}

function scheduleSave() {
  clearTimeout(state.saveTimer);
  state.saveTimer = setTimeout(() => saveSettings().catch(showSaveError), SAVE_DEBOUNCE_MS);
}

function showSaveError(error) {
  setStatus(`Couldn't save settings: ${error.message}`);
}

async function saveSettings() {
  clearTimeout(state.saveTimer);
  const update = { token: $("token").value };
  for (const field of SETTINGS_FIELDS) update[field] = $(field).value;
  const saved = await api("PUT", "/api/settings", update);
  if (update.token && $("token").value === update.token) $("token").value = "";
  showTokenState(saved.hasToken);
}

// ----- Connect / discover -----

function setStatus(text) {
  $("status").textContent = text;
}

function updateButtons() {
  $("discover").disabled = !state.connected;
  $("refreshTargets").disabled = !state.connected;
  $("start").disabled = !state.connected || state.tracking || state.objects.length === 0;
  $("stop").disabled = !state.tracking;
  $("send").disabled = !state.connected || !$("writeElementId").value.trim() || !$("writePayload").value.trim();
}

function clearObjects() {
  state.objects = [];
  state.byId = new Map();
  state.selectedRow = null;
  $("objects").replaceChildren();
  renderLegend();
  requestDraw();
}

async function connect() {
  stopTracking();
  clearObjects();
  state.connected = false;
  updateButtons();
  try {
    setStatus("Connecting…");
    await saveSettings();
    const info = await api("POST", "/api/connect");
    state.connected = true;
    setStatus(`Connected to ${info.serverName ?? $("baseUrl").value} (spec ${info.specVersion ?? "?"}).`);
    await loadWriteTargets();
  } catch (error) {
    setStatus(`Connection failed: ${error.message}`);
  }
  updateButtons();
}

async function discover() {
  const filter = $("typeFilter").value;
  try {
    setStatus(`Discovering objects of type '${filter}'…`);
    await saveSettings();
    const found = await api("GET", `/api/objects?type=${encodeURIComponent(filter)}`);

    stopTracking();
    clearObjects();
    found
      .sort((a, b) => (a.displayName ?? a.elementId).localeCompare(b.displayName ?? b.elementId))
      .forEach((info, index) => addObject(info, index));
    renderLegend();
    requestDraw();

    setStatus(found.length === 0
      ? `No objects found with type '${filter}'.`
      : `Found ${found.length} object(s) of type '${filter}'.`);
  } catch (error) {
    setStatus(`Discovery failed: ${error.message}`);
  }
  updateButtons();
}

// ----- Objects table -----

function addObject(info, index) {
  const row = document.createElement("tr");
  const cell = (className) => {
    const td = document.createElement("td");
    if (className) td.className = className;
    row.append(td);
    return td;
  };

  const obj = {
    elementId: info.elementId,
    name: info.displayName || info.elementId,
    color: PALETTE[index] ?? OVERFLOW_COLOR,
    tracked: true,
    points: [],
    seenTimestamps: new Set(),
  };

  const checkbox = document.createElement("input");
  checkbox.type = "checkbox";
  checkbox.checked = true;
  checkbox.setAttribute("aria-label", `Show ${obj.name} on the chart`);
  checkbox.addEventListener("click", event => event.stopPropagation());
  checkbox.addEventListener("change", () => {
    obj.tracked = checkbox.checked;
    renderLegend();
    requestDraw();
  });
  cell().append(checkbox);
  cell().textContent = obj.name;
  cell().textContent = obj.elementId;

  obj.cells = {
    x: cell("num"), y: cell("num"), product: cell(), battery: cell("num"),
    moving: cell(), updated: cell(), status: cell(),
  };
  obj.cells.status.textContent = "waiting for data…";

  row.addEventListener("click", () => {
    state.selectedRow?.classList.remove("selected");
    row.classList.add("selected");
    state.selectedRow = row;
    setWriteElementId(obj.elementId);
  });

  $("objects").append(row);
  state.objects.push(obj);
  state.byId.set(obj.elementId, obj);
}

const formatCoordinate = value => value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const formatTime = timestamp => new Date(timestamp).toLocaleTimeString(undefined, { hour12: false });

function applyUpdate(update) {
  const obj = state.byId.get(update.elementId);
  if (!obj) return false;

  if (update.error) {
    obj.cells.status.textContent = update.error;
    return false;
  }
  if (obj.seenTimestamps.has(update.timestamp)) return false;
  obj.seenTimestamps.add(update.timestamp);

  obj.points.push({ x: update.x, y: update.y, t: update.timestamp });

  obj.cells.x.textContent = formatCoordinate(update.x);
  obj.cells.y.textContent = formatCoordinate(update.y);
  obj.cells.product.textContent = update.product ?? "";
  obj.cells.battery.textContent = update.battery == null ? "" : Math.round(update.battery).toString();
  obj.cells.moving.textContent = update.isMoving == null ? "" : update.isMoving ? "True" : "False";
  obj.cells.updated.textContent = formatTime(update.timestamp);
  obj.cells.status.textContent = "live";
  return true;
}

function trimOldPoints(obj) {
  const cutoff = Date.now() - ROLLING_WINDOW_MS;
  let expired = 0;
  while (expired < obj.points.length && obj.points[expired].t < cutoff) {
    obj.seenTimestamps.delete(obj.points[expired].t);
    expired++;
  }
  if (expired > 0) obj.points.splice(0, expired);
}

// ----- Live tracking -----

function startTracking() {
  const query = state.objects.map(o => `id=${encodeURIComponent(o.elementId)}`).join("&");
  const source = new EventSource(`/api/stream?${query}`);
  state.source = source;
  state.tracking = true;
  setStatus("Opening subscription…");
  updateButtons();

  source.addEventListener("status", event => setStatus(JSON.parse(event.data)));
  source.addEventListener("fatal", event => {
    endStream();
    setStatus(JSON.parse(event.data));
  });
  source.addEventListener("update", event => {
    let changed = false;
    for (const update of JSON.parse(event.data)) changed = applyUpdate(update) || changed;
    if (changed) requestDraw();
  });
  source.onerror = () => {
    if (source.readyState === EventSource.CLOSED) {
      endStream();
      setStatus("Lost the connection to the web server.");
    } else {
      setStatus("Lost the connection to the web server; reconnecting…");
    }
  };
}

function endStream() {
  state.source?.close();
  state.source = null;
  state.tracking = false;
  updateButtons();
}

function stopTracking() {
  if (!state.tracking) return;
  endStream();
  setStatus("Stopped.");
}

// ----- Write command -----

async function loadWriteTargets() {
  try {
    const all = await api("GET", "/api/objects");
    all.sort((a, b) => a.elementId.localeCompare(b.elementId, undefined, { sensitivity: "base" }));

    const options = [new Option("", "")];
    for (const target of all) {
      const label = !target.displayName || target.displayName === target.elementId
        ? target.elementId
        : `${target.displayName}  —  ${target.elementId}`;
      options.push(new Option(label, target.elementId));
    }
    $("writeTarget").replaceChildren(...options);
    syncWriteTarget();
    $("writeStatus").textContent = `${all.length} object(s) available as write targets.`;
  } catch (error) {
    $("writeStatus").textContent = `Couldn't list objects for the write picker (${error.message}); you can still type an Element Id.`;
  }
}

function syncWriteTarget() {
  const id = $("writeElementId").value;
  const isListed = [...$("writeTarget").options].some(option => option.value === id);
  $("writeTarget").value = isListed ? id : "";
}

function setWriteElementId(id) {
  $("writeElementId").value = id;
  syncWriteTarget();
  updateButtons();
  scheduleSave();
}

function rebuildPayload() {
  $("writePayload").value = JSON.stringify({ Command: $("writeCommand").value, Color: $("writeColor").value });
  updateButtons();
}

async function sendCommand() {
  const elementId = $("writeElementId").value.trim();
  const time = () => new Date().toLocaleTimeString(undefined, { hour12: false });

  let value;
  try {
    value = JSON.parse($("writePayload").value);
  } catch (error) {
    $("writeStatus").textContent = `Payload is not valid JSON: ${error.message}`;
    return;
  }

  $("send").disabled = true;
  try {
    $("writeStatus").textContent = `Writing to ${elementId}…`;
    await api("POST", "/api/write", { elementId, value });
    $("writeStatus").textContent = `${time()}  Wrote ${JSON.stringify(value)} to ${elementId}.`;
  } catch (error) {
    $("writeStatus").textContent = `${time()}  Write to ${elementId} failed: ${error.message}`;
  }
  updateButtons();
}

// ----- Trajectory chart -----

const canvas = $("chart");
const context = canvas.getContext("2d");
const MARGIN = { left: 72, right: 24, top: 14, bottom: 46 };
const GRID_COLOR = "#3a3a3d";
const SURFACE_COLOR = "#252526";
const TEXT_COLOR = "#dcdcdc";
const MUTED_COLOR = "#9aa0a6";
const HOVER_RADIUS = 24;
const LABEL_LIMIT = 4;
const LABEL_LINE_HEIGHT = 15;

let drawPending = false;
let plot = null;
let pointer = null;

function requestDraw() {
  if (drawPending) return;
  drawPending = true;
  requestAnimationFrame(() => {
    drawPending = false;
    draw();
  });
}

function niceTicks(min, max, approxCount) {
  const rawStep = (max - min) / Math.max(approxCount, 1);
  const magnitude = 10 ** Math.floor(Math.log10(rawStep));
  const step = [1, 2, 5, 10].map(n => n * magnitude).find(s => s >= rawStep) ?? 10 * magnitude;
  const ticks = [];
  for (let v = Math.ceil(min / step) * step; v <= max + step * 1e-9; v += step) ticks.push(Math.round(v / step) * step);
  return ticks;
}

function axisRange(values) {
  if (values.length === 0) return [0, 100];
  let min = Math.min(...values);
  let max = Math.max(...values);
  if (min === max) { min -= 1; max += 1; }
  const padding = (max - min) * 0.06;
  return [min - padding, max + padding];
}

function drawStar(x, y, radius, color) {
  context.beginPath();
  for (let i = 0; i < 10; i++) {
    const r = i % 2 === 0 ? radius : radius * 0.45;
    const angle = -Math.PI / 2 + i * Math.PI / 5;
    context[i === 0 ? "moveTo" : "lineTo"](x + r * Math.cos(angle), y + r * Math.sin(angle));
  }
  context.closePath();
  context.fillStyle = color;
  context.fill();
  context.lineWidth = 1;
  context.strokeStyle = "#ffffff";
  context.stroke();
}

function draw() {
  const dpr = window.devicePixelRatio || 1;
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) {
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
  }
  context.setTransform(dpr, 0, 0, dpr, 0, 0);
  context.clearRect(0, 0, width, height);

  state.objects.forEach(trimOldPoints);
  const visible = state.objects.filter(o => o.tracked && o.points.length > 0);

  const [xMin, xMax] = axisRange(visible.flatMap(o => o.points.map(p => p.x)));
  const [yMin, yMax] = axisRange(visible.flatMap(o => o.points.map(p => p.y)));
  const plotWidth = Math.max(width - MARGIN.left - MARGIN.right, 1);
  const plotHeight = Math.max(height - MARGIN.top - MARGIN.bottom, 1);
  const toX = x => MARGIN.left + (x - xMin) / (xMax - xMin) * plotWidth;
  const toY = y => MARGIN.top + plotHeight - (y - yMin) / (yMax - yMin) * plotHeight;
  plot = { visible, toX, toY };

  drawAxes(xMin, xMax, yMin, yMax, plotWidth, plotHeight, toX, toY);

  context.save();
  context.beginPath();
  context.rect(MARGIN.left, MARGIN.top, plotWidth, plotHeight);
  context.clip();
  for (const obj of visible) {
    context.beginPath();
    obj.points.forEach((p, i) => context[i === 0 ? "moveTo" : "lineTo"](toX(p.x), toY(p.y)));
    context.lineWidth = 2;
    context.lineJoin = "round";
    context.strokeStyle = obj.color;
    context.stroke();

    context.fillStyle = obj.color;
    for (const p of obj.points) {
      context.beginPath();
      context.arc(toX(p.x), toY(p.y), 2.5, 0, Math.PI * 2);
      context.fill();
    }
  }
  context.restore();

  context.font = "12px 'Segoe UI', system-ui, sans-serif";
  const placedLabels = [];
  for (const obj of visible) {
    const head = obj.points[obj.points.length - 1];
    const x = toX(head.x);
    const y = toY(head.y);
    drawStar(x, y, 9, obj.color);

    if (visible.length <= LABEL_LIMIT) {
      const labelWidth = context.measureText(obj.name).width;
      const flip = x + 14 + labelWidth > width - 4;
      const left = flip ? x - 14 - labelWidth : x + 14;

      // Objects at nearly the same spot would print over each other, so stack their names.
      let labelY = y;
      while (placedLabels.some(l => Math.abs(l.y - labelY) < LABEL_LINE_HEIGHT && left < l.right && left + labelWidth > l.left)) {
        labelY += LABEL_LINE_HEIGHT;
      }
      placedLabels.push({ left, right: left + labelWidth, y: labelY });

      context.fillStyle = TEXT_COLOR;
      context.textAlign = "left";
      context.textBaseline = "middle";
      context.fillText(obj.name, left, labelY);
    }
  }

  updateTooltip();
}

function drawAxes(xMin, xMax, yMin, yMax, plotWidth, plotHeight, toX, toY) {
  context.font = "12px 'Segoe UI', system-ui, sans-serif";
  context.lineWidth = 1;
  context.strokeStyle = GRID_COLOR;
  context.fillStyle = MUTED_COLOR;

  const bottom = MARGIN.top + plotHeight;
  const right = MARGIN.left + plotWidth;

  context.textAlign = "center";
  context.textBaseline = "top";
  for (const tick of niceTicks(xMin, xMax, Math.max(Math.floor(plotWidth / 90), 2))) {
    const x = Math.round(toX(tick)) + 0.5;
    context.beginPath();
    context.moveTo(x, MARGIN.top);
    context.lineTo(x, bottom);
    context.stroke();
    context.fillText(tick.toLocaleString(), x, bottom + 6);
  }

  context.textAlign = "right";
  context.textBaseline = "middle";
  for (const tick of niceTicks(yMin, yMax, Math.max(Math.floor(plotHeight / 50), 2))) {
    const y = Math.round(toY(tick)) + 0.5;
    context.beginPath();
    context.moveTo(MARGIN.left, y);
    context.lineTo(right, y);
    context.stroke();
    context.fillText(tick.toLocaleString(), MARGIN.left - 8, y);
  }

  context.fillStyle = TEXT_COLOR;
  context.textAlign = "center";
  context.textBaseline = "alphabetic";
  context.fillText("X", MARGIN.left + plotWidth / 2, bottom + 38);
  context.save();
  context.translate(16, MARGIN.top + plotHeight / 2);
  context.rotate(-Math.PI / 2);
  context.fillText("Y", 0, 0);
  context.restore();
}

function updateTooltip() {
  const tooltip = $("tooltip");
  let nearest = null;
  if (pointer && plot) {
    let bestDistance = HOVER_RADIUS * HOVER_RADIUS;
    for (const obj of plot.visible) {
      for (const point of obj.points) {
        const dx = plot.toX(point.x) - pointer.x;
        const dy = plot.toY(point.y) - pointer.y;
        const distance = dx * dx + dy * dy;
        if (distance < bestDistance) {
          bestDistance = distance;
          nearest = { obj, point };
        }
      }
    }
  }

  if (!nearest) {
    tooltip.hidden = true;
    return;
  }

  const { obj, point } = nearest;
  const x = plot.toX(point.x);
  const y = plot.toY(point.y);

  context.beginPath();
  context.arc(x, y, 6, 0, Math.PI * 2);
  context.lineWidth = 2;
  context.strokeStyle = SURFACE_COLOR;
  context.fillStyle = obj.color;
  context.fill();
  context.stroke();

  const title = document.createElement("strong");
  title.textContent = obj.name;
  tooltip.replaceChildren(title, `X ${formatCoordinate(point.x)}   Y ${formatCoordinate(point.y)}`,
    document.createElement("br"), formatTime(point.t));
  tooltip.hidden = false;

  const box = tooltip.getBoundingClientRect();
  const left = x + 14 + box.width > canvas.clientWidth ? x - 14 - box.width : x + 14;
  const top = Math.max(4, Math.min(y - box.height / 2, canvas.clientHeight - box.height - 4));
  tooltip.style.left = `${left}px`;
  tooltip.style.top = `${top}px`;
}

function renderLegend() {
  const items = state.objects.filter(o => o.tracked).map(obj => {
    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";
    swatch.style.background = obj.color;
    const item = document.createElement("span");
    item.className = "legend-item";
    item.append(swatch, obj.name);
    return item;
  });
  $("legend").replaceChildren(...items);
}

// ----- Wiring -----

$("connect").addEventListener("click", connect);
$("discover").addEventListener("click", discover);
$("start").addEventListener("click", startTracking);
$("stop").addEventListener("click", stopTracking);
$("refreshTargets").addEventListener("click", loadWriteTargets);
$("send").addEventListener("click", sendCommand);

for (const id of ["baseUrl", "authScheme", "token", "apiKeyHeader", "typeFilter"]) {
  $(id).addEventListener("input", scheduleSave);
}
for (const id of ["writeCommand", "writeColor"]) {
  $(id).addEventListener("input", () => { rebuildPayload(); scheduleSave(); });
}
$("writeTarget").addEventListener("input", () => {
  if ($("writeTarget").value) setWriteElementId($("writeTarget").value);
});
$("writeElementId").addEventListener("input", () => {
  syncWriteTarget();
  updateButtons();
  scheduleSave();
});
$("writePayload").addEventListener("input", updateButtons);

canvas.addEventListener("mousemove", event => {
  const rect = canvas.getBoundingClientRect();
  pointer = { x: event.clientX - rect.left, y: event.clientY - rect.top };
  requestDraw();
});
canvas.addEventListener("mouseleave", () => {
  pointer = null;
  requestDraw();
});
new ResizeObserver(requestDraw).observe(canvas);

loadSettings().catch(error => setStatus(`Couldn't load settings: ${error.message}`));
requestDraw();
