# PYRE — PS5 jailbreak, made simple

**PYRE turns a multi-step PS5 jailbreak into a single Windows program you double-click.**

The pieces to jailbreak a PS5 already existed, and they're excellent. The
self-host route was already local: you ran a Python script, it told you which
DNS address to use, you pointed your console at your own PC, and the guide and
your payloads came from you.

**PYRE is that same thing, reskinned and turned into one Windows program** — no
Python, no root, no command-line setup, and the DNS now blocks Sony and ads.

It runs the same job from one window on your own PC:

- **Hosts the exploit** to your console over your own network.
- **Answers your console's DNS** itself — the console's guide and update traffic
  goes through your PC, not through a service you don't control.
- **Serves the guide over HTTPS** so the console's User's Guide lands on your PC.
- **Blocks Sony** — update servers and telemetry are refused, so your console
  stays where you put it.
- **Blocks ads and trackers for free**, because everything that isn't Sony is
  forwarded to filtering resolvers. No blocklist to maintain.
- **Hosts your payloads** — the ones you choose, served from beside the program.

Nobody needs Python, and nothing here is hosted by anyone but you.

---

## Quick start

> ### Two things will happen on first run. Neither is a problem.
>
> **1. "Windows protected your PC" (SmartScreen).** PYRE is not code-signed — a
> signing certificate costs real money and this is free homebrew. Click **More
> info → Run anyway.** Antivirus may grumble too; it's a jailbreak tool, that's
> normal for one.
>
> **2. A prompt for the ".NET Desktop Runtime."** Needed once; it offers a
> download link. Install, then run Pyre again. **That is the only thing to
> install** — no Python, no Node, no scripts, nothing from a command line.

1. Download the **Release zip** and extract it somewhere permanent.
2. Run **`Pyre.exe`**. If Windows asks for the **.NET 8 Desktop Runtime**, install
   it and run Pyre again — that's the only dependency.
3. Press **START HOSTING** and say yes to the firewall prompt (one time).
4. Point the PS5's DNS at your PC:
   `Settings → Network → Settings → Set Up Internet Connection → your network →
   Options (…) → Advanced Settings → DNS Settings → Manual`
   Primary = the address Pyre shows, **Secondary = `0.0.0.0`**.
5. On the PS5 open `Settings → Guide & Tips, Health and Safety, and Other
   Information → Guide and Tips → User's Guide`. Accept the certificate warning —
   it's PYRE's own certificate, and it's meant to be there.
6. Press **RUN PYRE** and keep the page open.

There's a **START-HERE.txt** in the release with the same steps, and the program
itself explains each tab as you go.

> **Note:** while your console's DNS points at your PC, Pyre has to be running or
> the console can't resolve anything at all. Finished with Pyre? Put the console's
> DNS back to **Automatic**.

---

## What's in this repo

| Folder | What it is |
|---|---|
| *(root)* | **PYRE** itself — the Windows host, the site it serves, and the exploit chain it sends |
| `tool/` | the WPF/C# source for `Pyre.exe` (the GUI, the HTTP server, the DNS responder, the HTTPS listener) |
| `src/`, `offsets/`, `haze-*.js` | the served site and the exploit chain — **frozen**, see `_FROZEN-MANIFEST.txt` |
| `autoloader/` | **PYRE Autoloader** — our build of the WebKit Autoloader, skinned, with a start delay |
| `payload-manager/` | **PYRE Payloads** — our build of Payload Manager, re-themed, install-from-list removed |

Payload binaries (`*.elf`) and Windows build output are **not** in this repo — they
ship in the [Releases](../../releases) as a ready-to-run folder. The repo is the
source.

### Building it

Building the Windows host needs the .NET 8 SDK:

```powershell
cd tool
powershell -File publish.ps1 -IncludeOptionalPayloads
```

That builds the site into the exe, then copies the payloads beside it into
`dist\`. Building the two payload projects (`autoloader/`, `payload-manager/`)
additionally needs the [PS5 payload SDK](https://github.com/ps5-payload-dev/sdk);
see each folder's README.

---

## Firmware

PYRE targets PS5 firmware **13.60** (`26.05-13.60.00`). The exploit chain has
per-firmware offsets, so the version matters.

**This repo does not host firmware.** System software is Sony's, redistributing it
isn't ours to do, and a takedown notice on it would take the source with it. Get
13.60 from wherever you normally get firmware, or ask in the releases.

---

## Credits — the actual engineers

PYRE is a packaging and usability project. Every hard part of this was solved by
somebody else first, and this wouldn't exist without them. See **[CREDITS.md](CREDITS.md)**
for the full list and for what we changed in each borrowed piece.

The short version:

- **[Relapse](https://github.com/ntfargo/Relapse-Exploit)** by ntfargo — the
  WebKit + kernel exploit chain. MIT.
- **[itsPLK](https://github.com/itsPLK)** — the WebKit Autoloader and Payload
  Manager, plus the ELF loader and unified autoloader. GPL-3.0.
- **[idlesauce](https://github.com/idlesauce)** (umtx2), **[jordyidk](https://github.com/jordyidk)**
  (slopkit), **[soniciso1](https://github.com/soniciso1)** (Relapse down to 7.00),
  **[ufm42](https://github.com/ufm42)** (kexp), **[john-tornblom](https://github.com/john-tornblom)**
  (ps5-payload-sdk, elfldr), **[drakmor](https://github.com/drakmor)** (ftpsrv, nanoDNS),
  and the whole PS5 homebrew scene.

They built the rocket. We built the ignition key.

---

## Licence

Multiple licences apply, kept per component:

- **PYRE** (this packaging, the host program, the site skin, our payload builds) — MIT
- **Relapse** and the exploit chain — MIT (see `LICENSE`)
- **PYRE Autoloader** and **PYRE Payloads** — **GPL-3.0**, inherited from the
  upstream projects they're built from. Their `LICENSE` files are intact, and
  `CREDITS.md` states what we changed.

If you redistribute this, keep those notices. For the GPL components that isn't
optional, and for the rest it's just right.

*This is a homebrew project for research and personal use. Use at your own risk.*
