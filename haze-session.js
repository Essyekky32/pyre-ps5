import { renderCards, payloads } from './haze-catalog.js?v=services-1';
import { sendPayload } from './haze-sender.js?v=red-menu-1';
export async function attachMenu(p, chain) {
  const style = document.createElement('link'); style.rel='stylesheet'; style.href='haze-menu.css?v=services-1';
  function loadStyle() { if (!style.parentNode) document.head.appendChild(style); }
  const original = Array.from(document.body.children).filter(el=>el.tagName!=='SCRIPT');
  const originalHidden = new Map(original.map(node=>[node,node.hidden]));
  const panel = document.createElement('section'); panel.id='haze-runtime';
  panel.innerHTML = '<p class="haze-session-progress" id="haze-session-progress">Startup is running. Stay on this page until the sequence finishes. Then press Circle once for the Haze menu.</p><div class="haze-actions" id="haze-run-controls"><button type="button" class="haze-pill" id="haze-open-extras" disabled>Additional Payloads - waiting for startup</button><button type="button" class="haze-pill secondary" id="haze-open-home" disabled>Haze menu</button></div>' +
    '<div id="haze-session-home" hidden class="haze-home"><p class="haze-eyebrow">NOFINALLEVEL / PS5</p><h1 class="haze-title">HAZE</h1><p class="haze-tagline">Your console. Your choice.</p><div class="haze-choices"><button class="haze-choice" id="haze-show-startup"><span class="haze-number">01</span><span><strong>HAZE SESSION</strong><small>View this session’s startup log</small></span></button><button class="haze-choice" id="haze-home-extras"><span class="haze-number">02</span><span><strong>ADDITIONAL PAYLOADS</strong><small>Payload Manager · App Dumper · more</small></span></button></div><p class="haze-help">Use your existing session to launch additional tools.</p></div>' +
    '<div id="haze-session-extras" hidden class="haze-catalog"><button class="haze-pill secondary" id="haze-extras-home">← Haze menu</button><p class="haze-eyebrow" style="margin-top:30px">HAZE / YOUR TOOLKIT</p><h1>Additional Payloads</h1><p class="haze-intro">Choose a tool. Payloads launch one at a time.</p><div id="haze-send-status" class="haze-notice" role="status">Ready to send. Watch PS5 notifications to confirm startup.</div><div id="haze-live-cards" class="haze-grid"></div><a class="haze-pill secondary" href="http://127.0.0.1:8084/">Open running Payload Manager</a><p class="haze-help">Payload Manager and App Dumper start with Haze. Open either tool when you need it; all other tools are optional.</p></div>';
  document.body.appendChild(panel);
  const el = id=>document.getElementById(id);
  const loaderOnly = new URLSearchParams(location.search).get('loader')==='1';
  let busy = !loaderOnly, historyReady=false, sessionFailed=false, currentView='run';
  const sent = new Set();
  let buttons = [], cardsReady = false;
  const servicePorts = new Map();
  const items = new Map(payloads.map(item=>[item.id,item]));
  function applyService(id,port) {
    const button=el('haze-live-cards').querySelector('[data-payload="'+id+'"]');
    if(button) button.textContent='Running';
    const link=el('haze-live-cards').querySelector('[data-service="'+id+'"]');
    if(link) link.href='http://127.0.0.1:'+port+'/';
  }
  function prepareCards() {
    if (cardsReady) return;
    buttons = renderCards(el('haze-live-cards'),launch);
    cardsReady = true;
    servicePorts.forEach((port,id)=>applyService(id,port));
    refresh();
  }
  function refresh() {
    buttons.forEach(button=>{
      const item=items.get(button.dataset.payload);
      const conflicting=item.loaderOnly && (!loaderOnly || Array.from(sent).some(id=>items.get(id)?.loaderOnly && id!==item.id));
      button.disabled=busy || sessionFailed || conflicting || (!item.repeatable && sent.has(item.id));
      if(conflicting) button.textContent='Requires a fresh loader-only session';
    });
    ['haze-open-extras','haze-open-home','haze-show-startup','haze-home-extras','haze-extras-home'].forEach(id=>{el(id).disabled=busy;});
  }
  function show(view,push) {
    if (busy) return;
    // Do not trigger font parsing and menu restyling automatically at completion.
    if (view!=='run') loadStyle();
    if (view==='payloads') prepareCards();
    if (push) history.pushState({hazeView:view},'',location.pathname+location.search+'#'+view);
    if (currentView==='run') original.forEach(node=>originalHidden.set(node,node.hidden));
    currentView=view;
    original.forEach(node=>node.hidden=view!=='run' || originalHidden.get(node));
    document.body.classList.toggle('haze-session-screen',view!=='run');
    el('haze-session-progress').hidden=view!=='run'; el('haze-run-controls').hidden=view!=='run'; el('haze-session-home').hidden=view!=='home'; el('haze-session-extras').hidden=view!=='payloads';
    document.title=view==='payloads'?'Live Payloads | Haze':view==='home'?'Haze Session | PS5':'Haze PS5 Autoloader';
    const focus = view==='home' ? el('haze-home-extras') : view==='payloads' ? el('haze-extras-home') : el('haze-open-extras');
    focus.focus(); window.scrollTo(0,0);
  }
  function initializeHistory() {
    if (!historyReady) {
      // Circle/back returns to a menu in this document, retaining the live sender.
      history.replaceState({hazeView:'home'},'',location.pathname+location.search+'#home');
      history.pushState({hazeView:'run'},'',location.pathname+location.search+'#run'); historyReady=true;
    }
  }
  function enableNavigation() {
    el('haze-open-extras').textContent='Additional Payloads';
    el('haze-session-progress').textContent=loaderOnly ? 'Loader-only session. Choose Additional Payloads to start a tool.' : 'Haze startup finished. Payload Manager and App Dumper answered their readiness checks. Press Circle once for the Haze menu, then choose Additional Payloads.';
    refresh();
  }
  async function launch(item,button) {
    if (busy || sessionFailed || button.disabled || (!item.repeatable && sent.has(item.id))) return;
    busy=true;refresh(); const status=el('haze-send-status');status.className='haze-notice';status.textContent='Downloading and sending '+item.title+'…';
    try {
      const length=await sendPayload(item,p,chain); sent.add(item.id);button.textContent=item.repeatable?'Send again':'Transferred';
      if(item.id==='poords4-stop') { sent.delete('poords4'); const start=el('haze-live-cards').querySelector('[data-payload="poords4"]'); if(start) start.textContent='Launch PoorDS4'; }
      status.textContent=item.title+': '+length.toLocaleString()+' bytes transferred. Watch its PS5 notification; transfer does not confirm startup.';
    } catch(error) {
      if (p.worker_is_usable && !p.worker_is_usable()) sessionFailed=true;
      status.className='haze-notice error';status.textContent='Could not send '+item.title+': '+error.message+(sessionFailed?'. Restart before another exploit attempt.':'');
    } finally {busy=false;refresh();}
  }
  el('haze-open-extras').onclick=()=>show('payloads',true);el('haze-home-extras').onclick=()=>show('payloads',true);
  el('haze-open-home').onclick=()=>show('home',true);el('haze-extras-home').onclick=()=>show('home',true);el('haze-show-startup').onclick=()=>show('run',true);
  addEventListener('popstate',event=>{if(historyReady){if(busy){history.pushState({hazeView:currentView},'',location.pathname+location.search+'#'+currentView);return;}show(event.state&&event.state.hazeView||'home',false);}});
  initializeHistory();refresh();if(loaderOnly) enableNavigation();
  return {
    serviceReady(id,port){
      sent.add(id);
      servicePorts.set(id,port);
      if(cardsReady) applyService(id,port);
      refresh();
    },
    beginStartup(){busy=true;refresh();},
    endStartup(ok){
      busy=false;sessionFailed=!ok;enableNavigation();
      if(!ok){
        el('haze-session-progress').textContent='Startup stopped with an error. Read the final log above before another attempt.';
        el('haze-send-status').textContent='Haze startup reported an error. Restart before another attempt.';el('haze-send-status').className='haze-notice error';
      }
    }
  };
}
