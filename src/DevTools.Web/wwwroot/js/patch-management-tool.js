(() => {
  const { byId, escapeHtml, notify, jsonRequest, appUrl } = window.devTools;
  const modal = id => bootstrap.Modal.getOrCreateInstance(byId(id));
  const systemModal = modal('patchSystemModal'), modulesModal = modal('patchModulesModal'), moduleModal = modal('patchModuleModal');
  const compileModal = modal('patchCompileModal'), latestModal = modal('patchLatestModal');
  let systems = [], snapshots = [], packages = [], selectedSystemId = null, editingSystemId = null, editingModuleId = null, returnToManager = false;
  let changes = null;

  const selectedSystem = () => systems.find(item => item.id === selectedSystemId);
  const bytes = value => { const units = ['B','KB','MB','GB','TB']; let size = Number(value) || 0, unit = 0; while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit++; } return `${size.toFixed(unit && size < 10 ? 1 : 0)} ${units[unit]}`; };
  const when = value => value ? new Date(value).toLocaleString() : '';
  const plural = (count, word) => `${Number(count).toLocaleString()} ${word}${count === 1 ? '' : 's'}`;
  const setBusy = (button, busy, label) => {
    if (busy) { button.dataset.original = button.innerHTML; button.disabled = true; button.innerHTML = `<span class="spinner-border spinner-border-sm me-1" aria-hidden="true"></span>${escapeHtml(label)}`; }
    else { button.disabled = false; if (button.dataset.original) button.innerHTML = button.dataset.original; }
  };

  // ---------- Systems (the open system is kept in ?system=<id>, so a reload reopens it) ----------
  const setSystemInUrl = systemId => {
    const url = new URL(location.href);
    if (systemId) url.searchParams.set('system', String(systemId)); else url.searchParams.delete('system');
    if (url.href !== location.href) history.replaceState(null, '', url);
  };

  const loadSystems = async () => {
    systems = await jsonRequest('/api/patch-management/systems');
    renderSystems();
    if (selectedSystemId && !selectedSystem()) showList(); else if (selectedSystemId) renderDetail();
  };

  const renderSystems = () => {
    const host = byId('patch-system-list');
    if (!systems.length) { host.innerHTML = '<div class="empty-state">No systems yet. Add one, then add its module folders.</div>'; return; }
    host.innerHTML = systems.map(system => `
      <button class="system-row" type="button" data-system-id="${system.id}">
        <span class="system-avatar">${escapeHtml(system.name.trim().charAt(0).toUpperCase() || 'P')}</span>
        <span class="system-row-main"><strong>${escapeHtml(system.name)}</strong><small>${plural(system.modules.length, 'module')}${system.latestVersion ? ` · latest version ${when(system.latestVersion.createdAt)}` : ' · no latest version yet'}</small></span>
        <span class="system-row-meta">${system.modules.map(module => escapeHtml(module.name)).join(', ')}</span><span class="row-arrow" aria-hidden="true">›</span>
      </button>`).join('');
    host.querySelectorAll('[data-system-id]').forEach(button => button.addEventListener('click', () => showSystem(Number(button.dataset.systemId))));
  };

  const showList = () => {
    selectedSystemId = null; changes = null; setSystemInUrl(null);
    byId('patch-system-detail').classList.add('d-none'); byId('patch-systems-view').classList.remove('d-none');
  };

  const showSystem = async systemId => {
    selectedSystemId = systemId; changes = null; setSystemInUrl(systemId);
    byId('patch-systems-view').classList.add('d-none'); byId('patch-system-detail').classList.remove('d-none');
    byId('patch-changes').classList.add('d-none');
    renderDetail();
    await loadHistory();
  };

  const renderDetail = () => {
    const system = selectedSystem(); if (!system) return;
    byId('patch-system-name').textContent = system.name;
    byId('patch-modules-count').textContent = system.modules.length;
    byId('patch-no-modules').classList.toggle('d-none', system.modules.length > 0);
    const latest = system.latestVersion;
    byId('patch-latest-title').textContent = latest ? (latest.label || 'Latest version') : 'Not set yet';
    byId('patch-latest-meta').textContent = latest
      ? `${when(latest.markedLatestAt || latest.createdAt)} · ${plural(latest.fileCount, 'file')} · ${bytes(latest.totalBytes)}${latest.createdBy ? ` · by ${latest.createdBy}` : ''}`
      : 'Set the current files as the latest version once they match UAT/production. Until then, every file counts as ADDED.';
    ['patch-view-changes', 'patch-compile', 'patch-set-latest'].forEach(id => byId(id).disabled = !system.modules.length);
    renderModules();
  };

  const openSystemModal = system => {
    editingSystemId = system?.id ?? null;
    byId('patch-system-name-input').value = system?.name ?? '';
    byId('patch-system-output-input').value = system?.outputDirectory ?? '';
    byId('patch-system-modal-title').textContent = system ? 'Edit system' : 'Add system';
    byId('patch-system-submit').textContent = system ? 'Save changes' : 'Add system';
    systemModal.show();
  };

  const saveSystem = async event => {
    event.preventDefault();
    const body = { name:byId('patch-system-name-input').value, outputDirectory:byId('patch-system-output-input').value || null };
    try {
      const saved = await jsonRequest(editingSystemId ? `/api/patch-management/systems/${editingSystemId}` : '/api/patch-management/systems', editingSystemId ? 'PUT' : 'POST', body);
      systemModal.hide(); await loadSystems(); notify(editingSystemId ? 'System updated.' : 'System added.');
      if (!editingSystemId) showSystem(saved.id);
    } catch (error) { notify(error.message, true); }
  };

  const deleteSystem = async () => {
    const system = selectedSystem();
    if (!system || !confirm(`Delete ${system.name}, its modules, snapshots and compiled patches? Module folders are not touched.`)) return;
    try { await jsonRequest(`/api/patch-management/systems/${system.id}`, 'DELETE'); showList(); await loadSystems(); notify('System deleted.'); }
    catch (error) { notify(error.message, true); }
  };

  // ---------- Modules ----------
  const renderModules = () => {
    const system = selectedSystem(), host = byId('patch-module-list');
    host.innerHTML = system.modules.length ? system.modules.map(module => {
      const exclusions = module.exclusions ? module.exclusions.split('\n').length : 0;
      return `<article class="deploy-module-row" data-module-id="${module.id}">
        <span class="deploy-module-icon" aria-hidden="true">▣</span>
        <span class="deploy-module-main"><strong>${escapeHtml(module.name)}</strong><code title="${escapeHtml(module.sourcePath)}">${escapeHtml(module.sourcePath)}</code>
          <span class="deploy-module-chips">${exclusions ? `<span class="deploy-chip muted" title="${escapeHtml(module.exclusions)}">${plural(exclusions, 'ignored pattern')}</span>` : '<span class="deploy-chip muted">No ignored files</span>'}</span></span>
        <span class="deploy-row-actions"><button class="btn btn-sm btn-ghost module-edit" type="button">Edit</button><button class="icon-button module-delete" type="button" title="Remove module" aria-label="Remove ${escapeHtml(module.name)}">×</button></span>
      </article>`;
    }).join('') : '<div class="empty-state compact">No modules yet.</div>';
    host.querySelectorAll('.module-edit').forEach(button => button.addEventListener('click', () => openModuleModal(Number(button.closest('[data-module-id]').dataset.moduleId))));
    host.querySelectorAll('.module-delete').forEach(button => button.addEventListener('click', () => deleteModule(Number(button.closest('[data-module-id]').dataset.moduleId))));
  };

  const openModuleModal = moduleId => {
    returnToManager = byId('patchModulesModal').classList.contains('show');
    if (returnToManager) modulesModal.hide();
    const module = selectedSystem()?.modules.find(item => item.id === moduleId);
    editingModuleId = module?.id ?? null;
    byId('patch-module-name').value = module?.name ?? '';
    byId('patch-module-source').value = module?.sourcePath ?? '';
    byId('patch-module-exclusions').value = module?.exclusions ?? '';
    byId('patch-module-modal-title').textContent = module ? 'Edit module' : 'Add module';
    byId('patch-module-submit').textContent = module ? 'Save changes' : 'Add module';
    moduleModal.show();
  };

  const saveModule = async event => {
    event.preventDefault();
    const body = { name:byId('patch-module-name').value, sourcePath:byId('patch-module-source').value, exclusions:byId('patch-module-exclusions').value || null };
    try {
      await jsonRequest(editingModuleId ? `/api/patch-management/systems/${selectedSystemId}/modules/${editingModuleId}` : `/api/patch-management/systems/${selectedSystemId}/modules`, editingModuleId ? 'PUT' : 'POST', body);
      moduleModal.hide(); await loadSystems(); notify(editingModuleId ? 'Module updated.' : 'Module added.');
    } catch (error) { notify(error.message, true); }
  };

  const deleteModule = async moduleId => {
    const module = selectedSystem()?.modules.find(item => item.id === moduleId);
    if (!module || !confirm(`Remove module ${module.name}? Its folder is not touched; existing snapshots keep its files.`)) return;
    try { await jsonRequest(`/api/patch-management/modules/${moduleId}`, 'DELETE'); await loadSystems(); notify('Module removed.'); }
    catch (error) { notify(error.message, true); }
  };

  // ---------- Snapshots and patches ----------
  const loadHistory = async () => {
    if (!selectedSystemId) return;
    [snapshots, packages] = await Promise.all([
      jsonRequest(`/api/patch-management/systems/${selectedSystemId}/snapshots`),
      jsonRequest(`/api/patch-management/systems/${selectedSystemId}/packages`)
    ]);
    renderCompareOptions(); renderSnapshots(); renderPackages();
  };

  const snapshotName = snapshot => `${snapshot.label || (snapshot.source === 'patch' ? 'Patch snapshot' : 'Snapshot')} · ${when(snapshot.createdAt)}`;

  const renderCompareOptions = () => {
    const select = byId('patch-compare-base'), previous = select.value;
    const latest = snapshots.find(item => item.isLatest);
    select.innerHTML = `<option value="">${latest ? 'Latest version' : 'Nothing (all files are new)'}</option>`
      + snapshots.filter(item => !item.isLatest).map(item => `<option value="${item.id}">${escapeHtml(snapshotName(item))}</option>`).join('');
    if ([...select.options].some(option => option.value === previous)) select.value = previous;
  };

  const snapshotActions = item => `
    ${item.isLatest ? '' : `<button class="btn btn-sm deploy-btn-modules snapshot-latest" type="button" data-snapshot-id="${item.id}">Set as latest</button>`}
    ${item.isLatest || item.patchId ? '' : `<button class="icon-button snapshot-delete" type="button" data-snapshot-id="${item.id}" title="Delete snapshot" aria-label="Delete snapshot">×</button>`}`;
  const wireSnapshotActions = host => {
    host.querySelectorAll('.snapshot-latest').forEach(button => button.addEventListener('click', event => { event.stopPropagation(); detailsModal.hide(); setSnapshotLatest(Number(button.dataset.snapshotId)); }));
    host.querySelectorAll('.snapshot-delete').forEach(button => button.addEventListener('click', event => { event.stopPropagation(); detailsModal.hide(); deleteSnapshot(Number(button.dataset.snapshotId)); }));
  };

  const packageActions = item => `
    ${item.fileExists ? `<a class="btn btn-sm btn-primary" href="${appUrl(`/api/patch-management/packages/${item.id}/download`)}" download>Download</a>` : '<span class="deploy-chip muted">File missing</span>'}
    ${item.snapshotIsLatest ? '' : `<button class="btn btn-sm deploy-btn-modules package-latest" type="button" data-package-id="${item.id}" title="Mark what this patch contains as the version now on UAT/production">Set as latest</button>`}
    <button class="icon-button package-delete" type="button" data-package-id="${item.id}" title="Delete patch" aria-label="Delete ${escapeHtml(item.fileName)}">×</button>`;
  const wirePackageActions = host => {
    host.querySelectorAll('a[download]').forEach(link => link.addEventListener('click', event => event.stopPropagation()));
    host.querySelectorAll('.package-latest').forEach(button => button.addEventListener('click', event => {
      event.stopPropagation(); detailsModal.hide();
      const item = packages.find(candidate => candidate.id === Number(button.dataset.packageId));
      setSnapshotLatest(item.snapshotId, `Set the contents of ${item.fileName} as the latest version (the version now on UAT/production)?`);
    }));
    host.querySelectorAll('.package-delete').forEach(button => button.addEventListener('click', event => {
      event.stopPropagation(); detailsModal.hide(); deletePackage(packages.find(candidate => candidate.id === Number(button.dataset.packageId)));
    }));
  };

  // Clicking a row (or pressing Enter on it) opens its details; buttons inside keep their own action.
  const makeRowsClickable = (host, open) => host.querySelectorAll('[data-row-id]').forEach(row => {
    row.addEventListener('click', event => { if (!event.target.closest('button, a')) open(Number(row.dataset.rowId)); });
    row.addEventListener('keydown', event => { if ((event.key === 'Enter' || event.key === ' ') && event.target === row) { event.preventDefault(); open(Number(row.dataset.rowId)); } });
  });

  const renderSnapshots = () => {
    const host = byId('patch-snapshot-list');
    if (!snapshots.length) { host.innerHTML = '<div class="empty-state">No snapshots yet. Use “Set current as latest” or compile a patch.</div>'; return; }
    host.innerHTML = snapshots.map(item => `<article class="deploy-package-row compact" data-row-id="${item.id}" tabindex="0" role="button" aria-label="View snapshot details">
        <span class="deploy-package-icon" aria-hidden="true">${item.source === 'patch' ? 'Δ' : '◉'}</span>
        <span class="deploy-package-main">
          <span class="deploy-package-line">
            <strong>${escapeHtml(item.label || (item.source === 'patch' ? 'Patch snapshot' : 'Snapshot'))}</strong>
            ${item.isLatest ? '<span class="deploy-status deploy-status-deployed">Latest version</span>' : ''}
            <span class="deploy-status ${item.source === 'patch' ? 'deploy-status-uploaded' : 'patch-status-snapshot'}">${item.source === 'patch' ? 'From patch' : 'Set as latest'}</span>
          </span>
          <small class="deploy-package-meta">${when(item.createdAt)} · ${plural(item.fileCount, 'file')} · ${bytes(item.totalBytes)}${item.createdBy ? ` · by ${escapeHtml(item.createdBy)}` : ''}</small>
        </span>
        <span class="deploy-row-actions">${snapshotActions(item)}</span>
      </article>`).join('');
    wireSnapshotActions(host);
    makeRowsClickable(host, openSnapshotDetails);
  };

  const renderPackages = () => {
    const host = byId('patch-package-list');
    if (!packages.length) { host.innerHTML = '<div class="empty-state">No patches compiled yet.</div>'; return; }
    host.innerHTML = packages.map(item => `<article class="deploy-package-row compact" data-row-id="${item.id}" tabindex="0" role="button" aria-label="View ${escapeHtml(item.fileName)} details">
        <span class="deploy-package-icon" aria-hidden="true">${item.format === '7z' ? '7z' : 'zip'}</span>
        <span class="deploy-package-main">
          <span class="deploy-package-line">
            <strong title="${escapeHtml(item.fileName)}">${escapeHtml(item.fileName)}</strong>
            ${item.snapshotIsLatest ? '<span class="deploy-status deploy-status-deployed">Latest version</span>' : ''}
            ${item.label ? `<span class="deploy-package-description" title="${escapeHtml(item.label)}">${escapeHtml(item.label)}</span>` : ''}
          </span>
          <small class="deploy-package-meta"><span class="patch-count added">${item.added} added</span> · <span class="patch-count changed">${item.changed} changed</span> · <span class="patch-count deleted">${item.deleted} deleted</span> · ${bytes(item.sizeBytes)} · ${when(item.createdAt)}${item.createdBy ? ` · by ${escapeHtml(item.createdBy)}` : ''} · compared with ${item.baseCreatedAt ? when(item.baseCreatedAt) : 'nothing'}</small>
        </span>
        <span class="deploy-row-actions">${packageActions(item)}</span>
      </article>`).join('');
    wirePackageActions(host);
    makeRowsClickable(host, openPackageDetails);
  };

  const setSnapshotLatest = async (snapshotId, question = 'Set this snapshot as the latest version? Changes will be measured from it.') => {
    if (!confirm(question)) return;
    try { await jsonRequest(`/api/patch-management/snapshots/${snapshotId}/latest`, 'POST'); await Promise.all([loadSystems(), loadHistory()]); changes = null; byId('patch-changes').classList.add('d-none'); notify('Latest version updated.'); }
    catch (error) { notify(error.message, true); }
  };

  const deleteSnapshot = async snapshotId => {
    if (!confirm('Delete this snapshot?')) return;
    try { await jsonRequest(`/api/patch-management/snapshots/${snapshotId}`, 'DELETE'); await loadHistory(); notify('Snapshot deleted.'); }
    catch (error) { notify(error.message, true); }
  };

  const deletePackage = async item => {
    if (!confirm(`Delete ${item.fileName}? Its snapshot is kept.`)) return;
    try { await jsonRequest(`/api/patch-management/packages/${item.id}`, 'DELETE'); await loadHistory(); notify('Patch deleted.'); }
    catch (error) { notify(error.message, true); }
  };

  // ---------- Changes (grouped by module) ----------
  const kindLabels = { ADDED:'Added', CHANGED:'Changed', DELETED:'Deleted' };
  const sizeText = file => file.kind === 'DELETED' ? bytes(file.previousSize) : file.kind === 'CHANGED' ? `${bytes(file.previousSize)} → ${bytes(file.size)}` : bytes(file.size);

  // Renders a change list into host, grouped by module, with filter chips that only affect this host.
  const mountChanges = (host, result) => {
    let filter = '';
    const draw = () => {
      const total = result.added + result.changed + result.deleted;
      if (!total) { host.innerHTML = '<div class="empty-state compact">No changes — the files match.</div>'; return; }
      const modules = result.modules.filter(module => module.added + module.changed + module.deleted > 0);
      host.innerHTML = `
        <div class="patch-change-summary">
          ${[['', 'All', total], ['ADDED', 'Added', result.added], ['CHANGED', 'Changed', result.changed], ['DELETED', 'Deleted', result.deleted]].map(([kind, label, count]) =>
            `<button type="button" class="patch-change-chip kind-${(kind || 'all').toLowerCase()} ${filter === kind ? 'active' : ''}" data-kind="${kind}"><strong>${count.toLocaleString()}</strong>${label}</button>`).join('')}
        </div>
        ${modules.map(module => {
          const files = result.files.filter(file => file.module === module.module && (!filter || file.kind === filter));
          if (!files.length) return '';
          return `<section class="patch-module-group">
            <header><strong>${escapeHtml(module.module)}</strong>
              <span class="patch-module-counts">${module.added ? `<span class="patch-count added">${module.added} added</span>` : ''}${module.changed ? `<span class="patch-count changed">${module.changed} changed</span>` : ''}${module.deleted ? `<span class="patch-count deleted">${module.deleted} deleted</span>` : ''}</span></header>
            <ul class="deploy-file-list patch-file-list">${files.map(file => `<li><span class="deploy-file-tag patch-kind-${file.kind.toLowerCase()}">${kindLabels[file.kind]}</span><code title="${escapeHtml(file.path)}">${escapeHtml(file.path)}</code><small>${sizeText(file)}</small></li>`).join('')}</ul>
          </section>`;
        }).join('')}
        ${result.truncated ? `<p class="deploy-preview-note">Showing the first ${result.files.length.toLocaleString()} changes.</p>` : ''}`;
      host.querySelectorAll('[data-kind]').forEach(button => button.addEventListener('click', () => { filter = button.dataset.kind; draw(); }));
    };
    draw();
  };

  const viewChanges = async () => {
    const button = byId('patch-view-changes'); setBusy(button, true, 'Checking files');
    const base = byId('patch-compare-base').value;
    try {
      changes = await jsonRequest(`/api/patch-management/systems/${selectedSystemId}/changes${base ? `?baseSnapshotId=${base}` : ''}`);
      const card = byId('patch-changes'); card.classList.remove('d-none');
      byId('patch-changes-title').textContent = 'Changes since ' + (changes.baseCreatedAt ? (changes.baseLabel || 'the selected version') : 'nothing (all files are new)');
      byId('patch-changes-meta').textContent = `Checked ${when(changes.comparedAt)}${changes.baseCreatedAt ? ` · compared with ${when(changes.baseCreatedAt)}` : ''}`;
      mountChanges(byId('patch-changes-body'), changes);
      card.scrollIntoView({ behavior:'smooth', block:'nearest' });
    } catch (error) { notify(error.message, true); }
    finally { setBusy(button, false); }
  };

  // ---------- Details for a patch or a snapshot ----------
  const detailsModal = modal('patchDetailsModal');
  let detailsToken = 0;
  const showDetails = async ({ title, subtitle, facts, changesTitle, changesUrl, actions, wire }) => {
    const token = ++detailsToken;
    byId('patch-details-title').innerHTML = title;
    byId('patch-details-subtitle').textContent = subtitle || '';
    byId('patch-details-facts').innerHTML = facts.filter(([, value]) => value !== null && value !== undefined && value !== '')
      .map(([label, value]) => `<div><dt>${label}</dt><dd>${escapeHtml(String(value))}</dd></div>`).join('');
    byId('patch-details-changes-title').textContent = changesTitle;
    byId('patch-details-changes').innerHTML = '<div class="empty-state compact">Loading changes…</div>';
    byId('patch-details-actions').innerHTML = actions;
    wire(byId('patch-details-actions'));
    detailsModal.show();
    try {
      const result = await jsonRequest(changesUrl);
      if (token === detailsToken) mountChanges(byId('patch-details-changes'), result);
    } catch (error) { if (token === detailsToken) byId('patch-details-changes').innerHTML = `<div class="deploy-error">${escapeHtml(error.message)}</div>`; }
  };

  const openPackageDetails = id => {
    const item = packages.find(candidate => candidate.id === id); if (!item) return;
    showDetails({
      title: `${escapeHtml(item.fileName)}${item.snapshotIsLatest ? ' <span class="deploy-status deploy-status-deployed">Latest version</span>' : ''}`,
      subtitle: item.label || 'No description',
      facts: [['Format', `.${item.format}`], ['Size', bytes(item.sizeBytes)], ['Compiled', when(item.createdAt)], ['By', item.createdBy],
        ['Compared with', item.baseCreatedAt ? when(item.baseCreatedAt) : 'Nothing (all files new)'], ['Added', item.added], ['Changed', item.changed], ['Deleted', item.deleted]],
      changesTitle: 'Files in this patch',
      changesUrl: `/api/patch-management/packages/${item.id}/changes`,
      actions: packageActions(item),
      wire: wirePackageActions
    });
  };

  const openSnapshotDetails = id => {
    const item = snapshots.find(candidate => candidate.id === id); if (!item) return;
    showDetails({
      title: `${escapeHtml(item.label || 'Snapshot')}${item.isLatest ? ' <span class="deploy-status deploy-status-deployed">Latest version</span>' : ''}`,
      subtitle: item.source === 'patch' ? 'Recorded when a patch was compiled' : 'Recorded by “Set current as latest”',
      facts: [['Taken', when(item.createdAt)], ['By', item.createdBy], ['Files', item.fileCount.toLocaleString()], ['Total size', bytes(item.totalBytes)],
        ['Set as latest', item.markedLatestAt ? when(item.markedLatestAt) : null]],
      changesTitle: 'Changes compared with the snapshot before it',
      changesUrl: `/api/patch-management/snapshots/${item.id}/changes`,
      actions: snapshotActions(item),
      wire: wireSnapshotActions
    });
  };

  // ---------- Compile and set latest ----------
  const openCompile = () => {
    const base = byId('patch-compare-base');
    byId('patch-compile-subtitle').textContent = `Changes since: ${base.options[base.selectedIndex]?.text || 'latest version'}`;
    byId('patch-compile-label').value = '';
    compileModal.show();
  };

  const compile = async event => {
    event.preventDefault();
    const button = byId('patch-compile-submit'); setBusy(button, true, 'Compiling');
    const base = byId('patch-compare-base').value;
    try {
      const result = await jsonRequest(`/api/patch-management/systems/${selectedSystemId}/compile`, 'POST', {
        format:document.querySelector('input[name="patch-format"]:checked').value, baseSnapshotId:base ? Number(base) : null, label:byId('patch-compile-label').value || null
      });
      compileModal.hide(); await loadHistory();
      notify(`${result.fileName} compiled: ${result.added} added, ${result.changed} changed, ${result.deleted} deleted.`);
    } catch (error) { notify(error.message, true); }
    finally { setBusy(button, false); }
  };

  const setCurrentLatest = async event => {
    event.preventDefault();
    const button = byId('patch-latest-submit'); setBusy(button, true, 'Taking snapshot');
    try {
      await jsonRequest(`/api/patch-management/systems/${selectedSystemId}/latest`, 'POST', { label:byId('patch-latest-label').value || null });
      latestModal.hide(); changes = null; byId('patch-changes').classList.add('d-none');
      await Promise.all([loadSystems(), loadHistory()]); notify('The current files are now the latest version.');
    } catch (error) { notify(error.message, true); }
    finally { setBusy(button, false); }
  };

  // ---------- Wiring ----------
  byId('patch-system-add').addEventListener('click', () => openSystemModal(null));
  byId('patch-system-edit').addEventListener('click', () => openSystemModal(selectedSystem()));
  byId('patch-system-delete').addEventListener('click', deleteSystem);
  byId('patch-system-back').addEventListener('click', showList);
  byId('patch-system-form').addEventListener('submit', saveSystem);
  byId('patch-modules-manage').addEventListener('click', () => modulesModal.show());
  byId('patch-no-modules-add').addEventListener('click', () => openModuleModal(null));
  byId('patch-module-add').addEventListener('click', () => openModuleModal(null));
  byId('patch-module-form').addEventListener('submit', saveModule);
  byId('patchModuleModal').addEventListener('hidden.bs.modal', () => { if (returnToManager) { returnToManager = false; modulesModal.show(); } });
  byId('patch-view-changes').addEventListener('click', viewChanges);
  byId('patch-compile').addEventListener('click', openCompile);
  byId('patch-compile-form').addEventListener('submit', compile);
  byId('patch-set-latest').addEventListener('click', () => { byId('patch-latest-label').value = ''; latestModal.show(); });
  byId('patch-latest-form').addEventListener('submit', setCurrentLatest);
  loadSystems()
    .then(() => {
      const requested = Number(new URL(location.href).searchParams.get('system'));
      if (requested && systems.some(system => system.id === requested)) showSystem(requested);
      else if (requested) setSystemInUrl(null);
    })
    .catch(error => notify(error.message, true));
})();
