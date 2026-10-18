import './polyfills.js'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.jsx'
import { RepositoryProvider } from './contexts/RepositoryContext'
import ErrorBoundary from './components/ui/ErrorBoundary'
import './i18n'

// Setup global uncaught error display before anything else runs
if (typeof window !== 'undefined') {
  const escapeHtml = (str) => {
    return String(str || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  };

  const showGlobalError = (title, err) => {
    try {
      let container = document.getElementById('ps5-global-error-overlay');
      if (!container) {
        container = document.createElement('div');
        container.id = 'ps5-global-error-overlay';
        container.style.cssText =
          'position:fixed;top:0;left:0;right:0;bottom:0;background:#08080a;color:#f87171;padding:24px;font-family:monospace;font-size:13px;z-index:9999999;overflow:auto;white-space:pre-wrap;line-height:1.5;box-sizing:border-box;';
        const target = document.body || document.documentElement;
        if (target) {
          target.appendChild(container);
        } else {
          window.addEventListener('DOMContentLoaded', () => {
            (document.body || document.documentElement).appendChild(container);
          });
        }
      }
      const rawMsg = err?.stack || err?.message || String(err || 'Unknown error');
      const safeTitle = escapeHtml(title);
      const safeMsg = escapeHtml(rawMsg);
      container.innerHTML = `
        <div style="max-width:900px;margin:20px auto;background:rgba(80,10,10,0.4);border:2px solid #ef4444;border-radius:16px;padding:24px;">
          <div style="font-size:20px;font-weight:bold;color:#fff;margin-bottom:12px;text-transform:uppercase;">${safeTitle}</div>
          <div style="color:#d4d4d8;margin-bottom:16px;font-size:14px;">An uncaught error was triggered outside of the React render cycle:</div>
          <div style="background:#000;border:1px solid #7f1d1d;border-radius:8px;padding:16px;color:#fca5a5;overflow-x:auto;max-height:300px;margin-bottom:20px;">${safeMsg}</div>
          <div style="display:flex;gap:12px;flex-wrap:wrap;">
            <button onclick="window.location.reload()" style="background:#dc2626;color:#fff;border:none;padding:10px 20px;border-radius:8px;font-weight:bold;cursor:pointer;font-family:monospace;text-transform:uppercase;">Reload</button>
            <button onclick="try{localStorage.clear();sessionStorage.clear();}catch(e){}window.location.reload();" style="background:#27272a;color:#e4e4e7;border:1px solid #3f3f46;padding:10px 20px;border-radius:8px;font-weight:bold;cursor:pointer;font-family:monospace;text-transform:uppercase;">Reset Storage & Reload</button>
          </div>
        </div>
      `;
    } catch (e) {
      console.error('Failed to show global error overlay:', e);
    }
  };

  window.addEventListener('error', (event) => {
    console.error('Global error caught:', event);
    showGlobalError(
      `Uncaught Error: ${event.message || 'Error'}`,
      event.error || `${event.filename || 'unknown'}:${event.lineno || 0}:${event.colno || 0}`
    );
  });

  window.addEventListener('unhandledrejection', (event) => {
    console.error('Unhandled rejection caught:', event);
    showGlobalError('Unhandled Promise Rejection', event.reason);
  });
}

createRoot(document.getElementById('root')).render(
  <ErrorBoundary>
    <StrictMode>
      <RepositoryProvider>
        <App />
      </RepositoryProvider>
    </StrictMode>
  </ErrorBoundary>,
)

if (typeof window !== 'undefined' && window.applicationCache) {
  try {
    window.applicationCache.addEventListener(
      'updateready',
      () => {
        try {
          if (window.applicationCache.status === window.applicationCache.UPDATEREADY) {
            window.applicationCache.swapCache();
            const lastReload = Number(sessionStorage.getItem('last_appcache_reload') || 0);
            if (Date.now() - lastReload > 8000) {
              sessionStorage.setItem('last_appcache_reload', String(Date.now()));
              window.location.reload();
            }
          }
        } catch (e) {
          console.warn('applicationCache swapCache failed:', e);
        }
      },
      false
    );
  } catch (e) {
    console.warn('applicationCache listener registration failed:', e);
  }
}
