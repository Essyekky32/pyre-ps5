# Credits

PYRE is a **packaging and usability project**. None of the hard parts are ours —
the exploit, the payloads, the loaders, the tooling all came from other people who
did the actual engineering, in the open, for free.

This file exists for two reasons: to say thank you properly, and because the GPL
components we build on require us to state what we changed.

---

## The exploit chain

| Project | Author | Licence | What it gave us |
|---|---|---|---|
| [Relapse-Exploit](https://github.com/ntfargo/Relapse-Exploit) | **ntfargo** and contributors | MIT | The WebKit + kernel exploit chain PYRE serves |
| [Relapse](https://github.com/soniciso1/relapse) | **soniciso1** | MIT | Relapse brought down to firmware 7.00–8.60 |
| [umtx2](https://github.com/idlesauce/umtx2) | **idlesauce** | — | The chain for firmware 1.00–5.50 |
| [slopkit / poops](https://github.com/jordyidk/slopkit) | **jordyidk** | — | The fully-offline chain for firmware 7.00–12.00 |
| [kexp](https://github.com/ufm42/kexp) | **ufm42** | — | The kernel exploit shellcode |
| [Haze](https://github.com/haze-ps5) UI | the Haze project | see `HazeUI-LICENSE.txt` | The menu skin, CSS and font the site is built on |

**The frozen chain is untouched.** `src/`, `offsets/`, and the eight `haze-*.js`
files are byte-identical to upstream — `_FROZEN-MANIFEST.txt` holds the SHA256 of
all 84 of them precisely so you can verify that. What PYRE changed is around the
chain, not in it: the branding, the surrounding pages, and the fact that a Windows
program now hosts it.

## The payloads and tooling

| Project | Author | Licence | In PYRE |
|---|---|---|---|
| [ps5-elfldr](https://github.com/itsPLK/ps5-elfldr) | **itsPLK** | — | The ELF loader the chain boots |
| [ps5-unified-autoloader](https://github.com/itsPLK/ps5-unified-autoloader) | **itsPLK** | — | The payload that loads your payloads |
| [ps5-payload-sdk](https://github.com/ps5-payload-dev/sdk) + [elfldr](https://github.com/ps5-payload-dev/elfldr) | **john-tornblom** | — | The SDK every PS5 payload is built with, and elfldr |
| [puff](https://github.com/madler/zlib) | **Mark Adler** | zlib | Raw DEFLATE for the embedded site |
| [ftpsrv](https://github.com/drakmor/ftpsrv) | **drakmor** | — | FTP on the console |
| [nanoDNS](https://github.com/drakmor/nanoDNS) | **drakmor** | GPL-3.0 | Local DNS proxy (read this for how a PS5 DNS proxy should behave) |

**The self-hosting technique** — answering the console's DNS yourself and serving
the User's Guide over HTTPS on 443 with a self-signed certificate — is the approach
used by **idlesauce's** PS5-Exploit-Host-Self-Host-Tool and by **john-tornblom's**
PS5-Exploit-Host. PYRE's `DnsServer` and `TlsProxy` are our own implementation of
that idea, and that idea is theirs.

## The parts PYRE rebuilds

### PYRE Autoloader — from [itsPLK/ps5-webkit-autoloader](https://github.com/itsPLK/ps5-webkit-autoloader) (GPL-3.0)

Source is in `autoloader/`. Upstream copyright and `LICENSE` are intact.

**What we changed** (as GPL-3.0 requires us to state):

- Renamed and re-skinned to PYRE, credited to Essy. Upstream's attribution line is
  kept in the page footer, because that is the condition of using it.
- Added a **start delay** before the exploit arms (default 5 seconds, adjustable on
  the page, `0` disables). Upstream arms the exploit the instant the page loads,
  which hands the kernel race the noisiest possible window — boot, a freshly
  started browser, payloads still loading.
- New XMB icon and page theme.
- Renamed the version string so our build can't be confused with upstream's.

**What we did not touch:** the exploit, the offsets, the ROP chain, the kernel
stage, the payloads, and every patch to the Relapse/slopkit/umtx2 chains. The
build verifies those patch files are unmodified.

### PYRE Payloads — from [itsPLK/ps5-payload-manager](https://github.com/itsPLK/ps5-payload-manager) (GPL-3.0)

Source is in `payload-manager/`. Upstream copyright and `LICENSE` are intact.

**What we changed:**

- Renamed and re-themed to PYRE.
- **Removed the install-from-repository list.** Upstream fetches its payload
  catalogue from a third-party GitHub Pages mirror, which means the list — and the
  payloads on it — are somebody else's to change. Ours is upload-only: you put your
  own payloads on your console and nothing phones out for a catalogue.
- New XMB icon, page header, and translated app name in the shipped language.

Nothing else was altered — autoload, payload launching, USB import, process
management and settings all behave as upstream.

## PYRE itself

Written by **Essyekky32**: the Windows host (GUI, HTTP server, DNS responder,
HTTPS listener, firewall handling, payload inventory), the site skin, the
packaging, and the launcher.

MIT licensed. Take it, change it, ship it — just keep the notices above intact,
because the people in this file earned them.

---

*If you're one of the developers named here and you'd like something worded
differently, or removed, say so in an issue and it'll be done. You built the
rocket.*
