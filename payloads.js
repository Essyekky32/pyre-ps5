// payloads.js - PYRE "launch a payload" page (wave 6).
//
// NEW, non-frozen layer. Nothing frozen is edited: it reuses the same three pieces
// install-autoloader.js uses, and the same catalogue/sender the live menu uses.
//   * the memory primitive : src/webkit.js establishPrimitive + src/utils/mem.js installWindowP
//   * the ROP chain        : window.prepareRop from haze-main.js (WebKit ROP only)
//   * the payload transport: sendPayload from haze-sender.js (fetch -> length/ELF magic/SHA-256
//                            check -> push to the console's loader on port 9021)
//
// Why this page exists: the cards page (additional.html) is static HTML with no script and
// cannot send anything, and the live menu only exists inside the RUN PYRE document. This page
// builds the primitive itself, so the payload list works as a launcher after a jailbreak.
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
    "jailbroken in this session: press RUN PYRE first, then come back.",
    "info",
  );
} else {
  fail("The payload list is missing from this page.");
}

if (reloadButton) reloadButton.addEventListener("click", () => location.reload());
