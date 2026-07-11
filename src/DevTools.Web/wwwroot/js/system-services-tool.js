(() => {
  const { byId, escapeHtml, notify, jsonRequest } = window.devTools;
  const systemModal = bootstrap.Modal.getOrCreateInstance(byId('serviceSystemModal'));
  const serviceModal = bootstrap.Modal.getOrCreateInstance(byId('managedServiceModal'));
  let systems = [];
  let selectedSystemId = null;

  const selectedSystem = () => systems.find(item => item.id === selectedSystemId);
  const kindLabel = kind => kind === 'app-pool' ? 'IIS app pool' : 'Windows service';
  const statusClass = status => `status-${String(status || 'unknown').replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase()}`;
  const statusLabel = status => status === 'NotChecked' ? 'Not checked' : status || 'Unknown';
  const setBusy = (button, busy, label) => {
    if (!button) return;
    if (busy) { button.dataset.original = button.innerHTML; button.disabled = true; button.innerHTML = `<span class="spinner-border spinner-border-sm me-1" aria-hidden="true"></span>${escapeHtml(label)}`; }
    else { button.disabled = false; if (button.dataset.original) button.innerHTML = button.dataset.original; }
  };

  const renderSystems = () => {
    const host = byId('service-system-list');
    if (!systems.length) { host.innerHTML = '<div class="empty-state">No systems yet. Add one to begin monitoring services.</div>'; return; }
    host.innerHTML = systems.map(system => `
      <button class="system-row service-system-row" type="button" data-system-id="${system.id}">
        <span class="system-avatar">${escapeHtml(system.name.trim().charAt(0).toUpperCase() || 'S')}</span>
        <span class="system-row-main"><strong>${escapeHtml(system.name)}</strong><small>${system.services.length.toLocaleString()} configured ${system.services.length === 1 ? 'service' : 'services'}</small></span>
        <span class="system-row-meta">Local machine</span><span class="row-arrow" aria-hidden="true">›</span>
      </button>`).join('');
    host.querySelectorAll('[data-system-id]').forEach(button => button.addEventListener('click', () => showSystem(Number(button.dataset.systemId))));
  };

  const renderDetail = () => {
    const system = selectedSystem();
    if (!system) return;
    byId('service-system-name').textContent = system.name;
    const running = system.services.filter(item => item.status === 'Running').length;
    const checked = system.services.filter(item => item.checkedAt).length;
    byId('service-system-summary').textContent = checked ? `${running} running · ${system.services.length} configured` : `${system.services.length} configured ${system.services.length === 1 ? 'service' : 'services'}`;
    const host = byId('managed-service-list');
    if (!system.services.length) { host.innerHTML = '<div class="empty-state">No services configured. Add a Windows service or IIS application pool.</div>'; return; }
    host.innerHTML = system.services.map(service => {
      const canStart = service.status !== 'Running' && !['Starting', 'Stopping'].includes(service.status);
      const canStop = service.status === 'Running' || service.status === 'Paused';
      const detail = service.statusDetail ? `<span class="service-status-detail" title="${escapeHtml(service.statusDetail)}">${escapeHtml(service.statusDetail)}</span>` : '';
      return `<article class="managed-service-row" data-service-id="${service.id}">
        <span class="managed-service-icon" aria-hidden="true">${service.kind === 'app-pool' ? 'IIS' : '⚙'}</span>
        <span class="managed-service-identity"><strong>${escapeHtml(service.name)}</strong><small>${escapeHtml(service.targetName)}</small></span>
        <span class="service-kind">${kindLabel(service.kind)}</span>
        <span class="service-state"><span class="service-status ${statusClass(service.status)}"><i></i>${escapeHtml(statusLabel(service.status))}</span>${detail}</span>
        <span class="service-row-actions">
          <button class="btn btn-sm btn-outline-secondary service-action" type="button" data-action="start" ${canStart ? '' : 'disabled'}>Start</button>
          <button class="btn btn-sm btn-outline-secondary service-action" type="button" data-action="stop" ${canStop ? '' : 'disabled'}>Stop</button>
          <button class="btn btn-sm btn-outline-primary service-action" type="button" data-action="restart" ${canStop ? '' : 'disabled'}>Restart</button>
          <button class="icon-button service-delete" type="button" title="Remove from DevTools" aria-label="Remove ${escapeHtml(service.name)}">×</button>
        </span>
      </article>`;
    }).join('');
    host.querySelectorAll('.service-action').forEach(button => button.addEventListener('click', () => executeAction(button)));
    host.querySelectorAll('.service-delete').forEach(button => button.addEventListener('click', () => deleteService(Number(button.closest('[data-service-id]').dataset.serviceId))));
    const checkedTimes = system.services.map(item => item.checkedAt && new Date(item.checkedAt)).filter(Boolean);
    byId('service-refresh-meta').textContent = checkedTimes.length ? `Last checked ${new Date(Math.max(...checkedTimes)).toLocaleString()}` : 'Select Refresh status to check all configured services.';
  };

  const loadSystems = async () => {
    try { systems = await jsonRequest('/api/system-services/systems'); renderSystems(); }
    catch (error) { byId('service-system-list').innerHTML = `<div class="empty-state text-danger">${escapeHtml(error.message)}</div>`; }
  };

  const showSystem = async id => {
    selectedSystemId = id;
    byId('service-systems-view').classList.add('d-none');
    byId('service-system-detail').classList.remove('d-none');
    renderDetail();
    await refreshStatuses();
  };

  const refreshStatuses = async (sourceButton = byId('service-status-refresh')) => {
    if (!selectedSystemId) return;
    setBusy(sourceButton, true, 'Checking');
    byId('service-refresh-meta').textContent = 'Checking local service states…';
    try {
      const fresh = await jsonRequest(`/api/system-services/systems/${selectedSystemId}?refresh=true`);
      systems = systems.map(item => item.id === fresh.id ? fresh : item);
      renderDetail();
    } catch (error) { notify(error.message, true); byId('service-refresh-meta').textContent = error.message; }
    finally { setBusy(sourceButton, false); }
  };

  const executeAction = async button => {
    const serviceId = Number(button.closest('[data-service-id]').dataset.serviceId);
    const action = button.dataset.action;
    const service = selectedSystem()?.services.find(item => item.id === serviceId);
    if (!service) return;
    if ((action === 'stop' || action === 'restart') && !confirm(`${action === 'stop' ? 'Stop' : 'Restart'} ${service.name}?`)) return;
    setBusy(button, true, `${action.charAt(0).toUpperCase()}${action.slice(1)}ing`);
    try {
      const updated = await jsonRequest(`/api/system-services/services/${serviceId}/${action}`, 'POST');
      const system = selectedSystem();
      system.services = system.services.map(item => item.id === updated.id ? updated : item);
      renderDetail();
      notify(`${service.name} is ${statusLabel(updated.status).toLowerCase()}.`);
    } catch (error) { notify(error.message, true); }
    finally { setBusy(button, false); }
  };

  const deleteService = async serviceId => {
    const service = selectedSystem()?.services.find(item => item.id === serviceId);
    if (!service || !confirm(`Remove ${service.name} from DevTools? The Windows/IIS service itself will not be deleted.`)) return;
    try { await jsonRequest(`/api/system-services/services/${serviceId}`, 'DELETE'); selectedSystem().services = selectedSystem().services.filter(item => item.id !== serviceId); renderDetail(); notify('Service removed from DevTools.'); }
    catch (error) { notify(error.message, true); }
  };

  byId('service-system-add').addEventListener('click', () => { byId('service-system-form').reset(); systemModal.show(); setTimeout(() => byId('service-system-input').focus(), 150); });
  byId('service-system-form').addEventListener('submit', async event => {
    event.preventDefault(); const button = byId('service-system-submit'); setBusy(button, true, 'Adding');
    try { const system = await jsonRequest('/api/system-services/systems', 'POST', { name: byId('service-system-input').value }); systems.push(system); systems.sort((a,b) => a.name.localeCompare(b.name)); renderSystems(); systemModal.hide(); await showSystem(system.id); }
    catch (error) { notify(error.message, true); } finally { setBusy(button, false); }
  });
  byId('managed-service-add').addEventListener('click', () => { byId('managed-service-form').reset(); updateTargetHelp(); serviceModal.show(); setTimeout(() => byId('managed-service-name').focus(), 150); });
  byId('managed-service-kind').addEventListener('change', updateTargetHelp);
  byId('managed-service-form').addEventListener('submit', async event => {
    event.preventDefault(); if (!selectedSystemId) return; const button = byId('managed-service-submit'); setBusy(button, true, 'Adding');
    try {
      const created = await jsonRequest(`/api/system-services/systems/${selectedSystemId}/services`, 'POST', { name: byId('managed-service-name').value, kind: byId('managed-service-kind').value, targetName: byId('managed-service-target').value });
      selectedSystem().services.push(created); selectedSystem().services.sort((a,b) => a.name.localeCompare(b.name)); serviceModal.hide(); renderDetail(); notify('Service added.');
    } catch (error) { notify(error.message, true); } finally { setBusy(button, false); }
  });
  byId('service-system-back').addEventListener('click', () => { selectedSystemId = null; byId('service-system-detail').classList.add('d-none'); byId('service-systems-view').classList.remove('d-none'); renderSystems(); });
  byId('service-status-refresh').addEventListener('click', event => refreshStatuses(event.currentTarget));
  byId('service-system-delete').addEventListener('click', async () => {
    const system = selectedSystem(); if (!system || !confirm(`Delete ${system.name} from DevTools? Its services will not be changed.`)) return;
    try { await jsonRequest(`/api/system-services/systems/${system.id}`, 'DELETE'); systems = systems.filter(item => item.id !== system.id); selectedSystemId = null; byId('service-system-detail').classList.add('d-none'); byId('service-systems-view').classList.remove('d-none'); renderSystems(); notify('System removed from DevTools.'); }
    catch (error) { notify(error.message, true); }
  });

  function updateTargetHelp() {
    const appPool = byId('managed-service-kind').value === 'app-pool';
    byId('managed-service-target-label').textContent = appPool ? 'IIS application pool name' : 'Windows service name';
    byId('managed-service-target').placeholder = appPool ? 'e.g. OrdersApiPool' : 'e.g. OrderWorker';
    byId('managed-service-target-help').textContent = appPool ? 'Use the exact application pool name shown in IIS Manager.' : 'Use the service name shown in Windows Services properties, not necessarily its display name.';
  }

  loadSystems();
})();
