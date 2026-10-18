(function () {
  var runLink = document.getElementById('run-haze');
  if (runLink) runLink.addEventListener('click', function () {
    var destination = new URL(runLink.href, location.href);
    destination.searchParams.delete('once');
    destination.searchParams.delete('resume');
    try {
      var token = Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
      sessionStorage.setItem('haze-start-once', token);
      if (sessionStorage.getItem('haze-start-once') === token)
        destination.searchParams.set('once', token);
    } catch (_) {}
    runLink.href = destination.href;
  });
  // Existing recovery links still enter the same runner with their options intact.
  var options = new URLSearchParams(location.search);
  if (options.has('loader') || options.has('manual') || options.has('browser')) {
    location.replace('run.html' + location.search); return;
  }
  var canvas = document.getElementById('haze-rain');
  if (!canvas) return;
  var ctx = canvas.getContext('2d');
  var symbols = 'ハゼアカサタナマヤラワ0123456789<>+*';
  var size = 18, drops = [], last = 0, animation;
  function resize() {
    canvas.width = innerWidth; canvas.height = innerHeight;
    drops = Array.from({length: Math.ceil(innerWidth / size)}, function () { return Math.random() * -innerHeight / size; });
  }
  function paint(time) {
    if (!reduceMotion) animation = requestAnimationFrame(paint);
    if (time - last < 65) return;
    last = time;
    ctx.fillStyle = 'rgba(9,5,7,.08)'; ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.font = size + 'px monospace';
    for (var i = 0; i < drops.length; i++) {
      ctx.fillStyle = Math.random() < .025 ? '#ff9ca6' : '#842437';
      ctx.fillText(symbols[Math.floor(Math.random() * symbols.length)], i * size, drops[i] * size);
      drops[i] = drops[i] * size > canvas.height && Math.random() > .975 ? 0 : drops[i] + .55;
    }
  }
  var reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
  resize(); addEventListener('resize', resize);
  if (!reduceMotion) animation = requestAnimationFrame(paint);
  else { for (var n=0;n<80;n++) paint(n*70); }
  addEventListener('pagehide', function () { cancelAnimationFrame(animation); });
})();
