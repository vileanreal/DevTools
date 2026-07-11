(() => {
  const byId = id => document.getElementById(id);
  const escapeHtml = value => String(value).replace(/[&<>'"]/g, character => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', "'":'&#39;', '"':'&quot;' })[character]);
  const toastElement = byId('app-toast');
  const toast = toastElement ? bootstrap.Toast.getOrCreateInstance(toastElement, { delay: 2500 }) : null;
  const notify = (message, error = false) => { if (!toastElement) return; toastElement.querySelector('.toast-body').textContent = message; toastElement.classList.toggle('text-bg-danger', error); toast.show(); };
  const jsonRequest = async (url, method = 'GET', body = null) => {
    const options = { method, headers: {} };
    if (body !== null) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
    const response = await fetch(url, options);
    if (response.status === 401) {
      const returnUrl = encodeURIComponent(`${location.pathname}${location.search}`);
      location.assign(`/account/login?returnUrl=${returnUrl}`);
      throw new Error('Your session has expired.');
    }
    if (response.status === 204) return null;
    const result = await response.json();
    if (!response.ok || !result.success) throw new Error(result.error || 'The operation could not be completed.');
    return result.data;
  };
  const keywordGroups = {
    sql:'add all alter and any as asc authorization backup begin between break browse bulk by cascade case check checkpoint close clustered coalesce collate column commit compute constraint contains continue convert create cross current database dbcc deallocate declare default delete deny desc disk distinct distributed double drop dump else end errlvl escape except exec execute exists exit external fetch file fillfactor for foreign freetext from full function goto grant group having holdlock identity identity_insert identitycol if in index inner insert intersect into is join key kill left like lineno load merge national nocheck nonclustered not null nullif of off offsets on open opendatasource openquery openrowset option or order outer over percent pivot plan precision primary print proc procedure public raiserror read readtext reconfigure references replication restore restrict return revert revoke right rollback rowcount rowguidcol rule save schema securityaudit select semantickeyphrasetable set setuser shutdown some statistics system_user table tablesample textsize then to top tran transaction trigger truncate try_convert tsequal union unique unpivot update updatetext use user values varying view waitfor when where while with writetext true false'.split(' '),
    csharp:'abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while async await var dynamic get set init required'.split(' '),
    java:'abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new null package private protected public return short static strictfp super switch synchronized this throw throws transient true try void volatile while false record sealed permits var'.split(' '),
    javascript:'async await break case catch class const continue debugger default delete do else export extends false finally for from function get if import in instanceof let new null of return set static super switch this throw true try typeof undefined var void while with yield'.split(' '),
    python:'and as assert async await break class continue def del elif else except false finally for from global if import in is lambda match none nonlocal not or pass raise return true try while with yield'.split(' '),
    cpp:'alignas alignof asm auto bool break case catch char class const constexpr continue default delete do double else enum explicit export extern false float for friend goto if inline int long mutable namespace new noexcept nullptr operator private protected public register reinterpret_cast return short signed sizeof static struct switch template this throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while'.split(' '),
    css:'important inherit initial unset auto none block inline flex grid relative absolute fixed sticky transparent currentcolor'.split(' '),
    json:['true','false','null']
  };
  const languageAliases = { typescript:'javascript', java:'java', kotlin:'java', c:'cpp', go:'cpp', rust:'cpp', swift:'cpp', ruby:'python', php:'javascript', scss:'css', yaml:'python', shell:'python' };
  const markupLanguages = new Set(['html','xml']);
  const highlightMarkup = source => {
    const pattern = /<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<!DOCTYPE[^>]*>|<\/?[A-Za-z][^>]*>/gi;
    let output = '', cursor = 0;
    for (const match of source.matchAll(pattern)) {
      output += escapeHtml(source.slice(cursor, match.index));
      const kind = match[0].startsWith('<!--') ? 'comment' : 'tag';
      output += `<span class="syntax-token ${kind}">${escapeHtml(match[0])}</span>`;
      cursor = match.index + match[0].length;
    }
    return output + escapeHtml(source.slice(cursor));
  };
  const highlightSyntax = (value, requestedLanguage = 'plaintext') => {
    const source = String(value ?? '');
    const requested = String(requestedLanguage || 'plaintext').toLowerCase();
    if (!source || requested === 'plaintext') return escapeHtml(source);
    if (markupLanguages.has(requested)) return highlightMarkup(source);
    const language = languageAliases[requested] || requested;
    const keywords = new Set(keywordGroups[language] || keywordGroups.javascript);
    const lineComment = language === 'sql' ? '--[^\\n]*' : ['python','yaml','shell'].includes(language) ? '#[^\\n]*' : '\\/\\/[^\\n]*';
    const tokenPattern = new RegExp([
      '\\/\\*[\\s\\S]*?\\*\\/', lineComment,
      '"(?:\\\\.|[^"\\\\])*"', "'(?:\\\\.|[^'\\\\])*'", '`(?:\\\\.|[^`\\\\])*`',
      '\\b(?:0x[0-9a-f]+|\\d+(?:\\.\\d+)?)\\b', '[A-Za-z_@$][A-Za-z0-9_@$]*'
    ].join('|'), 'gi');
    let output = '', cursor = 0;
    for (const match of source.matchAll(tokenPattern)) {
      const token = match[0];
      output += escapeHtml(source.slice(cursor, match.index));
      let kind = '';
      if (token.startsWith('//') || token.startsWith('--') || token.startsWith('#') || token.startsWith('/*')) kind = 'comment';
      else if (/^["'`]/.test(token)) kind = source.slice(match.index + token.length).trimStart().startsWith(':') && ['json','javascript'].includes(language) ? 'property' : 'string';
      else if (/^(?:0x|\d)/i.test(token)) kind = 'number';
      else if (keywords.has(token.toLowerCase())) kind = 'keyword';
      else if (token.startsWith('@')) kind = 'variable';
      else if (language === 'css' && source.slice(match.index + token.length).trimStart().startsWith(':')) kind = 'property';
      output += kind ? `<span class="syntax-token ${kind}">${escapeHtml(token)}</span>` : escapeHtml(token);
      cursor = match.index + token.length;
    }
    return output + escapeHtml(source.slice(cursor));
  };
  window.devTools = { byId, escapeHtml, notify, jsonRequest, highlightSyntax };

  const themeToggle = byId('theme-toggle');
  const applyTheme = theme => {
    const dark = theme === 'dark';
    document.documentElement.dataset.theme = theme;
    document.documentElement.dataset.bsTheme = theme;
    if (!themeToggle) return;
    themeToggle.querySelector('.theme-toggle-icon').textContent = dark ? '☀' : '☾';
    themeToggle.querySelector('.theme-toggle-label').textContent = dark ? 'Light' : 'Dark';
    themeToggle.setAttribute('aria-label', `Switch to ${dark ? 'light' : 'dark'} theme`);
    themeToggle.setAttribute('aria-pressed', String(dark));
  };
  applyTheme(localStorage.getItem('devtools-theme') || 'light');
  themeToggle?.addEventListener('click', () => { const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark'; localStorage.setItem('devtools-theme', next); applyTheme(next); });
})();
