// Small line diff (LCS) -> unified-ish text. No dependencies.
export function lineDiff(a, b, labelA = 'live', labelB = 'local') {
  const A = a.split('\n'), B = b.split('\n');
  const n = A.length, m = B.length;
  const L = Array.from({ length: n + 1 }, () => new Int32Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--)
    L[i][j] = A[i] === B[j] ? L[i + 1][j + 1] + 1 : Math.max(L[i + 1][j], L[i][j + 1]);
  const out = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (A[i] === B[j]) { out.push('  ' + A[i]); i++; j++; }
    else if (L[i + 1][j] >= L[i][j + 1]) out.push('- ' + A[i++]);
    else out.push('+ ' + B[j++]);
  }
  while (i < n) out.push('- ' + A[i++]);
  while (j < m) out.push('+ ' + B[j++]);
  if (!out.some((l) => l[0] === '-' || l[0] === '+')) return '';
  // keep 2 lines of context around changes
  const keep = new Set();
  out.forEach((l, k) => { if (l[0] !== ' ') for (let d = -2; d <= 2; d++) keep.add(k + d); });
  const res = ['--- ' + labelA, '+++ ' + labelB];
  let last = -1;
  out.forEach((l, k) => {
    if (!keep.has(k)) return;
    if (last !== -1 && k !== last + 1) res.push('  ...');
    res.push(l); last = k;
  });
  return res.join('\n');
}
