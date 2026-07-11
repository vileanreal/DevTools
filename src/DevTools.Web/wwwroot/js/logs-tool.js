(() => {
  const { byId, escapeHtml, notify, jsonRequest } = window.devTools;
  let rawLogs = '', pageEntries = [];
  const render = async (page = 1) => {
    if (!rawLogs) return;
    try {
      const result = await jsonRequest('/api/tools/logs', 'POST', { input:rawLogs, level:byId('logs-level').value, search:byId('logs-search').value, page, pageSize:Number(byId('logs-page-size').value) });
      pageEntries = result.entries;
      byId('logs-summary').textContent = `${result.filteredCount.toLocaleString()} matching · ${result.totalCount.toLocaleString()} total`;
      const count = level => result.statistics.find(item => item.level === level)?.count ?? 0;
      byId('logs-stats').innerHTML = `<div class="stat-card"><strong>${result.filteredCount.toLocaleString()}</strong><span>Matching entries</span></div><div class="stat-card stat-danger"><strong>${(count('ERROR') + count('FATAL')).toLocaleString()}</strong><span>Errors / fatal</span></div><div class="stat-card stat-warning"><strong>${count('WARN').toLocaleString()}</strong><span>Warnings</span></div><div class="stat-card"><strong>${count('INFO').toLocaleString()}</strong><span>Information</span></div>`;
      byId('logs-body').innerHTML = result.entries.length ? result.entries.map((entry, index) => `<tr><td class="text-nowrap text-muted">${escapeHtml(entry.timestamp || '—')}</td><td><span class="level-pill level-${entry.level.toLowerCase()}">${escapeHtml(entry.level)}</span></td><td class="log-message-cell"><span class="log-message-preview manual-message">${escapeHtml(entry.message.length > 160 ? `${entry.message.slice(0,160)}…` : entry.message)}</span>${entry.message.length > 160 ? `<button class="view-message manual-view-message" data-index="${index}" type="button">View more</button>` : ''}</td></tr>`).join('') : '<tr><td colspan="3" class="empty-state">No matching log entries.</td></tr>';
      renderPagination(result);
      document.querySelectorAll('.manual-view-message').forEach(button => button.addEventListener('click', () => toggleMessage(button)));
    } catch (error) { notify(error.message, true); }
  };
  const renderPagination = result => {
    if (result.totalPages <= 1) { byId('logs-pagination').innerHTML = ''; return; }
    const start = Math.max(1,result.page-2), end = Math.min(result.totalPages,result.page+2); let numbers = '';
    if (start > 1) numbers += `<button class="page-number log-page" data-page="1">1</button>${start > 2 ? '<span class="page-ellipsis">…</span>' : ''}`;
    for (let number=start; number<=end; number++) numbers += `<button class="page-number log-page ${number===result.page?'active':''}" data-page="${number}">${number}</button>`;
    if (end < result.totalPages) numbers += `${end < result.totalPages-1?'<span class="page-ellipsis">…</span>':''}<button class="page-number log-page" data-page="${result.totalPages}">${result.totalPages.toLocaleString()}</button>`;
    byId('logs-pagination').innerHTML = `<nav class="pager-row"><button class="btn btn-sm btn-outline-secondary log-page" data-page="${result.page-1}" ${result.page<=1?'disabled':''}>Previous</button><div class="page-numbers">${numbers}</div><button class="btn btn-sm btn-outline-secondary log-page" data-page="${result.page+1}" ${result.page>=result.totalPages?'disabled':''}>Next</button></nav><div class="page-summary">Page ${result.page.toLocaleString()} of ${result.totalPages.toLocaleString()}</div>`;
    document.querySelectorAll('.log-page:not(.active)').forEach(button => button.addEventListener('click', () => render(Number(button.dataset.page))));
  };
  const toggleMessage = button => { const preview=button.closest('.log-message-cell').querySelector('.manual-message'); const expanded=button.textContent==='View less'; preview.textContent=expanded?`${pageEntries[Number(button.dataset.index)].message.slice(0,160)}…`:pageEntries[Number(button.dataset.index)].message; preview.classList.toggle('log-message-preview',expanded); button.textContent=expanded?'View more':'View less'; };
  byId('logs-run').addEventListener('click', () => { rawLogs=byId('logs-input').value; render(1); });
  byId('logs-file').addEventListener('change', async event => { const file=event.target.files[0]; if(!file)return; byId('logs-file-name').textContent=`${file.name} · reading ${(file.size/1024/1024).toFixed(2)} MB…`; try { byId('logs-input').value=await file.text(); byId('logs-file-name').textContent=`${file.name} · ${(file.size/1024/1024).toFixed(2)} MB`; rawLogs=byId('logs-input').value; render(1); } catch { notify('The selected log file could not be read.',true); } });
  byId('logs-level').addEventListener('change',()=>render(1)); byId('logs-page-size').addEventListener('change',()=>render(1));
  let timer; byId('logs-search').addEventListener('input',()=>{clearTimeout(timer);timer=setTimeout(()=>render(1),300);});
  document.querySelectorAll('.example-btn').forEach(button => button.addEventListener('click',()=>{byId(button.dataset.target).value=button.dataset.example;}));
})();
