(() => {
  const { byId, notify, jsonRequest, highlightSyntax } = window.devTools;
  const examples = { json:'{"project":"DevTools","features":["hashing","formatting"],"ready":true}', html:'<main><h1>DevTools</h1><p>Developer utilities</p></main>', xml:'<project><name>DevTools</name><ready>true</ready></project>', sql:'select id,name from developers where active=true order by name;', csharp:'public class Tool{public string Name{get;set;}public void Run(){Console.WriteLine(Name);}}', java:'public class Tool{private String name;public void run(){System.out.println(name);}}', javascript:'function format(value){if(value){console.log(value);}return value;}', typescript:'function format(value:string):string{if(value){console.log(value);}return value;}', css:'.tool{display:flex;color:#625bf6;}.tool:hover{opacity:.9;}', scss:'.tool{display:flex;&:hover{opacity:.9;}}' };
  const syncScroll = name => {
    const source = byId(`code-${name}`), layer = byId(`code-${name}-highlight`);
    if (!source || !layer) return;
    layer.scrollLeft = source.scrollLeft;
    layer.scrollTop = source.scrollTop;
  };
  const refreshHighlight = name => {
    const source = byId(`code-${name}`), layer = byId(`code-${name}-highlight`);
    if (!source || !layer) return;
    layer.querySelector('code').innerHTML = highlightSyntax(source.value, byId('code-language').value) + (source.value.endsWith('\n') ? ' ' : '\n');
    syncScroll(name);
  };
  const refreshAll = () => { refreshHighlight('input'); refreshHighlight('output'); };
  ['input','output'].forEach(name => {
    const source = byId(`code-${name}`);
    source.addEventListener('scroll', () => syncScroll(name));
    if (!source.readOnly) source.addEventListener('input', () => refreshHighlight(name));
  });
  byId('code-language').addEventListener('change', refreshAll);
  byId('code-format').addEventListener('click', async () => { try { const language = byId('code-language').value; const result = await jsonRequest('/api/tools/format', 'POST', { input:byId('code-input').value, language, indentSize:Number(byId('code-indent-size').value), useTabs:byId('code-indent-type').value === 'tabs' }); byId('code-output').value = result.output; refreshHighlight('output'); notify(`${byId('code-language').selectedOptions[0].text} formatting complete.`); } catch (error) { notify(error.message, true); } });
  byId('code-example').addEventListener('click', () => { byId('code-input').value = examples[byId('code-language').value] || '# Paste your code here\n'; refreshHighlight('input'); });
  document.querySelectorAll('.copy-btn').forEach(button => button.addEventListener('click', async () => { const value = byId(button.dataset.target).value; if (!value) return notify('There is nothing to copy.', true); await navigator.clipboard.writeText(value); notify('Copied to clipboard.'); }));
  document.querySelectorAll('.clear-pair').forEach(button => button.addEventListener('click', () => { byId(`${button.dataset.key}-input`).value = ''; byId(`${button.dataset.key}-output`).value = ''; refreshAll(); }));
  refreshAll();
})();
