(function () {
  // A landing-page click grants one start. Consume it before WebKit runs.
  // Reloads, crash recovery, stale links and unavailable storage require a click.
  var key = 'haze-start-once';
  var panel = document.getElementById('haze-start-guard');
  var button = document.getElementById('haze-start-fresh');
  var status = document.getElementById('last-status');
  var started = false;
  var resolveStart;
  window.hazeStartPromise = new Promise(function (resolve) { resolveStart = resolve; });

  function start() {
    if (started || window.hazeEntryRedirecting) return;
    started = true;
    button.disabled = true;
    panel.hidden = true;
    try { sessionStorage.removeItem(key); } catch (_) {}
    try {
      var destination = new URL(location.href);
      destination.searchParams.delete('once');
      destination.searchParams.set('resume', '1');
      history.replaceState(null, '', destination.href);
    } catch (_) {}
    status.textContent = 'Latest status: starting Haze once for this page.';
    resolveStart();
  }

  button.addEventListener('click', start);
  if (window.hazeEntryRedirecting) return;
  var permitted = false;
  try {
    var token = new URL(location.href).searchParams.get('once');
    var saved = sessionStorage.getItem(key);
    // A repeated or untrusted URL alone cannot authorize another automatic run.
    sessionStorage.removeItem(key);
    permitted = !!token && token === saved && sessionStorage.getItem(key) === null;
  } catch (_) {}
  if (permitted) start();
  else {
    panel.hidden = false;
    status.textContent = 'Latest status: paused. No exploit has been started by this page.';
  }
}());
