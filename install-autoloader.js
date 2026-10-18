// install-autoloader.js - PYRE "push the PYRE autoloader" page (wave 3).
//
// NEW, non-frozen layer. Nothing frozen is edited. It reuses the frozen pieces exactly:
//   * the memory primitive : src/webkit.js establishPrimitive + src/utils/mem.js installWindowP
//                            (the same primitive haze-site.js builds for RUN PYRE)
//   * the ROP chain        : window.prepareRop from haze-main.js (the same one RUN PYRE uses)
//   * the payload transport: sendPayload from haze-sender.js (the same socket writer RUN PYRE
//                            uses for kstuff/etaHEN/Payload Manager - no new transport invented)
//
// It sends EXACTLY ONE file and nothing else:
//   our-payloads/pyre-autoloader-installer_v0.5.2-pyre.elf
// No kernel exploit, no kstuff, no shadowmountplus, no etaHEN, no Payload Manager, no App
// Dumper, no browser installer. The RUN PYRE sequence is not run here at all.
//
// It cannot conjure a loader: the console's ELF loader only listens on port 9021 once the
// exploit has been run in this console session. On a cold boot the push fails fast, this
// page says the loader is not answering, and it stops.

import { establishPrimitive } from "./src/webkit.js";
import { installWindowP } from "./src/utils/mem.js"; // also installs globalThis.int64, which prepareRop needs

// bytes/sha256 are descriptive here (shown in the log); the push does not re-verify them, so
// keep them in step with the file in our-payloads/ rather than treating them as a check.
const AUTOLOADER = {
  id: "pyre-autoloader",
  title: "PYRE autoloader installer",
  file: "/our-payloads/pyre-autoloader-installer_v0.5.2-pyre.elf",
  bytes: 2278736,
  sha256: "2226f9b519280df117634a64d1b4ca3eb483b46fbb762e36241a046b3ed70a8b",
};

const logBox = document.getElementById("autoloader-log");
const statusLine = document.getElementById("autoloader-status");
const errorBox = document.getElementById("autoloader-error");
const button = document.getElementById("install-autoloader");

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
  if (statusLine) {
    statusLine.textContent = "Latest status: " + message;
  }
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

let running = false;

async function pushAutoloader() {
  if (running) return;
  running = true;
  if (button) button.disabled = true;
  if (errorBox) errorBox.hidden = true;

  try {
    const rejection = window.firmware && typeof window.firmware.rejection === "function"
      ? window.firmware.rejection()
      : "The firmware check did not load.";
    if (rejection) {
      fail(rejection);
      return;
    }

    if (typeof window.prepareRop !== "function") {
      fail("The ROP layer did not load, so the push cannot start. Reload this page.");
      return;
    }

    if (!(await waitForOffsets(5000))) {
      fail("The firmware offset tables did not load for " + String(window.fw_str) + ". Reload this page.");
      return;
    }

    writeLog("Building the memory primitive (the same WebKit primitive RUN PYRE uses).", "info");
    const primitive = installWindowP(await establishPrimitive(writeEvent));
    if (!primitive || typeof primitive.read8 !== "function") {
      fail("The memory primitive was not available, so the push cannot start.");
      return;
    }

    writeLog("Building the payload socket chain (WebKit ROP only - no kernel exploit is run).", "info");
    const { p, chain } = await window.prepareRop(primitive);

    const { sendPayload } = await import("./haze-sender.js?v=red-menu-1");
    writeLog("Pushing " + AUTOLOADER.file + " (" + AUTOLOADER.bytes.toLocaleString() + " bytes) to the console's loader.", "info");
    const length = await sendPayload(AUTOLOADER, p, chain);

    writeLog(
      AUTOLOADER.title + ": " + length.toLocaleString() +
      " bytes transferred. Watch the console's notification; transfer alone does not confirm installation.",
      "success",
    );
  } catch (error) {
    const message = String(error && error.message ? error.message : error);
    if (/9021|ELF loader/i.test(message)) {
      fail(
        "Nothing answered on the console's ELF loader port 9021. That loader only exists after the " +
        "exploit has been run once in this console session. Go back to the PYRE home page and press " +
        "RUN PYRE first, then return here and press this button again. This page cannot start the " +
        "exploit for you, and it has sent nothing.",
      );
    } else {
      fail("Could not push the PYRE autoloader installer: " + message);
    }
  } finally {
    running = false;
    if (button) button.disabled = false;
  }
}

if (button) {
  button.addEventListener("click", pushAutoloader);
} else {
  fail("The install button is missing from the page.");
}
