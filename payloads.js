// payloads.js - PYRE "launch a payload" page (wave 6).
//
// NEW, non-frozen layer. Nothing frozen is edited: it reuses the same three pieces
// install-autoloader.js uses, and the same catalogue/sender the live menu uses.
//   * the memory primitive : src/webkit.js establishPrimitive + src/utils/mem.js installWindowP
//   * the ROP chain        : window.prepareRop from haze-main.js (WebKit ROP only)
//   * the payload transport: sendPayload from haze-sender.js (fetch -> length/ELF magic/SHA-256
//                            check -> push to the console's loader on port 9021)
//
// After a payload is sent it is ALSO filed into PYRE Payloads (pldmgr) so it can be launched
// again later from the manager, including after a reboot, without the PC. That copy is written
// straight into pldmgr's own storage with the console's file syscalls - see fileIntoManager.
// pldmgr refuses cross-origin HTTP writes on purpose ("Mutation origin or request marker
// rejected"), so an HTTP upload from this page is not possible; and it lists *slots* (a folder
// plus meta.json), not loose files, so the file has to land in that shape. The layout below is
// copied from pldmgr's source: storage is /data/pldmgr/payloads, a local slot is
// local/<slug>/ with the ELF and a meta.json beside it, schema 2, kind "local".
//
// Why this page exists: the live menu only exists inside the RUN PYRE document, so a separate
// page that builds the primitive itself is the only way to have a working payload list you can
// come back to after a jailbreak.
//
// It cannot conjure a loader. Port 9021 only listens once the kernel stage has run in this
// console session, so on a cold boot every button fails fast with a plain explanation and
// sends nothing.

import { establishPrimitive } from "./src/webkit.js";
import { installWindowP } from "./src/utils/mem.js"; // also installs globalThis.int64 for prepareRop
import { renderCards, payloads } from "./haze-catalog.js?v=services-1";

const logBox = document.getElementById("payloads-log");
const statusLine = document.getElementById("payloads-status");
const errorBox = document.getElementById("payloads-error");
const grid = document.getElementById("haze-live-cards");
const reloadButton = document.getElementById("payloads-reload");

function writeLog(message, type = "log") {
  if (logBox) {
    const line = document.createElement("div");
    let marker = "*";
    if (type === "error") marker = "-";
    if (type === "info" || type === "success") marker = "+";
    line.textContent = `[${marker}] ${message}`;
    logBox.appendChild(line);
    logBox.scrollTop = logBox.scrollHeight;
  }
  if (statusLine) statusLine.textContent = "Latest status: " + message;
}

function writeEvent(name, detail, type) {
  writeLog(
    detail == null || detail === "" ? name : `${name}: ${detail}`,
    type || (name === "Failed" ? "error" : "log"),
  );
}

// haze-main.js's prepareRop / watchR2 call log() and jb.mark(); provide both, as haze-site.js does.
window.writeLog = writeLog;
window.jb = { mark: writeEvent };

function fail(message) {
  if (errorBox) {
    errorBox.textContent = message;
    errorBox.hidden = false;
  }
  writeLog(message, "error");
}

// The firmware offset tables (offsets/<fw>.js) are added as an async <script> by haze-main.js.
// prepareRop cannot run without them, so wait briefly instead of dying on a ReferenceError.
function offsetsReady() {
  try {
    return (
      typeof OFFSET_wk_host_constructor_candidates !== "undefined" &&
      typeof syscall_map !== "undefined" &&
      typeof wk_gadgetmap !== "undefined"
    );
  } catch (_) {
    return false;
  }
}

function waitForOffsets(timeoutMs) {
  return new Promise((resolve) => {
    const started = Date.now();
    (function poll() {
      if (offsetsReady()) return resolve(true);
      if (Date.now() - started > timeoutMs) return resolve(false);
      setTimeout(poll, 50);
    }());
  });
}

// ------------------------------------------------------------------ PYRE Payloads filing

// Copied from pldmgr: PAYLOADS_STORAGE_DIR, PAYLOAD_SLOT_SCHEMA, SLOT_KIND_LOCAL.
const MANAGER_ROOT = "/data/pldmgr/payloads";
const MANAGER_SCHEMA = 2;
const MANAGER_KIND = "local";
const FILE_CHUNK = 0x100000; // 1 MB at a time, so a 30 MB payload never lands in one buffer

// pldmgr's slot_slug: lowercase alphanumerics kept, '.' and '_' kept, anything else becomes a
// single dash, no trailing dash. The extension is dropped first, as slot_local_key does.
function slotSlug(input) {
  let out = "";
  let lastDash = true; // as in C: starts as a dash so a leading separator cannot be emitted
  for (const character of String(input)) {
    const c = character.toLowerCase();
    if (/[a-z0-9]/.test(c)) {
      out += c;
      lastDash = false;
    } else if ((c === "." || c === "_") && out.length > 0 && out[out.length - 1] !== ".") {
      out += c;
      lastDash = false;
    } else if (!lastDash) {
      out += "-";
      lastDash = true;
    }
  }
  while (out.endsWith("-")) out = out.slice(0, -1);
  return out;
}

function slotKeyFor(filename) {
  const base = filename.replace(/\.[^.]*$/, "");
  const slug = slotSlug(base) || "payload";
  return `local/${slug}`;
}

function installer() {
  // The same native_socket bridge haze-services.js uses for config files.
  const p = session.p;
  const chain = session.chain;
  const call = async (n, ...args) => (await chain.syscall(n, ...args)).low | 0;
  const usable = () => !p.worker_is_usable || p.worker_is_usable();
  const buffer = p.malloc(FILE_CHUNK + 1, 1);
  const close = async (fd) => { if (usable()) await call(SYS_CLOSE, fd); };

  async function exists(path) {
    return (await call(SYS_ACCESS, p.stringify(path), 0)) === 0;
  }

  async function mkdir(path) {
    if ((await call(SYS_MKDIR, p.stringify(path), 0o755)) < 0 && !(await exists(path)))
      throw new Error("Cannot create folder: " + path);
  }

  async function writeText(path, text) {
    const bytes = new TextEncoder().encode(text);
    if (bytes.length > FILE_CHUNK) throw new Error("Text too large for the write buffer");
    for (let i = 0; i < bytes.length; i++) p.write1(buffer.add32(i), bytes[i]);
    const fd = await call(SYS_OPEN, p.stringify(path), 1 | 0x200 | 0x400, 0o644);
    if (fd < 0) throw new Error("Cannot write " + path);
    try {
      for (let offset = 0; offset < bytes.length;) {
        const n = await call(SYS_WRITE, fd, buffer.add32(offset), bytes.length - offset);
        if (n <= 0 || n > bytes.length - offset) throw new Error("Short write: " + path);
        offset += n;
      }
      await call(SYS_FSYNC, fd);
    } finally { await close(fd); }
  }

  async function writeFromUrl(path, url, knownBytes) {
    // The exact size comes from the catalogue, so the copy does not depend on the host
    // reporting Content-Range. The header is only a fallback.
    let total = Number(knownBytes) || 0;
    if (!total) {
      const head = await fetch(url, { headers: { Range: "bytes=0-0" } });
      if (!head.ok) throw new Error("Copy source is not readable: HTTP " + head.status);
      total = Number((/bytes\s+\d+-(\d+)\/(\d+)/.exec(head.headers.get("content-range") || "") || [])[2] || 0);
      if (!total) throw new Error("This host did not report the file size for the copy");
    }

    const fd = await call(SYS_OPEN, p.stringify(path), 1 | 0x200 | 0x400, 0o644);
    if (fd < 0) throw new Error("Cannot write " + path);
    try {
      for (let offset = 0; offset < total;) {
        const end = Math.min(offset + FILE_CHUNK, total) - 1;
        const response = await fetch(url, { headers: { Range: `bytes=${offset}-${end}` } });
        if (!response.ok) throw new Error("Copy stopped: HTTP " + response.status);
        const bytes = new Uint8Array(await response.arrayBuffer());
        if (bytes.length === 0) throw new Error("Copy stopped: empty chunk");
        const dwords = bytes.length & ~3;
        for (let i = 0; i < dwords; i += 4)
          p.write4(buffer.add32(i), bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24));
        for (let i = dwords; i < bytes.length; i++) p.write1(buffer.add32(i), bytes[i]);
        let written = 0;
        while (written < bytes.length) {
          const n = await call(SYS_WRITE, fd, buffer.add32(written), bytes.length - written);
          if (n <= 0 || n > bytes.length - written) throw new Error("Short write: " + path);
          written += n;
        }
        offset += bytes.length;
      }
      await call(SYS_FSYNC, fd);
    } finally { await close(fd); }
  }

  return { exists, mkdir, writeText, writeFromUrl };
}

function localTimestamp() {
  const now = new Date();
  const pad = (value) => String(value).padStart(2, "0");
  const offsetMinutes = -now.getTimezoneOffset();
  const sign = offsetMinutes < 0 ? "-" : "+";
  const offset = sign + pad(Math.floor(Math.abs(offsetMinutes) / 60)) + pad(Math.abs(offsetMinutes) % 60);
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T` +
         `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}${offset}`;
}

// Put the payload into PYRE Payloads so it can be launched again from the manager later.
// Never fatal: the payload has already been sent at this point, so any failure is a warning.
async function fileIntoManager(item) {
  const filename = item.file.split("/").pop();
  const base = slotKeyFor(filename);
  const files = installer();
  try {
    await files.mkdir("/data/pldmgr");
    await files.mkdir(MANAGER_ROOT);
    await files.mkdir(MANAGER_ROOT + "/local");

    let key = base;
    for (let i = 2; i <= 1000; i++) {
      if (!(await files.exists(`${MANAGER_ROOT}/${key}`))) break;
      key = `${base}-${i}`;
    }
    if (await files.exists(`${MANAGER_ROOT}/${key}`))
      throw new Error("the manager has no free slot name for this payload");

    await files.mkdir(`${MANAGER_ROOT}/${key}`);
    await files.writeFromUrl(`${MANAGER_ROOT}/${key}/${filename}`, item.file, item.bytes);
    await files.writeText(`${MANAGER_ROOT}/${key}/meta.json`, JSON.stringify({
      schema: MANAGER_SCHEMA,
      kind: MANAGER_KIND,
      filename,
      installed_at: localTimestamp(),
      origin: "pyre_payloads_page",
      origin_detail: item.title || filename,
    }, null, 2) + "\n");

    writeLog("Added to PYRE Payloads as " + key + " - open the manager to launch it again.", "success");
    return true;
  } catch (error) {
    writeLog(
      "Sent, but could not add it to PYRE Payloads: " + String(error && error.message ? error.message : error),
      "error",
    );
    return false;
  }
}

// ------------------------------------------------------------------ sending

let sending = false;
let session = null; // { p, chain } - built once, on the first launch
const sent = new Set();

function busy(on) {
  sending = on;
  document.querySelectorAll("#haze-live-cards button").forEach((button) => {
    const item = payloads.find((entry) => entry.id === button.dataset.payload);
    button.disabled = on || (item && !item.repeatable && sent.has(item.id));
  });
}

async function buildSession() {
  if (session) return session;

  const rejection = window.firmware && typeof window.firmware.rejection === "function"
    ? window.firmware.rejection()
    : "The firmware check did not load.";
  if (rejection) throw new Error(rejection);

  if (typeof window.prepareRop !== "function") {
    throw new Error("The ROP layer did not load, so nothing can be sent. Reload this page.");
  }

  if (!(await waitForOffsets(5000))) {
    throw new Error("The firmware offset tables did not load for " + String(window.fw_str) + ". Reload this page.");
  }

  writeLog("Building the memory primitive (the same WebKit primitive RUN PYRE uses).", "info");
  const primitive = installWindowP(await establishPrimitive(writeEvent));
  if (!primitive || typeof primitive.read8 !== "function") {
    throw new Error("The memory primitive was not available, so nothing can be sent.");
  }

  writeLog("Building the payload socket chain (WebKit ROP only - no kernel exploit is run).", "info");
  session = await window.prepareRop(primitive);
  writeLog("Ready. Choose a payload.", "success");
  return session;
}

async function launch(item, button) {
  if (sending) return;
  if (!item.repeatable && sent.has(item.id)) return;

  busy(true);
  if (errorBox) errorBox.hidden = true;
  writeLog("Downloading " + item.title + " from this PC and checking it against the catalogue.", "info");

  try {
    const { p, chain } = await buildSession();
    const { sendPayload } = await import("./haze-sender.js?v=red-menu-1");
    const length = await sendPayload(item, p, chain);
    sent.add(item.id);
    button.textContent = item.repeatable ? "Send again" : "Transferred";
    writeLog(
      item.title + ": " + length.toLocaleString() + " bytes transferred. Watch the console's " +
      "notification; a transfer alone does not confirm it started.",
      "success",
    );
    await fileIntoManager(item);
  } catch (error) {
    const message = String(error && error.message ? error.message : error);
    if (/9021|ELF loader/i.test(message)) {
      fail(
        "Nothing answered on the console's ELF loader port 9021, so nothing was sent. That loader only " +
        "exists after the kernel stage has run once in this console session. Go back to the PYRE menu, " +
        "press RUN PYRE, wait for the sequence to finish, then come back here and press the button again. " +
        "This page cannot start the exploit for you.",
      );
    } else if (/HTTPS/i.test(message)) {
      fail(
        "The console refused the checksum step. Open this page the same way you reached PYRE (through " +
        "the console's User's Guide), not over a plain http address.",
      );
    } else {
      fail("Could not send " + item.title + ": " + message);
    }
  } finally {
    busy(false);
  }
}

if (grid) {
  renderCards(grid, launch);
  writeLog(
    "This page sends payloads already sitting beside Pyre.exe on your PC. It needs the console already " +
    "jailbroken in this session: press RUN PYRE first, then come back. Whatever you send here is also " +
    "filed into PYRE Payloads, so you can launch it again from the manager.",
    "info",
  );
} else {
  fail("The payload list is missing from this page.");
}

if (reloadButton) reloadButton.addEventListener("click", () => location.reload());
