# Haze PS5 Autoloader

Site: https://haze7714.github.io/Relapse-Exploit/

The public URL opens a red Matrix-style menu. **Run Haze** opens `run.html?v=aio-trace-1` and runs the existing startup sequence. **Additional Payloads** describes the available tools; live launch buttons are enabled on the runner after its startup sequence completes. The existing manual, loader-only, and browser-skip URL options are preserved.

The Matrix animation runs only on the landing page. It is not loaded into the exploit page. The existing kernel implementation, offsets, original startup payload binaries, etaHEN repair, and 15-second spacing are retained. The original page is available at `classic.html?v=aio-trace-1`.

After the sequence completes, **the startup log stays open**. Press **Circle once**, then choose **Additional Payloads** in the Haze session menu. You can also select Additional Payloads directly below the completed log. Select a card or its labeled launch button; the live menu's page title is **Live Payloads | Haze**. This menu stays in the same browser document so its native payload sender remains available. The initial back-navigation entry is installed while startup is running, preventing a single Back press from leaving during that stage. Returning all the way to a separate page discards that sender; that page cannot confirm whether the console is still jailbroken.

The separate **Payload Catalog** is rendered as static HTML so a script download cannot leave the list empty. The live and static menus now bundle Noto Sans locally (OFL license in HazeUI-LICENSE.txt) to address the missing-label reports. This is a rendering mitigation awaiting a PS5 test, not a confirmed diagnosis of the console's blank-text behavior.

## Services 1 startup

On the public Run Haze entry, startup is now **Kstuff → ShadowMountPlus → repaired etaHEN → Payload Manager v0.5.2 → App Dumper v2.00 → original browser installation check**. Each added service is fetched from this host and its size and SHA256 are checked before sending. Fifteen-second pauses remain between sends. Haze probes the service's local HTTP endpoint and checks its identity before reporting readiness; a transfer alone is not treated as success. A failed check stops startup with a named error, without blindly resending the ELF. Already-answering services are not sent again. The classic entry retains its original three-payload sequence and browser check.

Haze configures Payload Manager to stay in the background: AUTO_BROWSER_OPEN=0, AUTOLOAD_ENABLED=0, and KILL_DISC_PLAYER_ON_STARTUP=0. The manager's saved payload list remains intact, but Haze controls this startup sequence to avoid duplicate autoloads. App Dumper's existing USB/current and legacy settings and its console settings are set to enable_webui=1 and auto_start=0, so startup prepares its interface without starting a dump. Other settings, including access codes, are preserved. The original configuration is kept once as an adjacent .haze-backup file before replacement; writes use a verified temporary file followed by rename. A settings failure stops startup rather than launching with unsuitable settings.

The log stays open after completion. **Circle once → Additional Payloads** remains available. Manager and Dumper show an Open link and are protected from duplicate sends. The catalog includes the 18 entries from the green Relapse list that were shown for firmware 13.42. Alternate etaHEN, OnionHEN, and ShadowMountPlus versions are restricted to fresh loader-only sessions because the standard sequence has already started a HEN and mount service. These alternatives never replace the working startup binaries. Status/stop/toggle payloads can be sent again. Individual optional tools have not all been console-tested on 13.42.

All added files are pinned to the listed developer releases. See payload-sources.json for hashes, release URLs and corresponding source archives, and OPTIONAL-PAYLOAD-NOTICES.txt for license notices. The menu and startup are verified with simulated transport/configuration tests; the new automatic service sequence still needs PS5 validation. This does not fix the earlier kernel/AIO stall or guarantee every attempt succeeds.

This update does not add offline caching. The public site and its additional payload files still require access to this host.

One complete, error-free run was reported by the owner on firmware 13.42 using the versioned URL. A later bare-URL attempt after a full restart stopped or froze; its final stage was not visible. This entry update addresses navigation consistency and log visibility, not the unidentified kernel failure. The new public entry route still needs PS5 validation, and a completely clean installation has not been established.

## First visit without a browser package

The free [first-visit guide](https://haze7714.github.io/Relapse-Exploit/access.html) explains how to reach this host from the existing User's Guide browser. Relapse's developer recommends primary DNS `45.56.67.85`. Open the User's Guide, enter `https://haze7714.github.io/Relapse-Exploit/` in the redirect page's URL box, and choose Go. That entry page's redirect was verified in a desktop browser; the initial PS5 DNS route still needs console confirmation. The community DNS is a separate service; this GitHub site serves the exploit and payloads.

Once the Internet Browser package is installed, it remains installed across restarts on the owner's console. Use its home-screen icon for future visits; installing it again is unnecessary. Installing the browser requires a working exploit first. The autoloader now sends an original-browser installer after etaHEN; see the browser installation section below.

## Browser favorites

The [Haze Browser home page](https://haze7714.github.io/Relapse-Exploit/browser.html) starts with only this host in Favorites. Users can add, edit, or remove favorites; saved changes, including an empty favorites list, are preserved when they return. Clearing website data resets favorites. No exploit runs until its favorite is selected.

The original Internet Browser package still opens its creator's home page. The owner selected the original browser package. Publishing this HTML does not change its starting URL; users can add the Haze host themselves.

## Reload protection and browser precheck (Restart guard 1)

The owner confirmed on firmware 13.42 that the Memory 1 run started etaHEN/debug settings, ShadowMountPlus, Payload Manager and App Dumper. App Dumper and ShadowMountPlus worked afterward. The original browser installer reported existing title files and skipped installation. A memory warning and CE-108262-9 still occurred at the end; their cause is not confirmed.

The Run Haze link now grants one start through sessionStorage. The runner consumes that token before WebKit starts and removes it from the current URL. A reload/recovered runner or direct runner URL without that token stays on a paused screen with links to the running tools. When storage is unavailable, an explicit start button is required. The page cannot infer a PS5 restart: choose the restart/start button only after a full console restart. This guards automatic reloads of the updated runner; it cannot control a stale old page or the console crash itself. Classic and separate diagnostic entries retain their existing behavior.

After Manager and Dumper answer, the host checks /user/app/MOUU12023 using a read-only existence check. If files are present, it skips downloading and launching the 8.4 MB browser-installer ELF. If absent, the existing installer handles installation and its own registration/pending checks. The final browser result remains subject to console notifications. Menu styles and the custom font now load only when the user opens the menu, not automatically at the end of startup. This reduces work at the reported crash point without establishing that either step caused the crash. Console validation of this change remains pending.

## Startup memory and error reporting (Memory 1)

After Services 1, console screenshots showed an AIO-stage stop, a system-software error, and a browser memory error. They do not establish one shared cause. The public sender now downloads, maps, sends, and unmaps one startup ELF at a time instead of retaining all three mappings. The browser-installer staging buffer is also released after sending. The established kernel code, payload bytes, and 15-second waits remain unchanged.

Menu cards are created only when Additional Payloads is opened; the menu stylesheet and custom font wait until startup finishes. Kernel stop reasons stay in a separate visible error box even when rescue/cleanup logs follow. The host no longer claims port 9021 is listening solely because the bootstrap returned; actual payload transfers test the connection. These are memory-lifetime and diagnostic corrections, not a confirmed fix for the kernel/AIO hang. Simulated checks cannot establish console stability, and the updated build still needs console validation.

## Payload loading

### AIO diagnostics 1

The current investigation concerns a stall at `Kernel: checking aio groups`, before the payload sequence. The page now reports each AIO request being checked and, if a native call waits more than five seconds, its operation and syscall number. On the existing 20-second worker timeout or a worker error, the host rejects further calls and stack changes on that worker and skips native rescue calls that require it. Completed calls cancel their watchdogs. These changes prevent worker reuse after a timeout; they do not cancel a blocked kernel call or establish a fix for the original stall. Restart the console before another attempt after a failure.

The timeout lifecycle and failure reporting have passed simulated JavaScript tests. Console validation is pending. Kernel offsets, race parameters, payload versions, and 15-second payload pauses are unchanged.

After a fresh PS5 restart, open the site and keep it open. Once the exploit reports its loader ready, payload loading begins automatically after 5 seconds: Kstuff, a 15-second pause, ShadowMountPlus, another 15-second pause, then etaHEN. The public Run Haze page then starts and checks Payload Manager and App Dumper before the browser check. Transfer messages are not proof of successful payload startup.

This personal experimental branch is based on upstream commit 09f10f5. The next upstream commit removed this payload bundle for compatibility issues. This host retains the original Kstuff Lite and ShadowMountPlus payloads and selects a repaired version of its bundled etaHEN.

The etaHEN repair corrects a stale four-byte health-check discriminator in both embedded private-loader copies: eight changed bytes in the decompressed bootstrapper. The original `payloads/etaHEN.elf` remains available; the site uses `payloads/etaHEN-loader-fix.elf`. See [repair notes](payloads/etaHEN-loader-fix-notes.txt) for hashes and source references.

Console validation: one successful hosted session on PS5 firmware 13.42 with 15-second pauses. The 15-second pauses have been restored after an unsuccessful test of faster timing. The reported kernel-stage stall occurs before these pauses; this rollback is not a confirmed fix for that stall. Kstuff Lite 1.11, ShadowMountPlus 1.7beta1, and etaHEN started in order; the Toolbox was online, runtime readiness markers were present, and the previous loader-recovery errors did not recur during a 3 minute 40 second log observation. This does not establish stability across consoles or firmware. The kernel exploit can still stop, hang, or panic before payload loading. Failed attempts now report that the ELF loader did not start instead of claiming privilege success.

The separate [loader repair page](https://haze7714.github.io/Relapse-Exploit/loader-fix.html) uses the same repaired etaHEN as the main page.

Recovery URLs (use on a fresh session):
- `?manual=1`: wait for R2 before sending the same sequence.
- `?loader=1`: start only the exploit and ELF loader; send payloads separately.

Hosting source: branch `ps5-autoload`, root folder. Keep the original project credits and licenses. A full restart clears the active jailbreak; this is not permanent firmware.

## Credits and license

Based on [ntfargo/Relapse-Exploit](https://github.com/ntfargo/Relapse-Exploit). Upstream exploit firmware range: 7.00 through 13.60; individual payload compatibility is separate.

Original credits: ntfargo, ufm42, Sonic_Iso, Jordy, Dr. Yenyen, TheFlow, SlidyBat, Flatz, cow, nhk, bollarz, Sleirsgoevy, EchoStretch, EarthOnion. ShadowMountPlus credits: Drakmor and Community. etaHEN credits: LightningMods and contributors.

The original LICENSE file is retained. Payloads retain their respective authorship and licenses. Use only on devices you own or are authorized to test.

## Original browser installation

After the two added services answer (or after etaHEN on the classic entry), the page waits 15 seconds and sends `haze-browser-installer.elf`. Keep the page open until the installer notification appears. The worker checks for the original browser (MOUU12023) and skips existing title files or a registered installation. Otherwise it waits for repaired etaHEN readiness, downloads the exact original package over verified HTTPS, checks its size and SHA256, and submits it to the native installer. Watch the PS5 notification for the result. A payload transfer does not mean the package installed.

The original [browser package](https://github.com/haze7714/Relapse-Exploit/releases/tag/browser-original-1) opens its original WBrowser home page. It was previously installed and launched by the host owner. This new automatic installation path has compiled successfully but still needs its first console test. It currently accepts only the 13.42 SDK firmware identifier; other/unknown identifiers stop without installation. An unresolved previous submission is not automatically retried.

Use `?browser=0` to run the same payload sequence without the browser installer. Existing `?loader=1` and `?manual=1` modes remain available. Browser installation cannot fix an exploit stall before the ELF loader starts.

Build: [successful installer build](https://github.com/haze7714/Relapse-Exploit/actions/runs/36681744614). Installer SHA256: `c3af67c1311724654e1bab275cfbcac829a5bdeec187e3fe1030e1ebd8128e97`. Matching source and license notices: [browser-installer-source.zip](browser-installer-source.zip). Original package SHA256: `90d29db230458acfd2e3332dc338afe6e63f3d0c0948517918286497c9ac17ca`.
