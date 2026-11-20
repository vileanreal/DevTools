(() => {
  const { byId, escapeHtml, notify, jsonRequest, appUrl } = window.devTools;
  const systemModal = bootstrap.Modal.getOrCreateInstance(byId('deploySystemModal'));
  const moduleModal = bootstrap.Modal.getOrCreateInstance(byId('deployModuleModal'));
  const runModal = bootstrap.Modal.getOrCreateInstance(byId('deployRunModal'));
  const rollbackModal = bootstrap.Modal.getOrCreateInstance(byId('deployRollbackModal'));
  const modulesModal = bootstrap.Modal.getOrCreateInstance(byId('deployModulesModal'));
  // Bootstrap can't stack two dialogs, so the module form temporarily replaces the manager and returns to it.
  let returnToManager = false;
  let systems = [], packages = [], selectedSystemId = null, editingSystemId = null, editingModuleId = null;
  let deployPackageId = null, rollbackPackageId = null, job = null, pollTimer = null, upload = null, stagedFile = null;

  const selectedSystem = () => systems.find(item => item.id === selectedSystemId);
  const bytes = value => { const units = ['B','KB','MB','GB','TB']; let size = Number(value) || 0, unit = 0; while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit++; } return `${size.toFixed(unit && size < 10 ? 1 : 0)} ${units[unit]}`; };
  const when = value => value ? new Date(value).toLocaleString() : '';
  const plural = (count, word) => `${count.toLocaleString()} ${word}${count === 1 ? '' : 's'}`;
  const statusLabels = { Uploaded:'Ready', Deploying:'Deploying', Deployed:'Deployed', Failed:'Failed', Restored:'Failed · restored', RollingBack:'Rolling back', RolledBack:'Rolled back', Interrupted:'Interrupted' };
  const eventLabels = { Uploaded:'Uploaded', DeployStarted:'Deployment started', Deployed:'Deployed', DeployFailed:'Deployment failed', Restored:'Deployment failed · restored', RollbackStarted:'Rollback started', RolledBack:'Rolled back', RollbackFailed:'Rollback failed', Interrupted:'Interrupted' };
  const statusClass = status => `deploy-status-${String(status).toLowerCase()}`;
  const busy = () => job && job.status === 'running';

  // ---------- Systems ----------
  const loadSystems = async () => {
    systems = await jsonRequest('/api/deployment/systems');
    renderSystems();
    if (selectedSystemId && !selectedSystem()) showList();
    else if (selectedSystemId) renderDetail();
  };

  const renderSystems = () => {
    const host = byId('deploy-system-list');
    if (!systems.length) { host.innerHTML = '<div class="empty-state">No systems yet. Add one, then define its modules.</div>'; return; }
    host.innerHTML = systems.map(system => `
      <button class="system-row" type="button" data-system-id="${system.id}">
        <span class="system-avatar">${escapeHtml(system.name.trim().charAt(0).toUpperCase() || 'D')}</span>
        <span class="system-row-main"><strong>${escapeHtml(system.name)}</strong><small>${plural(system.modules.length, 'module')}${system.modules.length ? ` · ${escapeHtml(system.modules.map(module => module.name).join(', '))}` : ''}</small></span>
        <span class="system-row-meta">Local machine</span><span class="row-arrow" aria-hidden="true">›</span>
      </button>`).join('');
    host.querySelectorAll('[data-system-id]').forEach(button => button.addEventListener('click', () => showSystem(Number(button.dataset.systemId))));
  };

  // The open system is kept in the address (?system=<id>), so a reload or a shared link reopens it.
  const setSystemInUrl = systemId => {
    const url = new URL(location.href);
    if (systemId) url.searchParams.set('system', String(systemId)); else url.searchParams.delete('system');
    if (url.href !== location.href) history.replaceState(null, '', url);
  };

  const showList = () => {
    selectedSystemId = null; stopPolling(); stopWatching(); job = null;
    setSystemInUrl(null);
    byId('deploy-system-detail').classList.add('d-none'); byId('deploy-systems-view').classList.remove('d-none');
  };

  const showSystem = async systemId => {
    selectedSystemId = systemId; packagePage = 1;
    setSystemInUrl(systemId);
    byId('deploy-systems-view').classList.add('d-none'); byId('deploy-system-detail').classList.remove('d-none');
    renderDetail();
    // Packages first, so the progress panel can name the package of the latest job.
    await loadPackages();
    await loadLatestJob();
    startWatching();
  };

  // While a system is open, look for jobs every few seconds, including ones started from another browser.
  let watchTimer = null;
  const startWatching = () => {
    stopWatching();
    watchTimer = setInterval(async () => {
      if (!selectedSystemId || busy()) return;
      try {
        const latest = await jsonRequest(`/api/deployment/systems/${selectedSystemId}/job`);
        // A job this page hasn't seen: still running (follow it), or one that already finished between checks.
        if (latest && latest.jobId !== job?.jobId && (!job || new Date(latest.startedAt) > new Date(job.startedAt))) {
          job = latest; renderJob(); await loadPackages();
          if (latest.status === 'running') { startPolling(); notify(`${latest.kind === 'rollback' ? 'A rollback' : 'A deployment'} was started for this system.`); }
          else notify(jobTitle(), latest.status !== 'succeeded');
        }
      } catch { /* the next tick retries */ }
    }, 3000);
  };
  const stopWatching = () => { if (watchTimer) clearInterval(watchTimer); watchTimer = null; };

  const renderDetail = () => {
    const system = selectedSystem(); if (!system) return;
    byId('deploy-system-name').textContent = system.name;
    const host = byId('deploy-module-list');
    host.innerHTML = system.modules.length ? system.modules.map(module => {
      const controls = [module.appPoolName && `<span class="deploy-chip">IIS · ${escapeHtml(module.appPoolName)}</span>`, module.serviceName && `<span class="deploy-chip">Service · ${escapeHtml(module.serviceName)}</span>`].filter(Boolean).join('') || '<span class="deploy-chip muted">No app pool or service</span>';
      const exclusions = module.defaultExclusions ? module.defaultExclusions.split('\n').length : 0;
      return `<article class="deploy-module-row" data-module-id="${module.id}">
        <span class="deploy-module-icon" aria-hidden="true">${module.appPoolName ? 'IIS' : module.serviceName ? '⚙' : '▣'}</span>
        <span class="deploy-module-main"><strong>${escapeHtml(module.name)}</strong><code title="${escapeHtml(module.targetPath)}">${escapeHtml(module.targetPath)}</code><span class="deploy-module-chips">${controls}${exclusions ? `<span class="deploy-chip muted" title="${escapeHtml(module.defaultExclusions)}">${plural(exclusions, 'exclusion')}</span>` : ''}</span></span>
        <span class="deploy-row-actions"><button class="btn btn-sm btn-ghost module-edit" type="button">Edit</button><button class="icon-button module-delete" type="button" title="Remove module" aria-label="Remove ${escapeHtml(module.name)}">×</button></span>
      </article>`;
    }).join('') : '<div class="empty-state compact">No modules yet. Add one for each folder in your packages.</div>';
    host.querySelectorAll('.module-edit').forEach(button => button.addEventListener('click', () => openModuleModal(Number(button.closest('[data-module-id]').dataset.moduleId))));
    host.querySelectorAll('.module-delete').forEach(button => button.addEventListener('click', () => deleteModule(Number(button.closest('[data-module-id]').dataset.moduleId))));
    byId('deploy-dropzone').classList.toggle('disabled', !system.modules.length || Boolean(upload));
    byId('deploy-modules-count').textContent = system.modules.length;
    byId('deploy-no-modules').classList.toggle('d-none', system.modules.length > 0);
  };

  const openSystemModal = system => {
    editingSystemId = system?.id ?? null;
    byId('deploy-system-name-input').value = system?.name ?? '';
    byId('deploy-system-backup-input').value = system?.backupDirectory ?? '';
    byId('deploy-system-package-input').value = system?.packageDirectory ?? '';
    byId('deploy-system-modal-title').textContent = system ? 'Edit system' : 'Add system';
    byId('deploy-system-submit').textContent = system ? 'Save changes' : 'Add system';
    systemModal.show();
  };

  const saveSystem = async event => {
    event.preventDefault();
    const body = { name:byId('deploy-system-name-input').value, backupDirectory:byId('deploy-system-backup-input').value || null, packageDirectory:byId('deploy-system-package-input').value || null };
    try {
      const saved = await jsonRequest(editingSystemId ? `/api/deployment/systems/${editingSystemId}` : '/api/deployment/systems', editingSystemId ? 'PUT' : 'POST', body);
      systemModal.hide(); await loadSystems(); notify(editingSystemId ? 'System updated.' : 'System added.');
      if (!editingSystemId) showSystem(saved.id);
    } catch (error) { notify(error.message, true); }
  };

  const deleteSystem = async () => {
    const system = selectedSystem();
    if (!system || !confirm(`Delete ${system.name}, its modules and its package history? Backup files on disk are kept.`)) return;
    try { await jsonRequest(`/api/deployment/systems/${system.id}`, 'DELETE'); showList(); await loadSystems(); notify('System deleted.'); }
    catch (error) { notify(error.message, true); }
  };

  // ---------- Modules ----------
  const openModuleModal = moduleId => {
    returnToManager = byId('deployModulesModal').classList.contains('show');
    if (returnToManager) modulesModal.hide();
    const module = selectedSystem()?.modules.find(item => item.id === moduleId);
    editingModuleId = module?.id ?? null;
    byId('deploy-module-name').value = module?.name ?? '';
    byId('deploy-module-target').value = module?.targetPath ?? '';
    byId('deploy-module-apppool').value = module?.appPoolName ?? '';
    byId('deploy-module-service').value = module?.serviceName ?? '';
    byId('deploy-module-exclusions').value = module?.defaultExclusions ?? '';
    byId('deploy-module-modal-title').textContent = module ? 'Edit module' : 'Add module';
    byId('deploy-module-submit').textContent = module ? 'Save changes' : 'Add module';
    moduleModal.show();
  };

  const saveModule = async event => {
    event.preventDefault();
    const body = { name:byId('deploy-module-name').value, targetPath:byId('deploy-module-target').value, appPoolName:byId('deploy-module-apppool').value || null, serviceName:byId('deploy-module-service').value || null, defaultExclusions:byId('deploy-module-exclusions').value || null };
    try {
      await jsonRequest(editingModuleId ? `/api/deployment/systems/${selectedSystemId}/modules/${editingModuleId}` : `/api/deployment/systems/${selectedSystemId}/modules`, editingModuleId ? 'PUT' : 'POST', body);
      moduleModal.hide(); await loadSystems(); notify(editingModuleId ? 'Module updated.' : 'Module added.');
    } catch (error) { notify(error.message, true); }
  };

  const deleteModule = async moduleId => {
    const module = selectedSystem()?.modules.find(item => item.id === moduleId);
    if (!module || !confirm(`Remove module ${module.name}? Its target folder is not touched.`)) return;
    try { await jsonRequest(`/api/deployment/modules/${moduleId}`, 'DELETE'); await loadSystems(); notify('Module removed.'); }
    catch (error) { notify(error.message, true); }
  };

  // ---------- Upload (streamed, with progress) ----------
  // Choosing or dropping a file only stages it; the upload starts once a description is entered.
  const stageFile = file => {
    const system = selectedSystem();
    if (!file || !system || upload) return;
    if (!/\.(zip|7z)$/i.test(file.name)) return notify('Upload a .zip or .7z package.', true);
    if (!system.modules.length) return notify('Add the system\'s modules before uploading a package.', true);
    stagedFile = file;
    byId('deploy-staged-name').textContent = file.name;
    byId('deploy-staged-size').textContent = bytes(file.size);
    byId('deploy-staged-icon').textContent = /\.7z$/i.test(file.name) ? '7z' : 'zip';
    byId('deploy-upload-form').classList.remove('d-none');
    byId('deploy-dropzone').classList.add('d-none');
    updateUploadForm();
    byId('deploy-upload-description').focus();
  };

  const clearStaged = () => {
    stagedFile = null; byId('deploy-file').value = '';
    byId('deploy-upload-form').classList.add('d-none');
    byId('deploy-dropzone').classList.remove('d-none');
  };

  const updateUploadForm = () => {
    const description = byId('deploy-upload-description').value.trim();
    byId('deploy-upload-counter').textContent = `${byId('deploy-upload-description').value.length} / 500`;
    byId('deploy-upload-submit').disabled = !stagedFile || !description || Boolean(upload);
  };

  const uploadConfirmModal = bootstrap.Modal.getOrCreateInstance(byId('deployUploadConfirmModal'));
  const startUpload = (confirmed = false) => {
    const system = selectedSystem(), file = stagedFile, description = byId('deploy-upload-description').value.trim();
    if (!file || !system || upload) return;
    if (!description) { byId('deploy-upload-description').focus(); return notify('Enter a package description before uploading.', true); }
    const pending = packages.filter(item => item.status === 'Uploaded');
    if (pending.length && !confirmed) {
      byId('deploy-upload-confirm-text').textContent = `${system.name} already has ${plural(pending.length, 'package')} that ${pending.length === 1 ? 'hasn\'t' : 'haven\'t'} been deployed yet:`;
      byId('deploy-upload-confirm-list').innerHTML = pending.slice(0, 8).map(item => `<li><strong>${escapeHtml(item.fileName)}</strong><span>${escapeHtml(item.description || 'No description')} · uploaded ${when(item.uploadedAt)}</span></li>`).join('')
        + (pending.length > 8 ? `<li><span>…and ${pending.length - 8} more</span></li>` : '');
      uploadConfirmModal.show();
      return;
    }
    const xhr = new XMLHttpRequest(), started = performance.now(), form = new FormData();
    // The description must come first: the server reads it, then streams the file straight to disk.
    form.append('description', description);
    form.append('package', file, file.name);
    upload = { xhr, file };
    byId('deploy-upload-form').classList.add('d-none');
    byId('deploy-upload-progress').classList.remove('d-none');
    byId('deploy-upload-name').textContent = file.name;
    byId('deploy-upload-cancel').disabled = false;
    setUploadProgress(0, file.size, 0, 'Uploading…');
    renderDetail();
    xhr.upload.addEventListener('progress', event => {
      if (!event.lengthComputable) return;
      const seconds = Math.max((performance.now() - started) / 1000, 0.001);
      setUploadProgress(event.loaded, event.total, event.loaded / seconds, event.loaded >= event.total ? 'Checking package folders…' : 'Uploading…');
    });
    xhr.addEventListener('load', async () => {
      if (xhr.status === 401) { location.assign(`${appUrl('account/login')}?returnUrl=${encodeURIComponent(location.pathname)}`); return; }
      let result = null; try { result = JSON.parse(xhr.responseText); } catch { /* not JSON */ }
      const succeeded = xhr.status >= 200 && xhr.status < 300 && result?.success;
      finishUpload(succeeded);
      if (succeeded) {
        notify(`${file.name} uploaded. Matched ${plural(result.data.analysis.modules.length, 'module')}.`);
        await loadPackages();
      } else notify(result?.error || (xhr.status === 413 ? 'The package is larger than the server allows.' : 'The upload failed.'), true);
    });
    xhr.addEventListener('error', () => { finishUpload(false); notify('The upload failed. Check the connection and try again.', true); });
    xhr.addEventListener('abort', () => { finishUpload(false); notify('Upload cancelled.'); });
    xhr.open('POST', appUrl(`/api/deployment/systems/${system.id}/packages`));
    xhr.send(form);
  };

  const setUploadProgress = (loaded, total, speed, phase) => {
    const percent = total ? Math.min(100, Math.round(loaded / total * 100)) : 0;
    byId('deploy-upload-bar').style.width = `${percent}%`;
    byId('deploy-upload-bar').closest('.progress').setAttribute('aria-valuenow', String(percent));
    byId('deploy-upload-bar').classList.toggle('progress-bar-striped', percent >= 100);
    byId('deploy-upload-bar').classList.toggle('progress-bar-animated', percent >= 100);
    byId('deploy-upload-stats').textContent = `${percent}% · ${bytes(loaded)} of ${bytes(total)}${speed ? ` · ${bytes(speed)}/s` : ''}`;
    byId('deploy-upload-phase').textContent = phase;
    byId('deploy-upload-cancel').disabled = percent >= 100;
  };

  // After a failed or cancelled upload the file and description stay staged, so it can be retried.
  const finishUpload = succeeded => {
    upload = null;
    byId('deploy-upload-progress').classList.add('d-none');
    if (succeeded) { byId('deploy-upload-description').value = ''; clearStaged(); }
    else byId('deploy-upload-form').classList.remove('d-none');
    updateUploadForm();
    renderDetail();
  };

  // ---------- Packages ----------
  const loadPackages = async () => {
    if (!selectedSystemId) return;
    packages = await jsonRequest(`/api/deployment/systems/${selectedSystemId}/packages`);
    renderPackages();
  };

  // Actions follow the package's state. A deployed package is live, so Rollback is its only action. Any deployed package
  // can be rolled back while its backup exists; overlaps with newer deployments are shown as a warning before confirming.
  const packageActions = item => {
    if (item.status === 'Deployed') {
      return `<button class="btn btn-sm btn-danger package-rollback" type="button" data-package-id="${item.id}" ${item.canRollback && !busy() ? '' : 'disabled'} title="${escapeHtml(item.canRollback ? (item.rollbackWarning ? 'Restore the version before this deployment (newer deployments of the same modules will be overwritten)' : 'Restore the version that was in place before this deployment') : item.rollbackBlockedReason || 'This deployment can’t be rolled back right now')}">Rollback</button>`;
    }
    return `<button class="btn btn-sm btn-primary package-deploy" type="button" data-package-id="${item.id}" ${item.canDeploy && !busy() ? '' : 'disabled'}>Deploy</button>
      ${item.canRollback ? `<button class="btn btn-sm btn-danger package-rollback" type="button" data-package-id="${item.id}" ${busy() ? 'disabled' : ''}>Rollback</button>` : ''}
      <button class="icon-button package-delete" type="button" data-package-id="${item.id}" title="Delete uploaded package" aria-label="Delete ${escapeHtml(item.fileName)}" ${busy() ? 'disabled' : ''}>×</button>`;
  };

  const wireActions = host => {
    host.querySelectorAll('.package-deploy').forEach(button => button.addEventListener('click', event => { event.stopPropagation(); packageModal.hide(); openDeploy(Number(button.dataset.packageId)); }));
    host.querySelectorAll('.package-rollback').forEach(button => button.addEventListener('click', event => { event.stopPropagation(); packageModal.hide(); openRollback(Number(button.dataset.packageId)); }));
    host.querySelectorAll('.package-delete').forEach(button => button.addEventListener('click', event => { event.stopPropagation(); packageModal.hide(); deletePackage(Number(button.dataset.packageId)); }));
  };

  const latestEvent = (item, kinds) => [...item.events].reverse().find(event => kinds.includes(event.kind));
  const packageDates = item => {
    const deployed = latestEvent(item, ['Deployed']), rolledBack = latestEvent(item, ['RolledBack']);
    return [`Uploaded ${when(item.uploadedAt)}`, deployed && `Last deployed ${when(deployed.at)}`, rolledBack && `Last rolled back ${when(rolledBack.at)}`].filter(Boolean).join(' · ');
  };

  // Packages are paged on the client: the list for one system is small, and paging keeps the page short.
  let packagePage = 1, packagePageSize = 10;
  const renderPackages = () => {
    const host = byId('deploy-package-list'), pager = byId('deploy-package-pager');
    if (!packages.length) { host.innerHTML = '<div class="empty-state">No packages uploaded yet.</div>'; pager.classList.add('d-none'); return; }
    const pages = Math.max(1, Math.ceil(packages.length / packagePageSize));
    packagePage = Math.min(Math.max(1, packagePage), pages);
    const start = (packagePage - 1) * packagePageSize, visible = packages.slice(start, start + packagePageSize);
    host.innerHTML = visible.map(item => {
      const modules = item.analysis.modules.map(module => module.moduleName).join(', ');
      return `<article class="deploy-package-row compact" data-package-id="${item.id}" tabindex="0" role="button" aria-label="View ${escapeHtml(item.fileName)} details">
        <span class="deploy-package-icon" aria-hidden="true">${/\.7z$/i.test(item.fileName) ? '7z' : 'zip'}</span>
        <span class="deploy-package-main">
          <span class="deploy-package-line">
            <strong title="${escapeHtml(item.fileName)}">${escapeHtml(item.fileName)}</strong>
            <span class="deploy-status ${statusClass(item.status)}">${escapeHtml(statusLabels[item.status] || item.status)}</span>
            <span class="deploy-package-description${item.description ? '' : ' muted'}" title="${escapeHtml(item.description || '')}">${escapeHtml(item.description || 'No description')}</span>
          </span>
          <small class="deploy-package-meta">${bytes(item.sizeBytes)} · ${packageDates(item)}${modules ? ` · ${escapeHtml(modules)}` : ''}</small>
          ${item.error ? `<small class="deploy-package-error" title="${escapeHtml(item.error)}">${escapeHtml(item.error)}</small>` : ''}
        </span>
        <span class="deploy-row-actions">${packageActions(item)}</span>
      </article>`;
    }).join('');
    host.querySelectorAll('.deploy-package-row').forEach(row => {
      const open = () => openPackage(Number(row.dataset.packageId));
      row.addEventListener('click', event => { if (!event.target.closest('button, a, input, summary')) open(); });
      row.addEventListener('keydown', event => { if ((event.key === 'Enter' || event.key === ' ') && event.target === row) { event.preventDefault(); open(); } });
    });
    wireActions(host);

    const showPager = packages.length > 10;
    pager.classList.toggle('d-none', !showPager);
    if (!showPager) return;
    const pageButtons = Array.from({ length:pages }, (_, index) => index + 1)
      .filter(page => page === 1 || page === pages || Math.abs(page - packagePage) <= 1)
      .reduce((list, page, index, all) => { if (index && page - all[index - 1] > 1) list.push('…'); list.push(page); return list; }, []);
    pager.innerHTML = `<span class="deploy-pager-info">${(start + 1).toLocaleString()}–${Math.min(start + packagePageSize, packages.length).toLocaleString()} of ${packages.length.toLocaleString()}</span>
      <span class="deploy-pager-pages">
        <button class="btn btn-sm btn-ghost" type="button" data-page="${packagePage - 1}" ${packagePage === 1 ? 'disabled' : ''} aria-label="Previous page">‹</button>
        ${pageButtons.map(page => page === '…' ? '<span class="deploy-pager-gap">…</span>' : `<button class="btn btn-sm ${page === packagePage ? 'btn-primary' : 'btn-ghost'}" type="button" data-page="${page}" ${page === packagePage ? 'aria-current="page"' : ''}>${page}</button>`).join('')}
        <button class="btn btn-sm btn-ghost" type="button" data-page="${packagePage + 1}" ${packagePage === pages ? 'disabled' : ''} aria-label="Next page">›</button>
      </span>
      <label class="deploy-pager-size">Rows <select class="form-select form-select-sm">${[10, 25, 50].map(size => `<option ${size === packagePageSize ? 'selected' : ''}>${size}</option>`).join('')}</select></label>`;
    pager.querySelectorAll('[data-page]').forEach(button => button.addEventListener('click', () => { packagePage = Number(button.dataset.page); renderPackages(); }));
    pager.querySelector('select').addEventListener('change', event => { packagePageSize = Number(event.target.value); packagePage = 1; renderPackages(); });
  };

  // ---------- File trees (package details and deploy preview) ----------
  // Files are grouped by top-level folder (bin/, wwwroot/ …) that can be collapsed, and at most TREE_ROWS files are listed
  // until "Show more" is pressed. A list with more files than that starts with its folders collapsed. The state lives on
  // the host element, so re-rendering after a filter change keeps what the user expanded.
  const TREE_ROWS = 15;
  const treeState = new WeakMap();
  const folderOf = path => { const index = path.search(/[\\/]/); return index < 0 ? '' : path.slice(0, index); };
  const renderFileTree = (host, files, { tag = null, reset = false, expandAll = false, emptyText = 'No files match this view.' } = {}) => {
    let state = treeState.get(host);
    if (!state || reset) {
      state = { collapsed:new Set(), limit:TREE_ROWS };
      const folders = new Set(files.map(file => folderOf(file.path)));
      if (files.length > TREE_ROWS && folders.size > 1) folders.forEach(folder => state.collapsed.add(folder));
      treeState.set(host, state);
    }
    if (!files.length) { host.innerHTML = `<p class="deploy-preview-note">${escapeHtml(emptyText)}</p>`; return; }
    const columns = tag ? 3 : 2;
    const groups = new Map();
    files.forEach(file => { const folder = folderOf(file.path); if (!groups.has(folder)) groups.set(folder, []); groups.get(folder).push(file); });
    const ordered = [...groups.entries()].sort(([a], [b]) => (a === '') - (b === '') || a.localeCompare(b, undefined, { sensitivity:'base' }));
    const rows = [];
    let shown = 0, hidden = 0;
    for (const [folder, folderFiles] of ordered) {
      const isCollapsed = !expandAll && state.collapsed.has(folder), starved = !isCollapsed && shown >= state.limit;
      rows.push(`<tr class="patch-folder-row ${isCollapsed ? 'collapsed' : ''}"><td colspan="${columns}">
        <button type="button" class="patch-folder-toggle" data-folder="${escapeHtml(folder)}" aria-expanded="${!isCollapsed}">
          <span class="patch-folder-caret" aria-hidden="true">▶</span><span class="patch-folder-name">${folder ? `${escapeHtml(folder)}/` : '(module root)'}</span>
          <span class="patch-folder-count">${plural(folderFiles.length, 'file')} · ${bytes(folderFiles.reduce((total, file) => total + (file.size || 0), 0))}${starved ? ' · use Show more below' : ''}</span>
        </button></td></tr>`);
      if (isCollapsed) continue;
      const visible = folderFiles.slice(0, Math.max(0, state.limit - shown));
      shown += visible.length; hidden += folderFiles.length - visible.length;
      visible.forEach(file => {
        const rest = folder ? file.path.slice(folder.length + 1) : file.path;
        rows.push(`<tr class="patch-change-row deploy-tree-row ${file.action ? `action-${file.action}` : ''}">${tag ? `<td class="patch-col-kind">${tag(file)}</td>` : ''}<td><code title="${escapeHtml(file.path)}">${folder ? `<span class="patch-path-prefix">${escapeHtml(folder)}/</span>` : ''}${escapeHtml(rest)}</code></td><td class="patch-col-size">${bytes(file.size)}</td></tr>`);
      });
    }
    if (hidden > 0) rows.push(`<tr class="patch-more-row"><td colspan="${columns}"><span class="patch-more">
        <button type="button" class="btn btn-sm btn-soft-primary" data-tree="more">Show ${Math.min(TREE_ROWS, hidden)} more</button>
        ${hidden > TREE_ROWS ? `<button type="button" class="btn btn-sm btn-ghost" data-tree="all">Show all ${hidden.toLocaleString()}</button>` : ''}
        <small>${plural(hidden, 'more file')}</small></span></td></tr>`);
    else if (state.limit > TREE_ROWS) rows.push(`<tr class="patch-more-row"><td colspan="${columns}"><span class="patch-more"><button type="button" class="btn btn-sm btn-ghost" data-tree="less">Show less</button></span></td></tr>`);
    host.innerHTML = `<div class="patch-table-wrap deploy-tree-wrap"><table class="patch-table patch-changes-table deploy-tree-table"><tbody>${rows.join('')}</tbody></table></div>`;
    const again = () => renderFileTree(host, files, { tag, expandAll, emptyText });
    host.querySelectorAll('[data-folder]').forEach(button => button.addEventListener('click', () => {
      const folder = button.dataset.folder;
      if (state.collapsed.has(folder)) state.collapsed.delete(folder); else state.collapsed.add(folder);
      again();
    }));
    host.querySelectorAll('[data-tree]').forEach(button => button.addEventListener('click', () => {
      const action = button.dataset.tree;
      state.limit = action === 'more' ? state.limit + TREE_ROWS : action === 'all' ? Infinity : TREE_ROWS;
      again();
    }));
  };

  // ---------- Package details ----------
  const packageModal = bootstrap.Modal.getOrCreateInstance(byId('deployPackageModal'));
  let openPackageId = null;
  const openPackage = async packageId => {
    const item = packages.find(candidate => candidate.id === packageId); if (!item) return;
    openPackageId = packageId;
    renderPackageDetails(item);
    packageModal.show();
    const filesHost = byId('deploy-package-files');
    try {
      const contents = await jsonRequest(`/api/deployment/packages/${packageId}/contents`);
      if (openPackageId !== packageId) return;
      filesHost.innerHTML = contents.modules.map(module => `<section class="deploy-details-module">
          <header><strong>${escapeHtml(module.moduleName || module.folderName)}</strong><span>${plural(module.files.length, 'file')} · ${bytes(module.files.reduce((total, file) => total + file.size, 0))}${module.truncated ? ' · first files only' : ''}</span></header>
          <div class="deploy-module-tree"></div>
        </section>`).join('') || '<div class="empty-state compact">No module files.</div>';
      const trees = [...filesHost.querySelectorAll('.deploy-module-tree')];
      trees.forEach((tree, index) => renderFileTree(tree, contents.modules[index].files, { reset:true }));
      // Searching lists every matching file with its folders expanded; clearing the search restores the folder view.
      const filter = byId('deploy-package-filter');
      filter.oninput = () => {
        const text = filter.value.trim().toLowerCase();
        trees.forEach((tree, index) => renderFileTree(tree, contents.modules[index].files.filter(file => !text || file.path.toLowerCase().includes(text)), { expandAll:Boolean(text), emptyText:'No files match the search.' }));
      };
    } catch (error) { if (openPackageId === packageId) filesHost.innerHTML = `<div class="deploy-error">${escapeHtml(error.message)}</div>`; }
  };

  // How the package was read (skipped wrapper folder, folders and root files with no module) belongs to the
  // upload, so it is shown on the Uploaded history entry instead of on the package itself.
  const uploadNotes = item => [
    item.analysis.wrapperFolder && `wrapper folder “${item.analysis.wrapperFolder}” skipped`,
    item.analysis.unmatchedFolders.length && `ignored folders with no matching module: ${item.analysis.unmatchedFolders.join(', ')}`,
    item.analysis.ignoredRootFiles && `${plural(item.analysis.ignoredRootFiles, 'file')} at the package root ignored`
  ].filter(Boolean);
  // History details are stored one fact per line ("By: …", "Duration: …", per-module results).
  const eventLines = (item, event) => {
    const lines = String(event.detail || '').split('\n').map(line => line.trim()).filter(Boolean);
    if (event.kind === 'Uploaded') lines.push(...uploadNotes(item).map(note => note.charAt(0).toUpperCase() + note.slice(1)));
    return lines;
  };
  const renderEventLines = lines => lines.length ? `<ul class="deploy-event-lines">${lines.map(line => {
    const match = /^(By|Duration|Failed at|Error|Backup|Auto-restore|File):\s*(.*)$/.exec(line);
    return `<li class="${match?.[1] === 'Error' || match?.[1] === 'Failed at' ? 'is-error' : ''}">${match ? `<b>${escapeHtml(match[1])}:</b> ${escapeHtml(match[2])}` : escapeHtml(line)}</li>`;
  }).join('')}</ul>` : '';

  const renderPackageDetails = item => {
    const analysis = item.analysis, system = selectedSystem();
    byId('deploy-package-title').innerHTML = `${escapeHtml(item.fileName)} <span class="deploy-status ${statusClass(item.status)}">${escapeHtml(statusLabels[item.status] || item.status)}</span>`;
    byId('deploy-package-subtitle').textContent = item.description || 'No description';
    const deployed = latestEvent(item, ['Deployed']), rolledBack = latestEvent(item, ['RolledBack']);
    const facts = [
      ['System', system?.name], ['Size', bytes(item.sizeBytes)], ['Uploaded', when(item.uploadedAt)],
      ['Last deployed', deployed && when(deployed.at)], ['Last rolled back', rolledBack && when(rolledBack.at)],
      ['Backup', item.backupFileName]
    ].filter(([, value]) => value);
    byId('deploy-package-body').innerHTML = `
      ${item.error ? `<div class="deploy-error mb-3">${escapeHtml(item.error)}</div>` : ''}
      <dl class="deploy-facts">${facts.map(([label, value]) => `<div><dt>${label}</dt><dd>${escapeHtml(value)}</dd></div>`).join('')}</dl>
      <div class="deploy-details-grid">
        <section>
          <div class="deploy-details-section-head"><h6>Files</h6><input id="deploy-package-filter" class="form-control form-control-sm" placeholder="Filter files…" aria-label="Filter files"></div>
          <div id="deploy-package-files"><div class="empty-state compact">Loading files…</div></div>
        </section>
        <section>
          <div class="deploy-details-section-head"><h6>History</h6></div>
          <ol class="deploy-history-list">${[...item.events].reverse().map(event => `<li class="deploy-event deploy-event-${event.kind.toLowerCase()}"><div class="deploy-event-head"><strong>${escapeHtml(eventLabels[event.kind] || event.kind)}</strong><time datetime="${escapeHtml(event.at)}">${when(event.at)}</time></div>${renderEventLines(eventLines(item, event))}</li>`).join('')}</ol>
        </section>
      </div>`;
    const actions = byId('deploy-package-actions');
    actions.innerHTML = packageActions(item);
    wireActions(actions);
  };

  const deletePackage = async packageId => {
    const item = packages.find(candidate => candidate.id === packageId);
    if (!item || !confirm(`Delete ${item.fileName} and its history entry? Backup files on disk are kept.`)) return;
    try { await jsonRequest(`/api/deployment/packages/${packageId}`, 'DELETE'); await loadPackages(); notify('Package deleted.'); }
    catch (error) { notify(error.message, true); }
  };

  // ---------- Deploy ----------
  // The update type is never preselected: every included module needs an explicit choice before Deploy is enabled.
  const openDeploy = packageId => {
    const item = packages.find(candidate => candidate.id === packageId), system = selectedSystem();
    if (!item || !system) return;
    deployPackageId = packageId;
    byId('deploy-run-subtitle').textContent = `${item.fileName}${item.description ? ` — ${item.description}` : ''} → ${system.name}`;
    byId('deploy-run-modules').innerHTML = item.analysis.modules.map(found => {
      const module = system.modules.find(candidate => candidate.id === found.moduleId);
      if (!module) return '';
      return `<section class="deploy-run-module needs-mode" data-module-id="${module.id}">
        <header>
          <label class="form-check"><input class="form-check-input run-include" type="checkbox" checked> <span class="form-check-label"><strong>${escapeHtml(module.name)}</strong><small>${plural(found.fileCount, 'file')} · ${bytes(found.totalBytes)} → <code>${escapeHtml(module.targetPath)}</code></small></span></label>
          <div class="dc-segmented-modes" role="radiogroup" aria-label="Update type for ${escapeHtml(module.name)}" aria-required="true">
            <label><input type="radio" name="mode-${module.id}" value="partial"><span><strong>Partial update</strong><small>Replace and add files from the package</small></span></label>
            <label><input type="radio" name="mode-${module.id}" value="full"><span><strong>Whole package</strong><small>Delete everything except excluded files, then copy</small></span></label>
          </div>
        </header>
        <p class="deploy-mode-required">Choose Partial update or Whole package.</p>
        <label class="field-label" for="run-exclusions-${module.id}">Files to exclude when replacing</label>
        <textarea id="run-exclusions-${module.id}" class="form-control code-input run-exclusions" rows="2" maxlength="4000" spellcheck="false" placeholder="web.config&#10;logs/">${escapeHtml(module.defaultExclusions || '')}</textarea>
        <p class="deploy-full-warning d-none">Whole package deletes every file in this folder that isn't excluded, and backs up the entire folder first.</p>
        <details class="deploy-preview"><summary>Show files</summary><div class="deploy-preview-body"><div class="empty-state compact">Loading…</div></div></details>
      </section>`;
    }).join('');
    byId('deploy-run-modules').querySelectorAll('.deploy-run-module').forEach(section => {
      const preview = section.querySelector('.deploy-preview');
      let timer = null;
      const refreshPreview = () => { if (!preview.open) return; clearTimeout(timer); timer = setTimeout(() => loadPreview(section), 250); };
      const sync = () => {
        const mode = section.querySelector('input[type=radio]:checked')?.value, included = section.querySelector('.run-include').checked;
        section.classList.toggle('excluded', !included);
        section.classList.toggle('needs-mode', included && !mode);
        section.querySelector('.deploy-full-warning').classList.toggle('d-none', mode !== 'full' || !included);
        updateRunSummary();
      };
      section.querySelectorAll('input').forEach(input => input.addEventListener('change', () => { sync(); refreshPreview(); }));
      section.querySelector('.run-exclusions').addEventListener('input', refreshPreview);
      preview.addEventListener('toggle', () => { if (preview.open) loadPreview(section); });
      sync();
    });
    byId('deploy-run-autorestore').checked = true;
    updateRunSummary();
    runModal.show();
  };

  const previewLabels = { new:'New', replace:'Replace', excluded:'Excluded', delete:'Delete', keep:'Kept' };
  const loadPreview = async section => {
    const body = section.querySelector('.deploy-preview-body');
    const request = { moduleId:Number(section.dataset.moduleId), mode:section.querySelector('input[type=radio]:checked')?.value || null, exclusions:section.querySelector('.run-exclusions').value || null };
    const key = JSON.stringify(request);
    section.dataset.previewKey = key;
    body.innerHTML = '<div class="empty-state compact">Loading…</div>';
    try {
      const result = await jsonRequest(`/api/deployment/packages/${deployPackageId}/preview`, 'POST', request);
      if (section.dataset.previewKey !== key) return;
      const counts = [['new', result.newCount], ['replace', result.replaceCount], ['excluded', result.excludedCount], ['delete', result.deleteCount], ['keep', result.keepCount]].filter(([, count]) => count);
      body.innerHTML = `
        <div class="deploy-preview-toolbar">
          <label class="form-check form-switch"><input class="form-check-input preview-patch-only" type="checkbox" ${section.dataset.patchOnly === 'false' ? '' : 'checked'}> <span class="form-check-label">Patch files only</span></label>
          <div class="deploy-preview-counts">${counts.map(([action, count]) => `<button type="button" class="deploy-file-tag action-${action}" data-filter="${action}">${previewLabels[action]} ${count.toLocaleString()}</button>`).join('')}<button type="button" class="deploy-file-tag active" data-filter="">All</button></div>
        </div>
        ${request.mode ? '' : '<p class="deploy-preview-note">Choose an update type to also see which existing files a whole-package update would delete.</p>'}
        <div class="deploy-preview-tree"></div>
        ${result.truncated ? `<p class="deploy-preview-note">Showing the first ${result.files.length.toLocaleString()} files.</p>` : ''}`;
      // "Patch files only" (the default) shows just the files that will be copied: new and replaced.
      const patchOnly = body.querySelector('.preview-patch-only');
      let filter = '';
      const tree = body.querySelector('.deploy-preview-tree');
      const tag = file => `<span class="deploy-file-tag action-${file.action}">${previewLabels[file.action]}</span>`;
      const apply = () => {
        section.dataset.patchOnly = String(patchOnly.checked);
        const files = result.files.filter(file => (!patchOnly.checked || ['new', 'replace'].includes(file.action)) && (!filter || file.action === filter));
        // A new view (patch-only switch or a different filter) starts again from the folder overview.
        renderFileTree(tree, files, { tag, reset:true });
        body.querySelectorAll('[data-filter]').forEach(button => button.classList.toggle('active', button.dataset.filter === filter));
      };
      patchOnly.addEventListener('change', apply);
      body.querySelectorAll('[data-filter]').forEach(button => button.addEventListener('click', () => {
        filter = button.dataset.filter;
        if (filter && !['new', 'replace'].includes(filter)) patchOnly.checked = false;
        apply();
      }));
      apply();
    } catch (error) { if (section.dataset.previewKey === key) body.innerHTML = `<div class="deploy-error">${escapeHtml(error.message)}</div>`; }
  };

  const runSelections = () => [...byId('deploy-run-modules').querySelectorAll('.deploy-run-module')]
    .filter(section => section.querySelector('.run-include').checked)
    .map(section => ({ moduleId:Number(section.dataset.moduleId), name:section.querySelector('strong').textContent, mode:section.querySelector('input[type=radio]:checked')?.value || null, exclusions:section.querySelector('.run-exclusions').value || null }));

  const updateRunSummary = () => {
    const selections = runSelections(), missing = selections.filter(item => !item.mode), full = selections.filter(item => item.mode === 'full').length;
    // Only shown while something blocks Deploy: no module selected, or a module without an update type.
    const message = !selections.length ? 'Select at least one module'
      : missing.length ? `Choose the update type for ${missing.map(item => item.name).join(', ')}` : '';
    byId('deploy-run-summary-text').textContent = message;
    byId('deploy-run-summary').classList.toggle('d-none', !message);
    byId('deploy-run-submit').title = full ? `${plural(full, 'module')} will be replaced as a whole package` : '';
    byId('deploy-run-submit').disabled = !selections.length || missing.length > 0;
  };

  const submitDeploy = async event => {
    event.preventDefault();
    const button = byId('deploy-run-submit'); button.disabled = true;
    try {
      const modules = runSelections().map(({ moduleId, mode, exclusions }) => ({ moduleId, mode, exclusions }));
      job = await jsonRequest(`/api/deployment/packages/${deployPackageId}/deploy`, 'POST', { modules, autoRestore:byId('deploy-run-autorestore').checked });
      runModal.hide(); renderJob(); startPolling(); await loadPackages();
      byId('deploy-job').scrollIntoView({ behavior:'smooth', block:'start' });
    } catch (error) { notify(error.message, true); updateRunSummary(); }
  };

  // ---------- Rollback ----------
  const openRollback = packageId => {
    const item = packages.find(candidate => candidate.id === packageId); if (!item) return;
    rollbackPackageId = packageId;
    byId('deploy-rollback-text').textContent = `Restore ${selectedSystem()?.name} to how it was before ${item.fileName} was deployed${item.deployedAt ? ` on ${when(item.deployedAt)}` : ''}, using ${item.backupFileName}.`;
    // Rolling back an older deployment is allowed, but newer deployments of the same modules get overwritten.
    byId('deploy-rollback-warning').textContent = item.rollbackWarning || '';
    byId('deploy-rollback-warning').classList.toggle('d-none', !item.rollbackWarning);
    byId('deploy-rollback-confirm').value = ''; byId('deploy-rollback-submit').disabled = true;
    rollbackModal.show();
  };

  const submitRollback = async event => {
    event.preventDefault();
    byId('deploy-rollback-submit').disabled = true;
    try {
      job = await jsonRequest(`/api/deployment/packages/${rollbackPackageId}/rollback`, 'POST');
      rollbackModal.hide(); renderJob(); startPolling(); await loadPackages();
      byId('deploy-job').scrollIntoView({ behavior:'smooth', block:'start' });
    } catch (error) { notify(error.message, true); byId('deploy-rollback-submit').disabled = false; }
  };

  // ---------- Job progress (shared background job; leaving the page does not stop it) ----------
  const loadLatestJob = async () => {
    job = await jsonRequest(`/api/deployment/systems/${selectedSystemId}/job`);
    renderJob();
    if (busy()) startPolling();
  };

  const stepIcon = status => ({ done:'✓', running:'', failed:'✕', skipped:'–', cancelled:'–', pending:'' }[status] ?? '');
  const jobTitle = () => {
    const verb = job.kind === 'rollback' ? 'Rollback' : 'Deployment';
    return { running:`${verb} in progress`, succeeded:`${verb} completed`, restored:'Deployment failed · previous version restored', failed:`${verb} failed` }[job.status] || verb;
  };

  const renderJob = () => {
    const host = byId('deploy-job');
    if (!job) { host.classList.add('d-none'); return; }
    const packageName = packages.find(item => item.id === job.packageId)?.fileName || 'package';
    const log = byId('deploy-job-log'), atBottom = !log || log.scrollTop + log.clientHeight >= log.scrollHeight - 8;
    host.className = `deploy-card deploy-job deploy-job-${job.status}`;
    host.innerHTML = `
      <div class="deploy-job-head">
        <div><span class="eyebrow">${job.kind === 'rollback' ? 'ROLLBACK' : 'DEPLOYMENT'}</span><h2>${escapeHtml(jobTitle())}</h2><p>${escapeHtml(packageName)} · started ${when(job.startedAt)}${job.completedAt ? ` · finished ${when(job.completedAt)}` : ''}${job.backupFileName ? ` · backup ${escapeHtml(job.backupFileName)}` : ''}</p></div>
        ${job.status === 'running' ? '<span class="spinner-border text-primary" role="status" aria-label="Running"></span>' : ''}
      </div>
      ${job.error ? `<div class="deploy-error">${escapeHtml(job.error)}</div>` : ''}
      <ol class="deploy-steps">${job.steps.map(step => `<li class="deploy-step step-${step.status}"><span class="deploy-step-mark">${step.status === 'running' ? '<span class="spinner-border spinner-border-sm"></span>' : stepIcon(step.status)}</span><span><strong>${escapeHtml(step.label)}</strong>${step.detail ? `<small>${escapeHtml(step.detail)}</small>` : ''}</span></li>`).join('')}</ol>
      <details class="deploy-log" ${['failed', 'restored'].includes(job.status) ? 'open' : ''}><summary>Activity log (${job.log.length})</summary><div id="deploy-job-log" class="deploy-log-lines">${job.log.map(line => `<div class="log-${line.level}"><time>${new Date(line.at).toLocaleTimeString()}</time><span>${escapeHtml(line.message)}</span></div>`).join('')}</div></details>`;
    const newLog = byId('deploy-job-log'); if (newLog && atBottom) newLog.scrollTop = newLog.scrollHeight;
    renderPackages();
  };

  // Fast polling while a job runs: progress every second, and the package list (status and history) whenever
  // a step changes, so the history fills in live.
  const startPolling = () => {
    stopPolling();
    let lastSteps = '';
    pollTimer = setInterval(async () => {
      if (!job) return stopPolling();
      try {
        job = await jsonRequest(`/api/deployment/jobs/${job.jobId}`);
        renderJob();
        const steps = job.steps.map(step => step.status).join();
        if (steps !== lastSteps && lastSteps) loadPackages().catch(() => {});
        lastSteps = steps;
        if (!busy()) {
          stopPolling(); await loadPackages();
          notify(jobTitle(), job.status !== 'succeeded');
        }
      } catch (error) { stopPolling(); notify(error.message, true); }
    }, 1000);
  };
  const stopPolling = () => { if (pollTimer) clearInterval(pollTimer); pollTimer = null; };

  // ---------- Wiring ----------
  byId('deploy-system-add').addEventListener('click', () => openSystemModal(null));
  byId('deploy-system-edit').addEventListener('click', () => openSystemModal(selectedSystem()));
  byId('deploy-system-delete').addEventListener('click', deleteSystem);
  byId('deploy-system-back').addEventListener('click', showList);
  byId('deploy-system-form').addEventListener('submit', saveSystem);
  byId('deploy-module-add').addEventListener('click', () => openModuleModal(null));
  byId('deploy-modules-manage').addEventListener('click', () => modulesModal.show());
  byId('deploy-no-modules-add').addEventListener('click', () => openModuleModal(null));
  byId('deployModuleModal').addEventListener('hidden.bs.modal', () => { if (returnToManager) { returnToManager = false; modulesModal.show(); } });
  byId('deploy-module-form').addEventListener('submit', saveModule);
  byId('deploy-file').addEventListener('change', event => stageFile(event.target.files[0]));
  byId('deploy-staged-clear').addEventListener('click', clearStaged);
  byId('deploy-upload-description').addEventListener('input', updateUploadForm);
  byId('deploy-upload-form').addEventListener('submit', event => { event.preventDefault(); startUpload(); });
  byId('deploy-upload-confirm-submit').addEventListener('click', () => { uploadConfirmModal.hide(); startUpload(true); });
  byId('deploy-upload-cancel').addEventListener('click', () => upload?.xhr.abort());
  const dropzone = byId('deploy-dropzone');
  ['dragenter','dragover'].forEach(name => dropzone.addEventListener(name, event => { event.preventDefault(); dropzone.classList.add('dragging'); }));
  ['dragleave','drop'].forEach(name => dropzone.addEventListener(name, event => { event.preventDefault(); dropzone.classList.remove('dragging'); }));
  dropzone.addEventListener('drop', event => stageFile(event.dataTransfer.files[0]));
  byId('deploy-run-form').addEventListener('submit', submitDeploy);
  byId('deploy-rollback-form').addEventListener('submit', submitRollback);
  byId('deploy-rollback-confirm').addEventListener('input', event => byId('deploy-rollback-submit').disabled = event.target.value !== 'ROLLBACK');
  window.addEventListener('beforeunload', event => { if (upload) { event.preventDefault(); event.returnValue = ''; } });
  loadSystems()
    .then(() => {
      const requested = Number(new URL(location.href).searchParams.get('system'));
      if (requested && systems.some(system => system.id === requested)) showSystem(requested);
      else if (requested) setSystemInUrl(null); // the system was deleted
    })
    .catch(error => notify(error.message, true));
})();
