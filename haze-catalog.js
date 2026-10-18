import { payloads } from './haze-catalog-data.js?v=services-1';
export { payloads };
export function renderCards(container, onLaunch) {
  for (const item of payloads) {
    const card = document.createElement('article'); card.className = 'haze-card';
    const version = document.createElement('span'); version.className = 'haze-version'; version.textContent = 'v' + item.version + ' / ' + item.author;
    const title = document.createElement('h2'); title.textContent = item.title;
    const description = document.createElement('p'); description.textContent = item.description;
    const footer = document.createElement('div'); footer.className = 'haze-card-footer';
    const button = document.createElement(onLaunch ? 'button' : 'span'); button.className = onLaunch ? 'haze-launch' : 'haze-availability'; button.textContent = onLaunch ? 'Launch ' + item.title : 'Available when Run Haze finishes'; button.dataset.payload = item.id;
    if (onLaunch) {
      button.type = 'button';
      button.addEventListener('click', function () { onLaunch(item, button); });
      // Clicking the title or card also activates the visible launch control.
      card.classList.add('haze-card-live');
      card.addEventListener('click', function (event) {
        if (event.target.closest('a,button') || button.disabled) return;
        button.click();
      });
    }
    if (item.id === 'manager' || item.id === 'dumper') {
      description.textContent += ' Starts automatically with Run Haze.';
      const open = document.createElement('a');
      open.className='haze-open-service'; open.dataset.service=item.id;
      open.href='http://127.0.0.1:'+(item.id==='manager'?8084:8081)+'/';
      open.textContent='Open '+item.title;
      footer.appendChild(open);
    }
    if (item.loaderOnly) {
      description.textContent += ' Alternative version: use only in a fresh loader-only session. The regular Haze sequence already starts its own HEN and mount service.';
    }
    const source = document.createElement('a'); source.href = item.source; source.textContent = 'Developer & release'; source.target = '_blank'; source.rel = 'noopener';
    footer.append(button, source); card.append(version, title, description, footer); container.appendChild(card);
  }
  return container.querySelectorAll('button');
}
