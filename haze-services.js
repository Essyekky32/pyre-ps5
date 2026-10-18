import { int64 } from './src/utils/int64.js';
import { payloads } from './haze-catalog.js?v=services-1';
import { sendPayload } from './haze-sender.js?v=red-menu-1';

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const usable = p => !p.worker_is_usable || p.worker_is_usable();
const CONFIG_LIMIT = 65536;

// Keep unrelated settings, comments, saved lists, and access codes intact.
export function updateSettings(text, values) {
  const pending = new Set(Object.keys(values));
  const lines = text.split(/\r?\n/).filter(line => {
    const match = /^\s*([A-Za-z_]+)\s*=/.exec(line);
    return !match || !pending.has(match[1]);
  });
  while (lines.length && lines[lines.length - 1] === '') lines.pop();
  for (const key of pending) lines.push(key + '=' + values[key]);
  return lines.join('\n') + '\n';
}

export function consoleFiles(p, chain) {
  const buffer = p.malloc(CONFIG_LIMIT + 1, 1);
  const call = async (n, ...args) => (await chain.syscall(n, ...args)).low | 0;
  const close = async fd => { if (usable(p)) await call(SYS_CLOSE, fd); };
  const exists = async path => (await call(SYS_ACCESS, p.stringify(path), 0)) === 0;
  async function read(path) {
    const fd = await call(SYS_OPEN, p.stringify(path), 0, 0);
    if (fd < 0) {
      if (await exists(path)) throw new Error('Cannot read settings: ' + path);
      return null;
    }
    let count = 0;
    try {
      for (;;) {
        const n = await call(SYS_READ, fd, buffer.add32(count), CONFIG_LIMIT + 1 - count);
        if (n < 0) throw new Error('Settings read failed: ' + path);
        if (!n) break;
        count += n;
        if (count > CONFIG_LIMIT) throw new Error('Settings file exceeds the safe size limit: ' + path);
      }
      const bytes = new Uint8Array(count);
      for (let i = 0; i < count; i++) bytes[i] = p.read1(buffer.add32(i));
      return new TextDecoder('utf-8', {fatal:true}).decode(bytes);
    } finally { await close(fd); }
  }
  async function write(path, text, exclusive) {
    const bytes = new TextEncoder().encode(text);
    if (bytes.length > CONFIG_LIMIT) throw new Error('Settings are too large');
    for (let i = 0; i < bytes.length; i++) p.write1(buffer.add32(i), bytes[i]);
    const fd = await call(SYS_OPEN, p.stringify(path), 1 | 0x200 | (exclusive ? 0x800 : 0x400), 0o644);
    if (fd < 0) throw new Error('Cannot save settings: ' + path);
    try {
      for (let offset = 0; offset < bytes.length;) {
        const n = await call(SYS_WRITE, fd, buffer.add32(offset), bytes.length - offset);
        if (n <= 0 || n > bytes.length - offset) throw new Error('Settings write failed: ' + path);
        offset += n;
      }
      if ((await call(SYS_FSYNC, fd)) < 0) throw new Error('Settings sync failed: ' + path);
    } finally { await close(fd); }
  }
  async function patch(path, values, create) {
    const before = await read(path);
    if (before === null && !create) return null;
    const after = updateSettings(before || '', values);
    if (after === before) return after;
    if (before !== null && !await exists(path + '.haze-backup')) await write(path + '.haze-backup', before, true);
    await write(path + '.haze-tmp', after, false);
    if ((await read(path + '.haze-tmp')) !== after) throw new Error('Settings verification failed: ' + path);
    if ((await call(SYS_RENAME, p.stringify(path + '.haze-tmp'), p.stringify(path))) < 0)
      throw new Error('Settings replacement failed: ' + path);
    return after;
  }
  async function mkdir(path) {
    if ((await call(SYS_MKDIR, p.stringify(path), 0o755)) < 0 && !await exists(path))
      throw new Error('Cannot create settings folder: ' + path);
  }
  return {read, patch, mkdir};
}

// Read-only local HTTP checks through the existing native socket bridge.
// Nonblocking sockets and bounded polls avoid waiting indefinitely on a daemon.
export function serviceProbe(p, chain) {
  const addr = p.malloc(16, 1), pollfd = p.malloc(8, 1);
  const error = p.malloc(4, 1), errorLength = p.malloc(4, 1);
  const response = p.malloc(4096, 1);
  const call = async (n, ...args) => (await chain.syscall(n, ...args)).low | 0;
  async function poll(fd, events, milliseconds) {
    p.write4(pollfd, fd); p.write4(pollfd.add32(4), events);
    return (await call(SYS_POLL, pollfd, 1, milliseconds)) > 0;
  }
  return async function probe(port, path, marker) {
    if (p.assert_worker_usable) p.assert_worker_usable();
    let fd = -1;
    try {
      fd = await call(SYS_SOCKET, 2, 1, 0);
      if (fd < 0) return false;
      if ((await call(SYS_FCNTL, fd, 4, 4)) < 0) throw new Error('Could not make the readiness socket nonblocking');
      p.write8(addr, new int64(0,0)); p.write8(addr.add32(8), new int64(0,0));
      p.write4(addr, (0x210 | ((port >>> 8) << 16) | ((port & 255) << 24)) >>> 0);
      p.write4(addr.add32(4), 0x0100007f);
      const connected = await call(SYS_CONNECT, fd, addr, 16);
      if (connected !== 0) {
        if (!await poll(fd, 4, 100)) return false;
        p.write4(error, 0); p.write4(errorLength, 4);
        if ((await call(SYS_GETSOCKOPT, fd, 0xffff, 0x1007, error, errorLength)) < 0 || p.read4(error) !== 0) return false;
      }
      const request = 'GET ' + path + ' HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n';
      const data = p.stringify(request);
      for (let offset = 0; offset < request.length;) {
        if (!await poll(fd, 4, 250)) return false;
        const n = await call(SYS_WRITE, fd, data.add32(offset), request.length - offset);
        if (n <= 0 || n > request.length - offset) return false;
        offset += n;
      }
      let text = '';
      for (let attempt = 0; attempt < 12 && text.length < 16384; attempt++) {
        if (!await poll(fd, 1, 100)) continue;
        const n = await call(SYS_READ, fd, response, 4096);
        if (n === 0) break;
        if (n < 0) continue;
        for (let i = 0; i < n; i++) text += String.fromCharCode(p.read1(response.add32(i)));
        if (/^HTTP\/1\.[01] 200\b/.test(text) && marker.test(text)) return true;
      }
      return false;
    } finally { if (fd >= 0 && usable(p)) await call(SYS_CLOSE, fd); }
  };
}

export async function prepareManager(files) {
  await files.mkdir('/data/pldmgr');
  await files.patch('/data/pldmgr/pldmgr_config.txt', {
    AUTO_BROWSER_OPEN:0, AUTOLOAD_ENABLED:0, KILL_DISC_PLAYER_ON_STARTUP:0
  }, true);
}

export async function prepareDumper(files) {
  const ports = new Set([8081]);
  // These are the v2.00 webhb settings locations, including its legacy imports.
  // Read only existing USB files; never create folders or settings on a USB drive.
  for (let i = 0; i < 8; i++) {
    for (const suffix of ['/homebrew/ps5-app-dumper/config.ini','/ps5-app-dumper/config.ini','/homebrew/config.ini']) {
      const text = await files.patch('/mnt/usb' + i + suffix, {enable_webui:1, auto_start:0}, false);
      const match = text && /^\s*web_port\s*=\s*(\d+)/m.exec(text);
      if (match && +match[1] > 0 && +match[1] <= 65535) ports.add(+match[1]);
    }
  }
  await files.mkdir('/data/homebrew'); await files.mkdir('/data/homebrew/ps5-app-dumper');
  const text = await files.patch('/data/homebrew/ps5-app-dumper/config.ini', {enable_webui:1, auto_start:0}, true);
  const match = /^\s*web_port\s*=\s*(\d+)/m.exec(text);
  if (match && +match[1] > 0 && +match[1] <= 65535) ports.add(+match[1]);
  const expanded = new Set();
  for (const port of ports) for (let i = 0; i < 10 && port + i <= 65535; i++) expanded.add(port + i);
  return Array.from(expanded);
}

export async function startServices(p, chain, log, onReady, dependencies = {}) {
  const files = dependencies.files || consoleFiles(p, chain);
  const probe = dependencies.probe || serviceProbe(p, chain);
  const send = dependencies.send || (item => sendPayload(item, p, chain));
  const wait = dependencies.wait || pause;
  const manager = payloads.find(item => item.id === 'manager');
  const dumper = payloads.find(item => item.id === 'dumper');
  const findDumper = async ports => {
    for (const port of ports) if (await probe(port, '/api/whb/self', /"file"\s*:\s*"ps5-app-dumper_v/)) return port;
    return 0;
  };
  log('Starting Payload Manager in the background...');
  await prepareManager(files);
  let managerReady = await probe(8084, '/get_config', /"AUTO_BROWSER_OPEN"\s*:/);
  if (!managerReady) {
    await send(manager);
    log('Payload Manager sent; waiting for its service to answer...');
    for (let i = 0; i < 20 && !managerReady; i++) {
      await wait(1000);
      managerReady = await probe(8084, '/get_config', /"AUTO_BROWSER_OPEN"\s*:/);
    }
  }
  if (!managerReady) throw new Error('Payload Manager did not answer on port 8084. Startup is incomplete; it has not been resent.');
  log('Payload Manager is responding on port 8084.');
  onReady('manager', 8084);
  log('Waiting 15 seconds before App Dumper.'); await wait(15000);
  const ports = await prepareDumper(files);
  let dumperPort = await findDumper(ports);
  if (!dumperPort) {
    log('Starting App Dumper with automatic dumping turned off...');
    await send(dumper);
    for (let i = 0; i < 12 && !dumperPort; i++) {
      await wait(1000);
      dumperPort = await findDumper(ports);
    }
  }
  if (!dumperPort) throw new Error('App Dumper did not answer on its expected ports. Startup is incomplete; it has not been resent.');
  log('App Dumper is responding on port ' + dumperPort + '.');
  onReady('dumper', dumperPort);
}
