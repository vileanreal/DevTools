(() => {
  const { byId, escapeHtml, notify, jsonRequest, appUrl } = window.devTools;
  const modal = id => bootstrap.Modal.getOrCreateInstance(byId(id));
  const systemModal = modal('patchSystemModal'), modulesModal = modal('patchModulesModal'), moduleModal = modal('patchModuleModal');
  const compileModal = modal('patchCompileModal'), latestModal = modal('patchLatestModal'), detailsModal = modal('patchDetailsModal');
  const snapshotLatestModal = modal('patchSnapshotLatestModal'), undoModal = modal('patchUndoModal');
  let systems = [], snapshots = [], packages = [], selectedSystemId = null, editingSystemId = null, editingModuleId = null, returnToManager = false;
  // changes: the last check of the module folders. selection: module ids ticked for the next patch / set-as-latest.
  let changes = null, checking = false, selection = new Set(), selectionTouched = false, checkToken = 0;
  const pages = { packages: { page:1, size:10 }, snapshots: { page:1, size:10 }, history: { page:1, size:10 } };
  let history = [], snapshotLatestTarget = null, undoTarget = null;
  const sourceLabels = { initial:'Recorded at start', compile:'Compiled patch', current:'Set as latest (current files)', snapshot:'Set as latest (snapshot)', patch:'Set as latest (patch)', undo:'Undo' };

  const selectedSystem = () => systems.find(item => item.id === selectedSystemId);
  const bytes = value => { const units = ['B','KB','MB','GB','TB']; let size = Number(value) || 0, unit = 0; while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit++; } return `${size.toFixed(unit && size < 10 ? 1 : 0)} ${units[unit]}`; };
  const when = value => value ? new Date(value).toLocaleString() : '';
  const plural = (count, word) => `${Number(count).toLocaleString()} ${word}${count === 1 ? '' : 's'}`;
  const listText = names => names.length <= 1 ? (names[0] || '') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
  const setBusy = (button, busy, label) => {
    if (busy) { button.dataset.original = button.innerHTML; button.disabled = true; button.innerHTML = `<span class="spinner-border spinner-border-sm me-1" aria-hidden="true"></span>${escapeHtml(label)}`; }
    else { button.disabled = false; if (button.dataset.original) button.innerHTML = button.dataset.original; }
  };
  const summaryFor = moduleId => changes?.modules.find(item => item.moduleId === moduleId);
  const pending = summary => summary ? summary.added + summary.changed + summary.deleted : 0;
  const moduleChips = (names, highlight = []) => names.length
    ? `<span class="patch-chips">${names.map(name => `<span class="patch-chip ${highlight.includes(name) ? 'latest' : ''}" title="${highlight.includes(name) ? `Latest version of ${escapeHtml(name)}` : ''}">${highlight.includes(name) ? '★ ' : ''}${escapeHtml(name)}</span>`).join('')}</span>`
    : '<span class="text-muted">—</span>';

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
    host.innerHTML = systems.map(system => {
      const versioned = system.modules.filter(module => module.latestAt).length;
      return `<button class="system-row" type="button" data-system-id="${system.id}">
        <span class="system-avatar">${escapeHtml(system.name.trim().charAt(0).toUpperCase() || 'P')}</span>
        <span class="system-row-main"><strong>${escapeHtml(system.name)}</strong><small>${plural(system.modules.length, 'module')}${system.modules.length ? ` · ${versioned === system.modules.length ? 'all have a latest version' : `${versioned} of ${system.modules.length} with a latest version`}` : ''}</small></span>
        <span class="system-row-meta">${system.modules.map(module => escapeHtml(module.name)).join(', ')}</span><span class="row-arrow" aria-hidden="true">›</span>
      </button>`;
    }).join('');
    host.querySelectorAll('[data-system-id]').forEach(button => button.addEventListener('click', () => showSystem(Number(button.dataset.systemId))));
  };

  const showList = () => {
    selectedSystemId = null; changes = null; setSystemInUrl(null);
    byId('patch-system-detail').classList.add('d-none'); byId('patch-systems-view').classList.remove('d-none');
  };

  const showSystem = async systemId => {
    selectedSystemId = systemId; changes = null; selection = new Set(); selectionTouched = false;
    pages.packages.page = 1; pages.snapshots.page = 1; pages.history.page = 1;
    byId('patch-history-module').value = ''; byId('patch-history-date').value = '';
    setSystemInUrl(systemId);
    byId('patch-systems-view').classList.add('d-none'); byId('patch-system-detail').classList.remove('d-none');
    byId('patch-compare-base').value = '';
    renderDetail();
    await Promise.all([loadHistory(), checkChanges()]);
  };

  const renderDetail = () => {
    const system = selectedSystem(); if (!system) return;
    byId('patch-system-name').textContent = system.name;
    byId('patch-modules-count').textContent = system.modules.length;
    byId('patch-no-modules').classList.toggle('d-none', system.modules.length > 0);
    byId('patch-changes').classList.toggle('d-none', !system.modules.length);
    // Drop ticks of modules that were removed.
    selection = new Set([...selection].filter(id => system.modules.some(module => module.id === id)));
    renderModuleTable(); renderModules(); renderChanges();
  };

  const openSystemModal = system => {
    editingSystemId = system?.id ?? null;
    byId('patch-system-name-input').value = system?.name ?? '';
    byId('patch-system-output-input').value = system?.outputDirectory ?? '';
    byId('patch-system-helper-title').value = system?.helperTitle ?? '';
    byId('patch-system-helper-subtitle').value = system?.helperSubtitle ?? '';
    byId('patch-system-support').value = system?.supportContact ?? '';
    byId('patch-system-helper-file').value = system?.helperFileName ?? '';
    byId('patch-system-modal-title').textContent = system ? 'Edit system' : 'Add system';
    byId('patch-system-submit').textContent = system ? 'Save changes' : 'Add system';
    systemModal.show();
  };

  const saveSystem = async event => {
    event.preventDefault();
    const body = { name:byId('patch-system-name-input').value, outputDirectory:byId('patch-system-output-input').value || null,
      helperTitle:byId('patch-system-helper-title').value || null, helperSubtitle:byId('patch-system-helper-subtitle').value || null,
      supportContact:byId('patch-system-support').value || null, helperFileName:byId('patch-system-helper-file').value || null };
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

  // ---------- Module table: selection, latest version and pending changes per module ----------
  const renderModuleTable = () => {
    const system = selectedSystem(), body = byId('patch-module-rows');
    if (!system.modules.length) { body.innerHTML = '<tr><td colspan="6" class="patch-empty-cell">No modules yet.</td></tr>'; updateSelectionUi(); return; }
    body.innerHTML = system.modules.map(module => {
      const summary = summaryFor(module.id), count = pending(summary);
      const cell = (value, kind) => checking && !summary ? '<span class="patch-muted">…</span>'
        : summary ? (value ? `<strong class="patch-count ${kind}">${value.toLocaleString()}</strong>` : '<span class="patch-muted">0</span>') : '<span class="patch-muted">—</span>';
      const base = summary && summary.baseSnapshotId !== module.latestSnapshotId && summary.baseCreatedAt
        ? `<small class="patch-base-note" title="Compared with the snapshot chosen in “Compare with”">compared with ${escapeHtml(summary.baseLabel || 'snapshot')} · ${when(summary.baseCreatedAt)}</small>` : '';
      return `<tr class="patch-module-row ${selection.has(module.id) ? 'selected' : ''} ${summary && !count ? 'up-to-date' : ''}" data-module-id="${module.id}">
        <td class="patch-col-check"><input class="form-check-input patch-module-check" type="checkbox" ${selection.has(module.id) ? 'checked' : ''} aria-label="Include ${escapeHtml(module.name)}"></td>
        <td><span class="patch-module-name"><strong>${escapeHtml(module.name)}</strong>${summary && !count ? '<span class="patch-tag ok">Up to date</span>' : ''}</span><code class="patch-path" title="${escapeHtml(module.sourcePath)}">${escapeHtml(module.sourcePath)}</code></td>
        <td>${module.latestAt
          ? `<span class="patch-version-cell"><strong title="${escapeHtml(module.latestLabel || '')}">${escapeHtml(module.latestLabel || 'Latest version')}</strong><small>${when(module.latestAt)}</small>${module.latestRemarks ? `<small class="patch-remarks" title="${escapeHtml(`${module.latestRemarks}${module.latestChangedBy ? ` — ${module.latestChangedBy}` : ''}, ${when(module.latestChangedAt)}`)}">“${escapeHtml(module.latestRemarks)}”</small>` : ''}${base}</span>`
          : `<span class="patch-version-cell"><span class="patch-tag warn">Not set</span><small>Every file counts as added</small>${base}</span>`}</td>
        <td class="patch-col-num">${cell(summary?.added, 'added')}</td>
        <td class="patch-col-num">${cell(summary?.changed, 'changed')}</td>
        <td class="patch-col-num">${cell(summary?.deleted, 'deleted')}</td>
      </tr>`;
    }).join('');
    body.querySelectorAll('.patch-module-row').forEach(row => {
      const id = Number(row.dataset.moduleId), box = row.querySelector('.patch-module-check');
      box.addEventListener('change', () => setSelected(id, box.checked));
      row.addEventListener('click', event => { if (event.target !== box) setSelected(id, !selection.has(id)); });
    });
    updateSelectionUi();
  };

  const setSelected = (id, on) => {
    selectionTouched = true;
    if (on) selection.add(id); else selection.delete(id);
    renderModuleTable(); renderChanges();
  };

  const updateSelectionUi = () => {
    const system = selectedSystem(); if (!system) return;
    const all = byId('patch-select-all'), count = selection.size, total = system.modules.length;
    all.checked = total > 0 && count === total; all.indeterminate = count > 0 && count < total; all.disabled = !total;
    const selectedChanges = system.modules.filter(module => selection.has(module.id)).reduce((sum, module) => sum + pending(summaryFor(module.id)), 0);
    const compile = byId('patch-compile'), latest = byId('patch-set-latest');
    compile.disabled = !count || checking; latest.disabled = !count || checking; byId('patch-view-changes').disabled = !total || checking;
    compile.textContent = count ? `Compile ${plural(count, 'module')}` : 'Compile patch';
    byId('patch-modules-meta').textContent = !total ? 'Add the module folders to track.'
      : count ? `${count} of ${total} selected${changes ? ` · ${plural(selectedChanges, 'pending change')}` : ''}`
      : 'Each module has its own latest version. Tick the modules to include in a patch.';
  };

  // ---------- Manage modules dialog ----------
  const renderModules = () => {
    const system = selectedSystem(), host = byId('patch-module-list');
    host.innerHTML = system.modules.length ? system.modules.map(module => {
      const exclusions = module.exclusions ? module.exclusions.split('\n').length : 0;
      return `<article class="deploy-module-row" data-module-id="${module.id}">
        <span class="deploy-module-icon" aria-hidden="true">▣</span>
        <span class="deploy-module-main"><strong>${escapeHtml(module.name)}</strong><code title="${escapeHtml(module.sourcePath)}">${escapeHtml(module.sourcePath)}</code>
          <span class="deploy-module-chips">${exclusions ? `<span class="deploy-chip muted" title="${escapeHtml(module.exclusions)}">${plural(exclusions, 'ignored pattern')}</span>` : '<span class="deploy-chip muted">No ignored files</span>'}</span></span>
        <span class="deploy-row-actions"><button class="btn btn-sm btn-soft-primary module-edit" type="button">Edit</button><button class="icon-button btn-soft-danger module-delete" type="button" title="Remove module" aria-label="Remove ${escapeHtml(module.name)}">×</button></span>
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
    byId('patch-module-client-folder').value = module?.clientFolder ?? '';
    byId('patch-module-app-pool').value = module?.appPoolName ?? '';
    byId('patch-module-service').value = module?.serviceName ?? '';
    byId('patch-module-modal-title').textContent = module ? 'Edit module' : 'Add module';
    byId('patch-module-submit').textContent = module ? 'Save changes' : 'Add module';
    moduleModal.show();
  };

  const saveModule = async event => {
    event.preventDefault();
    const body = { name:byId('patch-module-name').value, sourcePath:byId('patch-module-source').value, exclusions:byId('patch-module-exclusions').value || null,
      clientFolder:byId('patch-module-client-folder').value || null, appPoolName:byId('patch-module-app-pool').value || null, serviceName:byId('patch-module-service').value || null };
    try {
      await jsonRequest(editingModuleId ? `/api/patch-management/systems/${selectedSystemId}/modules/${editingModuleId}` : `/api/patch-management/systems/${selectedSystemId}/modules`, editingModuleId ? 'PUT' : 'POST', body);
      moduleModal.hide(); await loadSystems(); notify(editingModuleId ? 'Module updated.' : 'Module added.');
      checkChanges();
    } catch (error) { notify(error.message, true); }
  };

  const deleteModule = async moduleId => {
    const module = selectedSystem()?.modules.find(item => item.id === moduleId);
    if (!module || !confirm(`Remove module ${module.name}? Its folder is not touched; existing snapshots and patches keep its files.`)) return;
    try { await jsonRequest(`/api/patch-management/modules/${moduleId}`, 'DELETE'); await loadSystems(); notify('Module removed.'); checkChanges(); }
    catch (error) { notify(error.message, true); }
  };

  // ---------- Pending changes ----------
  const checkChanges = async () => {
    const system = selectedSystem();
    if (!system?.modules.length) { changes = null; renderDetail(); return; }
    const token = ++checkToken, base = byId('patch-compare-base').value;
    checking = true; renderModuleTable(); renderChanges();
    const button = byId('patch-view-changes'); setBusy(button, true, 'Checking');
    try {
      const result = await jsonRequest(`/api/patch-management/systems/${selectedSystemId}/changes${base ? `?baseSnapshotId=${base}` : ''}`);
      if (token !== checkToken) return;
      changes = result;
      // Until the user picks modules themselves, tick the ones that have changes.
      if (!selectionTouched) selection = new Set(result.modules.filter(item => pending(item) > 0).map(item => item.moduleId));
    } catch (error) { if (token === checkToken) { changes = null; notify(error.message, true); } }
    finally {
      if (token === checkToken) { checking = false; setBusy(button, false); renderModuleTable(); renderChanges(); }
    }
  };

  const renderChanges = () => {
    const system = selectedSystem(); if (!system) return;
    const host = byId('patch-changes-body');
    if (checking) { host.innerHTML = '<div class="empty-state compact"><span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>Checking the module folders…</div>'; byId('patch-changes-meta').textContent = ''; return; }
    if (!changes) { host.innerHTML = '<div class="empty-state compact">Press “Check changes” to compare the module folders with their latest version.</div>'; byId('patch-changes-meta').textContent = ''; return; }
    byId('patch-changes-meta').textContent = `Checked ${when(changes.comparedAt)}`;
    if (!selection.size) { host.innerHTML = '<div class="empty-state compact">Tick one or more modules above to see the changes that would go into the patch.</div>'; return; }
    const names = new Set(system.modules.filter(module => selection.has(module.id)).map(module => module.name));
    const modules = changes.modules.filter(item => names.has(item.module));
    const files = changes.files.filter(file => names.has(file.module));
    byId('patch-changes-title').textContent = `Pending changes · ${plural(modules.length, 'module')}`;
    mountChanges(host, { ...changes, modules, files,
      added: modules.reduce((sum, item) => sum + item.added, 0), changed: modules.reduce((sum, item) => sum + item.changed, 0), deleted: modules.reduce((sum, item) => sum + item.deleted, 0) });
  };

  // Renders a change list as a table grouped by module, with filter chips that only affect this host.
  const kindLabels = { ADDED:'Added', CHANGED:'Changed', DELETED:'Deleted' };
  const sizeText = file => file.kind === 'DELETED' ? bytes(file.previousSize) : file.kind === 'CHANGED' ? `${bytes(file.previousSize)} → ${bytes(file.size)}` : bytes(file.size);
  // Each module's files are grouped by top-level folder (bin/, wwwroot/ …) that can be collapsed, and a module shows at most
  // PAGE_ROWS files until "Show more" is pressed. Collapsed folders and row limits survive filter changes in this host.
  const PAGE_ROWS = 15;
  const folderOf = path => { const index = path.search(/[\\/]/); return index < 0 ? '' : path.slice(0, index); };
  const countsHtml = (added, changed, deleted) => `${added ? `<span class="patch-count added">+${added}</span>` : ''}${changed ? `<span class="patch-count changed">~${changed}</span>` : ''}${deleted ? `<span class="patch-count deleted">−${deleted}</span>` : ''}`;
  const mountChanges = (host, result, { showBase = true } = {}) => {
    let filter = '';
    const collapsed = new Set(), limits = new Map();
    // A module with more changes than fit starts with its folders collapsed, so its folder list is the overview.
    result.modules.filter(item => pending(item) > PAGE_ROWS).forEach(item => {
      const folders = new Set(result.files.filter(file => file.module === item.module).map(file => folderOf(file.path)));
      if (folders.size > 1) folders.forEach(folder => collapsed.add(`${item.module}\n${folder}`));
    });
    const draw = () => {
      const total = result.added + result.changed + result.deleted;
      if (!total) { host.innerHTML = '<div class="empty-state compact">No changes — the files match their version.</div>'; return; }
      const groups = result.modules.filter(item => pending(item) > 0).map(item => ({ item, files: result.files.filter(file => file.module === item.module && (!filter || file.kind === filter)) })).filter(group => group.files.length);
      const moduleBody = ({ item, files }) => {
        const folders = new Map();
        files.forEach(file => { const folder = folderOf(file.path); if (!folders.has(folder)) folders.set(folder, []); folders.get(folder).push(file); });
        // Folders first (alphabetical), then files in the module root.
        const ordered = [...folders.entries()].sort(([a], [b]) => (a === '') - (b === '') || a.localeCompare(b, undefined, { sensitivity:'base' }));
        const limit = limits.get(item.module) ?? PAGE_ROWS;
        let shown = 0, hidden = 0;
        const rows = [];
        for (const [folder, folderFiles] of ordered) {
          const key = `${item.module}\n${folder}`, isCollapsed = collapsed.has(key), starved = !isCollapsed && shown >= limit;
          const kinds = kind => folderFiles.filter(file => file.kind === kind).length;
          rows.push(`<tr class="patch-folder-row ${isCollapsed ? 'collapsed' : ''}"><td colspan="3">
            <button type="button" class="patch-folder-toggle" data-folder="${escapeHtml(key)}" aria-expanded="${!isCollapsed}">
              <span class="patch-folder-caret" aria-hidden="true">▶</span><span class="patch-folder-name">${folder ? `${escapeHtml(folder)}/` : '(module root)'}</span>
              <span class="patch-folder-count">${plural(folderFiles.length, 'file')}${starved ? ' · use Show more below' : ''}</span><span class="patch-module-counts">${countsHtml(kinds('ADDED'), kinds('CHANGED'), kinds('DELETED'))}</span>
            </button></td></tr>`);
          if (isCollapsed) continue;
          const visible = folderFiles.slice(0, limit - shown);
          shown += visible.length; hidden += folderFiles.length - visible.length;
          visible.forEach(file => {
            const rest = folder ? file.path.slice(folder.length + 1) : file.path;
            rows.push(`<tr class="patch-change-row kind-${file.kind.toLowerCase()}"><td class="patch-col-kind"><span class="deploy-file-tag patch-kind-${file.kind.toLowerCase()}">${kindLabels[file.kind]}</span></td><td><code title="${escapeHtml(file.path)}">${folder ? `<span class="patch-path-prefix">${escapeHtml(folder)}/</span>` : ''}${escapeHtml(rest)}</code></td><td class="patch-col-size">${sizeText(file)}</td></tr>`);
          });
        }
        if (hidden > 0) rows.push(`<tr class="patch-more-row"><td colspan="3"><span class="patch-more">
            <button type="button" class="btn btn-sm btn-soft-primary" data-more="${escapeHtml(item.module)}">Show ${Math.min(PAGE_ROWS, hidden)} more</button>
            ${hidden > PAGE_ROWS ? `<button type="button" class="btn btn-sm btn-ghost" data-all="${escapeHtml(item.module)}">Show all ${hidden.toLocaleString()}</button>` : ''}
            <small>${plural(hidden, 'more file')} in ${escapeHtml(item.module)}</small></span></td></tr>`);
        else if (limit > PAGE_ROWS) rows.push(`<tr class="patch-more-row"><td colspan="3"><span class="patch-more"><button type="button" class="btn btn-sm btn-ghost" data-less="${escapeHtml(item.module)}">Show less</button></span></td></tr>`);
        return rows.join('');
      };
      host.innerHTML = `
        <div class="patch-change-summary">
          ${[['', 'All', total], ['ADDED', 'Added', result.added], ['CHANGED', 'Changed', result.changed], ['DELETED', 'Deleted', result.deleted]].map(([kind, label, count]) =>
            `<button type="button" class="patch-change-chip kind-${(kind || 'all').toLowerCase()} ${filter === kind ? 'active' : ''}" data-kind="${kind}" ${count || !kind ? '' : 'disabled'}><strong>${count.toLocaleString()}</strong>${label}</button>`).join('')}
          <span class="patch-change-tools"><button type="button" class="btn btn-sm btn-ghost" data-fold="collapse">Collapse folders</button><button type="button" class="btn btn-sm btn-ghost" data-fold="expand">Expand folders</button></span>
        </div>
        <div class="patch-table-wrap patch-changes-wrap">
          <table class="patch-table patch-changes-table">
            <thead><tr><th class="patch-col-kind">Type</th><th>File</th><th class="patch-col-size">Size</th></tr></thead>
            ${groups.map(group => `<tbody>
              <tr class="patch-group-row"><th colspan="3"><span class="patch-group-head"><strong>${escapeHtml(group.item.module)}</strong>
                <span class="patch-module-counts">${countsHtml(group.item.added, group.item.changed, group.item.deleted)}</span>
                ${showBase ? `<small>${group.item.baseCreatedAt ? `since ${escapeHtml(group.item.baseLabel || 'snapshot')} · ${when(group.item.baseCreatedAt)}` : 'no earlier version — all files are new'}</small>` : ''}</span></th></tr>
              ${moduleBody(group)}
            </tbody>`).join('')}
          </table>
        </div>
        ${result.truncated ? `<p class="deploy-preview-note">Showing the first ${result.files.length.toLocaleString()} changes.</p>` : ''}`;
      host.querySelectorAll('[data-kind]').forEach(button => button.addEventListener('click', () => { filter = button.dataset.kind; draw(); }));
      host.querySelectorAll('[data-folder]').forEach(button => button.addEventListener('click', () => { const key = button.dataset.folder; if (collapsed.has(key)) collapsed.delete(key); else collapsed.add(key); draw(); }));
      host.querySelectorAll('[data-more]').forEach(button => button.addEventListener('click', () => { limits.set(button.dataset.more, (limits.get(button.dataset.more) ?? PAGE_ROWS) + PAGE_ROWS); draw(); }));
      host.querySelectorAll('[data-all]').forEach(button => button.addEventListener('click', () => { limits.set(button.dataset.all, Infinity); draw(); }));
      host.querySelectorAll('[data-less]').forEach(button => button.addEventListener('click', () => { limits.delete(button.dataset.less); draw(); }));
      host.querySelectorAll('[data-fold]').forEach(button => button.addEventListener('click', () => {
        collapsed.clear();
        if (button.dataset.fold === 'collapse') groups.forEach(({ item, files }) => files.forEach(file => { const folder = folderOf(file.path); if (folder) collapsed.add(`${item.module}\n${folder}`); }));
        draw();
      }));
    };
    draw();
  };

  // ---------- Snapshots and patches ----------
  const loadHistory = async () => {
    if (!selectedSystemId) return;
    [snapshots, packages, history] = await Promise.all([
      jsonRequest(`/api/patch-management/systems/${selectedSystemId}/snapshots`),
      jsonRequest(`/api/patch-management/systems/${selectedSystemId}/packages`),
      jsonRequest(`/api/patch-management/systems/${selectedSystemId}/latest-history`)
    ]);
    renderCompareOptions(); renderSnapshots(); renderPackages(); renderHistoryFilter(); renderHistory();
  };

  const snapshotName = snapshot => `${snapshot.label || (snapshot.source === 'patch' ? 'Patch snapshot' : 'Snapshot')} · ${when(snapshot.createdAt)}`;

  const renderCompareOptions = () => {
    const select = byId('patch-compare-base'), previous = select.value;
    select.innerHTML = '<option value="">Each module’s latest version</option>'
      + snapshots.map(item => `<option value="${item.id}">${escapeHtml(snapshotName(item))} (${escapeHtml(item.modules.join(', '))})</option>`).join('');
    if ([...select.options].some(option => option.value === previous)) select.value = previous;
  };

  // Shared pager under a table: page numbers, range and rows per page.
  const renderPager = (pager, total, state, rerender) => {
    const pageCount = Math.max(1, Math.ceil(total / state.size));
    state.page = Math.min(Math.max(1, state.page), pageCount);
    pager.classList.toggle('d-none', total <= 10);
    if (total <= 10) return;
    const start = (state.page - 1) * state.size, numbers = [];
    for (let page = 1; page <= pageCount; page++)
      if (page === 1 || page === pageCount || Math.abs(page - state.page) <= 1) numbers.push(page);
      else if (numbers[numbers.length - 1] !== '…') numbers.push('…');
    pager.innerHTML = `<span class="deploy-pager-info">${(start + 1).toLocaleString()}–${Math.min(start + state.size, total).toLocaleString()} of ${total.toLocaleString()}</span>
      <span class="deploy-pager-pages">
        <button class="btn btn-sm btn-ghost" type="button" data-page="${state.page - 1}" ${state.page === 1 ? 'disabled' : ''} aria-label="Previous page">‹</button>
        ${numbers.map(page => page === '…' ? '<span class="deploy-pager-gap">…</span>' : `<button class="btn btn-sm ${page === state.page ? 'btn-primary' : 'btn-ghost'}" type="button" data-page="${page}" ${page === state.page ? 'aria-current="page"' : ''}>${page}</button>`).join('')}
        <button class="btn btn-sm btn-ghost" type="button" data-page="${state.page + 1}" ${state.page === pageCount ? 'disabled' : ''} aria-label="Next page">›</button>
      </span>
      <label class="deploy-pager-size">Rows <select class="form-select form-select-sm">${[10, 25, 50].map(size => `<option ${size === state.size ? 'selected' : ''}>${size}</option>`).join('')}</select></label>`;
    pager.querySelectorAll('[data-page]').forEach(button => button.addEventListener('click', () => { state.page = Number(button.dataset.page); rerender(); }));
    pager.querySelector('select').addEventListener('change', event => { state.size = Number(event.target.value); state.page = 1; rerender(); });
  };
  const pageOf = (items, state) => items.slice((state.page - 1) * state.size, state.page * state.size);

  const snapshotActions = item => `
    ${item.isLatest && item.latestFor.length === item.modules.length ? '' : `<button class="btn btn-sm btn-soft-success snapshot-latest" type="button" data-snapshot-id="${item.id}" title="Make this the latest version of ${escapeHtml(item.modules.join(', '))}">Set as latest</button>`}
    ${item.isLatest || item.patchId ? '' : `<button class="icon-button btn-soft-danger snapshot-delete" type="button" data-snapshot-id="${item.id}" title="Delete snapshot" aria-label="Delete snapshot">×</button>`}`;
  const wireSnapshotActions = host => {
    host.querySelectorAll('.snapshot-latest').forEach(button => button.addEventListener('click', event => { event.stopPropagation(); detailsModal.hide(); setSnapshotLatest(Number(button.dataset.snapshotId)); }));
    host.querySelectorAll('.snapshot-delete').forEach(button => button.addEventListener('click', event => { event.stopPropagation(); detailsModal.hide(); deleteSnapshot(Number(button.dataset.snapshotId)); }));
  };

  const packageActions = item => `
    ${item.fileExists ? `<a class="btn btn-sm btn-soft-primary" href="${appUrl(`/api/patch-management/packages/${item.id}/download`)}" download title="Download ${escapeHtml(item.fileName)}">↓ Download</a>` : '<span class="deploy-chip muted">File missing</span>'}
    ${item.snapshotIsLatest ? '' : `<button class="btn btn-sm btn-soft-success package-latest" type="button" data-package-id="${item.id}" title="Mark what this patch contains as the version now on UAT/production">Set as latest</button>`}
    <button class="icon-button btn-soft-danger package-delete" type="button" data-package-id="${item.id}" title="Delete patch" aria-label="Delete ${escapeHtml(item.fileName)}">×</button>`;
  const wirePackageActions = host => {
    host.querySelectorAll('a[download]').forEach(link => link.addEventListener('click', event => event.stopPropagation()));
    host.querySelectorAll('.package-latest').forEach(button => button.addEventListener('click', event => {
      event.stopPropagation(); detailsModal.hide();
      const item = packages.find(candidate => candidate.id === Number(button.dataset.packageId));
      setSnapshotLatest(item.snapshotId, item.fileName);
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

  const renderPackages = () => {
    const host = byId('patch-package-list'), pager = byId('patch-package-pager');
    if (!packages.length) { host.innerHTML = '<div class="empty-state compact">No patches compiled yet. Tick modules above and press Compile.</div>'; pager.classList.add('d-none'); return; }
    renderPager(pager, packages.length, pages.packages, renderPackages);
    host.innerHTML = `<div class="patch-table-wrap"><table class="patch-table patch-list-table">
      <thead><tr><th>Patch</th><th>Modules</th><th class="patch-col-changes">Changes</th><th class="patch-col-size">Size</th><th class="patch-col-date">Compiled</th><th class="patch-col-actions"><span class="visually-hidden">Actions</span></th></tr></thead>
      <tbody>${pageOf(packages, pages.packages).map(item => `<tr class="patch-click-row" data-row-id="${item.id}" tabindex="0" aria-label="View ${escapeHtml(item.fileName)} details">
        <td><span class="patch-title-cell"><span class="patch-title-line"><span class="patch-format">${item.format}</span><strong title="${escapeHtml(item.fileName)}">${escapeHtml(item.fileName)}</strong>${item.snapshotIsLatest ? '<span class="patch-tag ok">Latest</span>' : ''}</span>${item.label ? `<small title="${escapeHtml(item.label)}">${escapeHtml(item.label)}</small>` : ''}</span></td>
        <td>${moduleChips(item.modules)}</td>
        <td class="patch-col-changes"><span class="patch-module-counts"><span class="patch-count added">+${item.added}</span><span class="patch-count changed">~${item.changed}</span><span class="patch-count deleted">−${item.deleted}</span></span></td>
        <td class="patch-col-size">${bytes(item.sizeBytes)}</td>
        <td class="patch-col-date"><span class="patch-date">${when(item.createdAt)}${item.createdBy ? `<small>by ${escapeHtml(item.createdBy)}</small>` : ''}</span></td>
        <td class="patch-col-actions"><span class="deploy-row-actions">${packageActions(item)}</span></td>
      </tr>`).join('')}</tbody></table></div>`;
    wirePackageActions(host);
    makeRowsClickable(host, openPackageDetails);
  };

  const renderSnapshots = () => {
    const host = byId('patch-snapshot-list'), pager = byId('patch-snapshot-pager');
    if (!snapshots.length) { host.innerHTML = '<div class="empty-state compact">No snapshots yet. Use “Set as latest” or compile a patch.</div>'; pager.classList.add('d-none'); return; }
    renderPager(pager, snapshots.length, pages.snapshots, renderSnapshots);
    host.innerHTML = `<div class="patch-table-wrap"><table class="patch-table patch-list-table">
      <thead><tr><th>Snapshot</th><th>Modules <small class="patch-muted">(★ latest version)</small></th><th class="patch-col-num">Files</th><th class="patch-col-size">Size</th><th class="patch-col-date">Taken</th><th class="patch-col-actions"><span class="visually-hidden">Actions</span></th></tr></thead>
      <tbody>${pageOf(snapshots, pages.snapshots).map(item => `<tr class="patch-click-row" data-row-id="${item.id}" tabindex="0" aria-label="View snapshot details">
        <td><span class="patch-title-cell"><span class="patch-title-line"><span class="patch-tag ${item.source === 'patch' ? 'info' : 'muted'}">${item.source === 'patch' ? 'Patch' : 'Snapshot'}</span><strong>${escapeHtml(item.label || (item.source === 'patch' ? 'Patch snapshot' : 'Snapshot'))}</strong></span></span></td>
        <td>${moduleChips(item.modules, item.latestFor)}</td>
        <td class="patch-col-num">${item.fileCount.toLocaleString()}</td>
        <td class="patch-col-size">${bytes(item.totalBytes)}</td>
        <td class="patch-col-date"><span class="patch-date">${when(item.createdAt)}${item.createdBy ? `<small>by ${escapeHtml(item.createdBy)}</small>` : ''}</span></td>
        <td class="patch-col-actions"><span class="deploy-row-actions">${snapshotActions(item)}</span></td>
      </tr>`).join('')}</tbody></table></div>`;
    wireSnapshotActions(host);
    makeRowsClickable(host, openSnapshotDetails);
  };

  // After a version moves, tick whichever modules still have changes again.
  const refreshAfterVersionChange = async () => { selectionTouched = false; await Promise.all([loadSystems(), loadHistory()]); await checkChanges(); };

  // Setting a snapshot or patch as latest asks why (the remarks go into the version history).
  const setSnapshotLatest = (snapshotId, fileName) => {
    const snapshot = snapshots.find(item => item.id === snapshotId);
    const names = (snapshot?.modules ?? []).filter(name => !(snapshot?.latestFor ?? []).includes(name));
    snapshotLatestTarget = { snapshotId, names };
    byId('patch-snapshot-latest-subtitle').textContent = fileName || (snapshot ? snapshotName(snapshot) : '');
    byId('patch-snapshot-latest-text').textContent = `Make ${fileName ? `the contents of ${fileName}` : 'this snapshot'} the latest version of ${listText(names) || 'its modules'}? Their changes will be measured from it; other modules keep their own version.`;
    byId('patch-snapshot-latest-remarks').value = '';
    snapshotLatestModal.show();
  };

  const submitSnapshotLatest = async event => {
    event.preventDefault();
    if (!snapshotLatestTarget) return;
    const button = byId('patch-snapshot-latest-submit'); setBusy(button, true, 'Saving');
    try {
      await jsonRequest(`/api/patch-management/snapshots/${snapshotLatestTarget.snapshotId}/latest`, 'POST', { remarks:byId('patch-snapshot-latest-remarks').value });
      snapshotLatestModal.hide(); notify(`Latest version updated for ${listText(snapshotLatestTarget.names)}.`);
      await refreshAfterVersionChange();
    } catch (error) { notify(error.message, true); }
    finally { setBusy(button, false); }
  };

  // ---------- Version history ----------
  const versionName = (label, at) => label ? `${label} · ${when(at)}` : at ? when(at) : 'no version';
  const renderHistoryFilter = () => {
    const select = byId('patch-history-module'), previous = select.value;
    const names = [...new Set(history.map(item => item.moduleName))].sort((a, b) => a.localeCompare(b));
    select.innerHTML = '<option value="">All modules</option>' + names.map(name => `<option>${escapeHtml(name)}</option>`).join('');
    if (names.includes(previous)) select.value = previous;
  };

  const renderHistory = () => {
    const host = byId('patch-history-list'), pager = byId('patch-history-pager'), module = byId('patch-history-module').value;
    renderAsOf();
    const items = history.filter(item => !module || item.moduleName === module);
    if (!items.length) { host.innerHTML = '<div class="empty-state compact">No latest-version changes recorded yet.</div>'; pager.classList.add('d-none'); return; }
    renderPager(pager, items.length, pages.history, renderHistory);
    host.innerHTML = `<div class="patch-table-wrap"><table class="patch-table patch-list-table patch-history-table">
      <thead><tr><th class="patch-col-date">When</th><th>Module</th><th>Latest version</th><th>Remarks</th><th class="patch-col-actions"><span class="visually-hidden">Actions</span></th></tr></thead>
      <tbody>${pageOf(items, pages.history).map(item => `<tr class="patch-history-row source-${item.source}">
        <td class="patch-col-date"><span class="patch-date">${when(item.at)}${item.createdBy ? `<small>by ${escapeHtml(item.createdBy)}</small>` : ''}</span></td>
        <td><strong>${escapeHtml(item.moduleName)}</strong></td>
        <td><span class="patch-history-change">
          ${item.source === 'initial' ? '' : `<span class="patch-history-from" title="Previous latest version">${escapeHtml(versionName(item.previousLabel, item.previousCreatedAt))}</span><span class="patch-history-arrow" aria-hidden="true">→</span>`}
          <strong>${escapeHtml(item.snapshotId ? versionName(item.snapshotLabel, item.snapshotCreatedAt) : 'No version (every file counts as added)')}</strong></span>
          <small class="patch-history-source"><span class="patch-tag ${item.source === 'undo' ? 'warn' : item.source === 'initial' ? 'muted' : 'info'}">${escapeHtml(sourceLabels[item.source] || item.source)}</span>${item.patchFileName ? ` ${escapeHtml(item.patchFileName)}` : ''}</small></td>
        <td class="patch-history-remarks">${escapeHtml(item.remarks)}</td>
        <td class="patch-col-actions"><span class="deploy-row-actions">${item.canUndo ? `<button class="btn btn-sm btn-soft-primary history-undo" type="button" data-event-id="${item.id}" title="Move ${escapeHtml(item.moduleName)} back to ${escapeHtml(versionName(item.previousLabel, item.previousCreatedAt))}">↶ Undo</button>` : ''}</span></td>
      </tr>`).join('')}</tbody></table></div>`;
    host.querySelectorAll('.history-undo').forEach(button => button.addEventListener('click', () => openUndo(history.find(item => item.id === Number(button.dataset.eventId)))));
  };

  // "Latest on <date>": for each module, the newest history entry at or before that moment.
  const renderAsOf = () => {
    const host = byId('patch-history-asof'), value = byId('patch-history-date').value;
    if (!value) { host.classList.add('d-none'); host.innerHTML = ''; return; }
    const at = new Date(value).getTime();
    const modules = [...new Set(history.map(item => item.moduleName))].sort((a, b) => a.localeCompare(b));
    const rows = modules.map(name => {
      const entry = history.filter(item => item.moduleName === name && new Date(item.at).getTime() <= at)[0];
      return `<li><strong>${escapeHtml(name)}</strong><span>${entry ? `${escapeHtml(entry.snapshotId ? versionName(entry.snapshotLabel, entry.snapshotCreatedAt) : 'No version')}<small>since ${when(entry.at)} · “${escapeHtml(entry.remarks)}”</small>` : '<span class="patch-muted">Not recorded yet at that time</span>'}</span></li>`;
    });
    host.innerHTML = `<div class="patch-asof-head"><strong>Latest versions on ${escapeHtml(new Date(value).toLocaleString())}</strong><button type="button" class="btn btn-sm btn-ghost" id="patch-history-date-clear">Clear</button></div><ul>${rows.join('')}</ul>`;
    host.classList.remove('d-none');
    byId('patch-history-date-clear').addEventListener('click', () => { byId('patch-history-date').value = ''; renderHistory(); });
  };

  const openUndo = item => {
    if (!item) return;
    undoTarget = item;
    byId('patch-undo-text').textContent = `Move ${item.moduleName} back from “${versionName(item.snapshotLabel, item.snapshotCreatedAt)}” to “${versionName(item.previousLabel, item.previousCreatedAt)}”? Its pending changes will be measured from that version again.`;
    byId('patch-undo-remarks').value = '';
    undoModal.show();
  };

  const submitUndo = async event => {
    event.preventDefault();
    if (!undoTarget) return;
    const button = byId('patch-undo-submit'); setBusy(button, true, 'Undoing');
    try {
      await jsonRequest(`/api/patch-management/modules/${undoTarget.moduleId}/latest/undo`, 'POST', { remarks:byId('patch-undo-remarks').value });
      undoModal.hide(); notify(`${undoTarget.moduleName} is back on its previous version.`);
      await refreshAfterVersionChange();
    } catch (error) { notify(error.message, true); }
    finally { setBusy(button, false); }
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

  // ---------- Details for a patch or a snapshot ----------
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
      title: `${escapeHtml(item.fileName)}${item.snapshotIsLatest ? ' <span class="patch-tag ok">Latest version</span>' : ''}`,
      subtitle: item.label || 'No description',
      facts: [['Modules', item.modules.join(', ')], ['Format', `.${item.format}`], ['Size', bytes(item.sizeBytes)], ['Compiled', when(item.createdAt)], ['By', item.createdBy],
        ['Added', item.added], ['Changed', item.changed], ['Deleted', item.deleted]],
      changesTitle: 'Files in this patch',
      changesUrl: `/api/patch-management/packages/${item.id}/changes`,
      actions: packageActions(item),
      wire: wirePackageActions
    });
  };

  const openSnapshotDetails = id => {
    const item = snapshots.find(candidate => candidate.id === id); if (!item) return;
    showDetails({
      title: `${escapeHtml(item.label || 'Snapshot')}${item.isLatest ? ' <span class="patch-tag ok">Latest version</span>' : ''}`,
      subtitle: item.source === 'patch' ? 'Recorded when a patch was compiled' : 'Recorded by “Set as latest”',
      facts: [['Modules', item.modules.join(', ')], ['Latest version of', item.latestFor.join(', ') || 'none'], ['Taken', when(item.createdAt)], ['By', item.createdBy],
        ['Files', item.fileCount.toLocaleString()], ['Total size', bytes(item.totalBytes)]],
      changesTitle: 'Changes compared with each module’s previous snapshot',
      changesUrl: `/api/patch-management/snapshots/${item.id}/changes`,
      actions: snapshotActions(item),
      wire: wireSnapshotActions
    });
  };

  // ---------- Compile and set latest (both act on the ticked modules) ----------
  const renderCheckList = (hostId, ids, { onlyChanged = false } = {}) => {
    const system = selectedSystem(), host = byId(hostId);
    host.innerHTML = system.modules.map(module => {
      const summary = summaryFor(module.id), count = pending(summary);
      return `<label class="patch-check-item ${summary && !count ? 'up-to-date' : ''}">
        <input class="form-check-input" type="checkbox" value="${module.id}" ${ids.has(module.id) ? 'checked' : ''}>
        <strong>${escapeHtml(module.name)}</strong>
        <span class="patch-module-counts">${summary ? (count ? `${summary.added ? `<span class="patch-count added">+${summary.added}</span>` : ''}${summary.changed ? `<span class="patch-count changed">~${summary.changed}</span>` : ''}${summary.deleted ? `<span class="patch-count deleted">−${summary.deleted}</span>` : ''}` : '<span class="patch-muted">no changes</span>') : ''}</span>
        <small>${module.latestAt ? `since ${escapeHtml(module.latestLabel || 'latest version')}` : 'no version yet'}</small>
      </label>`;
    }).join('');
    host.dataset.onlyChanged = onlyChanged ? '1' : '';
  };
  const checkedIn = hostId => [...byId(hostId).querySelectorAll('input:checked')].map(input => Number(input.value));

  const updateCompileSummary = () => {
    const ids = checkedIn('patch-compile-modules'), system = selectedSystem();
    const total = system.modules.filter(module => ids.includes(module.id)).reduce((sum, module) => sum + pending(summaryFor(module.id)), 0);
    const base = byId('patch-compare-base');
    byId('patch-compile-subtitle').textContent = `${plural(ids.length, 'module')} · ${plural(total, 'change')} · compared with ${base.value ? base.options[base.selectedIndex].text : 'each module’s latest version'}`;
    const submit = byId('patch-compile-submit');
    submit.disabled = !ids.length;
    submit.textContent = byId('patch-compile-latest').checked ? 'Compile and set as latest' : 'Compile';
    byId('patch-compile-remarks-wrap').classList.toggle('d-none', !byId('patch-compile-latest').checked);
  };

  const openCompile = () => {
    renderCheckList('patch-compile-modules', selection);
    byId('patch-compile-label').value = '';
    byId('patch-compile-latest').checked = false;
    byId('patch-compile-remarks').value = '';
    updateCompileSummary();
    compileModal.show();
  };

  const compile = async event => {
    event.preventDefault();
    const moduleIds = checkedIn('patch-compile-modules');
    if (!moduleIds.length) { notify('Choose at least one module.', true); return; }
    const setAsLatest = byId('patch-compile-latest').checked;
    const latestRemarks = byId('patch-compile-remarks').value.trim();
    if (setAsLatest && latestRemarks.length < 3) { byId('patch-compile-remarks').focus(); notify('Enter remarks: why is this patch the latest version?', true); return; }
    const button = byId('patch-compile-submit'); setBusy(button, true, 'Compiling');
    const base = byId('patch-compare-base').value;
    try {
      const result = await jsonRequest(`/api/patch-management/systems/${selectedSystemId}/compile`, 'POST', {
        format:document.querySelector('input[name="patch-format"]:checked').value, baseSnapshotId:base ? Number(base) : null,
        label:byId('patch-compile-label').value || null, moduleIds, setAsLatest, latestRemarks:setAsLatest ? latestRemarks : null
      });
      compileModal.hide();
      notify(`${result.fileName} compiled: ${result.added} added, ${result.changed} changed, ${result.deleted} deleted${setAsLatest ? ' — now the latest version' : ''}.`);
      selection = new Set(moduleIds); selectionTouched = true;
      if (setAsLatest) await refreshAfterVersionChange(); else await loadHistory();
    } catch (error) { notify(error.message, true); }
    finally { setBusy(button, false); updateCompileSummary(); }
  };

  const openSetLatest = () => {
    renderCheckList('patch-latest-modules', selection);
    byId('patch-latest-label').value = '';
    byId('patch-latest-remarks').value = '';
    latestModal.show();
  };

  const setCurrentLatest = async event => {
    event.preventDefault();
    const moduleIds = checkedIn('patch-latest-modules');
    if (!moduleIds.length) { notify('Choose at least one module.', true); return; }
    const button = byId('patch-latest-submit'); setBusy(button, true, 'Taking snapshot');
    try {
      await jsonRequest(`/api/patch-management/systems/${selectedSystemId}/latest`, 'POST', { label:byId('patch-latest-label').value || null, moduleIds, remarks:byId('patch-latest-remarks').value });
      latestModal.hide();
      const names = selectedSystem().modules.filter(module => moduleIds.includes(module.id)).map(module => module.name);
      notify(`The current files of ${listText(names)} are now the latest version.`);
      await refreshAfterVersionChange();
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
  byId('patch-select-all').addEventListener('change', event => {
    selectionTouched = true;
    selection = event.target.checked ? new Set(selectedSystem().modules.map(module => module.id)) : new Set();
    renderModuleTable(); renderChanges();
  });
  byId('patch-compare-base').addEventListener('change', checkChanges);
  byId('patch-view-changes').addEventListener('click', checkChanges);
  byId('patch-compile').addEventListener('click', openCompile);
  byId('patch-compile-modules').addEventListener('change', updateCompileSummary);
  byId('patch-compile-latest').addEventListener('change', updateCompileSummary);
  byId('patch-compile-all').addEventListener('click', () => {
    byId('patch-compile-modules').querySelectorAll('input').forEach(input => { input.checked = pending(summaryFor(Number(input.value))) > 0; });
    updateCompileSummary();
  });
  byId('patch-compile-form').addEventListener('submit', compile);
  byId('patch-set-latest').addEventListener('click', openSetLatest);
  byId('patch-latest-form').addEventListener('submit', setCurrentLatest);
  byId('patch-snapshot-latest-form').addEventListener('submit', submitSnapshotLatest);
  byId('patch-undo-form').addEventListener('submit', submitUndo);
  byId('patch-history-module').addEventListener('change', () => { pages.history.page = 1; renderHistory(); });
  byId('patch-history-date').addEventListener('change', renderAsOf);
  loadSystems()
    .then(() => {
      const requested = Number(new URL(location.href).searchParams.get('system'));
      if (requested && systems.some(system => system.id === requested)) showSystem(requested);
      else if (requested) setSystemInUrl(null);
    })
    .catch(error => notify(error.message, true));
})();
