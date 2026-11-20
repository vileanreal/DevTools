(() => {
  const { byId, escapeHtml, notify, jsonRequest, highlightSyntax } = window.devTools;
  const sourceModal = bootstrap.Modal.getOrCreateInstance(byId('db-compare-source-modal'));
  const updateModal = bootstrap.Modal.getOrCreateInstance(byId('db-compare-update-modal'));
  let sources = [], comparison = null, activeKey = null;
  let combinedScript = '', combinedScriptGenerated = false, activeTab = 'diff';
  const selectedKeys = new Set();
  // Display order of the grouped list; unchanged objects start collapsed.
  const statusGroups = ['Deleted', 'Added', 'Changed', 'Unchanged'];
  const collapsedGroups = new Set(['Unchanged']);

  const statusClass = status => `compare-status-${String(status).toLowerCase()}`;
  const changedObjects = () => comparison?.objects.filter(item => item.status !== 'Unchanged') || [];
  const selectedObjects = () => changedObjects().filter(item => selectedKeys.has(item.key));
  const applicableSelection = () => selectedObjects().filter(item => item.canApply);

  const loadSources = async preferredId => {
    sources = await jsonRequest('/api/sql-doc/sources');
    const options = '<option value="">Choose a saved connection</option>' + sources.map(source => `<option value="${source.id}">${escapeHtml(source.name)} · ${escapeHtml(source.provider)}</option>`).join('');
    const left = byId('db-compare-left'), right = byId('db-compare-right');
    const previousLeft = Number(left.value), previousRight = Number(right.value);
    left.innerHTML = options; right.innerHTML = options;
    if (sources.some(item => item.id === previousLeft)) left.value = previousLeft;
    else if (preferredId) left.value = preferredId;
    else if (sources[0]) left.value = sources[0].id;
    if (sources.some(item => item.id === previousRight)) right.value = previousRight;
    else if (sources.find(item => item.id !== Number(left.value))) right.value = sources.find(item => item.id !== Number(left.value)).id;
  };

  const compare = async () => {
    const leftSourceId = Number(byId('db-compare-left').value), rightSourceId = Number(byId('db-compare-right').value);
    if (!leftSourceId || !rightSourceId || leftSourceId === rightSourceId) return notify('Choose two different saved database connections.', true);
    const button = byId('db-compare-run'), original = button.innerHTML;
    button.disabled = true; button.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Comparing';
    try {
      comparison = await jsonRequest('/api/database-compare', 'POST', { leftSourceId, rightSourceId });
      selectedKeys.clear();
      comparison.objects.filter(item => item.status !== 'Unchanged').forEach(item => selectedKeys.add(item.key));
      activeKey = statusGroups.map(status => comparison.objects.find(item => item.status === status)).find(Boolean)?.key || null;
      combinedScript = ''; combinedScriptGenerated = false;
      byId('db-compare-results').classList.remove('d-none'); byId('db-compare-empty').classList.add('d-none');
      fitWorkspace(); setTab('diff'); renderAll();
      if (comparison.warning) notify(comparison.warning, true);
    } catch (error) { notify(error.message, true); }
    finally { button.disabled = false; button.innerHTML = original; }
  };

  const renderAll = () => { renderSummary(); renderObjectList(); renderDiff(); renderSelectedScript(); renderCombinedScript(); };

  const renderSummary = () => {
    const differences = changedObjects().length;
    byId('db-compare-summary').textContent = `${differences} difference${differences === 1 ? '' : 's'} · ${comparison.objects.length} objects`;
  };

  const visibleObjects = () => {
    const search = byId('db-compare-search').value.trim().toLowerCase();
    return comparison.objects.filter(item => !search || `${item.type} ${item.name}`.toLowerCase().includes(search));
  };

  const objectRow = item => `<div class="db-compare-object-row ${item.key === activeKey ? 'active' : ''}" data-object-key="${escapeHtml(item.key)}">
      <input class="form-check-input compare-object-check" type="checkbox" ${selectedKeys.has(item.key) ? 'checked' : ''} ${item.status === 'Unchanged' ? 'disabled' : ''} aria-label="Select ${escapeHtml(item.name)}">
      <button type="button" class="db-compare-object-open" title="${escapeHtml(item.note || `${item.type} ${item.name}`)}"><strong>${escapeHtml(item.name)}</strong>${item.note ? '<i class="db-compare-note" aria-label="Needs manual review">!</i>' : ''}<small>${escapeHtml(item.type)}</small></button>
    </div>`;

  const renderObjectList = () => {
    const items = visibleObjects(), searching = Boolean(byId('db-compare-search').value.trim());
    const html = statusGroups.map(status => {
      const groupItems = items.filter(item => item.status === status);
      if (!groupItems.length) return '';
      const open = searching || !collapsedGroups.has(status);
      const applicable = groupItems.filter(item => item.canApply), checked = applicable.filter(item => selectedKeys.has(item.key)).length;
      const groupCheck = status === 'Unchanged' ? '<span class="db-compare-group-spacer"></span>'
        : `<input class="form-check-input compare-group-check" type="checkbox" ${applicable.length && checked === applicable.length ? 'checked' : ''} ${applicable.length ? '' : 'disabled'} data-indeterminate="${checked > 0 && checked < applicable.length}" aria-label="Select all ${status.toLowerCase()} objects">`;
      return `<section class="db-compare-group ${statusClass(status)}-group" data-group-status="${status}">
        <div class="db-compare-group-head">${groupCheck}<button type="button" class="db-compare-group-toggle" aria-expanded="${open}"><span class="db-compare-caret">${open ? '▾' : '▸'}</span><span class="${statusClass(status)}">${status.toUpperCase()}</span><em>${groupItems.length}</em></button></div>
        ${open ? `<div class="db-compare-group-items">${groupItems.map(objectRow).join('')}</div>` : ''}
      </section>`;
    }).join('');
    const list = byId('db-compare-object-list');
    list.innerHTML = html || '<div class="empty-state compact">No matching objects.</div>';
    list.querySelectorAll('.db-compare-group').forEach(group => {
      const status = group.dataset.groupStatus;
      group.querySelector('.db-compare-group-toggle').addEventListener('click', () => { collapsedGroups.has(status) ? collapsedGroups.delete(status) : collapsedGroups.add(status); renderObjectList(); });
      const check = group.querySelector('.compare-group-check');
      if (!check) return;
      check.indeterminate = check.dataset.indeterminate === 'true';
      check.addEventListener('change', () => { items.filter(item => item.status === status && item.canApply).forEach(item => check.checked ? selectedKeys.add(item.key) : selectedKeys.delete(item.key)); invalidateCombinedScript(); });
    });
    list.querySelectorAll('.db-compare-object-row').forEach(row => {
      const item = comparison.objects.find(candidate => candidate.key === row.dataset.objectKey);
      row.querySelector('.db-compare-object-open').addEventListener('click', () => { activeKey = item.key; if (activeTab === 'script') setTab('diff'); renderObjectList(); renderDiff(); renderSelectedScript(); });
      row.querySelector('.compare-object-check').addEventListener('change', event => { event.target.checked ? selectedKeys.add(item.key) : selectedKeys.delete(item.key); invalidateCombinedScript(); });
    });
    const applicable = comparison.objects.filter(item => item.canApply);
    byId('db-compare-select-all').checked = applicable.length > 0 && applicable.every(item => selectedKeys.has(item.key));
  };

  const setTab = tab => {
    activeTab = tab;
    document.querySelectorAll('[data-compare-tab]').forEach(button => { const active = button.dataset.compareTab === tab; button.classList.toggle('active', active); button.setAttribute('aria-selected', String(active)); });
    document.querySelectorAll('[data-compare-panel]').forEach(panel => panel.classList.toggle('d-none', panel.dataset.comparePanel !== tab));
  };

  // Only the view direction (side by side or stacked) is remembered, in browser storage, which may be unavailable.
  // Split sizes and hidden sections reset to their defaults on every page load.
  const layoutKey = 'devtools.databaseCompare.layout';
  const clampRatio = value => Math.min(80, Math.max(20, Math.round((Number(value) || 50) * 10) / 10));
  const layout = { orientation:'horizontal', ratios:{ horizontal:50, vertical:50 }, sqlRatio:50, hidden:[] };
  try { if (JSON.parse(localStorage.getItem(layoutKey) || 'null')?.orientation === 'vertical') layout.orientation = 'vertical'; }
  catch { /* storage unavailable or corrupt: use the default */ }
  const saveLayout = () => { try { localStorage.setItem(layoutKey, JSON.stringify({ orientation:layout.orientation })); } catch { /* storage unavailable */ } };
  const paneRatio = () => layout.ratios[layout.orientation];

  const applyLayout = () => {
    const workspace = byId('db-compare-results'), splitter = workspace.querySelector('.db-compare-splitter');
    workspace.dataset.orientation = layout.orientation;
    workspace.dataset.hidden = layout.hidden[0] || '';
    workspace.style.setProperty('--db-compare-split', `${paneRatio()}%`);
    workspace.style.setProperty('--db-compare-sql-split', `${layout.sqlRatio}%`);
    splitter.setAttribute('aria-orientation', layout.orientation === 'horizontal' ? 'vertical' : 'horizontal');
    splitter.setAttribute('aria-valuenow', String(Math.round(paneRatio())));
    const sqlSplitter = workspace.querySelector('.db-compare-sql-splitter');
    if (sqlSplitter) sqlSplitter.setAttribute('aria-valuenow', String(Math.round(layout.sqlRatio)));
    document.querySelectorAll('[data-compare-orientation]').forEach(button => { const active = button.dataset.compareOrientation === layout.orientation; button.classList.toggle('active', active); button.setAttribute('aria-checked', String(active)); });
    document.querySelectorAll('[data-compare-toggle]').forEach(button => { const visible = !layout.hidden.includes(button.dataset.compareToggle); button.classList.toggle('active', visible); button.setAttribute('aria-pressed', String(visible)); });
  };

  const setPaneVisible = (pane, visible) => {
    // At least one section always stays visible: hiding one reveals the other.
    layout.hidden = visible ? [] : [pane];
    applyLayout();
  };

  // Shared drag/keyboard behaviour for a divider. axis() is 'x' or 'y'; set() receives a 20–80 percentage.
  const attachSplitter = (handle, { container, axis, get, set }) => {
    const update = value => { set(clampRatio(value)); applyLayout(); };
    handle.addEventListener('pointerdown', event => {
      if (event.button !== 0) return;
      const box = container();
      event.preventDefault(); handle.setPointerCapture(event.pointerId); box.classList.add('resizing');
      const move = moveEvent => {
        const rect = box.getBoundingClientRect();
        update(axis() === 'x' ? (moveEvent.clientX - rect.left) / rect.width * 100 : (moveEvent.clientY - rect.top) / rect.height * 100);
      };
      const stop = () => { handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', stop); handle.removeEventListener('pointercancel', stop); box.classList.remove('resizing'); };
      handle.addEventListener('pointermove', move); handle.addEventListener('pointerup', stop); handle.addEventListener('pointercancel', stop);
    });
    handle.addEventListener('keydown', event => {
      const [decrease, increase] = axis() === 'x' ? ['ArrowLeft','ArrowRight'] : ['ArrowUp','ArrowDown'];
      const step = event.shiftKey ? 10 : 2;
      if (event.key === decrease) update(get() - step);
      else if (event.key === increase) update(get() + step);
      else if (event.key === 'Home') update(20);
      else if (event.key === 'End') update(80);
      else return;
      event.preventDefault();
    });
    handle.addEventListener('dblclick', () => update(50));
  };

  // Keep the two SQL columns on the same line while each scrolls horizontally on its own.
  const syncVerticalScroll = (first, second) => {
    let syncing = null;
    const follow = (source, target) => source.addEventListener('scroll', () => {
      if (syncing && syncing !== source) return;
      syncing = source; target.scrollTop = source.scrollTop;
      requestAnimationFrame(() => { syncing = null; });
    });
    follow(first, second); follow(second, first);
  };

  // Size the two panes to the space left in the viewport so the page itself does not scroll.
  const fitWorkspace = () => {
    const workspace = byId('db-compare-results');
    if (workspace.classList.contains('d-none')) return;
    const rect = workspace.getBoundingClientRect(), top = rect.top + window.scrollY;
    const container = workspace.closest('.workspace') || workspace.parentElement;
    const below = parseFloat(getComputedStyle(container).paddingBottom) || 0;
    workspace.style.setProperty('--db-compare-offset', `${Math.ceil(top + below + 2)}px`);
  };

  const diffLines = (leftText, rightText) => {
    const left = String(leftText || '').replace(/\r/g, '').split('\n'), right = String(rightText || '').replace(/\r/g, '').split('\n');
    if (left.length * right.length > 40000) return fallbackDiff(left, right);
    const matrix = Array.from({ length:left.length + 1 }, () => new Uint16Array(right.length + 1));
    for (let i = left.length - 1; i >= 0; i--) for (let j = right.length - 1; j >= 0; j--) matrix[i][j] = left[i] === right[j] ? matrix[i+1][j+1] + 1 : Math.max(matrix[i+1][j], matrix[i][j+1]);
    const rows = []; let i = 0, j = 0;
    while (i < left.length || j < right.length) {
      if (i < left.length && j < right.length && left[i] === right[j]) { rows.push({ left:left[i++], right:right[j++], kind:'same', li:i, ri:j }); continue; }
      if (i < left.length && j < right.length && matrix[i+1][j] === matrix[i][j+1]) { rows.push({ left:left[i++], right:right[j++], kind:'changed', li:i, ri:j }); continue; }
      if (j >= right.length || i < left.length && matrix[i+1][j] > matrix[i][j+1]) rows.push({ left:left[i++], right:'', kind:'left', li:i, ri:null });
      else rows.push({ left:'', right:right[j++], kind:'right', li:null, ri:j });
    }
    return rows;
  };
  const fallbackDiff = (left, right) => Array.from({ length:Math.max(left.length,right.length) }, (_,index) => ({ left:left[index]||'', right:right[index]||'', kind:left[index]===right[index]?'same':'changed', li:index<left.length?index+1:null, ri:index<right.length?index+1:null }));

  const sqlColumn = (side, sourceName, rows) => `<div class="dc-sql-col" data-side="${side}">
      <div class="dc-sql-label" title="${escapeHtml(sourceName)}">${side.toUpperCase()} · ${escapeHtml(sourceName)}</div>
      <div class="dc-sql-scroll" tabindex="0" aria-label="${side} definition"><div class="dc-sql-lines">${rows.map(row => {
        const number = side === 'left' ? row.li : row.ri, text = side === 'left' ? row.left : row.right;
        return `<div class="dc-sql-line diff-${row.kind}"><span class="dc-ln">${number ?? ''}</span><code>${highlightSyntax(text, 'sql') || ' '}</code></div>`;
      }).join('')}</div></div>
    </div>`;

  const renderDiff = () => {
    const item = comparison.objects.find(candidate => candidate.key === activeKey);
    if (!item) return byId('db-compare-diff').innerHTML = '<div class="empty-state">Select an object to compare definitions.</div>';
    const rows = diffLines(item.leftDefinition, item.rightDefinition);
    byId('db-compare-diff').innerHTML = `<div class="db-compare-diff-header"><div><span class="${statusClass(item.status)}">${escapeHtml(item.status)}</span><h2>${escapeHtml(item.name)}</h2><p>${escapeHtml(item.type)}${item.note ? ` · ${escapeHtml(item.note)}` : ''}</p></div></div>
      <div class="db-compare-sql-split">
        ${sqlColumn('left', comparison.leftSourceName, rows)}
        <div class="db-compare-sql-splitter" role="separator" tabindex="0" aria-label="Resize left and right SQL" aria-orientation="vertical" aria-valuemin="20" aria-valuemax="80" aria-valuenow="${Math.round(layout.sqlRatio)}" title="Drag to resize · double-click to reset"><span></span></div>
        ${sqlColumn('right', comparison.rightSourceName, rows)}
      </div>`;
    const split = byId('db-compare-diff').querySelector('.db-compare-sql-split');
    const [leftScroll, rightScroll] = split.querySelectorAll('.dc-sql-scroll');
    syncVerticalScroll(leftScroll, rightScroll);
    attachSplitter(split.querySelector('.db-compare-sql-splitter'), { container:() => split, axis:() => 'x', get:() => layout.sqlRatio, set:value => { layout.sqlRatio = value; } });
  };

  const currentScript = () => {
    const items = selectedObjects();
    if (!items.length) return '-- Select one or more changed objects to generate SQL.';
    return `-- Update ${comparison.rightSourceName} to match ${comparison.leftSourceName}\n-- Target provider: ${comparison.targetProvider}\n\n` + items.map(item => `-- ${item.status}: ${item.type} ${item.name}${item.note ? `\n-- NOTE: ${item.note}` : ''}\n${item.updateScript}`).join('\n\n');
  };

  const renderSelectedScript = () => {
    const item = comparison?.objects.find(candidate => candidate.key === activeKey);
    const script = !item ? '-- Select an object to view its update script.' : item.status === 'Unchanged' ? '-- This object is unchanged. No update SQL is required.' : item.updateScript || '-- This object requires manual review.';
    byId('db-compare-selected-script-title').textContent = item ? `${item.status}: ${item.type} ${item.name}` : 'No object selected';
    byId('db-compare-selected-script-code').innerHTML = highlightSyntax(script, 'sql');
  };

  const renderCombinedScript = () => {
    const placeholder = '-- Click Generate SQL to build the update script for all checked objects.';
    byId('db-compare-script-code').innerHTML = highlightSyntax(combinedScriptGenerated ? combinedScript : placeholder, 'sql');
    byId('db-compare-copy-script').disabled = !combinedScriptGenerated;
    byId('db-compare-download-script').disabled = !combinedScriptGenerated;
    const applicable = applicableSelection();
    const update = byId('db-compare-update');
    update.disabled = !combinedScriptGenerated || !applicable.length || Boolean(comparison.warning);
    update.textContent = applicable.length ? `Update right database (${applicable.length})` : 'Update right database';
    byId('db-compare-selected-count').textContent = selectedObjects().length;
  };

  const invalidateCombinedScript = () => { combinedScript = ''; combinedScriptGenerated = false; renderObjectList(); renderCombinedScript(); };
  const generateCombinedScript = () => { combinedScript = currentScript(); combinedScriptGenerated = true; setTab('script'); renderCombinedScript(); notify('SQL generated for the checked objects.'); };

  const copyScript = async () => { if (!combinedScriptGenerated) return; try { await navigator.clipboard.writeText(combinedScript); notify('SQL script copied.'); } catch { notify('Unable to copy the SQL script.', true); } };
  const downloadScript = () => { if (!combinedScriptGenerated) return; const blob = new Blob([combinedScript], { type:'text/sql;charset=utf-8' }), link = document.createElement('a'); link.href = URL.createObjectURL(blob); link.download = `update-${comparison.rightSourceName.replace(/[^a-z0-9]+/gi,'-').toLowerCase()}.sql`; link.click(); URL.revokeObjectURL(link.href); };

  const openUpdate = () => {
    if (!combinedScriptGenerated) { setTab('script'); return notify('Click Generate SQL before updating the right database.', true); }
    const items = applicableSelection(); if (!items.length) return;
    byId('db-compare-update-warning').textContent = `${items.length} selected schema change${items.length === 1 ? '' : 's'} will be executed against ${comparison.rightSourceName}.`;
    byId('db-compare-confirm').value = ''; byId('db-compare-confirm-update').disabled = true; updateModal.show();
  };

  const applyUpdate = async () => {
    const button = byId('db-compare-confirm-update'), keys = applicableSelection().map(item => item.key); button.disabled = true; button.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Updating';
    try { const result = await jsonRequest('/api/database-compare/apply', 'POST', { leftSourceId:comparison.leftSourceId, rightSourceId:comparison.rightSourceId, objectKeys:keys }); updateModal.hide(); notify(result.message); await compare(); }
    catch (error) { notify(error.message, true); button.disabled = false; button.textContent = 'Update right database'; }
  };

  byId('db-compare-add-source').addEventListener('click', () => sourceModal.show());
  byId('db-compare-source-form').addEventListener('submit', async event => { event.preventDefault(); const button = event.submitter, original = button.textContent; button.disabled = true; button.textContent = 'Saving…'; try { const source = await jsonRequest('/api/sql-doc/sources', 'POST', { name:byId('db-source-name').value, provider:byId('db-source-provider').value, connectionString:byId('db-source-connection').value }); sourceModal.hide(); event.target.reset(); await loadSources(source.id); notify('Database connection saved.'); } catch(error) { notify(error.message,true); } finally { button.disabled=false; button.textContent=original; } });
  byId('db-compare-swap').addEventListener('click', () => { const left=byId('db-compare-left'),right=byId('db-compare-right'),value=left.value;left.value=right.value;right.value=value; });
  byId('db-compare-run').addEventListener('click', compare);
  document.querySelectorAll('[data-compare-tab]').forEach(button => button.addEventListener('click', () => setTab(button.dataset.compareTab)));
  window.addEventListener('resize', fitWorkspace);
  document.querySelectorAll('[data-compare-orientation]').forEach(button => button.addEventListener('click', () => { layout.orientation = button.dataset.compareOrientation; applyLayout(); saveLayout(); }));
  document.querySelectorAll('[data-compare-toggle]').forEach(button => button.addEventListener('click', () => setPaneVisible(button.dataset.compareToggle, layout.hidden.includes(button.dataset.compareToggle))));
  document.querySelectorAll('[data-compare-hide]').forEach(button => button.addEventListener('click', () => setPaneVisible(button.dataset.compareHide, false)));
  attachSplitter(document.querySelector('.db-compare-splitter'), { container:() => byId('db-compare-results'), axis:() => layout.orientation === 'horizontal' ? 'x' : 'y', get:paneRatio, set:value => { layout.ratios[layout.orientation] = value; } });
  applyLayout();
  byId('db-compare-search').addEventListener('input', renderObjectList);
  byId('db-compare-select-all').addEventListener('change', event => { comparison.objects.filter(item=>item.canApply).forEach(item=>event.target.checked?selectedKeys.add(item.key):selectedKeys.delete(item.key));invalidateCombinedScript(); });
  byId('db-compare-generate-script').addEventListener('click', generateCombinedScript);
  byId('db-compare-copy-script').addEventListener('click', copyScript); byId('db-compare-download-script').addEventListener('click', downloadScript); byId('db-compare-update').addEventListener('click', openUpdate);
  byId('db-compare-confirm').addEventListener('input', event => byId('db-compare-confirm-update').disabled = event.target.value !== 'UPDATE'); byId('db-compare-confirm-update').addEventListener('click', applyUpdate);
  loadSources().catch(error => notify(error.message, true));
})();
