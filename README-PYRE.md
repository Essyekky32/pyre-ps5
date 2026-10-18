# PYRE — PS5 jailbreak self-host

Our own self-hosted copy of the **Haze / Relapse** autoloader, reskinned. It runs from
this PC, on your own LAN, so nothing depends on somebody else's GitHub Pages site.

Origin: `C:\Users\Essy\Downloads\Relapse-Exploit-ps5-autoload` (the `ps5-autoload`
branch of haze7714's Relapse fork).

---

## Run it

1. Double-click `start-server.cmd` (or run `python serve.py`).
2. It prints a URL like `http://192.168.x.x:8000/` — that's your host address.
3. On the PS5, open the User's Guide / browser and go to that URL.
4. Pick **RUN PYRE**. Keep the page open.

The console must be on the same network as this PC. Port 8000 is what `serve.py` uses.

---

## What PYRE changed (cosmetic, plus one functional addition)

| File | Change |
|---|---|
| `haze-menu.css` | Full respray: charcoal + ember palette, new type treatment. **Every original selector, id and class name is preserved** so the frozen scripts still find what they expect. |
| `index.html` | Our landing page — PYRE title, wording, and a corrected firmware range in the footer. |
| `run.html` | Visible wording + the inline style block recoloured. **All ids, script tags and their order are untouched.** |
| `additional.html` | Wording / brand only. Card content, links and ports unchanged. |
| `access.html`, `browser.html`, `classic.html`, `loader-fix.html` | Brand wording only. |
| `pyre-theme.js` | **New, presentation-only.** Some wording is baked into the frozen scripts (`haze-start.js` writes "starting Haze once for this page"; `haze-session.js` builds a menu containing "HAZE / YOUR TOOLKIT"). This layer rebrands those strings in the DOM after the fact. It never touches `#console` or `#startup-error`, so exploit output is reported exactly as the code produced it, and it no-ops on any surprise. Delete its `<script>` tag in `run.html` to remove it. |
| `install-autoloader.html` | **New page.** Installs the PYRE autoloader: it pushes `our-payloads/pyre-autoloader-installer_v0.5.2-pyre.elf` to the console's ELF loader (port 9021). Linked from Additional Payloads. |
| `install-autoloader.js` | **New, non-frozen logic.** Reuses the same WebKit memory primitive, ROP chain and payload transport as RUN PYRE to send exactly one file — it does not run the kernel exploit. |

Two deliberate choices:
- The landing rain animation is drawn in the old palette by `haze-home.js`. Instead of
  editing that file, `haze-menu.css` recolours the canvas with a CSS filter.
- It is kept **lean on purpose**. There is a documented memory warning and a crash at the
  end of startup in this build, so there are no extra effects or libraries. Pretty, not heavy.

---

## What is FROZEN (not touched, on purpose)

`src/` (exploit chain + ROP), `offsets/`, `payloads/`, and the logic scripts
`haze-main.js`, `haze-site.js`, `haze-sequence.js`, `haze-services.js`,
`haze-session.js`, `haze-start.js`, `haze-catalog.js`, `haze-catalog-data.js`,
`haze-sender.js`, `haze-home.js`, plus `serve.py`.

That means: kernel offsets, the exploit chain, payload binaries, the 15-second spacing,
SHA256 checks, the service sequence and the etaHEN loader-repair routing are all
byte-identical to the bundle we started from.

**Four deliberate exceptions, all in `haze-sequence.js` (2026-10-18), and nothing else in the
frozen set has moved:**

1. A failure in the Payload Manager / App Dumper startup step is now **logged and survived**
   instead of aborting the whole sequence. kstuff and ShadowMountPlus have already been sent by
   that point, so the console is jailed either way; aborting only hid the tool menu and made a
   working jailbreak look broken.
2. **etaHEN is no longer part of the automatic chain.** The kernel stage pushes the ELF loader
   (elfldr) and then kstuff and ShadowMountPlus are sent; etaHEN ships as an ordinary entry in
   the payload list and is launched when it is wanted. The sequence also confirms the loader is
   answering on 9021 before sending anything, since no payload can be pushed without it.
3. **Payload spacing is 10 seconds, not 15.**
4. The **original-browser installer is gone** - not shipped and not sent. Upstream's accepts only
   the 13.42 SDK identifier, so on a 13.60 console it stopped with an "unsupported version"
   notice and did nothing else. `payloads/haze-browser-installer.elf`, its build metadata and
   the `?browser=` option are all removed, and the pages that listed that step as part of
   startup no longer do. The way back into PYRE is the console's User's Guide, which Pyre's own
   DNS and TLS listeners answer.

`_FROZEN-MANIFEST.txt` holds the SHA256 of all 84 of those files, taken before any edit. It is a
record, not a gate: the two logic lines we have amended (`haze-catalog-data.js`,
`haze-sequence.js`) are kept current, while the payload binaries we swapped on request
(`etaHEN.elf`, `kstuff.elf`, `shadowmountplus.elf`) and the files we removed from the site root
(16 `optional-*.elf`, plus `haze-browser-installer.elf` and its build metadata) still carry
their original hashes - so a re-check reports those and nothing else.
Re-check with:

```powershell
$dst="D:\Projects\PYRE"
Get-Content "$dst\_FROZEN-MANIFEST.txt" | ForEach-Object {
  $h,$p = $_ -split '  ',2
  $n=(Get-FileHash (Join-Path $dst $p) -Algorithm SHA256).Hash
  if($n -ne $h){ "CHANGED: $p" }
}
```

Silence means nothing in the logic has moved.

---

## Caveats worth remembering

- **Validated on 13.60.** Run end to end on our own 13.60 console — host, DNS, guide and
  payloads. The 13.42 testing on record is upstream's, not ours; the exploit itself covers
  7.00–13.60.
- **The kernel stage can still hang or panic.** Expect roughly a 40% panic rate. Best
  odds: fresh boot, console offline, nothing else running, and always reboot after a
  failure rather than retrying on a dirty state.
- **The PYRE autoloader has its own install page.** `our-payloads/` contains
  `pyre-autoloader-installer_v0.5.2-pyre.elf` (our own build: the 0.5.2-kp-fix base plus a
  start delay and the PYRE skin), and `install-autoloader.html` (+ `install-autoloader.js`)
  pushes that one file to the console's ELF loader on demand. Wiring it into the RUN PYRE sequence itself
  would be a behaviour change, so the sequence is left alone and installation stays a
  deliberate, separate step (it needs the console already jailed — the loader only exists
  after RUN PYRE has run once). The ELFs now ship **beside** `Pyre.exe` rather than inside
  it; the desktop tool's own README (`tool/README-TOOL.md`) covers that layout.
- **`access.html` / `browser.html` still reference the public Haze host** for the
  console-browser favourite/URL box. That's a functional value, not a skin, so it was
  deliberately left for a decision rather than guessed at.

---

## Credits (kept, as the licences require)

Exploit — ntfargo, ufm42, Sonic_Iso, Jordy, Dr. Yenyen, and TheFlow, SlidyBat, Flatz,
cow, nhk, bollarz, Sleirsgoevy, EchoStretch, EarthOnion.
Autoloader / Haze — haze7714. ShadowMountPlus — Drakmor and community.
etaHEN — LightningMods and contributors. Bundle font — Noto Sans (OFL, see
`HazeUI-LICENSE.txt`). Original `LICENSE` retained. Per-payload notices in
`OPTIONAL-PAYLOAD-NOTICES.txt` and `payload-sources.json`.

Upstream docs are preserved untouched in `UPSTREAM-README.md`.
