(() => {
  const { byId, notify, jsonRequest } = window.devTools;
  byId('hash-input').addEventListener('input', event => byId('hash-count').textContent = `${event.target.value.length.toLocaleString()} characters`);
  byId('hash-run').addEventListener('click', async () => { try { const result = await jsonRequest('/api/tools/hash', 'POST', { input: byId('hash-input').value, algorithm: byId('hash-algorithm').value }); byId('hash-output').textContent = result.value; byId('hash-result-card').classList.remove('d-none'); } catch (error) { notify(error.message, true); } });
  document.querySelectorAll('.copy-btn').forEach(button => button.addEventListener('click', async () => { const element = byId(button.dataset.target); const value = element.textContent; if (!value) return notify('There is nothing to copy.', true); await navigator.clipboard.writeText(value); notify('Copied to clipboard.'); }));
  document.querySelectorAll('.clear-btn').forEach(button => button.addEventListener('click', () => { const element = byId(button.dataset.target); element.value = ''; element.dispatchEvent(new Event('input')); }));
})();
