# PYRE desktop tool (wave 4)

`Pyre.exe` is the hosted front end for the PYRE PS5 self-host: **one Windows exe with the whole
site packed inside it**, plus the ELF payloads it serves sitting **beside** it. Hand over the
`dist\` folder, run the exe from anywhere, and it unpacks its own copy of the site, checks every
byte against the manifest it was built with, and serves that - and the payloads - on the LAN.
There is no *site* folder to keep beside the exe (wave 1 required one, and that was the bug a
user hit the moment they pulled the exe out of the folder). Wave 4 moved the multi-megabyte ELF
payloads back out of the exe and beside it: the exe stays small and a payload can be swapped
without a repack, while the exe still resolves every payload from **its own folder**, so it stays
portable - drop it anywhere and it looks next to itself.
It replaces the **Python requirement** of `start-server.cmd` + `serve.py`.

The default build is **framework-dependent**: the user installs the **.NET 8 Desktop Runtime
(x64)** once and the exe stays small (~1 MB). It is still a single file - the site's pages and the
app's own code travel inside it, while the ELF payloads travel beside it; only the shared .NET
framework is left to the machine. If you would rather ship the old install-nothing build, use
`publish.ps1 -SelfContained`: the runtime then travels inside the exe (~81 MB) and the user
installs nothing.

## Requires (default, framework-dependent build)

| What | Value |
|---|---|
| Runtime | **.NET Desktop Runtime 8**, x64 (matches `net8.0-windows`) |
| Download | https://dotnet.microsoft.com/download/dotnet/8.0 (…/windows/desktop/x64) |
| Alternative | `publish.ps1 -SelfContained` — no runtime needed on the user's PC |

`dotnet --list-runtimes` returning `Microsoft.WindowsDesktop.App 8.0.x` is what the exe needs.
Without it, Windows shows the standard "To run this application, you must install .NET"
prompt with a download link — it does not fail silently.

---

## Where things live

| What | Where |
|---|---|
| Source | `D:\Projects\PYRE\tool\Pyre\` |
| Site packer (build time) | `D:\Projects\PYRE\tool\build-embed.ps1` |
| Build script | `D:\Projects\PYRE\tool\publish.ps1` |
| Shipped exe | `D:\Projects\PYRE\dist\Pyre.exe` |
| Shipped payloads | `D:\Projects\PYRE\dist\` **beside** the exe — `payloads\`, `our-payloads\`, and the root `optional-*.elf` |
| ELF resolution (runtime) | `tool\Pyre\Services\ElfLocator.cs` — derived from `Environment.ProcessPath`, never a hard-coded path |
| Settings | `%LOCALAPPDATA%\Pyre\settings.json` |
| Unpacked embedded site | `%LOCALAPPDATA%\Pyre\site\<buildId>\` |
| Served page root | **the unpacked embedded copy** — see below |

## Build / publish

Default — framework-dependent single file (needs the .NET 8 Desktop Runtime, x64, on the user's PC),
plus the required payloads beside it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Projects\PYRE\tool\publish.ps1
```

The old fat build — self-contained (runtime packed in, user installs nothing):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Projects\PYRE\tool\publish.ps1 -SelfContained
```

Either of those, plus the 16 root `optional-*.elf` payloads in the shipped folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Projects\PYRE\tool\publish.ps1 -IncludeOptionalPayloads
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Projects\PYRE\tool\publish.ps1 -SelfContained -IncludeOptionalPayloads
```

Or by hand (the site packer runs from an MSBuild target, so a plain `dotnet publish` works too;
add `-p:PyreIncludeOptionalPayloads=true` for the fat-payload build, and swap
`--self-contained false` for `true` to get the fat-runtime build):

```powershell
dotnet publish D:\Projects\PYRE\tool\Pyre\Pyre.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -o D:\Projects\PYRE\dist
```

`dotnet publish` writes the exe; `publish.ps1` then copies the payloads beside it and reports the
whole `dist\` tree. Result: one folder — `dist\Pyre.exe` (~1 MB framework-dependent, ~81 MB
self-contained; compression is switched on automatically only for self-contained builds —
`NETSDK1176` forbids it otherwise) plus the ELFs it expects, laid out exactly as the runtime looks
for them. `publish.ps1` finishes by running `--selftest` against the exe it just built, so a broken
pack or a missing payload cannot be shipped quietly.

**What the exe carries:** everything in `D:\Projects\PYRE` **except** `tool\`, `dist\`, the old
`Pyre.exe`, **every `*.elf`**, and `our-payloads\ps5-webkit-autoloader-0.5.2-kp-fix.zip` (the
autoloader *source* zip — never needed at runtime, excluded from every build). So the pages
(`*.html`), the `haze-*.js` + `install-autoloader.js`, `src\`, `offsets\`, the font and the small
text/json/licence files are embedded; the payload binaries are not.

**What ships beside the exe:** every `*.elf` the site asks for — the whole `payloads\` set
(kstuff, shadowmountplus, etaHEN, etaHEN-loader-fix, elfldr, the browser installer), the two root
ELFs `pldmgr_v0.5.2.elf` and `ps5-app-dumper_v2.00.elf`, the 16 root `optional-*.elf`
(**only** with `-IncludeOptionalPayloads`), and
`our-payloads\webkit-autoloader-installer_v0.5.2-kp-fix.elf` (always — `install-autoloader.html`
pushes it at runtime). The packer writes this list (path/size/sha256/optional) into the embedded
manifest, and `publish.ps1` copies exactly that list, so the folder and the exe always agree. The
packer asserts no `.elf` ships inside the exe, and that the payload list still names the autoloader.

**If you add or remove a string table, delete `tool\Pyre\obj` and `tool\Pyre\bin` first** — an
incremental build will happily keep a resource file you have deleted (`dotnet publish` reused a
stale `Pyre.dll` and re-embedded a deleted `de.json` once; a clean fixed it).

**NuGet note:** this machine's `%APPDATA%\NuGet\nuget.config` has an empty `<packageSources>`,
so restore finds nothing and cannot pull the self-contained runtime packs. `tool\NuGet.config`
adds nuget.org for this folder only — the user's global config is deliberately untouched.
Offline builds need the runtime packs in `%USERPROFILE%\.nuget\packages`.

## Running it

* **Double-click `Pyre.exe`** — normal use.
* `Pyre.exe --serve [--port N] [--root DIR] [--log FILE] [--duration SEC]` — headless host,
  no window. Used to verify the shipped exe without clicking anything. It logs how many
  payloads it found beside the exe and names any that are missing.
* `Pyre.exe --selftest [--port N] [--root DIR]` — verifies the unpacked embedded site against
  the embedded manifest, starts the server on a test port, drives real HTTP against it, and
  compares SHA256 of every response to the file on disk (payloads included). It reports the
  payload situation honestly but does **not** fail when payloads are absent. Exit 0 = pass.
* `Pyre.exe --add-firewall-rule` — the one-off elevated action. Pyre launches itself with this.
* `Pyre.exe --check-firewall` — read-only: is the inbound rule there? (exit 0 = yes)
* `--no-firewall` — never check or add the rule this run (diagnostics/automation).

## The embedded site

The site's pages are packed into the exe at build time and unpacked on the user's machine at
first run.

**Packing (build time).** `tool\build-embed.ps1` walks `D:\Projects\PYRE`, skips `tool\`,
`dist\`, `Pyre.exe`, every `*.elf` and the autoloader source zip, and writes two files into
`tool\Pyre\obj\siteassets\`:

* `site.zip` — a **deterministic** zip (fixed entry order and timestamps); entry names are the
  site-relative paths with `/` separators, so the same site always packs to the same bytes.
* `site-manifest.json` — every packed file's `path`, `size` and `sha256`, plus a `buildId`
  derived from those hashes, the total size, the zip's own SHA256, and a `payloads[]` list: every
  ELF that must sit beside the exe, with its own size/sha256 and an `optional` flag.

Both are embedded as `Pyre.Site.site.zip` and `Pyre.Site.site-manifest.json`, so they are inside
`Pyre.exe`. Publish re-runs the packer every build and skips rewriting when the site is unchanged.

**Unpacking (runtime).** On first use — or whenever the build id changes — the exe unpacks
`site.zip` to `%LOCALAPPDATA%\Pyre\site\<buildId>\`, hashing each file **as it is written** and
comparing it to the manifest. A single mismatch aborts the unpack and **nothing is served**: that
is the point of the check, because it proves the bytes the console receives are the bytes that
were packed. A verified copy is re-hashed on every later launch; anything missing or altered is
unpacked again from the exe. Unpacking goes to a temp folder and is then moved into place, with a
named mutex so two Pyre instances starting together cannot race.

**The GUI is honest about it.** The host card reads *"Serving the EMBEDDED copy · build
&lt;id&gt; · &lt;n&gt; files verified"*, and a second line reports the payloads: how many of the
expected set were found beside the exe, and - in warning colour - the names of any that are
**missing**. The activity log names the build id, the unpack path, the payload folders it searched,
and every missing payload. If the embedded copy ever fails to verify, a red banner says so and
START HOSTING is disabled rather than serving something unproven. A missing payload never stops
hosting: the page still serves, and the affected button 404s - visibly.

## The ELF payloads beside the exe

The payloads the site fetches are resolved **relative to the exe's own folder**, derived at
runtime from `Environment.ProcessPath` (`ElfLocator.cs`). No absolute path is baked in anywhere,
so the exe is fully portable.

Resolution is deliberately forgiving, because files get dropped in without reading a spec. For a
request such as `payloads/kstuff.elf` it tries, in order:

1. the **exact relative path** beside the exe — `<exe folder>\payloads\kstuff.elf`;
2. a single tidy **`elfs\`** folder matched on the **basename** — `<exe folder>\elfs\kstuff.elf`.

Both layouts work, and every URL the site uses (root `optional-*.elf`, `payloads\*.elf`,
`our-payloads\webkit-autoloader-installer_v0.5.2-kp-fix.elf`) resolves the same way. A path that
is rooted, carries a drive colon, or holds a `..` segment is refused outright, so a payload
request can never be coaxed outside those folders.

A payload that is not there returns a clean **404** — never a 500 and never an empty 200 — and the
GUI/`--serve` log says plainly which one is missing. `publish.ps1` ships the mirrored layout (the
exact relative paths beside the exe); the `elfs\` layout is there for when Cam just wants one
folder.

## The served root

There is no *site folder* dependency. With no arguments Pyre serves the **unpacked embedded copy**
— always present, always verified — and resolves ELFs from beside the exe.

`--root <dir>` is a deliberate override for development and testing: that folder is then served
straight off disk (read-only, bytes untouched) and the embedded copy is not touched; ELFs are
looked for in that folder first, then beside the exe. If the folder has no `index.html` the GUI
says so in a red banner and refuses to start; it never silently serves an empty tree. Without
`--root` there is nothing for the user to get wrong.

## The server (replaces serve.py)

* Binds `0.0.0.0:8000`, one task per connection, async throughout — the console pulls many
  assets at once and must not stall. No `SO_REUSEADDR`, so a second host on 8000 fails loudly
  rather than the two of them silently splitting requests.
* `Cache-Control: no-store` on **every** response, exactly like `serve.py`.
* GET and HEAD; single byte ranges (206 + `Content-Range`); 404 for a missing file (never a 500);
  405 for anything with a body; keep-alive on HTTP/1.1. No directory listing.
* Path traversal is refused (403) — only paths under the root are reachable, and a `.elf` request
  is resolved through the locator above (which refuses anything that could escape) before the
  normal root handling.
* Content types: `.html .js .css .json .txt .elf .ttf .wasm .png .jpg .svg .ico .bin` plus the
  rest of what the site ships (`.woff2 .otf .map .gif .webp .xml .zip .pdf .md`), default
  `application/octet-stream`. **`.elf` has no registered media type** — Python's `mimetypes`
  also returns `application/octet-stream` for it, so we match that, consistently.
* Request lines are **not** logged to the GUI by default. "Show every request" turns them on,
  still capped at the last 200 lines. Start/stop/errors always show.

## The firewall rule

Rule name: **`Pyre HTTP 8000`**, inbound, TCP 8000, allow, profile **private**.

1. On Start Hosting, Pyre checks with
   `netsh advfirewall firewall show rule name="Pyre HTTP 8000"` and greps the output for the
   rule **name** — never for netsh's prose, which is localised ("No rules match…" is English
   only on an English Windows).
2. If it is there: **nothing happens, no elevation, no prompt.**
3. If it is not: Pyre runs normally (`asInvoker` manifest, never an always-elevate one) and
   relaunches itself **once** with `ProcessStartInfo { Verb = "runas", Arguments = "--add-firewall-rule" }`.
   The elevated child checks *again* before adding — netsh will happily create a duplicate rule
   with the same name, so "add" is always "check, then add if missing". Added once, never twice.
4. **Declining UAC is a normal outcome, not an error.** Hosting still starts. The GUI says
   plainly that the rule was not added and what it may mean (the console may not reach port
   8000), and offers one deliberate **TRY AGAIN** button. It never asks again on its own —
   the outcome is written to `%LOCALAPPDATA%\Pyre\settings.json` (`firewall.state`) and a known
   outcome is never re-checked or re-asked, at that launch or any later one.

## The LAN address

The number under "This PC's address for the console" is read from the routing table: a UDP
socket is "connected" to a public address (no packet is sent) and the OS reports the local
address it would route from — i.e. the interface of the connection actually in use. If that
fails, Pyre falls back to the interface holding an IPv4 default gateway, and finally to the old
`serve.py` behaviour (scrape `ipconfig`, prefer `192.168.x.x`). **The GUI says which it used**,
so a fallback is visible rather than passed off as exact. There is no adapter picker and no VPN
diagnostics — as specified.

## Languages

`tool\Pyre\Localization\en.json` is the English string table. Every user-visible string in the
window comes from it; nothing is hard-coded in the XAML. Tables are discovered from the embedded
resources at runtime and the dropdown is built from whatever is present.

To add a language: copy `en.json` to `<code>.json` in the same folder (e.g. `de.json`), translate
the values, rebuild. It appears in the dropdown by itself and switching applies live, without a
restart, and persists.

`WithCulture=false` in the `.csproj` is load-bearing: MSBuild's `AssignCulture` sees the "en" in
`en.json`, decides it is a culture resource, ships it as a satellite assembly, and
`SatelliteResourceLanguages=en` then drops that satellite — so the whole table silently vanishes.
Do not remove it.

**Only English ships.** No other language's strings have been invented.

## Discipline about the frozen folder

Pyre is a **read-only servant** of `D:\Projects\PYRE`. At build time the packer opens the site
files for reading and never writes, renames, moves, reformats or rewrites anything in the folder.
Its own files live here in `tool\`; the only thing added to the parent folder is the build output
folder `dist\` (holding the shipped `Pyre.exe` and the payloads beside it). At runtime Pyre writes
nothing anywhere except `%LOCALAPPDATA%\Pyre\` (settings and the unpacked site) — it reads the
payloads beside the exe and nothing else in the site folder.

Re-verify the frozen manifest after any change:

```powershell
$dst="D:\Projects\PYRE"
Get-Content "$dst\_FROZEN-MANIFEST.txt" | ForEach-Object {
  $h,$p = $_ -split '  ',2
  $n=(Get-FileHash (Join-Path $dst $p) -Algorithm SHA256).Hash
  if($n -ne $h){ "CHANGED: $p" }
}
```

Silence means nothing in the logic has moved: **84/84 clean**.

## Not in this wave (deliberate)

No payload catalogue, **no DNS server** (the console's own DNS settings are step 3 of
the guide; whether the tool must answer port 53 is still being decided), no code signing, no
installer, no auto-update, no telemetry. The PS5 route is written steps only — no drawn diagram.
