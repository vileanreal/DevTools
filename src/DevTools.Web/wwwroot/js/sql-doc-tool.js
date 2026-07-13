(() => {
  const { byId, escapeHtml, notify, jsonRequest, highlightSyntax, appUrl, createUuid } = window.devTools;
  const modal = bootstrap.Modal.getOrCreateInstance(byId('sql-source-modal'));
  const diagramObjectsModal = bootstrap.Modal.getOrCreateInstance(byId('sql-diagram-objects-modal'));
  const providerLabels = { sqlserver:'SQL Server', sqlite:'SQLite', mysql:'MySQL', oracle:'Oracle', postgresql:'PostgreSQL' };
  let sources = [], selectedSourceId = null, selectedSchema = null, activeTab = 'details', loadingSourceId = null;
  let diagram = null, savedDiagrams = [], selectedNodeId = null, selectedEdgeId = null, connectDraft = null, dragState = null, edgeDragState = null, panState = null, diagramResizeObserver = null, diagramResizeFrame = null;
  const DIAGRAM_PADDING = 180;
  const DIAGRAM_VIEWPORT_BUFFER = 64;

  const selectedSource = () => sources.find(source => source.id === selectedSourceId);
  const qualified = (schema, name) => schema ? `${schema}.${name}` : name;
  const nodeById = id => diagram?.nodes.find(node => node.id === id);
  const edgeById = id => diagram?.edges.find(edge => edge.id === id);
  const zoomLevel = () => Math.min(2.5, Math.max(.35, Number(diagram?.zoom || 1)));

  const loadSources = async selectId => {
    try {
      sources = await jsonRequest('/api/sql-doc/sources');
      byId('sql-source-count').textContent = sources.length;
      renderSources();
      if (selectId && sources.some(source => source.id === selectId)) inspectSource(selectId);
      else if (!selectedSourceId) byId('sql-doc-workspace').innerHTML = `<div class="empty-state">${sources.length ? 'Select a database source to inspect objects.' : 'Add a database source to inspect objects.'}</div>`;
    } catch (error) { notify(error.message, true); }
  };

  const renderSources = () => {
    byId('sql-source-list').innerHTML = sources.length ? sources.map(source => `
      <button class="sql-source-row ${source.id === selectedSourceId ? 'active' : ''} ${source.id === loadingSourceId ? 'is-loading' : ''}" data-source-id="${source.id}" ${loadingSourceId !== null ? 'disabled' : ''}>
        <span class="system-avatar">${escapeHtml(source.name.charAt(0).toUpperCase())}</span>
        <span class="system-row-main"><strong>${escapeHtml(source.name)}</strong><small>${escapeHtml(providerLabels[source.provider] || source.provider)}</small></span>
        <span class="sql-source-database">${escapeHtml(connectionSummary(source.connectionString))}</span>
        ${source.id === loadingSourceId ? '<span class="spinner-border spinner-border-sm sql-source-spinner" role="status" aria-label="Loading database"></span>' : '<span class="row-arrow">›</span>'}
      </button>`).join('') : '<div class="empty-state">No database sources yet.</div>';
    document.querySelectorAll('.sql-source-row').forEach(button => button.addEventListener('click', () => inspectSource(Number(button.dataset.sourceId))));
  };

  const inspectSource = async id => {
    diagramResizeObserver?.disconnect();
    selectedSourceId = id;
    selectedSchema = null;
    diagram = null;
    selectedNodeId = null;
    selectedEdgeId = null;
    connectDraft = null;
    loadingSourceId = id;
    renderSources();
    byId('sql-doc-workspace').innerHTML = '<div class="sql-database-loading" role="status"><span class="spinner-border text-primary" aria-hidden="true"></span><strong>Loading database</strong><span>Inspecting objects and schema details. Large databases may take a moment.</span></div>';
    try {
      selectedSchema = await jsonRequest(`/api/sql-doc/sources/${id}/schema`);
      savedDiagrams = await jsonRequest(`/api/sql-doc/sources/${id}/diagrams`);
      // Keep the initial canvas light even when a source has a large schema. Saved
      // diagrams remain available from the diagram dropdown and load on demand.
      diagram = createEmptyDiagram();
      diagram.zoom = zoomLevel();
      diagram.name = diagram.name || savedDiagrams[0]?.name || 'Default diagram';
      loadingSourceId = null;
      renderSources();
      renderSchema();
    } catch (error) {
      loadingSourceId = null;
      renderSources();
      const source = selectedSource();
      byId('sql-doc-workspace').innerHTML = `
        <div class="sql-doc-header">
          <div><span class="eyebrow">${escapeHtml(providerLabels[source?.provider] || source?.provider || 'DATABASE')}</span><h1>${escapeHtml(source?.name || 'Database source')}</h1><p class="index-error-text">${escapeHtml(error.message)}</p></div>
          <div class="row-actions"><button id="sql-source-edit" class="btn btn-outline-secondary btn-sm">Edit</button><button id="sql-source-delete" class="btn btn-ghost text-danger btn-sm">Delete</button></div>
        </div>`;
      bindWorkspaceActions();
    }
  };

  const renderSchema = () => {
    const schema = selectedSchema;
    const objects = [
      ['Tables', schema.tables.length],
      ['Views', schema.views.length],
      ['Stored procedures', schema.storedProcedures.length],
      ['Functions', schema.functions.length],
      ['Indexes', schema.indexes.length]
    ];
    byId('sql-doc-workspace').innerHTML = `
      <div class="sql-doc-header">
        <div><span class="eyebrow">${escapeHtml(schema.provider)}</span><h1>${escapeHtml(schema.sourceName)}</h1><p>${escapeHtml(schema.databaseName)}</p></div>
        <div class="row-actions"><button id="sql-source-edit" class="btn btn-outline-secondary btn-sm">Edit</button><button id="sql-source-delete" class="btn btn-ghost text-danger btn-sm">Delete</button></div>
      </div>
      <div class="stats-grid">${objects.map(item => `<div class="stat-card"><strong>${item[1].toLocaleString()}</strong><span>${escapeHtml(item[0])}</span></div>`).join('')}</div>
      <ul class="nav nav-tabs sql-doc-tabs" role="tablist">
        <li class="nav-item"><button class="nav-link ${activeTab === 'details' ? 'active' : ''}" type="button" data-sql-tab="details">Database Details</button></li>
        <li class="nav-item"><button class="nav-link ${activeTab === 'diagram' ? 'active' : ''}" type="button" data-sql-tab="diagram">Database Diagram</button></li>
      </ul>
      <section class="sql-tab-pane ${activeTab === 'details' ? '' : 'd-none'}" data-sql-pane="details">${renderDatabaseDetails(schema)}</section>
      <section class="sql-tab-pane ${activeTab === 'diagram' ? '' : 'd-none'}" data-sql-pane="diagram">${renderDiagramComponent()}</section>`;
    bindWorkspaceActions();
    bindTabs();
    bindDiagram();
  };

  const renderDatabaseDetails = schema => `
    <section class="sql-doc-export">
      <div class="section-heading"><div><h2>Generate document</h2><p>Export the entire database or choose specific objects.</p></div><button id="sql-doc-download" class="btn btn-primary">Download .docx</button></div>
      <div class="sql-export-objects">
        <div class="object-selector-heading"><strong>Select objects</strong><span>SSMS-style database object selection</span></div>
        ${renderObjectScopeSelector('sql-doc', documentObjectGroups(schema))}
      </div>
    </section>
    <section id="sql-object-browser" class="sql-object-browser">
      <div class="section-heading sql-object-browser-heading"><div><h2>Objects</h2><p>Expand an object type to view its database objects.</p></div></div>
      <div id="sql-object-browser-content" class="sql-object-browser-content">${renderObjectBrowserContent(schema)}</div>
    </section>`;

  const objectSections = schema => [
    { key:'tables', label:'Tables', count:schema.tables.length },
    { key:'views', label:'Views', count:schema.views.length },
    { key:'stored-procedures', label:'Stored Procedures', count:schema.storedProcedures.length },
    { key:'functions', label:'Functions', count:schema.functions.length },
    { key:'indexes', label:'Indexes', count:schema.indexes.length }
  ];

  const renderObjectBrowserContent = schema => objectSections(schema).map(section => `
    <details class="sql-object-category" data-object-section="${section.key}">
      <summary><span>${escapeHtml(section.label)}</span><small>${Number(section.count || 0).toLocaleString()}</small></summary>
      <div class="sql-object-category-content">${renderObjectSectionRows(section.key)}</div>
    </details>`).join('');

  const renderDiagramComponent = () => `
    <div class="sql-diagram-shell">
      <div class="sql-diagram-toolbar">
        <div class="diagram-save-group">
          <label class="form-label mb-0 small" for="sql-diagram-name">Diagram</label>
          <input id="sql-diagram-name" class="form-control form-control-sm" maxlength="160" value="${escapeHtml(diagram?.name || 'Default diagram')}">
          <select id="sql-diagram-load" class="form-select form-select-sm">
            <option value="">Saved diagrams</option>
            ${savedDiagrams.map(item => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}
          </select>
        </div>
        <div class="diagram-actions">
          <button id="sql-diagram-save" class="btn btn-primary btn-sm">Save</button>
          <button id="sql-diagram-export" class="btn btn-outline-primary btn-sm">Export PNG</button>
          <button id="sql-diagram-fullscreen" class="btn btn-outline-secondary btn-sm">Fullscreen</button>
          <button id="sql-diagram-choose-objects" class="btn btn-outline-secondary btn-sm">Choose objects</button>
          <button id="sql-diagram-add-table" class="btn btn-outline-secondary btn-sm">+ Table</button>
          <button id="sql-diagram-reset" class="btn btn-ghost btn-sm">Clear canvas</button>
        </div>
      </div>
      <div class="sql-diagram-help">Drag tables to arrange. Drag a side handle to another table or column to connect it. Select a connector anywhere along its line, then drag the blue segment midpoint to reshape it.</div>
      <div class="sql-diagram-layout">
        <aside id="sql-diagram-inspector" class="sql-diagram-inspector">${renderInspector()}</aside>
        <div id="sql-diagram-canvas" class="sql-diagram-canvas">
          <svg id="sql-diagram-links" class="sql-diagram-links"></svg>
          <div id="sql-diagram-nodes" class="sql-diagram-nodes"></div>
        </div>
      </div>
    </div>`;

  const renderObjectRow = (kind, index, title, meta = '') => `
    <button class="sql-object-row" type="button" data-object-kind="${kind}" data-object-index="${index}">
      <span class="sql-object-title"><strong>${escapeHtml(title)}</strong>${meta ? `<small>${escapeHtml(meta)}</small>` : ''}</span><span class="row-arrow">›</span>
    </button>`;

  const renderObjectSectionRows = section => {
    if (section === 'tables') return selectedSchema.tables.map((table, index) => renderObjectRow('table', index, qualified(table.schema, table.name), `${table.columns.length} columns · ${table.foreignKeys.length} FKs`)).join('');
    if (section === 'views') return selectedSchema.views.map((item, index) => renderObjectRow('view', index, qualified(item.schema, item.name))).join('');
    if (section === 'stored-procedures') return selectedSchema.storedProcedures.map((item, index) => renderObjectRow('stored-procedure', index, qualified(item.schema, item.name))).join('');
    if (section === 'functions') return selectedSchema.functions.map((item, index) => renderObjectRow('function', index, qualified(item.schema, item.name))).join('');
    return selectedSchema.indexes.map((item, index) => renderObjectRow('index', index, item.name, qualified(item.schema, item.tableName))).join('');
  };

  const documentObjectGroups = schema => [
    { type:'tables', label:'Tables', items:schema.tables.map(item => ({ value:`table:${qualified(item.schema, item.name)}`, label:qualified(item.schema, item.name), meta:`${item.columns.length} columns` })) },
    { type:'views', label:'Views', items:schema.views.map(item => ({ value:`view:${qualified(item.schema, item.name)}`, label:qualified(item.schema, item.name) })) },
    { type:'procedures', label:'Stored Procedures', items:schema.storedProcedures.map(item => ({ value:`procedure:${qualified(item.schema, item.name)}`, label:qualified(item.schema, item.name) })) },
    { type:'functions', label:'User-Defined Functions', items:schema.functions.map(item => ({ value:`function:${qualified(item.schema, item.name)}`, label:qualified(item.schema, item.name) })) },
    { type:'indexes', label:'Indexes', items:schema.indexes.map(item => ({ value:`index:${item.name}`, label:item.name, meta:qualified(item.schema, item.tableName) })) }
  ];

  const diagramObjectGroups = schema => [
    { type:'tables', label:'Tables', items:schema.tables.map(item => ({ value:qualified(item.schema, item.name), label:qualified(item.schema, item.name), meta:`${item.columns.length} columns` })) }
  ];

  const renderObjectScopeSelector = (prefix, groups, selectedValues = null, scope = 'all') => {
    const selected = selectedValues instanceof Set ? selectedValues : new Set(groups.flatMap(group => group.items.map(item => item.value)));
    return `<div id="${prefix}-object-selector" class="object-scope-selector">
      <div class="object-scope-options">
        <label><input class="form-check-input" type="radio" name="${prefix}-scope" value="all" ${scope === 'all' ? 'checked' : ''}> <span><strong>Entire database</strong><small>Include every available database object.</small></span></label>
        <label><input class="form-check-input" type="radio" name="${prefix}-scope" value="specific" ${scope === 'specific' ? 'checked' : ''}> <span><strong>Select specific database objects</strong><small>Expand object types and choose individual objects.</small></span></label>
      </div>
      <div class="ssms-object-tree ${scope === 'all' ? 'is-disabled' : ''}">
        ${groups.map(group => {
          const checkedCount = group.items.filter(item => selected.has(item.value)).length;
          const groupChecked = group.items.length > 0 && checkedCount === group.items.length;
          return `<div class="object-tree-group" data-object-group="${escapeHtml(group.type)}">
            <div class="object-tree-group-header">
              <button class="object-tree-expander" type="button" aria-expanded="false" aria-label="Expand ${escapeHtml(group.label)}">+</button>
              <label><input class="form-check-input object-tree-group-input" type="checkbox" ${groupChecked ? 'checked' : ''} ${group.items.length ? '' : 'disabled'}> <span class="object-tree-folder" aria-hidden="true">▱</span><strong>${escapeHtml(group.label)}</strong><small>${Number(group.count ?? group.items.length).toLocaleString()}</small></label>
            </div>
            <div class="object-tree-children d-none">${group.items.length ? group.items.map(item => `<label class="object-tree-item"><input class="form-check-input object-tree-item-input" type="checkbox" value="${escapeHtml(item.value)}" ${selected.has(item.value) ? 'checked' : ''}><span>${escapeHtml(item.label)}</span>${item.meta ? `<small>${escapeHtml(item.meta)}</small>` : ''}</label>`).join('') : '<div class="object-tree-empty">No objects</div>'}</div>
          </div>`;
        }).join('')}
      </div>
      <div class="object-tree-actions"><button class="btn btn-outline-secondary btn-sm object-select-all" type="button">Select all</button><button class="btn btn-ghost btn-sm object-select-none" type="button">Deselect all</button><span class="object-selection-summary"></span></div>
    </div>`;
  };

  const bindTabs = () => {
    document.querySelectorAll('[data-sql-tab]').forEach(button => button.addEventListener('click', () => {
      if (button.dataset.sqlTab !== 'diagram') setDiagramFullscreen(false);
      activeTab = button.dataset.sqlTab;
      document.querySelectorAll('[data-sql-tab]').forEach(tab => tab.classList.toggle('active', tab.dataset.sqlTab === activeTab));
      document.querySelectorAll('[data-sql-pane]').forEach(pane => pane.classList.toggle('d-none', pane.dataset.sqlPane !== activeTab));
      if (activeTab === 'diagram') renderDiagram();
    }));
  };

  const bindWorkspaceActions = () => {
    byId('sql-source-edit')?.addEventListener('click', () => openSourceModal(selectedSourceId));
    byId('sql-source-delete')?.addEventListener('click', deleteSelectedSource);
    byId('sql-doc-download')?.addEventListener('click', downloadDocx);
    bindObjectScopeSelector('sql-doc');
    bindObjectBrowser();
  };

  const bindObjectBrowser = () => {
    const browser = byId('sql-object-browser');
    if (!browser) return;
    browser.querySelectorAll('.sql-object-row').forEach(row => row.addEventListener('click', openObjectModal));
  };

  const bindObjectScopeSelector = prefix => {
    const root = byId(`${prefix}-object-selector`);
    if (!root) return;
    const tree = root.querySelector('.ssms-object-tree');
    const itemInputs = () => [...root.querySelectorAll('.object-tree-item-input')];
    const activateSpecific = () => {
      const radio = root.querySelector(`input[name="${prefix}-scope"][value="specific"]`);
      if (radio) radio.checked = true;
      refreshScope();
    };
    const updateGroup = group => {
      const parent = group.querySelector('.object-tree-group-input');
      const children = [...group.querySelectorAll('.object-tree-item-input')];
      const selected = children.filter(input => input.checked).length;
      if (!parent) return;
      parent.checked = children.length > 0 && selected === children.length;
      parent.indeterminate = selected > 0 && selected < children.length;
    };
    const updateSummary = () => {
      root.querySelectorAll('.object-tree-group').forEach(updateGroup);
      const selected = itemInputs().filter(input => input.checked).length;
      const total = itemInputs().length;
      const target = root.querySelector('.object-selection-summary');
      if (target) target.textContent = `${selected.toLocaleString()} of ${total.toLocaleString()} selected`;
    };
    const refreshScope = () => {
      const specific = root.querySelector(`input[name="${prefix}-scope"]:checked`)?.value === 'specific';
      tree?.classList.toggle('is-disabled', !specific);
      tree?.querySelectorAll('input,button').forEach(control => control.disabled = !specific || (control.classList.contains('object-tree-group-input') && !control.closest('.object-tree-group')?.querySelector('.object-tree-item-input')));
      root.querySelectorAll('.object-select-all,.object-select-none').forEach(button => button.disabled = !specific);
      updateSummary();
    };
    root.querySelectorAll(`input[name="${prefix}-scope"]`).forEach(radio => radio.addEventListener('change', refreshScope));
    root.querySelectorAll('.object-tree-expander').forEach(button => button.addEventListener('click', () => {
      const children = button.closest('.object-tree-group')?.querySelector('.object-tree-children');
      const expanded = button.getAttribute('aria-expanded') !== 'true';
      button.setAttribute('aria-expanded', String(expanded));
      button.textContent = expanded ? '−' : '+';
      children?.classList.toggle('d-none', !expanded);
    }));
    root.querySelectorAll('.object-tree-group-input').forEach(input => input.addEventListener('change', () => {
      input.closest('.object-tree-group')?.querySelectorAll('.object-tree-item-input').forEach(child => child.checked = input.checked);
      updateSummary();
    }));
    itemInputs().forEach(input => input.addEventListener('change', updateSummary));
    root.querySelector('.object-select-all')?.addEventListener('click', () => { activateSpecific(); itemInputs().forEach(input => input.checked = true); updateSummary(); });
    root.querySelector('.object-select-none')?.addEventListener('click', () => { activateSpecific(); itemInputs().forEach(input => input.checked = false); updateSummary(); });
    refreshScope();
  };

  const openObjectModal = event => {
    const row = event.currentTarget;
    const index = Number(row.dataset.objectIndex);
    const kind = row.dataset.objectKind;
    let item = null, body = '';
    if (kind === 'table') {
      item = selectedSchema.tables[index];
      if (item) body = `<div class="table-responsive"><table class="table log-table"><thead><tr><th>Column</th><th>Type</th><th>Nullable</th><th>PK</th><th>Default</th></tr></thead><tbody>${item.columns.map(column => `<tr><td><strong>${escapeHtml(column.name)}</strong></td><td>${escapeHtml(column.dataType)}</td><td>${column.isNullable ? 'Yes' : 'No'}</td><td>${column.isPrimaryKey ? 'Yes' : ''}</td><td>${escapeHtml(column.defaultValue || '')}</td></tr>`).join('')}</tbody></table></div>${item.foreignKeys.length ? `<div class="sql-fk-list">${item.foreignKeys.map(fk => `<span>${escapeHtml(fk.columnName)} → ${escapeHtml(fk.referencedTable)}.${escapeHtml(fk.referencedColumn)}</span>`).join('')}</div>` : ''}`;
    } else if (kind === 'index') {
      item = selectedSchema.indexes[index];
      if (item) body = `<div class="table-responsive"><table class="table log-table"><tbody><tr><th>Table</th><td>${escapeHtml(qualified(item.schema, item.tableName))}</td></tr><tr><th>Unique</th><td>${item.isUnique ? 'Yes' : 'No'}</td></tr><tr><th>Columns</th><td>${escapeHtml(item.columns.join(', '))}</td></tr></tbody></table></div>`;
    } else {
      const collection = kind === 'view' ? selectedSchema.views : kind === 'stored-procedure' ? selectedSchema.storedProcedures : selectedSchema.functions;
      item = collection[index];
      if (item) body = item.definition ? `<pre>${escapeHtml(item.definition)}</pre>` : '<p class="text-muted small mb-0 p-3">No definition returned by provider.</p>';
    }
    if (!item) return;
    const title = kind === 'index' ? item.name : qualified(item.schema, item.name);
    byId('sql-object-modal-title').textContent = title;
    const modalBody = byId('sql-object-modal-body');
    modalBody.innerHTML = body || '<p class="text-muted">No details available.</p>';
    modalBody.querySelectorAll('pre').forEach(code => {
      const source = code.textContent || '';
      code.classList.add('syntax-code');
      code.innerHTML = highlightSyntax(source, 'sql');
    });
    bootstrap.Modal.getOrCreateInstance(byId('sql-object-modal')).show();
  };

  const bindDiagram = () => {
    if (!byId('sql-diagram-canvas')) return;
    diagramResizeObserver?.disconnect();
    diagramResizeObserver = new ResizeObserver(() => {
      cancelAnimationFrame(diagramResizeFrame);
      diagramResizeFrame = requestAnimationFrame(() => renderDiagram());
    });
    diagramResizeObserver.observe(byId('sql-diagram-canvas'));
    byId('sql-diagram-save')?.addEventListener('click', saveDiagram);
    byId('sql-diagram-export')?.addEventListener('click', exportDiagramPng);
    byId('sql-diagram-fullscreen')?.addEventListener('click', toggleDiagramFullscreen);
    byId('sql-diagram-choose-objects')?.addEventListener('click', openDiagramObjectSelector);
    byId('sql-diagram-add-table')?.addEventListener('click', addDiagramTable);
    byId('sql-diagram-reset')?.addEventListener('click', () => {
      if (!confirm('Clear every table and connector from this canvas? Unsaved layout changes will be lost.')) return;
      diagram = createEmptyDiagram(diagram?.name);
      selectedNodeId = null;
      selectedEdgeId = null;
      connectDraft = null;
      refreshDiagramUi();
    });
    byId('sql-diagram-name')?.addEventListener('input', event => diagram.name = event.target.value || 'Default diagram');
    byId('sql-diagram-load')?.addEventListener('change', async event => {
      if (!event.target.value) return;
      try {
        const saved = await jsonRequest(`/api/sql-doc/sources/${selectedSourceId}/diagrams/${event.target.value}`);
        diagram = normalizeDiagram(JSON.parse(saved.diagramJson));
        diagram.zoom = zoomLevel();
        diagram.name = saved.name;
        selectedNodeId = null;
        selectedEdgeId = null;
        connectDraft = null;
        refreshDiagramUi();
      } catch (error) { notify(error.message, true); }
    });
    bindInspector();
    renderDiagram();
  };

  const toggleDiagramFullscreen = () => {
    const shell = document.querySelector('.sql-diagram-shell');
    setDiagramFullscreen(!shell?.classList.contains('fullscreen'));
    setTimeout(renderDiagram, 0);
  };

  const setDiagramFullscreen = fullscreen => {
    const shell = document.querySelector('.sql-diagram-shell');
    if (!shell) return;
    shell.classList.toggle('fullscreen', fullscreen);
    document.body.classList.toggle('diagram-fullscreen-active', fullscreen);
    byId('sql-diagram-fullscreen').textContent = fullscreen ? 'Exit fullscreen' : 'Fullscreen';
  };

  const openDiagramObjectSelector = () => {
    if (!selectedSchema || !diagram) return;
    const databaseIds = new Set(selectedSchema.tables.map(table => qualified(table.schema, table.name)));
    const selected = new Set(diagram.nodes.filter(node => databaseIds.has(node.id) && !node.hidden).map(node => node.id));
    const scope = databaseIds.size > 0 && selected.size === databaseIds.size ? 'all' : 'specific';
    byId('sql-diagram-objects-body').innerHTML = renderObjectScopeSelector('sql-diagram', diagramObjectGroups(selectedSchema), selected, scope);
    bindObjectScopeSelector('sql-diagram');
    diagramObjectsModal.show();
  };

  const applyDiagramObjectSelection = () => {
    const root = byId('sql-diagram-object-selector');
    if (!root || !selectedSchema || !diagram) return;
    const scope = root.querySelector('input[name="sql-diagram-scope"]:checked')?.value || 'all';
    const selected = new Set([...root.querySelectorAll('.object-tree-item-input:checked')].map(input => input.value));
    const databaseIds = new Set(selectedSchema.tables.map(table => qualified(table.schema, table.name)));
    const selectedIds = scope === 'all' ? databaseIds : selected;
    const defaults = createDefaultDiagram(selectedSchema, selectedIds);

    // Keep custom tables and existing positions, but do not retain thousands of
    // unselected database nodes in memory.
    diagram.nodes = diagram.nodes.filter(node => !databaseIds.has(node.id) || selectedIds.has(node.id));
    defaults.nodes.filter(node => selectedIds.has(node.id)).forEach(node => {
      const existing = nodeById(node.id);
      if (existing) existing.hidden = false;
      else diagram.nodes.push(node);
    });
    const activeIds = new Set(diagram.nodes.map(node => node.id));
    diagram.edges = diagram.edges.filter(edge => activeIds.has(edge.from) && activeIds.has(edge.to));
    defaults.edges.filter(edge => selectedIds.has(edge.from) && selectedIds.has(edge.to)).forEach(edge => {
      const exists = diagram.edges.some(current => current.from === edge.from && current.to === edge.to && current.fromColumn === edge.fromColumn && current.toColumn === edge.toColumn);
      if (!exists) diagram.edges.push(edge);
    });
    selectedNodeId = null;
    selectedEdgeId = null;
    connectDraft = null;
    diagramObjectsModal.hide();
    refreshDiagramUi();
  };

  const createDefaultDiagram = (schema, includedIds = null) => {
    const includedTables = includedIds instanceof Set
      ? schema.tables.filter(table => includedIds.has(qualified(table.schema, table.name)))
      : schema.tables;
    const nodes = includedTables.map((table, index) => {
      const column = index % 3, row = Math.floor(index / 3);
      const id = qualified(table.schema, table.name);
      return {
        id,
        schema: table.schema,
        name: table.name,
        x: DIAGRAM_PADDING + column * 280,
        y: DIAGRAM_PADDING + row * 230,
        width: 230,
        color: '#f8fafc',
        borderColor: '#d0d5dd',
        hidden: false,
        collapsed: false,
        columns: table.columns.map(column => ({ name: column.name, type: column.dataType, pk: column.isPrimaryKey, hidden: false }))
      };
    });
    const edges = [];
    includedTables.forEach(table => {
      const from = qualified(table.schema, table.name);
      table.foreignKeys.forEach(fk => {
        const to = resolveReferencedNodeId(nodes, fk.referencedTable);
        if (!to) return;
        edges.push({ id: createUuid(), from, to, fromColumn: fk.columnName, toColumn: fk.referencedColumn, fromSide: 'right', toSide: 'left', label: fk.name || 'FK', kind: 'FK', relationship: '1:N', waypoints: [] });
      });
    });
    return { name: 'Default diagram', zoom: 1, routingVersion: 2, layoutVersion: 2, nodes, edges };
  };

  const createEmptyDiagram = (name = 'Default diagram') => ({
    name: name || 'Default diagram',
    zoom: 1,
    routingVersion: 2,
    layoutVersion: 2,
    nodes: [],
    edges: []
  });

  const normalizeDiagram = value => {
    const normalized = value && Array.isArray(value.nodes) && Array.isArray(value.edges) ? value : createEmptyDiagram();
    if (normalized.routingVersion !== 2) {
      normalized.edges.forEach(edge => edge.waypoints = []);
      normalized.routingVersion = 2;
    }
    normalized.edges.forEach(edge => {
      edge.waypoints = Array.isArray(edge.waypoints)
        ? edge.waypoints.filter(point => Number.isFinite(Number(point.x)) && Number.isFinite(Number(point.y))).map(point => ({ x:Number(point.x), y:Number(point.y) }))
        : [];
      if (!edge.relationship || (edge.kind === 'FK' && edge.relationship === 'N:1')) edge.relationship = '1:N';
    });
    if (normalized.layoutVersion !== 2) {
      const shift = DIAGRAM_PADDING - 40;
      normalized.nodes.forEach(node => { node.x = Number(node.x || 0) + shift; node.y = Number(node.y || 0) + shift; });
      normalized.edges.forEach(edge => edge.waypoints.forEach(point => { point.x += shift; point.y += shift; }));
      normalized.layoutVersion = 2;
    }
    return normalized;
  };

  const resolveReferencedNodeId = (nodes, reference) => nodes.find(node => node.id === reference || node.name === reference || `${node.schema}.${node.name}` === reference)?.id;

  const renderDiagram = () => {
    const canvas = byId('sql-diagram-canvas'), nodesHost = byId('sql-diagram-nodes'), links = byId('sql-diagram-links');
    if (!canvas || !nodesHost || !links || !diagram) return;
    const zoom = zoomLevel();
    const visibleNodes = diagram.nodes.filter(node => !node.hidden);
    const controlPoints = diagram.edges.flatMap(edge => Array.isArray(edge.waypoints) ? edge.waypoints : []);
    const maxX = Math.max(1200, Math.ceil((canvas.offsetWidth + DIAGRAM_VIEWPORT_BUFFER) / zoom), ...visibleNodes.map(node => node.x + node.width + DIAGRAM_PADDING), ...controlPoints.map(point => point.x + DIAGRAM_PADDING));
    const maxY = Math.max(840, Math.ceil((canvas.offsetHeight + DIAGRAM_VIEWPORT_BUFFER) / zoom), ...visibleNodes.map(node => node.y + nodeHeight(node) + DIAGRAM_PADDING), ...controlPoints.map(point => point.y + DIAGRAM_PADDING));
    canvas.style.minWidth = '';
    canvas.style.minHeight = '';
    links.setAttribute('width', maxX);
    links.setAttribute('height', maxY);
    links.setAttribute('viewBox', `0 0 ${maxX} ${maxY}`);
    links.style.width = `${Math.round(maxX * zoom)}px`;
    links.style.height = `${Math.round(maxY * zoom)}px`;
    nodesHost.style.width = `${Math.round(maxX * zoom)}px`;
    nodesHost.style.height = `${Math.round(maxY * zoom)}px`;
    nodesHost.innerHTML = visibleNodes.length
      ? visibleNodes.map(renderNode).join('')
      : '<div class="diagram-blank-state"><strong>Blank canvas</strong><span>Choose only the database objects you want to diagram.</span><button type="button" class="btn btn-primary btn-sm">Choose objects</button></div>';
    links.innerHTML = renderEdges();
    nodesHost.querySelectorAll('.diagram-table').forEach(element => {
      element.addEventListener('pointerdown', startDrag);
      element.addEventListener('click', event => {
        event.stopPropagation();
      });
    });
    nodesHost.querySelectorAll('.diagram-collapse').forEach(button => button.addEventListener('click', event => {
      event.stopPropagation();
      const node = nodeById(button.dataset.nodeId);
      if (!node) return;
      node.collapsed = !node.collapsed;
      selectedNodeId = node.id;
      refreshInspector();
      renderDiagram();
    }));
    nodesHost.querySelectorAll('.diagram-connect-handle').forEach(button => button.addEventListener('pointerdown', startConnectorDrag));
    nodesHost.querySelector('.diagram-blank-state button')?.addEventListener('click', event => {
      event.stopPropagation();
      openDiagramObjectSelector();
    });
    bindEdgeInteractions(links);
    canvas.oncontextmenu = event => event.preventDefault();
    canvas.onpointerdown = startCanvasPan;
    canvas.onwheel = zoomCanvas;
    canvas.onclick = event => {
      if (event.target.closest?.('.diagram-table')) return;
      selectedNodeId = null;
      selectedEdgeId = null;
      connectDraft = null;
      refreshInspector();
      renderDiagram();
    };
  };

  const renderNode = node => {
    const visibleColumns = node.columns.filter(column => !column.hidden);
    const sourceClass = connectDraft?.nodeId === node.id ? 'connect-source' : '';
    const zoom = zoomLevel();
    return `
      <div class="diagram-table ${node.id === selectedNodeId ? 'selected' : ''} ${sourceClass}" data-node-id="${escapeHtml(node.id)}" style="left:${node.x * zoom}px;top:${node.y * zoom}px;width:${node.width}px;transform:scale(${zoom});--table-color:${escapeHtml(node.color)};--table-border:${escapeHtml(node.borderColor)}">
        <div class="diagram-table-title">${renderConnector(node.id, '', 'left')}<span>▦</span><strong>${escapeHtml(node.id)}</strong><button class="diagram-collapse" type="button" data-node-id="${escapeHtml(node.id)}" title="${node.collapsed ? 'Show columns' : 'Hide columns'}">${node.collapsed ? '+' : '−'}</button>${renderConnector(node.id, '', 'right')}</div>
        <div class="diagram-columns ${node.collapsed ? 'd-none' : ''}">
          ${visibleColumns.length ? visibleColumns.map(column => `<div class="diagram-column">${renderConnector(node.id, column.name, 'left')}<span>${column.pk ? '🔑' : '•'}</span><span>${escapeHtml(column.name)}</span><small>${escapeHtml(column.type || '')}</small>${renderConnector(node.id, column.name, 'right')}</div>`).join('') : '<div class="diagram-column muted">All columns hidden</div>'}
        </div>
      </div>`;
  };

  const renderConnector = (nodeId, column, side) =>
    `<button class="diagram-connect-handle ${side}" type="button" title="${column ? `Connect ${column}` : `Connect ${nodeId}`}" data-node-id="${escapeHtml(nodeId)}" data-column="${escapeHtml(column)}" data-side="${side}"></button>`;

  const renderEdges = () => {
    const visible = new Set(diagram.nodes.filter(node => !node.hidden).map(node => node.id));
    return `
      ${diagram.edges.filter(edge => visible.has(edge.from) && visible.has(edge.to)).map(edge => {
        const points = edgePoints(edge);
        if (points.length < 2) return '';
        const path = points.map((point, index) => `${index === 0 ? 'M' : 'L'} ${point.x} ${point.y}`).join(' ');
        const segmentHandles = renderSegmentHandles(edge, points);
        return `<path d="${path}" class="diagram-edge-hit" data-edge-id="${edge.id}"></path><path d="${path}" class="diagram-edge ${edge.id === selectedEdgeId ? 'selected' : ''}" data-edge-id="${edge.id}"></path>${renderRelationshipEndpoints(edge, points)}${edge.id === selectedEdgeId ? segmentHandles : ''}`;
      }).join('')}${renderConnectionPreview()}`;
  };

  const renderRelationshipEndpoints = (edge, points) => {
    const [from, to] = cardinalityParts(edge.relationship || edge.kind || '1:N');
    const selected = edge.id === selectedEdgeId ? ' selected' : '';
    return `<path class="diagram-relation-symbol${selected}" d="${relationshipEndpointPath(points[0], points[1], from)}"></path><path class="diagram-relation-symbol${selected}" d="${relationshipEndpointPath(points[points.length - 1], points[points.length - 2], to)}"></path>`;
  };

  const relationshipEndpointPath = (endpoint, adjacent, cardinality) => {
    const dx = adjacent.x - endpoint.x, dy = adjacent.y - endpoint.y;
    const length = Math.max(1, Math.hypot(dx, dy));
    const unitX = dx / length, unitY = dy / length;
    const normalX = -unitY, normalY = unitX;
    const point = (distance, normal = 0) => ({ x:endpoint.x + unitX * distance + normalX * normal, y:endpoint.y + unitY * distance + normalY * normal });
    const value = point => `${point.x.toFixed(2)} ${point.y.toFixed(2)}`;
    if (cardinality === '1') {
      const top = point(5, 7), bottom = point(5, -7);
      return `M ${value(top)} L ${value(bottom)}`;
    }
    const upper = point(1, 7), center = point(1), lower = point(1, -7), apex = point(11);
    return `M ${value(upper)} L ${value(apex)} M ${value(center)} L ${value(apex)} M ${value(lower)} L ${value(apex)}`;
  };

  const renderSegmentHandles = (edge, points) => points.slice(0, -1).map((start, index) => {
    if (index === 0 || index === points.length - 2) return '';
    const end = points[index + 1];
    const length = Math.abs(end.x - start.x) + Math.abs(end.y - start.y);
    if (length < 28) return '';
    const x = Math.round((start.x + end.x) / 2), y = Math.round((start.y + end.y) / 2);
    const orientation = start.x === end.x ? 'vertical' : 'horizontal';
    return `<circle class="diagram-edge-segment-handle ${orientation}" cx="${x}" cy="${y}" r="5" data-edge-id="${edge.id}" data-segment-index="${index}"><title>Drag to move this ${orientation} segment</title></circle>`;
  }).join('');

  const bindEdgeInteractions = links => {
    links.querySelectorAll('.diagram-edge-segment-handle').forEach(handle => handle.addEventListener('pointerdown', startEdgeSegmentDrag));
    links.querySelectorAll('.diagram-edge,.diagram-edge-hit').forEach(path => {
      path.addEventListener('click', selectEdge);
      path.addEventListener('dblclick', addWaypointFromEdge);
    });
  };

  const renderConnectionPreview = () => {
    if (!connectDraft?.currentPoint) return '';
    const start = connectionPoint({ from: connectDraft.nodeId, fromColumn: connectDraft.column, fromSide: connectDraft.side }, 'from');
    if (!start) return '';
    const end = connectDraft.currentPoint;
    const endSide = end.x >= start.x ? 'left' : 'right';
    const points = automaticOrthogonalRoute(start, end, connectDraft.side, endSide);
    const path = points.map((point, index) => `${index === 0 ? 'M' : 'L'} ${point.x} ${point.y}`).join(' ');
    return `<path d="${path}" class="diagram-edge preview"></path>`;
  };

  const edgePoints = edge => {
    const start = connectionPoint(edge, 'from');
    const end = connectionPoint(edge, 'to');
    if (!start || !end) return [];
    const waypoints = Array.isArray(edge.waypoints) ? edge.waypoints : [];
    return waypoints.length
      ? routeThroughWaypoints(start, end, waypoints, edge.fromSide || 'right', edge.toSide || 'left')
      : automaticOrthogonalRoute(start, end, edge.fromSide || 'right', edge.toSide || 'left');
  };

  const automaticOrthogonalRoute = (start, end, fromSide, toSide) => {
    const clearance = 32;
    const startLead = { x:start.x + (fromSide === 'left' ? -clearance : clearance), y:start.y };
    const endLead = { x:end.x + (toSide === 'left' ? -clearance : clearance), y:end.y };
    const points = [start, startLead];

    if (startLead.y !== endLead.y) {
      const facingWithSpace =
        (fromSide === 'right' && toSide === 'left' && startLead.x <= endLead.x) ||
        (fromSide === 'left' && toSide === 'right' && startLead.x >= endLead.x);
      const routeX = facingWithSpace
        ? Math.round((startLead.x + endLead.x) / 2)
        : fromSide === 'right'
          ? Math.max(startLead.x, endLead.x) + clearance
          : Math.min(startLead.x, endLead.x) - clearance;
      points.push({ x:routeX, y:startLead.y }, { x:routeX, y:endLead.y });
    }

    points.push(endLead, end);
    return simplifyOrthogonalPoints(points);
  };

  const routeThroughWaypoints = (start, end, waypoints, fromSide, toSide) => {
    const clearance = 32;
    const startLead = { x:start.x + (fromSide === 'left' ? -clearance : clearance), y:start.y };
    const endLead = { x:end.x + (toSide === 'left' ? -clearance : clearance), y:end.y };
    const targets = [...waypoints, endLead];
    const points = [start, startLead];

    targets.forEach((target, index) => {
      const previous = points[points.length - 1];
      if (previous.x !== target.x && previous.y !== target.y) {
        points.push(index === targets.length - 1
          ? { x:previous.x, y:target.y }
          : { x:target.x, y:previous.y });
      }
      points.push({ x:target.x, y:target.y });
    });
    points.push(end);
    return simplifyOrthogonalPoints(points);
  };

  const simplifyOrthogonalPoints = points => {
    const unique = points.reduce((result, point) => {
      const normalized = { x:Math.round(point.x), y:Math.round(point.y) };
      const previous = result[result.length - 1];
      if (!previous || previous.x !== normalized.x || previous.y !== normalized.y) result.push(normalized);
      return result;
    }, []);
    for (let index = unique.length - 2; index > 0; index--) {
      const before = unique[index - 1], current = unique[index], after = unique[index + 1];
      if ((before.x === current.x && current.x === after.x) || (before.y === current.y && current.y === after.y)) unique.splice(index, 1);
    }
    return unique;
  };

  const connectionPoint = (edge, endpoint) => {
    const node = nodeById(endpoint === 'from' ? edge.from : edge.to);
    if (!node) return null;
    const side = (endpoint === 'from' ? edge.fromSide : edge.toSide) || (endpoint === 'from' ? 'right' : 'left');
    const columnName = endpoint === 'from' ? edge.fromColumn : edge.toColumn;
    const column = columnName ? node.columns.find(item => item.name === columnName) : null;
    const visibleColumns = node.columns.filter(item => !item.hidden);
    const columnIndex = column && !node.collapsed && !column.hidden ? visibleColumns.findIndex(item => item.name === columnName) : -1;
    const renderedColumn = columnIndex >= 0 ? columnName : '';
    const nodeElement = [...(byId('sql-diagram-nodes')?.querySelectorAll('.diagram-table') || [])].find(element => element.dataset.nodeId === node.id);
    const handle = [...(nodeElement?.querySelectorAll('.diagram-connect-handle') || [])].find(element => element.dataset.side === side && element.dataset.column === renderedColumn);
    const canvas = byId('sql-diagram-canvas');
    if (handle && canvas) {
      const handleRect = handle.getBoundingClientRect();
      if (handleRect.width === 0 && handleRect.height === 0) {
        const y = columnIndex >= 0 ? node.y + 34 + columnIndex * 24 + 12 : node.y + 15;
        return { x:side === 'left' ? node.x : node.x + node.width, y };
      }
      const canvasRect = canvas.getBoundingClientRect();
      const zoom = zoomLevel();
      return {
        x:side === 'left' ? node.x : node.x + node.width,
        y:(handleRect.top + handleRect.height / 2 - canvasRect.top - canvas.clientTop + canvas.scrollTop) / zoom
      };
    }
    const y = columnIndex >= 0 ? node.y + 34 + columnIndex * 24 + 12 : node.y + 15;
    return { x:side === 'left' ? node.x : node.x + node.width, y };
  };

  const cardinalityParts = relationship => ({
    '1:1': ['1', '1'],
    '1:N': ['1', '∞'],
    'N:1': ['∞', '1'],
    'N:N': ['∞', '∞']
  })[String(relationship).toUpperCase()] || ['1', '∞'];

  const nodeHeight = node => node.collapsed ? 32 : 34 + Math.max(1, node.columns.filter(column => !column.hidden).length) * 24;

  const startDrag = event => {
    if (event.button !== 0) return;
    if (!event.target.closest('.diagram-table')) return;
    if (event.target.closest('.diagram-collapse,.diagram-connect-handle')) return;
    event.stopPropagation();
    const element = event.currentTarget, node = nodeById(element.dataset.nodeId);
    if (!node) return;
    dragState = { element, node, pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, originalX: node.x, originalY: node.y, moved: false };
    document.addEventListener('pointermove', dragMove);
    document.addEventListener('pointerup', endDrag, { once: true });
  };

  const dragMove = event => {
    if (!dragState || event.pointerId !== dragState.pointerId) return;
    const deltaX = event.clientX - dragState.startX;
    const deltaY = event.clientY - dragState.startY;
    dragState.moved = dragState.moved || Math.abs(deltaX) > 4 || Math.abs(deltaY) > 4;
    const zoom = zoomLevel();
    dragState.node.x = Math.max(DIAGRAM_PADDING, dragState.originalX + deltaX / zoom);
    dragState.node.y = Math.max(DIAGRAM_PADDING, dragState.originalY + deltaY / zoom);
    dragState.element.style.left = `${dragState.node.x * zoom}px`;
    dragState.element.style.top = `${dragState.node.y * zoom}px`;
    const links = byId('sql-diagram-links');
    if (links) links.innerHTML = renderEdges();
  };

  const endDrag = event => {
    document.removeEventListener('pointermove', dragMove);
    const nodeId = dragState?.node.id;
    const wasClick = dragState && !dragState.moved;
    dragState = null;
    if (wasClick && nodeId) selectNode(nodeId);
    else renderDiagram();
  };

  const selectNode = id => {
    selectedNodeId = id;
    selectedEdgeId = null;
    refreshInspector();
    renderDiagram();
  };

  const startConnectorDrag = event => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    const button = event.currentTarget;
    connectDraft = { nodeId: button.dataset.nodeId, column: button.dataset.column || '', side: button.dataset.side || 'right', currentPoint: canvasPoint(event) };
    selectedNodeId = connectDraft.nodeId;
    selectedEdgeId = null;
    document.addEventListener('pointermove', dragConnector);
    document.addEventListener('pointerup', endConnectorDrag, { once: true });
    const links = byId('sql-diagram-links');
    if (links) links.innerHTML = renderEdges();
  };

  const dragConnector = event => {
    if (!connectDraft) return;
    connectDraft.currentPoint = canvasPoint(event);
    const links = byId('sql-diagram-links');
    if (links) links.innerHTML = renderEdges();
  };

  const endConnectorDrag = event => {
    document.removeEventListener('pointermove', dragConnector);
    const target = document.elementFromPoint(event.clientX, event.clientY)?.closest?.('.diagram-connect-handle');
    if (connectDraft && target) {
      const endpoint = { nodeId: target.dataset.nodeId, column: target.dataset.column || '', side: target.dataset.side || 'left' };
      if (connectDraft.nodeId !== endpoint.nodeId || connectDraft.column !== endpoint.column || connectDraft.side !== endpoint.side) {
        addRelationship(connectDraft, endpoint);
      }
    }
    connectDraft = null;
    refreshInspector();
    renderDiagram();
  };

  const selectEdge = event => {
    event.stopPropagation();
    selectedEdgeId = event.currentTarget.dataset.edgeId;
    selectedNodeId = null;
    connectDraft = null;
    refreshInspector();
    renderDiagram();
  };

  const addWaypointFromEdge = event => {
    event.stopPropagation();
    const edge = edgeById(event.currentTarget.dataset.edgeId);
    if (!edge) return;
    addManualWaypoint(edge, canvasPoint(event));
    selectedEdgeId = edge.id;
    selectedNodeId = null;
    refreshInspector();
    renderDiagram();
  };

  const startEdgeSegmentDrag = event => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    const edge = edgeById(event.currentTarget.dataset.edgeId);
    const route = edge ? edgePoints(edge) : [];
    const segmentIndex = Number(event.currentTarget.dataset.segmentIndex);
    if (!edge || route.length < 2 || !route[segmentIndex + 1]) return;
    selectedEdgeId = edge.id;
    selectedNodeId = null;
    edgeDragState = {
      mode:'segment', edge, segmentIndex, pointerId:event.pointerId,
      orientation:route[segmentIndex].x === route[segmentIndex + 1].x ? 'vertical' : 'horizontal',
      route:route.map(point => ({ ...point }))
    };
    document.addEventListener('pointermove', dragEdgeSegment);
    document.addEventListener('pointerup', endEdgeSegmentDrag, { once:true });
  };

  const dragEdgeSegment = event => {
    if (!edgeDragState || edgeDragState.mode !== 'segment' || event.pointerId !== edgeDragState.pointerId) return;
    const state = edgeDragState;
    const route = state.route.map(point => ({ ...point }));
    const point = canvasPoint(event);
    if (state.orientation === 'vertical') {
      route[state.segmentIndex].x = point.x;
      route[state.segmentIndex + 1].x = point.x;
    } else {
      route[state.segmentIndex].y = point.y;
      route[state.segmentIndex + 1].y = point.y;
    }
    const simplified = simplifyOrthogonalPoints(route);
    const controls = simplified.slice(2, -2);
    if (state.segmentIndex === 1 && simplified[1]) controls.unshift(simplified[1]);
    if (state.segmentIndex + 1 === state.route.length - 2 && simplified[simplified.length - 2]) controls.push(simplified[simplified.length - 2]);
    state.edge.waypoints = controls;
    const links = byId('sql-diagram-links');
    if (links) { links.innerHTML = renderEdges(); bindEdgeInteractions(links); }
  };

  const endEdgeSegmentDrag = () => {
    document.removeEventListener('pointermove', dragEdgeSegment);
    edgeDragState = null;
    refreshInspector();
    renderDiagram();
  };

  const canvasPoint = event => {
    const canvas = byId('sql-diagram-canvas');
    const rect = canvas.getBoundingClientRect();
    const zoom = zoomLevel();
    return { x: Math.max(0, Math.round((event.clientX - rect.left - canvas.clientLeft + canvas.scrollLeft) / zoom)), y: Math.max(0, Math.round((event.clientY - rect.top - canvas.clientTop + canvas.scrollTop) / zoom)) };
  };

  const startCanvasPan = event => {
    if (event.button !== 2) return;
    event.preventDefault();
    panState = { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, scrollLeft: event.currentTarget.scrollLeft, scrollTop: event.currentTarget.scrollTop, canvas: event.currentTarget };
    event.currentTarget.classList.add('panning');
    document.addEventListener('pointermove', panCanvas);
    document.addEventListener('pointerup', endCanvasPan, { once: true });
  };

  const panCanvas = event => {
    if (!panState || event.pointerId !== panState.pointerId) return;
    panState.canvas.scrollLeft = panState.scrollLeft - (event.clientX - panState.startX);
    panState.canvas.scrollTop = panState.scrollTop - (event.clientY - panState.startY);
  };

  const endCanvasPan = () => {
    panState?.canvas.classList.remove('panning');
    document.removeEventListener('pointermove', panCanvas);
    panState = null;
  };

  const zoomCanvas = event => {
    event.preventDefault();
    const canvas = event.currentTarget;
    const before = zoomLevel();
    const rect = canvas.getBoundingClientRect();
    const pointerX = event.clientX - rect.left - canvas.clientLeft;
    const pointerY = event.clientY - rect.top - canvas.clientTop;
    const worldX = (pointerX + canvas.scrollLeft) / before;
    const worldY = (pointerY + canvas.scrollTop) / before;
    diagram.zoom = Math.min(2.5, Math.max(.35, before * (event.deltaY < 0 ? 1.1 : .9)));
    renderDiagram();
    const after = zoomLevel();
    canvas.scrollLeft = worldX * after - pointerX;
    canvas.scrollTop = worldY * after - pointerY;
  };

  const refreshDiagramUi = () => {
    byId('sql-diagram-name').value = diagram.name || 'Default diagram';
    refreshInspector();
    renderDiagram();
  };

  const refreshInspector = () => {
    const host = byId('sql-diagram-inspector');
    if (host) host.innerHTML = renderInspector();
    bindInspector();
  };

  const renderInspector = () => {
    const edge = selectedEdgeId ? edgeById(selectedEdgeId) : null;
    if (edge) return `
      <div class="diagram-inspector-section">
        <h3>Connector</h3>
        <p class="text-muted small mb-2">${escapeHtml(edge.fromColumn || edge.from)} → ${escapeHtml(edge.toColumn || edge.to)}</p>
        <label class="form-label small" for="diagram-edge-relationship">Relationship</label>
        <select id="diagram-edge-relationship" class="form-select form-select-sm">
          ${['1:1', '1:N', 'N:1', 'N:N'].map(value => `<option value="${value}" ${normalizeRelationship(edge.relationship || edge.kind) === value ? 'selected' : ''}>${relationshipIcon(value)}</option>`).join('')}
        </select>
        <div class="d-flex flex-wrap gap-2 mt-2">
          <button id="diagram-add-waypoint" class="btn btn-outline-secondary btn-sm">Add bend</button>
          ${(edge.waypoints || []).length ? '<button id="diagram-reset-edge" class="btn btn-outline-secondary btn-sm">Reset path</button>' : ''}
          <button id="diagram-delete-edge" class="btn btn-outline-danger btn-sm">Delete connector</button>
        </div>
        <p class="text-muted small mt-2 mb-0">The endpoint symbols show one or many. Drag a blue midpoint to move that horizontal or vertical run. Double-click the line to add a bend, or reset the path to return to automatic routing.</p>
      </div>`;
    const node = selectedNodeId ? nodeById(selectedNodeId) : null;
    if (!node) return `<div class="empty-state compact">Select a table to edit its color, columns, visibility, or relationships.</div>
      <div class="diagram-hidden-list">${diagram?.nodes.filter(item => item.hidden).map(item => `<button class="btn btn-outline-secondary btn-sm diagram-show-node" data-node-id="${escapeHtml(item.id)}">Show ${escapeHtml(item.id)}</button>`).join('') || ''}</div>`;
    return `
      <div class="diagram-inspector-section">
        <h3>${escapeHtml(node.id)}</h3>
        <label class="form-label small">Header color</label>
        <input id="diagram-node-color" type="color" class="form-control form-control-color" value="${escapeHtml(node.color)}">
        <div class="d-flex gap-2 mt-2">
          <button id="diagram-collapse-node" class="btn btn-outline-secondary btn-sm">${node.collapsed ? 'Show columns' : 'Collapse'}</button>
          <button id="diagram-hide-node" class="btn btn-outline-danger btn-sm">Hide table</button>
        </div>
      </div>
      <div class="diagram-inspector-section">
        <h3>Columns</h3>
        ${node.columns.map(column => `<label class="diagram-column-toggle"><input type="checkbox" class="form-check-input diagram-column-visible" data-column="${escapeHtml(column.name)}" ${column.hidden ? '' : 'checked'}> <span>${column.pk ? '🔑 ' : ''}${escapeHtml(column.name)}</span></label>`).join('')}
      </div>
      <div class="diagram-inspector-section">
        <h3>Relationships</h3>
        ${diagram.edges.filter(edge => edge.from === node.id || edge.to === node.id).map(edge => `<div class="diagram-relation-row"><span>${relationshipIcon(edge.relationship || edge.kind)} ${escapeHtml(edge.fromColumn || edge.from)} → ${escapeHtml(edge.toColumn || edge.to)}</span><button class="btn btn-ghost btn-sm diagram-delete-edge" data-edge-id="${edge.id}">×</button></div>`).join('') || '<p class="text-muted small">No relationships yet.</p>'}
      </div>`;
  };

  const bindInspector = () => {
    byId('diagram-node-color')?.addEventListener('input', event => { const node = nodeById(selectedNodeId); if (node) { node.color = event.target.value; node.borderColor = event.target.value; renderDiagram(); } });
    byId('diagram-edge-relationship')?.addEventListener('change', event => { const edge = edgeById(selectedEdgeId); if (edge) { edge.relationship = event.target.value; edge.kind = event.target.value; edge.label = event.target.value; renderDiagram(); } });
    byId('diagram-delete-edge')?.addEventListener('click', deleteSelectedEdge);
    byId('diagram-add-waypoint')?.addEventListener('click', addWaypointToSelectedEdge);
    byId('diagram-reset-edge')?.addEventListener('click', () => { const edge = edgeById(selectedEdgeId); if (edge) { edge.waypoints = []; refreshInspector(); renderDiagram(); } });
    byId('diagram-hide-node')?.addEventListener('click', () => { const node = nodeById(selectedNodeId); if (node) { node.hidden = true; selectedNodeId = null; refreshInspector(); renderDiagram(); } });
    byId('diagram-collapse-node')?.addEventListener('click', () => { const node = nodeById(selectedNodeId); if (node) { node.collapsed = !node.collapsed; refreshInspector(); renderDiagram(); } });
    document.querySelectorAll('.diagram-column-visible').forEach(input => input.addEventListener('change', () => {
      const node = nodeById(selectedNodeId), column = node?.columns.find(item => item.name === input.dataset.column);
      if (column) { column.hidden = !input.checked; renderDiagram(); }
    }));
    document.querySelectorAll('.diagram-delete-edge').forEach(button => button.addEventListener('click', () => { diagram.edges = diagram.edges.filter(edge => edge.id !== button.dataset.edgeId); refreshInspector(); renderDiagram(); }));
    document.querySelectorAll('.diagram-show-node').forEach(button => button.addEventListener('click', () => { const node = nodeById(button.dataset.nodeId); if (node) node.hidden = false; refreshInspector(); renderDiagram(); }));
  };

  const addRelationship = (from, to) => {
    if (diagram.edges.some(edge => edge.from === from.nodeId && edge.to === to.nodeId && edge.fromColumn === from.column && edge.toColumn === to.column)) return notify('Relationship already exists.');
    const relationship = '1:N';
    const edge = {
      id: createUuid(),
      from: from.nodeId,
      to: to.nodeId,
      fromColumn: from.column,
      toColumn: to.column,
      fromSide: from.side,
      toSide: to.side,
      label: relationship,
      kind: relationship,
      relationship,
      waypoints: []
    };
    diagram.edges.push(edge);
    selectedEdgeId = edge.id;
    selectedNodeId = null;
    notify('Relationship added.');
  };

  const deleteSelectedEdge = () => {
    if (!selectedEdgeId) return;
    diagram.edges = diagram.edges.filter(edge => edge.id !== selectedEdgeId);
    selectedEdgeId = null;
    refreshInspector();
    renderDiagram();
  };

  const addWaypointToSelectedEdge = () => {
    const edge = edgeById(selectedEdgeId);
    if (!edge) return;
    const points = edgePoints(edge);
    if (points.length < 2) return;
    const longest = longestSegment(points);
    addManualWaypoint(edge, { x:Math.round((longest.start.x + longest.end.x) / 2), y:Math.round((longest.start.y + longest.end.y) / 2) });
    renderDiagram();
  };

  const addManualWaypoint = (edge, desiredPoint) => {
    const route = edgePoints(edge);
    if (route.length < 2) return;
    const projected = projectPointToRoute(route, desiredPoint);
    const segment = route[projected.segmentIndex] && route[projected.segmentIndex + 1]
      ? [route[projected.segmentIndex], route[projected.segmentIndex + 1]]
      : null;
    const isOnLine = projected.distance < 4;
    const point = isOnLine && segment
      ? segment[0].x === segment[1].x
        ? { x:projected.point.x + 48, y:projected.point.y }
        : { x:projected.point.x, y:projected.point.y + 48 }
      : desiredPoint;
    const waypoints = [...(edge.waypoints || []), point];
    edge.waypoints = waypoints
      .map(point => ({ point, position:projectPointToRoute(route, point).position }))
      .sort((left, right) => left.position - right.position)
      .map(item => item.point)
      .filter((point, index, values) => index === 0 || Math.abs(point.x - values[index - 1].x) > 2 || Math.abs(point.y - values[index - 1].y) > 2);
  };

  const projectPointToRoute = (points, desired) => {
    let travelled = 0;
    let best = { point:points[0], position:0, distance:Number.POSITIVE_INFINITY, segmentIndex:0 };
    for (let index = 0; index < points.length - 1; index++) {
      const start = points[index], end = points[index + 1];
      const vertical = start.x === end.x;
      const point = vertical
        ? { x:start.x, y:Math.min(Math.max(desired.y, Math.min(start.y, end.y)), Math.max(start.y, end.y)) }
        : { x:Math.min(Math.max(desired.x, Math.min(start.x, end.x)), Math.max(start.x, end.x)), y:start.y };
      const distance = Math.abs(point.x - desired.x) + Math.abs(point.y - desired.y);
      const offset = Math.abs(point.x - start.x) + Math.abs(point.y - start.y);
      if (distance < best.distance) best = { point, position:travelled + offset, distance, segmentIndex:index };
      travelled += Math.abs(end.x - start.x) + Math.abs(end.y - start.y);
    }
    return best;
  };

  const longestSegment = points => {
    let best = { start: points[0], end: points[1], distance: 0 };
    for (let index = 0; index < points.length - 1; index++) {
      const start = points[index], end = points[index + 1];
      const distance = Math.abs(end.x - start.x) + Math.abs(end.y - start.y);
      if (distance > best.distance) best = { start, end, distance };
    }
    return best;
  };

  const normalizeRelationship = value => {
    const normalized = String(value || '1:N').trim().toUpperCase().replace('*', 'N').replace('M', 'N');
    return ['1:1', '1:N', 'N:1', 'N:N'].includes(normalized) ? normalized : '1:N';
  };

  const relationshipIcon = value => {
    const [from, to] = cardinalityParts(value || '1:N');
    return `${from}:${to}`;
  };

  const addDiagramTable = () => {
    const name = prompt('New table name', 'NewTable');
    if (!name) return;
    const id = name.includes('.') ? name : `custom.${name}`;
    if (nodeById(id)) return notify('A table with that name already exists.', true);
    diagram.nodes.push({ id, schema:'custom', name, x:DIAGRAM_PADDING, y:DIAGRAM_PADDING, width:230, color:'#fff7ed', borderColor:'#f59e0b', hidden:false, collapsed:false, columns:[{ name:'Id', type:'int', pk:true, hidden:false }] });
    selectedNodeId = id;
    refreshDiagramUi();
  };

  const saveDiagram = async () => {
    const button = byId('sql-diagram-save'), original = button.innerHTML;
    const wasFullscreen = document.querySelector('.sql-diagram-shell')?.classList.contains('fullscreen') || false;
    diagram.name = byId('sql-diagram-name').value || 'Default diagram';
    button.disabled = true;
    button.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Saving';
    try {
      const saved = await jsonRequest(`/api/sql-doc/sources/${selectedSourceId}/diagrams`, 'POST', { name: diagram.name, diagramJson: JSON.stringify(diagram) });
      savedDiagrams = await jsonRequest(`/api/sql-doc/sources/${selectedSourceId}/diagrams`);
      notify(`Diagram saved: ${saved.name}`);
      activeTab = 'diagram';
      renderSchema();
      setDiagramFullscreen(wasFullscreen);
      if (wasFullscreen) setTimeout(renderDiagram, 0);
    } catch (error) { notify(error.message, true); }
    finally { button.disabled = false; button.innerHTML = original; }
  };

  const exportDiagramPng = async () => {
    try {
      const visibleNodes = diagram.nodes.filter(node => !node.hidden);
      if (!visibleNodes.length) return notify('Add at least one visible table before exporting.', true);
      const scale = 2;
      const padding = 96;
      const visible = new Set(visibleNodes.map(node => node.id));
      const connectorPoints = diagram.edges.filter(edge => visible.has(edge.from) && visible.has(edge.to)).flatMap(edge => edgePoints(edge));
      const minX = Math.min(...visibleNodes.map(node => node.x), ...connectorPoints.map(point => point.x));
      const minY = Math.min(...visibleNodes.map(node => node.y), ...connectorPoints.map(point => point.y));
      const maxX = Math.max(...visibleNodes.map(node => node.x + node.width), ...connectorPoints.map(point => point.x));
      const maxY = Math.max(...visibleNodes.map(node => node.y + nodeHeight(node)), ...connectorPoints.map(point => point.y));
      const width = Math.max(1, Math.ceil(maxX - minX));
      const height = Math.max(1, Math.ceil(maxY - minY));
      const canvas = document.createElement('canvas');
      canvas.width = Math.ceil((width + padding * 2) * scale);
      canvas.height = Math.ceil((height + padding * 2) * scale);
      const ctx = canvas.getContext('2d');
      ctx.scale(scale, scale);
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, width + padding * 2, height + padding * 2);
      ctx.translate(padding - minX, padding - minY);
      drawPngEdges(ctx);
      visibleNodes.forEach(node => drawPngNode(ctx, node));
      const link = document.createElement('a');
      link.download = `${safeFileName(diagram.name || selectedSchema.sourceName)}.png`;
      link.href = canvas.toDataURL('image/png');
      link.click();
    } catch (error) { notify(`Unable to export diagram: ${error.message}`, true); }
  };

  const drawPngEdges = ctx => {
    ctx.strokeStyle = '#667085';
    ctx.lineWidth = 1.4;
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';
    const visible = new Set(diagram.nodes.filter(node => !node.hidden).map(node => node.id));
    diagram.edges.filter(edge => visible.has(edge.from) && visible.has(edge.to)).forEach(edge => {
      const points = edgePoints(edge);
      if (points.length < 2) return;
      ctx.beginPath();
      points.forEach((point, index) => index === 0 ? ctx.moveTo(point.x, point.y) : ctx.lineTo(point.x, point.y));
      ctx.stroke();
      const [fromCardinality, toCardinality] = cardinalityParts(edge.relationship || edge.kind || '1:N');
      drawPngRelationshipMarker(ctx, points[0], points[1], fromCardinality);
      drawPngRelationshipMarker(ctx, points[points.length - 1], points[points.length - 2], toCardinality);
    });
  };

  const drawPngRelationshipMarker = (ctx, endpoint, adjacent, cardinality) => {
    const dx = adjacent.x - endpoint.x, dy = adjacent.y - endpoint.y;
    const length = Math.max(1, Math.hypot(dx, dy));
    const unitX = dx / length, unitY = dy / length;
    const normalX = -unitY, normalY = unitX;
    ctx.save();
    ctx.strokeStyle = '#667085';
    ctx.lineWidth = 1.8;
    ctx.beginPath();
    if (cardinality === '1') {
      const centerX = endpoint.x + unitX * 5, centerY = endpoint.y + unitY * 5;
      ctx.moveTo(centerX + normalX * 6, centerY + normalY * 6);
      ctx.lineTo(centerX - normalX * 6, centerY - normalY * 6);
    } else {
      const baseX = endpoint.x + unitX, baseY = endpoint.y + unitY;
      const apexX = endpoint.x + unitX * 10, apexY = endpoint.y + unitY * 10;
      ctx.moveTo(baseX + normalX * 7, baseY + normalY * 7);
      ctx.lineTo(apexX, apexY);
      ctx.moveTo(baseX, baseY);
      ctx.lineTo(apexX, apexY);
      ctx.moveTo(baseX - normalX * 7, baseY - normalY * 7);
      ctx.lineTo(apexX, apexY);
    }
    ctx.stroke();
    ctx.restore();
  };

  const drawPngNode = (ctx, node) => {
    const height = nodeHeight(node);
    ctx.fillStyle = '#ffffff';
    ctx.strokeStyle = node.borderColor || '#d0d5dd';
    ctx.lineWidth = 1;
    ctx.fillRect(node.x, node.y, node.width, height);
    ctx.strokeRect(node.x, node.y, node.width, height);
    ctx.fillStyle = node.color || '#f8fafc';
    ctx.fillRect(node.x, node.y, node.width, 30);
    ctx.strokeRect(node.x, node.y, node.width, 30);
    ctx.fillStyle = '#111827';
    ctx.font = 'bold 12px Segoe UI, Arial';
    ctx.fillText(node.id, node.x + 8, node.y + 20, node.width - 16);
    if (node.collapsed) return;
    ctx.font = '12px Segoe UI, Arial';
    node.columns.filter(column => !column.hidden).forEach((column, index) => {
      const y = node.y + 50 + index * 24;
      ctx.fillStyle = column.pk ? '#ca8a04' : '#475467';
      ctx.fillText(column.pk ? '🔑' : '•', node.x + 8, y);
      ctx.fillStyle = '#111827';
      ctx.fillText(column.name, node.x + 28, y, node.width - 90);
      ctx.fillStyle = '#667085';
      ctx.fillText(column.type || '', node.x + node.width - 70, y, 62);
    });
  };

  const openSourceModal = id => {
    const source = sources.find(item => item.id === id);
    byId('sql-source-id').value = source?.id || '';
    byId('sql-source-name').value = source?.name || '';
    byId('sql-source-provider').value = source?.provider || 'sqlite';
    byId('sql-source-server').value = '';
    byId('sql-source-port').value = '';
    byId('sql-source-username').value = '';
    byId('sql-source-password').value = '';
    byId('sql-source-integrated').checked = false;
    byId('sql-source-trust').checked = true;
    byId('sql-source-sqlite-path').value = '';
    byId('sql-source-database').innerHTML = '<option value="">Connect to load databases</option>';
    byId('sql-source-connection').value = source?.connectionString || '';
    byId('sql-source-modal-title').textContent = source ? 'Edit database source' : 'Add database source';
    byId('sql-source-submit').textContent = source ? 'Save changes' : 'Save source';
    toggleProviderFields();
    modal.show();
  };

  const deleteSelectedSource = async () => {
    const source = selectedSource();
    if (!source || !confirm(`Delete ${source.name}?`)) return;
    try {
      await fetch(appUrl(`/api/sql-doc/sources/${source.id}`), { method:'DELETE' });
      selectedSourceId = null;
      notify('Database source deleted.');
      await loadSources();
    } catch (error) { notify(error.message, true); }
  };

  const downloadDocx = async () => {
    if (!selectedSourceId) return;
    const downloadSourceName = selectedSource()?.name || selectedSchema?.sourceName || 'database-sql-doc';
    const selector = byId('sql-doc-object-selector');
    const specific = selector?.querySelector('input[name="sql-doc-scope"]:checked')?.value === 'specific';
    const selectedObjects = specific ? [...selector.querySelectorAll('.object-tree-item-input:checked')].map(input => input.value) : [];
    if (specific && selectedObjects.length === 0) return notify('Select at least one database object to include.', true);
    const hasType = prefix => !specific || selectedObjects.some(value => value.startsWith(`${prefix}:`));
    const options = {
      tables:hasType('table'),
      views:hasType('view'),
      storedProcedures:hasType('procedure'),
      functions:hasType('function'),
      indexes:hasType('index'),
      relationships:hasType('table'),
      objectNames:specific ? selectedObjects.join('|') : null
    };
    const button = byId('sql-doc-download');
    const original = button?.innerHTML;
    if (button) { button.disabled = true; button.innerHTML = '<span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>Generating document'; }
    try {
      const response = await fetch(appUrl(`/api/sql-doc/sources/${selectedSourceId}/docx`), { method:'POST', headers:{ 'Content-Type':'application/json' }, body:JSON.stringify(options) });
      if (!response.ok) throw new Error(await readError(response));
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = normalizeDocxFileName(downloadSourceName);
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
      notify('SQL documentation generated.');
    } catch (error) { notify(error.message, true); }
    finally { if (button) { button.disabled = false; button.innerHTML = original; } }
  };

  byId('sql-source-add').addEventListener('click', () => openSourceModal());
  byId('sql-source-provider').addEventListener('change', toggleProviderFields);
  byId('sql-source-integrated').addEventListener('change', toggleAuthFields);
  byId('sql-load-databases').addEventListener('click', loadDatabases);
  byId('sql-diagram-objects-apply').addEventListener('click', applyDiagramObjectSelection);
  byId('sql-source-form').addEventListener('submit', async event => {
    event.preventDefault();
    const button = byId('sql-source-submit'), original = button.innerHTML;
    button.disabled = true;
    button.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Saving';
    const id = Number(byId('sql-source-id').value);
    const body = { name:byId('sql-source-name').value, ...connectionBody() };
    try {
      const saved = await jsonRequest(id ? `/api/sql-doc/sources/${id}` : '/api/sql-doc/sources', id ? 'PUT' : 'POST', body);
      modal.hide();
      event.target.reset();
      notify(id ? 'Database source updated.' : 'Database source added.');
      await loadSources(saved.id);
    } catch (error) { notify(error.message, true); }
    finally { button.disabled = false; button.innerHTML = original; }
  });

  document.addEventListener('keydown', event => {
    if (['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName || '')) return;
    if (event.key === 'Delete' && selectedEdgeId) {
      event.preventDefault();
      deleteSelectedEdge();
    }
    if (event.key === 'Escape' && connectDraft) {
      connectDraft = null;
      renderDiagram();
    }
  });

  const readError = async response => { try { const result = await response.json(); return result.error || 'Unable to generate documentation.'; } catch { return await response.text() || 'Unable to generate documentation.'; } };
  const connectionSummary = connectionString => /(?:Database|Initial Catalog|Data Source)=([^;]+)/i.exec(connectionString || '')?.[1] || 'Open details';
  const safeFileName = value => String(value).replace(/[\\/:*?"<>|]+/g, '-').replace(/\s+/g, ' ').trim() || 'database-diagram';
  const normalizeDocxFileName = value => `${safeFileName(String(value).replace(/\.docx$/i, ''))}.docx`;

  function connectionBody() {
    return {
      provider: byId('sql-source-provider').value,
      connectionString: byId('sql-source-connection').value || null,
      server: byId('sql-source-server').value || null,
      port: byId('sql-source-port').value || null,
      database: byId('sql-source-database').value || null,
      username: byId('sql-source-username').value || null,
      password: byId('sql-source-password').value || null,
      integratedSecurity: byId('sql-source-integrated').checked,
      trustServerCertificate: byId('sql-source-trust').checked,
      sqlitePath: byId('sql-source-sqlite-path').value || null
    };
  }
  function toggleProviderFields() {
    const provider = byId('sql-source-provider').value;
    const sqlite = provider === 'sqlite', sqlServer = provider === 'sqlserver';
    document.querySelectorAll('.sql-network-field,.sql-auth-field,.sql-database-field').forEach(element => element.classList.toggle('d-none', sqlite));
    document.querySelectorAll('.sql-server-option').forEach(element => element.classList.toggle('d-none', !sqlServer || sqlite));
    document.querySelectorAll('.sql-sqlite-field').forEach(element => element.classList.toggle('d-none', !sqlite));
    byId('sql-source-port').placeholder = provider === 'sqlserver' ? '1433' : provider === 'postgresql' ? '5432' : provider === 'mysql' ? '3306' : '1521';
    toggleAuthFields();
  }
  function toggleAuthFields() {
    const integrated = byId('sql-source-integrated').checked && byId('sql-source-provider').value === 'sqlserver';
    document.querySelectorAll('.sql-auth-field input').forEach(input => input.disabled = integrated);
  }
  async function loadDatabases() {
    const button = byId('sql-load-databases'), original = button.innerHTML;
    button.disabled = true;
    button.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Loading';
    try {
      const result = await jsonRequest('/api/sql-doc/databases', 'POST', connectionBody());
      const options = result.databases.length ? result.databases.map(name => `<option value="${escapeHtml(name)}">${escapeHtml(name)}</option>`).join('') : '<option value="">No databases found</option>';
      byId('sql-source-database').innerHTML = options;
      notify('Databases loaded.');
    } catch (error) { notify(error.message, true); }
    finally { button.disabled = false; button.innerHTML = original; }
  }

  loadSources();
})();
