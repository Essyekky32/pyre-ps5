import express from 'express';
import { mutationPolicy } from './mock-policy.js';
import fs from 'fs';
import path from 'path';

const app = express();
app.use(mutationPolicy);
app.use(express.json());
app.use(express.text({ type: 'text/plain', limit: '1mb' }));
app.use((req, res, next) => {
  if (typeof req.body === 'string') {
    try { req.body = JSON.parse(req.body); }
    catch { return res.status(400).json({ error: 'Invalid JSON' }); }
  }
  next();
});

// Public development fixture files are fetched as ordinary cross-origin GETs
// by the README view; this exception never authorizes API preflights/mutations.
const publicFixture = (req, res, next) => {
  res.set('Access-Control-Allow-Origin', '*'); next();
};
// Serve the locally generated details/ README folder (repo root)
app.use('/details', publicFixture, express.static(path.resolve(process.cwd(), '..', 'details')));
app.use('/test_icons.json', publicFixture, express.static(path.resolve(process.cwd(), '..', 'test_icons.json')));
app.use('/test_repo.json', publicFixture, express.static(path.resolve(process.cwd(), '..', 'test_repo.json')));

// Log all requests to terminal
app.use((req, res, next) => {
  console.log(`[Mock] ${req.method} ${req.url}`);
  next();
});

const PORT = 8081;

let logs = [
  "[PLDMGR] Mock Server initialized",
  "[PLDMGR] System ready",
  "[PLDMGR] Found 3 payloads on storage"
];

// Load the new-schema test repository (icons, author, release_notes, details_url)
let remoteRepository = [];
let remoteIcons = {};
try {
  const testRepo = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), '..', 'test_repo.json'), 'utf8'));
  remoteRepository = testRepo.payloads || [];
  remoteIcons = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), "..", "test_icons.json"), "utf8")).icons;
} catch {
  console.log('[Mock] Could not load ../test_repo.json — using built-in payloads');
  remoteRepository = [
    {
      name: "ftpsrv",
      filename: "ftpsrv_v0.19.elf",
      url: "https://itsplk.github.io/ps5_payloads/payloads/ftpsrv_v0.19.elf",
      description: "A simple FTP server that accepts connections on port 2121",
      version: "v0.19",
      checksum: "e6c1babbfd5e1b766d12b659853b514b9faedf6333cbe8cb514b1a3e79b7ce39"
    }
  ];
}

const communityRepository = [
  {
    name: "PS5 Hen Test",
    filename: "ps5_hen_test_v1.0.elf",
    url: "https://example.com/payloads/ps5_hen_test_v1.0.elf",
    description: "Community hen test payload — no checksum",
    version: "v1.0",
    author: "hen-community",
    release_notes: "## v1.0\n\n- No icon set — exercises the deterministic fake-icon fallback",
    details_url: "details/etahen.md"
  },
  {
    name: "Debug Tool",
    filename: "debug_tool_v2.1.elf",
    url: "https://example.com/payloads/debug_tool_v2.1.elf",
    description: "Debug utility from community repo",
    version: "v2.1",
    author: "ps5-payload-dev",
    release_notes: "## v2.1\n\n- Second community payload",
    details_url: "details/elfldr.md"
  }
];

let lastRepositoryUpdate = Math.floor(Date.now() / 1000);

let mockSources = [
  { id: 'default', name: 'Official Repository', url: 'https://itsplk.github.io/ps5-payloads-mirror/payloads.json', removable: false },
  { id: 'source_community', name: 'Community Payloads', url: 'https://example.com/community-payloads.json', removable: true }
];

let autoloadStatus = {
  remaining: 10,
  total: 2,
  done: 0,
  current: "IDLE",
  list: "goldhen_v2.4b17.elf,etaHEN_1.8.elf"
};

const autoloadList = autoloadStatus.list.split(',');
let simulationTicks = 0;

setInterval(() => {
  if (autoloadStatus.remaining > 0) {
    autoloadStatus.remaining--;
    if (autoloadStatus.remaining === 0) {
      autoloadStatus.current = autoloadList[0];
      logs.push(`[PLDMGR] Autoload sequence started: ${autoloadList[0]}`);
    }
  } else if (autoloadStatus.remaining === 0 && autoloadStatus.current !== "DONE" && autoloadStatus.current !== "IDLE") {
    simulationTicks++;
    if (simulationTicks >= 3) { // 3 seconds per payload simulation
      simulationTicks = 0;
      autoloadStatus.done++;
      if (autoloadStatus.done < autoloadStatus.total) {
        autoloadStatus.current = autoloadList[autoloadStatus.done];
        logs.push(`[PLDMGR] Autoloading: ${autoloadStatus.current}`);
      } else {
        autoloadStatus.current = "DONE";
        logs.push(`[PLDMGR] Autoload sequence complete`);
      }
    }
  }
}, 1000);

// --- API Routes ---

app.get('/getip', (req, res) => {
  res.send('127.0.0.1');
});

app.get('/version', (req, res) => {
  res.send('1.0.2-mock');
});

app.get('/list_payloads', (req, res) => {
  res.json({
    payloads: [
      "/data/pldmgr/goldhen_v2.4b17.elf",
      "/data/pldmgr/etaHEN_1.8.elf",
      "/data/pldmgr/kstuff.elf",
      "/mnt/usb0/pldmgr/linux_loader.elf"
    ],
    meta: {
      // payloads with no entry here = local/direct upload (no badge shown)
      "kstuff.elf": {
        source_name: "Community Payloads",
        install_source: "repository",
        install_source_detail: "https://example.com/community-payloads.json"
      }
    }
  });
});

app.get('/autoload_status', (req, res) => {
  res.json(autoloadStatus);
});

app.get('/get_config', (req, res) => {
  res.json({
    AUTOLOAD_ENABLED: true,
    AUTOLOAD_LIST: "goldhen_v2.4b17.elf,etaHEN_1.8.elf",
    LAST_REPOSITORY_UPDATE: lastRepositoryUpdate,
    AUTO_INSTALL_APP: true,
    MULTI_SOURCES_ENABLED: true
  });
});

app.get('/repository_icons', (req, res) => {
  const items = req.query.source === 'default' ? remoteRepository : req.query.source === 'source_community' ? communityRepository : null;
  if (!items) return res.status(404).json({ icons: {} });
  res.json({ icons: req.query.source === 'default' ? remoteIcons : {} });
});
app.post('/repository_cache_clear', (req, res) => res.json({ ok: true }));

app.get('/repository_payloads', (req, res) => {
  // Return source-grouped format (multi-source mode always on in mock)
  res.json({
    sources: [
      {
        id: 'default',
        name: 'Official Repository',
        url: 'http://localhost:8081/test_repo.json',
        last_update: lastRepositoryUpdate,
        error: false,
        icons_revision: 'mock-icons-v1',
        payloads: remoteRepository.map(p => ({ ...p, source_id: 'default', source_name: 'Official Repository' }))
      },
      {
        id: 'source_community',
        name: 'Community Payloads',
        url: 'https://example.com/community-payloads.json',
        last_update: lastRepositoryUpdate,
        error: false,
        icons_revision: 'mock-icons-v1',
        payloads: communityRepository.map(p => ({ ...p, source_id: 'source_community', source_name: 'Community Payloads' }))
      }
    ]
  });
});

app.post('/repository_refresh', (req, res) => {
  lastRepositoryUpdate = Math.floor(Date.now() / 1000);
  logs.push(`[PLDMGR] Repository manually refreshed`);
  res.json({
    sources: [
      {
        id: 'default',
        name: 'Official Repository',
        url: 'http://localhost:8081/test_repo.json',
        last_update: lastRepositoryUpdate,
        error: false,
        icons_revision: 'mock-icons-v1',
        payloads: remoteRepository.map(p => ({ ...p, source_id: 'default', source_name: 'Official Repository' }))
      },
      {
        id: 'source_community',
        name: 'Community Payloads',
        url: 'https://example.com/community-payloads.json',
        last_update: lastRepositoryUpdate,
        error: false,
        icons_revision: 'mock-icons-v1',
        payloads: communityRepository.map(p => ({ ...p, source_id: 'source_community', source_name: 'Community Payloads' }))
      }
    ]
  });
});

app.get('/sources_list', (req, res) => {
  res.json({ sources: mockSources });
});

app.post('/sources_set', (req, res) => {
  const { sources } = req.body;
  if (Array.isArray(sources)) {
    mockSources = sources;
    logs.push(`[PLDMGR] Sources updated: ${sources.length} sources`);
  }
  res.send('OK');
});

app.post('/sources_add', (req, res) => {
  const { url } = req.query;
  if (!url) return res.status(400).json({ ok: false, message: 'Missing url' });
  // Mock: always succeed with a fake source name derived from the URL
  const fakeName = `Community Source (${new URL(url).hostname})`;
  const newSource = { id: `source_${Date.now()}`, name: fakeName, url, removable: true };
  mockSources.push(newSource);
  logs.push(`[PLDMGR] Source added: ${fakeName}`);
  res.json({ ok: true, name: fakeName });
});

app.post('/sources_remove', (req, res) => {
  const idx = parseInt(req.query.index);
  if (isNaN(idx) || idx <= 0 || idx >= mockSources.length) {
    return res.status(400).json({ ok: false, message: 'Invalid index or cannot remove default source' });
  }
  const removed = mockSources.splice(idx, 1);
  logs.push(`[PLDMGR] Source removed: ${removed[0]?.name}`);
  res.json({ ok: true, message: 'OK' });
});

app.post('/repository_install', (req, res) => {
  const { filename } = req.query;
  if (!filename) {
    return res.status(400).json({ ok: false, message: 'Missing filename' });
  }
  logs.push(`[PLDMGR] Repository install requested: ${filename}`);
  res.json({ ok: true, message: `Installed ${filename}` });
});

app.post('/set_config', (req, res) => {
  console.log('Received Config:', req.body);
  logs.push(`[PLDMGR] Config updated: ${JSON.stringify(req.body)}`);
  res.send('OK');
});

app.post(/^\/loadpayload:(.*)/, (req, res) => {
  const path = req.params[0];
  logs.push(`[PLDMGR] Executing payload: ${path}`);
  res.send('OK');
});

app.post('/manage\\:delete', (req, res) => {
  const filename = req.query.filename;
  logs.push(`[PLDMGR] Deleted payload: ${filename}`);
  res.send('OK');
});

app.post('/manage\\:upload', (req, res) => {
  const filename = req.query.filename;
  logs.push(`[PLDMGR] Uploaded payload: ${filename}`);
  res.send('OK');
});

app.post('/abort', (req, res) => {
  logs.push(`[PLDMGR] Autoload sequence aborted by user`);
  autoloadStatus.remaining = -1;
  autoloadStatus.current = "IDLE";
  res.send('OK');
});

app.post('/autoload_clear', (req, res) => {
  logs.push(`[PLDMGR] Autoload status cleared`);
  autoloadStatus.done = 0;
  autoloadStatus.current = "IDLE";
  autoloadStatus.remaining = -1;
  res.send('OK');
});

app.post('/shutdown', (req, res) => {
  logs.push(`[PLDMGR] System shutdown requested`);
  res.send('Shutting down...');
  process.exit(0);
});

// SSE for logs
app.get('/processes_list', (req, res) => res.json([]));
app.get('/history_list', (req, res) => res.json([]));
app.post('/process_kill', (req, res) => res.json({ ok: true }));
app.get('/usb_move_check', (req, res) => res.json({
  status: 'ready', path: req.query.path, filename: path.basename(req.query.path || 'test.elf'),
  folder_exists: false, existing: null,
}));
app.post('/usb_move_perform', (req, res) => res.json({ ok: true, message: 'Mock import complete' }));

app.get('/events', (req, res) => {
  res.setHeader('Content-Type', 'text/event-stream');
  res.setHeader('Cache-Control', 'no-cache');
  res.setHeader('Connection', 'keep-alive');

  const sendLog = (data) => {
    res.write(`data: ${data}\n\n`);
  };

  // Send history
  logs.forEach(sendLog);

  // Poll for new logs
  let lastCount = logs.length;
  const interval = setInterval(() => {
    if (logs.length > lastCount) {
      for (let i = lastCount; i < logs.length; i++) {
        sendLog(logs[i]);
      }
      lastCount = logs.length;
    }
  }, 500);

  req.on('close', () => {
    clearInterval(interval);
  });
});

app.listen(PORT, '127.0.0.1', () => {
  console.log(`\x1b[36m%s\x1b[0m`, `--- Payload Manager Mock Backend ---`);
  console.log(`Running at http://localhost:${PORT}`);
  console.log(`Proxy your Vite requests to this port to test the frontend locally.`);
});
