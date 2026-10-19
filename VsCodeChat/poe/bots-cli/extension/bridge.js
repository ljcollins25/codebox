// MAIN-world content script (document_start) on https://poe.com/*.
// Remembers the request headers the web app itself sends to /api/gql_POST (formkey, tchannel, ...) in a closure
// and exposes window.__poeGql(queryName, variables, hash). The headers are never returned or logged.
(() => {
  if (window.__poeGql) return;
  const orig = window.fetch;
  let saved = null;
  const isGql = (u) => /\/api\/gql_POST/.test(u);
  window.fetch = function (input, init) {
    try {
      const u = typeof input === 'string' ? input : input.url;
      if (isGql(u) && init && init.headers) saved = new Headers(init.headers);
    } catch (e) {}
    return orig.apply(this, arguments);
  };
  window.__poeGqlReady = () => !!saved;
  window.__poeGql = async (queryName, variables, hash) => {
    if (!saved) throw new Error('no app request seen yet');
    const h = new Headers(saved);
    h.set('poe-queryname', queryName);
    const r = await orig('/api/gql_POST', {
      method: 'POST', credentials: 'include', headers: h,
      body: JSON.stringify({ queryName, variables, extensions: { hash } }),
    });
    const text = await r.text();
    let json = null;
    try { json = JSON.parse(text); } catch (e) {}
    return { status: r.status, json, text: json ? undefined : text.slice(0, 300) };
  };
})();
