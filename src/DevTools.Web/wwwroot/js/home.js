(() => {
  // Filters the tool cards as you type; "/" focuses the search box from anywhere on the page.
  const search = document.getElementById('home-search');
  if (!search) return;
  const apply = () => {
    const terms = search.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
    let visible = 0;
    document.querySelectorAll('.home-group').forEach(group => {
      let groupVisible = 0;
      group.querySelectorAll('.home-tool').forEach(card => {
        const match = terms.every(term => card.dataset.search.includes(term));
        card.hidden = !match;
        if (match) groupVisible++;
      });
      group.hidden = groupVisible === 0;
      visible += groupVisible;
    });
    document.getElementById('home-empty').classList.toggle('d-none', visible > 0);
  };
  search.addEventListener('input', apply);
  search.addEventListener('keydown', event => {
    if (event.key === 'Escape') { search.value = ''; apply(); }
    if (event.key === 'Enter') { const first = document.querySelector('.home-tool:not([hidden])'); if (first) first.click(); }
  });
  document.addEventListener('keydown', event => {
    if (event.key === '/' && document.activeElement !== search && !/^(input|textarea|select)$/i.test(document.activeElement?.tagName || '')) {
      event.preventDefault(); search.focus();
    }
  });
})();
