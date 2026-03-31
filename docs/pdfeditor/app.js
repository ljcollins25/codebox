/* =========================================================
   DocuSign PDF Annotator — Full SPA Logic
   ========================================================= */

// PDF.js worker
pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js';

// ——— State ———
let pdfDoc = null;
let pdfFileName = '';
const pdfPageViewports = [];
const RENDER_SCALE = 1.5;

let annotations = [];
let selectedId = null;
let nextId = 1;
let library = [];
let previewMode = false;

// ——— DOM refs ———
const viewer = document.getElementById('pdf-viewer');
const emptyState = document.getElementById('empty-state');
const fileInput = document.getElementById('file-input');
const yamlInput = document.getElementById('yaml-input');

const btnOpen = document.getElementById('btn-open');
const btnText = document.getElementById('btn-text');
const btnSign = document.getElementById('btn-sign');
const btnDate = document.getElementById('btn-date');
const btnDup = document.getElementById('btn-dup');
const btnSave = document.getElementById('btn-save');
const btnLibrary = document.getElementById('btn-library');
const btnExport = document.getElementById('btn-export');
const btnImport = document.getElementById('btn-import');
const btnPreview = document.getElementById('btn-preview');
const btnDownload = document.getElementById('btn-download');

const modalOverlay = document.getElementById('modal-overlay');
const modalTitle = document.getElementById('modal-title');
const modalBody = document.getElementById('modal-body');
const modalFooter = document.getElementById('modal-footer');
const modalCloseBtn = document.getElementById('modal-close');

const toastEl = document.getElementById('toast');

// ——— Dark mode ———
function applyTheme() {
  if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
    document.documentElement.classList.add('dark');
  } else {
    document.documentElement.classList.remove('dark');
  }
}
applyTheme();
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', applyTheme);

// ——— Toast ———
let toastTimer = null;
function showToast(msg) {
  toastEl.textContent = msg;
  toastEl.classList.add('show');
  if (toastTimer) clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toastEl.classList.remove('show'), 2500);
}

// ——— Modal ———
function openModal(title, bodyHTML, footerButtons) {
  closeModal();
  modalTitle.textContent = title;
  modalBody.innerHTML = bodyHTML;
  modalFooter.innerHTML = '';
  (footerButtons || []).forEach(b => {
    const btn = document.createElement('button');
    btn.textContent = b.label;
    btn.className = 'modal-btn ' + (b.cls || '');
    if (b.id) btn.id = b.id;
    btn.onclick = b.onclick;
    modalFooter.appendChild(btn);
  });
  modalOverlay.classList.add('open');
}
function closeModal() { modalOverlay.classList.remove('open'); }
modalCloseBtn.onclick = closeModal;
modalOverlay.addEventListener('click', e => { if (e.target === modalOverlay) closeModal(); });

function showConfirm(message, onConfirm) {
  openModal('Confirm', `<p style="font-size:14px;color:var(--text-secondary)">${message}</p>`, [
    { label: 'Cancel', cls: '', onclick: closeModal },
    { label: 'Confirm', cls: 'modal-btn-danger', onclick: () => { closeModal(); onConfirm(); } }
  ]);
}

// ——— Toolbar state ———
function updateToolbar() {
  const loaded = !!pdfDoc;
  btnText.disabled = !loaded;
  btnSign.disabled = !loaded;
  btnDate.disabled = !loaded;
  btnDup.disabled = !loaded;
  btnSave.disabled = !loaded;
  btnExport.disabled = !loaded;
  btnDownload.disabled = !loaded;
}

// ——— PDF Loading ———
btnOpen.onclick = () => fileInput.click();
fileInput.addEventListener('change', async e => {
  const file = e.target.files[0];
  if (!file) return;
  fileInput.value = '';
  pdfFileName = file.name;
  const buf = await file.arrayBuffer();
  const doc = await pdfjsLib.getDocument({ data: buf }).promise;
  pdfDoc = doc;
  pdfPageViewports.length = 0;
  annotations = [];
  selectedId = null;
  nextId = 1;
  updateToolbar();
  await renderAllPages();
  emptyState.style.display = 'none';
  showToast('PDF loaded — ' + pdfDoc.numPages + ' page(s)');
});

async function renderAllPages() {
  const container = document.createElement('div');
  container.className = 'pdf-container';
  viewer.querySelectorAll('.pdf-container').forEach(c => c.remove());
  viewer.appendChild(container);

  for (let num = 1; num <= pdfDoc.numPages; num++) {
    const page = await pdfDoc.getPage(num);
    const baseVP = page.getViewport({ scale: 1 });
    pdfPageViewports[num] = baseVP;
    const vp = page.getViewport({ scale: RENDER_SCALE });

    const wrapper = document.createElement('div');
    wrapper.className = 'pdf-page-wrapper';

    const pageDiv = document.createElement('div');
    pageDiv.className = 'pdf-page';
    pageDiv.id = 'page-' + num;
    pageDiv.style.width = vp.width + 'px';
    pageDiv.style.height = vp.height + 'px';

    const canvas = document.createElement('canvas');
    canvas.className = 'pdf-canvas';
    canvas.width = vp.width;
    canvas.height = vp.height;
    canvas.style.width = vp.width + 'px';
    canvas.style.height = vp.height + 'px';

    const annLayer = document.createElement('div');
    annLayer.className = 'annotation-layer';
    annLayer.id = 'ann-layer-' + num;

    pageDiv.appendChild(canvas);
    pageDiv.appendChild(annLayer);

    const label = document.createElement('div');
    label.className = 'page-label';
    label.textContent = 'Page ' + num + ' of ' + pdfDoc.numPages;

    wrapper.appendChild(pageDiv);
    wrapper.appendChild(label);
    container.appendChild(wrapper);

    await page.render({ canvasContext: canvas.getContext('2d'), viewport: vp }).promise;
  }

  // Re-render existing annotations
  annotations.forEach(ann => renderAnnotation(ann));

  // Re-apply preview mode class
  if (previewMode) viewer.classList.add('preview-mode');
  else viewer.classList.remove('preview-mode');
}

// Click on viewer deselects
viewer.addEventListener('mousedown', e => {
  if (e.target.closest('.annotation')) return;
  deselectAll();
});

function deselectAll() {
  selectedId = null;
  document.querySelectorAll('.annotation.selected').forEach(el => el.classList.remove('selected'));
}

// ——— Finding best page for placement ———
function getMostCenteredPage() {
  const viewerRect = viewer.getBoundingClientRect();
  const viewerCenter = viewerRect.top + viewerRect.height / 2;
  let best = 1, bestDist = Infinity;
  for (let n = 1; n <= pdfDoc.numPages; n++) {
    const el = document.getElementById('page-' + n);
    if (!el) continue;
    const r = el.getBoundingClientRect();
    const pageCenter = r.top + r.height / 2;
    const dist = Math.abs(pageCenter - viewerCenter);
    if (dist < bestDist) { bestDist = dist; best = n; }
  }
  return best;
}

function getVisibleCenter(pageNum) {
  const pageEl = document.getElementById('page-' + pageNum);
  const viewerRect = viewer.getBoundingClientRect();
  const pageRect = pageEl.getBoundingClientRect();
  const visTop = Math.max(0, viewerRect.top - pageRect.top);
  const visBot = Math.min(pageRect.height, viewerRect.bottom - pageRect.top);
  const visLeft = Math.max(0, viewerRect.left - pageRect.left);
  const visRight = Math.min(pageRect.width, viewerRect.right - pageRect.left);
  return { x: (visLeft + visRight) / 2, y: (visTop + visBot) / 2 };
}

// ——— Annotation CRUD ———
function createAnnotation(type, opts) {
  const page = getMostCenteredPage();
  const center = getVisibleCenter(page);
  const w = opts.w || (type === 'sign' ? 240 : 200);
  const h = opts.h || (type === 'sign' ? 110 : 50);
  const jitterX = (Math.random() - 0.5) * 20;
  const jitterY = (Math.random() - 0.5) * 20;
  let x = Math.max(4, center.x - w / 2 + jitterX);
  let y = Math.max(4, center.y - h / 2 + jitterY);

  const ann = {
    id: nextId++,
    type: type,
    page: page,
    x, y, w, h,
    content: opts.content || null,
    strokes: opts.strokes || null
  };
  annotations.push(ann);
  renderAnnotation(ann);
  selectAnnotation(ann.id);
  return ann;
}

function selectAnnotation(id) {
  deselectAll();
  selectedId = id;
  const el = document.getElementById('ann-' + id);
  if (el) el.classList.add('selected');
}

function deleteAnnotation(id) {
  annotations = annotations.filter(a => a.id !== id);
  const el = document.getElementById('ann-' + id);
  if (el) el.remove();
  if (selectedId === id) selectedId = null;
}

function duplicateAnnotation(id) {
  const src = annotations.find(a => a.id === id);
  if (!src) return;
  const ann = {
    id: nextId++,
    type: src.type,
    page: src.page,
    x: src.x + 18,
    y: src.y + 18,
    w: src.w,
    h: src.h,
    content: src.content,
    strokes: src.strokes ? src.strokes.map(s => s.map(p => ({ ...p }))) : null
  };
  annotations.push(ann);
  renderAnnotation(ann);
  selectAnnotation(ann.id);
  showToast('Annotation duplicated');
}

// ——— Render annotation DOM ———
function renderAnnotation(ann) {
  const layer = document.getElementById('ann-layer-' + ann.page);
  if (!layer) return;

  // Remove if already rendered
  const existing = document.getElementById('ann-' + ann.id);
  if (existing) existing.remove();

  const el = document.createElement('div');
  el.className = 'annotation' + (ann.id === selectedId ? ' selected' : '');
  el.id = 'ann-' + ann.id;
  el.style.left = ann.x + 'px';
  el.style.top = ann.y + 'px';
  el.style.width = ann.w + 'px';
  el.style.height = ann.h + 'px';
  el.style.display = 'flex';
  el.style.flexDirection = 'column';

  // Header
  const header = document.createElement('div');
  header.className = 'annotation-header';
  const lbl = document.createElement('span');
  if (ann.type === 'text') { lbl.innerHTML = '<i class="fas fa-font"></i> Text'; }
  else { lbl.innerHTML = '<i class="fas fa-signature"></i> Signature'; }
  header.appendChild(lbl);

  const actions = document.createElement('div');
  actions.className = 'annotation-actions';

  const dupBtn = document.createElement('button');
  dupBtn.className = 'ann-btn';
  dupBtn.dataset.action = 'dup';
  dupBtn.innerHTML = '<i class="fas fa-clone"></i>';
  dupBtn.onmousedown = e => { e.stopPropagation(); duplicateAnnotation(ann.id); };

  const saveBtn = document.createElement('button');
  saveBtn.className = 'ann-btn';
  saveBtn.dataset.action = 'save';
  saveBtn.innerHTML = '<i class="fas fa-bookmark"></i>';
  saveBtn.onmousedown = e => { e.stopPropagation(); saveToLibrary(ann.id); };

  const delBtn = document.createElement('button');
  delBtn.className = 'ann-btn del-btn';
  delBtn.dataset.action = 'del';
  delBtn.innerHTML = '<i class="fas fa-xmark"></i>';
  delBtn.onmousedown = e => { e.stopPropagation(); deleteAnnotation(ann.id); };

  actions.appendChild(dupBtn);
  actions.appendChild(saveBtn);
  actions.appendChild(delBtn);
  header.appendChild(actions);
  el.appendChild(header);

  // Content
  if (ann.type === 'text') {
    const textDiv = document.createElement('div');
    textDiv.className = 'text-content';
    textDiv.contentEditable = 'true';
    if (ann.content) textDiv.innerHTML = ann.content;
    textDiv.addEventListener('input', () => { ann.content = textDiv.innerHTML; });
    textDiv.addEventListener('mousedown', e => e.stopPropagation());
    textDiv.addEventListener('touchstart', e => e.stopPropagation());
    el.appendChild(textDiv);
  } else {
    // Signature
    const signArea = document.createElement('div');
    signArea.className = 'sign-area';

    const signCanvas = document.createElement('canvas');
    signCanvas.style.width = '100%';
    signCanvas.style.height = '100%';

    const placeholder = document.createElement('span');
    placeholder.className = 'sign-placeholder';
    placeholder.textContent = 'Draw signature';

    const clearBtn = document.createElement('button');
    clearBtn.className = 'sign-clear';
    clearBtn.textContent = 'Clear';
    clearBtn.addEventListener('mousedown', e => { e.stopPropagation(); });
    clearBtn.addEventListener('click', e => {
      e.stopPropagation();
      ann.strokes = [];
      redrawSignature(signCanvas, ann);
      placeholder.style.display = '';
    });

    signArea.appendChild(signCanvas);
    signArea.appendChild(placeholder);
    signArea.appendChild(clearBtn);
    el.appendChild(signArea);

    // Drawing logic
    setupSignatureCanvas(signCanvas, ann, placeholder);
  }

  // Resize handle
  const resizeHandle = document.createElement('div');
  resizeHandle.className = 'resize-handle';
  el.appendChild(resizeHandle);

  layer.appendChild(el);

  // Setup dragging
  setupDrag(el, ann, header);
  // Setup resize
  setupResize(el, ann, resizeHandle);

  // Click to select
  el.addEventListener('mousedown', e => {
    if (e.target.closest('.ann-btn') || e.target.closest('.sign-clear')) return;
    selectAnnotation(ann.id);
  });
}

// ——— Signature Canvas ———
function setupSignatureCanvas(canvas, ann, placeholder) {
  let drawing = false;
  let currentStroke = [];

  function resizeCanvas() {
    const rect = canvas.parentElement.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    canvas.width = rect.width * dpr;
    canvas.height = rect.height * dpr;
    canvas.style.width = rect.width + 'px';
    canvas.style.height = rect.height + 'px';
    const ctx = canvas.getContext('2d');
    ctx.scale(dpr, dpr);
    redrawSignature(canvas, ann);
  }

  const ro = new ResizeObserver(() => resizeCanvas());
  ro.observe(canvas.parentElement);
  setTimeout(resizeCanvas, 60);

  function getPos(e) {
    const rect = canvas.getBoundingClientRect();
    const t = e.touches ? e.touches[0] : e;
    return { x: t.clientX - rect.left, y: t.clientY - rect.top };
  }

  function startDraw(e) {
    e.stopPropagation();
    e.preventDefault();
    drawing = true;
    currentStroke = [getPos(e)];
    if (!ann.strokes) ann.strokes = [];
    placeholder.style.display = 'none';
  }
  function moveDraw(e) {
    if (!drawing) return;
    e.stopPropagation();
    e.preventDefault();
    currentStroke.push(getPos(e));
    drawCurrentStroke(canvas, currentStroke);
  }
  function endDraw(e) {
    if (!drawing) return;
    e.stopPropagation();
    drawing = false;
    if (currentStroke.length > 1) {
      ann.strokes.push(currentStroke);
    }
    currentStroke = [];
    redrawSignature(canvas, ann);
  }

  canvas.addEventListener('mousedown', startDraw);
  canvas.addEventListener('mousemove', moveDraw);
  canvas.addEventListener('mouseup', endDraw);
  canvas.addEventListener('mouseleave', endDraw);
  canvas.addEventListener('touchstart', startDraw, { passive: false });
  canvas.addEventListener('touchmove', moveDraw, { passive: false });
  canvas.addEventListener('touchend', endDraw);

  // Initial draw if strokes exist
  if (ann.strokes && ann.strokes.length > 0) {
    placeholder.style.display = 'none';
  }
}

function drawCurrentStroke(canvas, stroke) {
  const ctx = canvas.getContext('2d');
  const dpr = window.devicePixelRatio || 1;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.strokeStyle = '#1a1d24';
  ctx.lineWidth = 2;
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ctx.beginPath();
  for (let i = 0; i < stroke.length; i++) {
    if (i === 0) ctx.moveTo(stroke[i].x, stroke[i].y);
    else ctx.lineTo(stroke[i].x, stroke[i].y);
  }
  ctx.stroke();
}

function redrawSignature(canvas, ann) {
  const ctx = canvas.getContext('2d');
  const dpr = window.devicePixelRatio || 1;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  if (!ann.strokes) return;
  ctx.strokeStyle = '#1a1d24';
  ctx.lineWidth = 2;
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ann.strokes.forEach(stroke => {
    if (stroke.length < 2) return;
    ctx.beginPath();
    ctx.moveTo(stroke[0].x, stroke[0].y);
    for (let i = 1; i < stroke.length; i++) ctx.lineTo(stroke[i].x, stroke[i].y);
    ctx.stroke();
  });
}

// ——— Dragging ———
function setupDrag(el, ann, header) {
  let dragging = false, startX, startY, origX, origY;

  function onStart(e) {
    if (e.target.closest('.text-content') || e.target.closest('.sign-area') || e.target.closest('.ann-btn') || e.target.closest('.sign-clear') || e.target.closest('.resize-handle')) return;
    e.preventDefault();
    dragging = true;
    const t = e.touches ? e.touches[0] : e;
    startX = t.clientX;
    startY = t.clientY;
    origX = ann.x;
    origY = ann.y;
    el.classList.add('dragging');
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onEnd);
    document.addEventListener('touchmove', onMove, { passive: false });
    document.addEventListener('touchend', onEnd);
  }
  function onMove(e) {
    if (!dragging) return;
    e.preventDefault();
    const t = e.touches ? e.touches[0] : e;
    let nx = origX + (t.clientX - startX);
    let ny = origY + (t.clientY - startY);
    const layer = document.getElementById('ann-layer-' + ann.page);
    if (layer) {
      nx = Math.max(0, Math.min(nx, layer.clientWidth - ann.w));
      ny = Math.max(0, Math.min(ny, layer.clientHeight - ann.h));
    }
    ann.x = nx;
    ann.y = ny;
    el.style.left = nx + 'px';
    el.style.top = ny + 'px';
  }
  function onEnd() {
    dragging = false;
    el.classList.remove('dragging');
    document.removeEventListener('mousemove', onMove);
    document.removeEventListener('mouseup', onEnd);
    document.removeEventListener('touchmove', onMove);
    document.removeEventListener('touchend', onEnd);
  }

  el.addEventListener('mousedown', onStart);
  el.addEventListener('touchstart', onStart, { passive: false });
}

// ——— Resizing ———
function setupResize(el, ann, handle) {
  let resizing = false, startX, startY, origW, origH;
  function onStart(e) {
    e.preventDefault();
    e.stopPropagation();
    resizing = true;
    const t = e.touches ? e.touches[0] : e;
    startX = t.clientX;
    startY = t.clientY;
    origW = ann.w;
    origH = ann.h;
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onEnd);
    document.addEventListener('touchmove', onMove, { passive: false });
    document.addEventListener('touchend', onEnd);
  }
  function onMove(e) {
    if (!resizing) return;
    e.preventDefault();
    const t = e.touches ? e.touches[0] : e;
    let nw = Math.max(100, origW + (t.clientX - startX));
    let nh = Math.max(44, origH + (t.clientY - startY));
    ann.w = nw;
    ann.h = nh;
    el.style.width = nw + 'px';
    el.style.height = nh + 'px';
  }
  function onEnd() {
    resizing = false;
    document.removeEventListener('mousemove', onMove);
    document.removeEventListener('mouseup', onEnd);
    document.removeEventListener('touchmove', onMove);
    document.removeEventListener('touchend', onEnd);
  }
  handle.addEventListener('mousedown', onStart);
  handle.addEventListener('touchstart', onStart, { passive: false });
}

// ——— Toolbar Actions ———
btnText.onclick = () => createAnnotation('text', { w: 200, h: 50 });
btnSign.onclick = () => createAnnotation('sign', { w: 240, h: 110, strokes: [] });
btnDate.onclick = () => {
  const now = new Date();
  const dateStr = (now.getMonth() + 1) + '/' + now.getDate() + '/' + now.getFullYear();
  createAnnotation('text', { w: 140, h: 44, content: dateStr });
};

btnDup.onclick = () => { if (selectedId) duplicateAnnotation(selectedId); };

// ——— Library ———
function saveToLibrary(id) {
  const ann = annotations.find(a => a.id === id);
  if (!ann) return;
  library.push({
    type: ann.type,
    w: ann.w,
    h: ann.h,
    content: ann.content,
    strokes: ann.strokes ? ann.strokes.map(s => s.map(p => ({ ...p }))) : null,
    savedAt: new Date().toISOString()
  });
  showToast('Saved to library');
}

btnSave.onclick = () => { if (selectedId) saveToLibrary(selectedId); };

btnLibrary.onclick = () => {
  let html = '';
  if (library.length === 0) {
    html = '<div class="library-empty">Library is empty. Save annotations to see them here.</div>';
  } else {
    html = '<div class="library-list">';
    library.forEach((item, idx) => {
      const badge = item.type === 'text' ? 'Text' : 'Sign';
      let preview = '';
      if (item.type === 'text') {
        const tmp = document.createElement('div');
        tmp.innerHTML = item.content || '';
        preview = (tmp.textContent || '').substring(0, 50) || '(empty)';
      } else {
        preview = (item.strokes ? item.strokes.length : 0) + ' stroke(s)';
      }
      html += `<div class="library-item" data-idx="${idx}">
        <span class="lib-badge">${badge}</span>
        <span class="lib-preview">${preview}</span>
        <button class="lib-place" data-idx="${idx}">Place</button>
        <button class="lib-del" data-idx="${idx}">Delete</button>
      </div>`;
    });
    html += '</div>';
  }
  html += '<div style="margin-top:16px;display:flex;gap:8px">';
  html += '<button class="modal-btn" id="lib-export-btn" style="font-size:12px"><i class="fas fa-download"></i> Export Library</button>';
  html += '<button class="modal-btn" id="lib-import-btn" style="font-size:12px"><i class="fas fa-upload"></i> Import Library</button>';
  html += '</div>';

  openModal('Library', html, [{ label: 'Close', cls: '', onclick: closeModal }]);

  // Place / Delete
  modalBody.querySelectorAll('.lib-place').forEach(btn => {
    btn.onclick = () => {
      const item = library[+btn.dataset.idx];
      if (!pdfDoc) { showToast('Load a PDF first'); return; }
      createAnnotation(item.type, {
        w: item.w, h: item.h,
        content: item.content,
        strokes: item.strokes ? item.strokes.map(s => s.map(p => ({ ...p }))) : null
      });
      closeModal();
      showToast('Placed from library');
    };
  });
  modalBody.querySelectorAll('.lib-del').forEach(btn => {
    btn.onclick = () => {
      library.splice(+btn.dataset.idx, 1);
      btnLibrary.click(); // refresh
    };
  });

  // Export library
  const libExportBtn = document.getElementById('lib-export-btn');
  if (libExportBtn) {
    libExportBtn.onclick = () => {
      const json = JSON.stringify(library, null, 2);
      downloadFile(json, 'docusign-library.json', 'application/json');
      showToast('Library exported');
    };
  }

  // Import library
  const libImportBtn = document.getElementById('lib-import-btn');
  if (libImportBtn) {
    libImportBtn.onclick = () => {
      openModal('Import Library', '<textarea id="lib-import-text" placeholder="Paste library JSON here…"></textarea>', [
        { label: 'Cancel', cls: '', onclick: () => btnLibrary.click() },
        { label: 'Import', cls: 'modal-btn-primary', onclick: () => {
          try {
            const data = JSON.parse(document.getElementById('lib-import-text').value);
            if (Array.isArray(data)) {
              library.push(...data);
              showToast('Imported ' + data.length + ' item(s)');
            }
          } catch (err) { showToast('Invalid JSON'); }
          btnLibrary.click();
        }}
      ]);
    };
  }
};

// ——— YAML Export ———
function yamlEscapeStr(s) {
  if (s === '' || s == null) return '""';
  const special = /[:{}\[\],&*?|><=@%#'"!\n\r\t\\]/;
  if (special.test(s) || s !== s.trim()) {
    const escaped = s.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/\n/g, '\\n').replace(/\r/g, '\\r').replace(/\t/g, '\\t');
    return '"' + escaped + '"';
  }
  return s;
}

function strokesToSVG(strokes, w, h) {
  let paths = '';
  strokes.forEach(stroke => {
    if (stroke.length < 1) return;
    let d = 'M ' + stroke[0].x.toFixed(2) + ' ' + stroke[0].y.toFixed(2);
    for (let i = 1; i < stroke.length; i++) {
      d += ' L ' + stroke[i].x.toFixed(2) + ' ' + stroke[i].y.toFixed(2);
    }
    paths += `<path d="${d}" fill="none" stroke="#1a1d24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>`;
  });
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" width="${w}" height="${h}">${paths}</svg>`;
}

btnExport.onclick = () => {
  if (!pdfDoc || annotations.length === 0) { showToast('No annotations to export'); return; }
  let yaml = '# DocuSign Annotation File\n';
  yaml += '# Generated: ' + new Date().toISOString() + '\n';
  yaml += 'version: 1\n';
  yaml += 'source_pdf: ' + yamlEscapeStr(pdfFileName) + '\n';
  yaml += 'annotations:\n';

  annotations.forEach(ann => {
    yaml += '  - type: ' + ann.type + '\n';
    yaml += '    page: ' + ann.page + '\n';
    yaml += '    x: ' + ann.x.toFixed(2) + '\n';
    yaml += '    y: ' + ann.y.toFixed(2) + '\n';
    yaml += '    w: ' + ann.w.toFixed(2) + '\n';
    yaml += '    h: ' + ann.h.toFixed(2) + '\n';
    if (ann.type === 'text') {
      const tmp = document.createElement('div');
      tmp.innerHTML = ann.content || '';
      yaml += '    content: ' + yamlEscapeStr(tmp.textContent || '') + '\n';
    } else {
      const headerEl = document.querySelector('#ann-' + ann.id + ' .annotation-header');
      const headerH = headerEl ? headerEl.offsetHeight : 28;
      const signArea = document.querySelector('#ann-' + ann.id + ' .sign-area');
      const sw = signArea ? signArea.clientWidth : ann.w;
      const sh = signArea ? signArea.clientHeight : (ann.h - headerH);
      const svg = strokesToSVG(ann.strokes || [], sw, sh);
      yaml += '    signature_svg: ' + yamlEscapeStr(svg) + '\n';
    }
  });

  const baseName = pdfFileName.replace(/\.pdf$/i, '');
  downloadFile(yaml, baseName + '-annotations.yaml', 'text/yaml');
  showToast('Annotations exported');
};

// ——— YAML Import ———
btnImport.onclick = () => {
  if (!pdfDoc) { showToast('Load a PDF first'); return; }
  yamlInput.click();
};
yamlInput.addEventListener('change', e => {
  const file = e.target.files[0];
  if (!file) return;
  yamlInput.value = '';
  const reader = new FileReader();
  reader.onload = () => {
    const parsed = parseYAML(reader.result);
    if (!parsed || !parsed.length) { showToast('No annotations found in file'); return; }
    if (annotations.length > 0) {
      openModal('Import Annotations',
        `<p style="font-size:14px;color:var(--text-secondary)">Found ${parsed.length} annotation(s). You have ${annotations.length} existing annotation(s). Replace all or merge?</p>`,
        [
          { label: 'Cancel', cls: '', onclick: closeModal },
          { label: 'Merge', cls: 'modal-btn-primary', onclick: () => { closeModal(); importAnnotations(parsed, false); } },
          { label: 'Replace', cls: 'modal-btn-danger', onclick: () => { closeModal(); importAnnotations(parsed, true); } }
        ]
      );
    } else {
      importAnnotations(parsed, true);
    }
  };
  reader.readAsText(file);
});

function importAnnotations(parsed, replace) {
  if (replace) {
    annotations.forEach(a => { const el = document.getElementById('ann-' + a.id); if (el) el.remove(); });
    annotations = [];
    selectedId = null;
  }
  parsed.forEach(item => {
    const page = Math.max(1, Math.min(item.page, pdfDoc.numPages));
    const ann = {
      id: nextId++,
      type: item.type,
      page: page,
      x: item.x || 20,
      y: item.y || 20,
      w: item.w || (item.type === 'sign' ? 240 : 200),
      h: item.h || (item.type === 'sign' ? 110 : 50),
      content: item.content || null,
      strokes: item.strokes || null
    };
    annotations.push(ann);
    renderAnnotation(ann);
  });
  showToast('Imported ' + parsed.length + ' annotation(s)');
}

function parseYAML(text) {
  const lines = text.split('\n');
  const result = [];
  let current = null;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    if (trimmed === '' || trimmed.startsWith('#')) continue;
    if (trimmed === '- type: text' || trimmed === '- type: sign') {
      if (current) result.push(current);
      current = { type: trimmed.split(': ')[1] };
      continue;
    }
    if (!current) continue;
    const m = trimmed.match(/^(\w[\w_]*)\s*:\s*(.*)$/);
    if (!m) continue;
    const key = m[1];
    let val = m[2];
    if (key === 'page') current.page = parseInt(val);
    else if (key === 'x') current.x = parseFloat(val);
    else if (key === 'y') current.y = parseFloat(val);
    else if (key === 'w') current.w = parseFloat(val);
    else if (key === 'h') current.h = parseFloat(val);
    else if (key === 'content') current.content = yamlUnescapeStr(val);
    else if (key === 'signature_svg') {
      const svg = yamlUnescapeStr(val);
      current.strokes = svgToStrokes(svg);
    }
  }
  if (current) result.push(current);
  return result;
}

function yamlUnescapeStr(s) {
  s = s.trim();
  if (s.startsWith('"') && s.endsWith('"')) {
    s = s.slice(1, -1);
    s = s.replace(/\\n/g, '\n').replace(/\\r/g, '\r').replace(/\\t/g, '\t').replace(/\\"/g, '"').replace(/\\\\/g, '\\');
  }
  return s;
}

function svgToStrokes(svg) {
  const strokes = [];
  const pathRe = /<path\s+d="([^"]+)"/g;
  let pm;
  while ((pm = pathRe.exec(svg))) {
    const d = pm[1];
    const cmdRe = /([ML])\s+([\d.eE+-]+)\s+([\d.eE+-]+)/g;
    let cm, stroke = [];
    while ((cm = cmdRe.exec(d))) {
      stroke.push({ x: parseFloat(cm[2]), y: parseFloat(cm[3]) });
    }
    if (stroke.length > 0) strokes.push(stroke);
  }
  return strokes;
}

// ——— Preview ———
btnPreview.onclick = togglePreview;
function togglePreview() {
  previewMode = !previewMode;
  if (previewMode) {
    viewer.classList.add('preview-mode');
    btnPreview.classList.add('active');
    btnPreview.querySelector('i').className = 'fas fa-eye-slash';
    deselectAll();
  } else {
    viewer.classList.remove('preview-mode');
    btnPreview.classList.remove('active');
    btnPreview.querySelector('i').className = 'fas fa-eye';
  }
}

// ——— Download (Flatten) ———
btnDownload.onclick = async () => {
  if (!pdfDoc) return;
  showToast('Generating PDF…');

  const { jsPDF } = window.jspdf;
  let doc = null;

  for (let num = 1; num <= pdfDoc.numPages; num++) {
    const baseVP = pdfPageViewports[num];
    const pageEl = document.getElementById('page-' + num);
    if (!pageEl) continue;

    const pdfCanvas = pageEl.querySelector('.pdf-canvas');
    const w = pdfCanvas.width;
    const h = pdfCanvas.height;

    // Clone canvas
    const outCanvas = document.createElement('canvas');
    outCanvas.width = w;
    outCanvas.height = h;
    const ctx = outCanvas.getContext('2d');
    ctx.drawImage(pdfCanvas, 0, 0);

    // Draw annotations
    const pageAnns = annotations.filter(a => a.page === num);
    pageAnns.forEach(ann => {
      const annEl = document.getElementById('ann-' + ann.id);
      if (!annEl) return;
      const header = annEl.querySelector('.annotation-header');
      const headerH = header ? header.offsetHeight : 28;

      if (ann.type === 'text') {
        const tmp = document.createElement('div');
        tmp.innerHTML = ann.content || '';
        const plainText = tmp.textContent || '';
        if (!plainText) return;

        ctx.save();
        ctx.font = '16px Inter, sans-serif';
        ctx.fillStyle = '#1a1d24';
        ctx.textBaseline = 'top';

        const maxW = ann.w - 16;
        const lines = wrapText(ctx, plainText, maxW);
        const lineH = 16 * 1.4;
        lines.forEach((line, li) => {
          ctx.fillText(line, ann.x + 8, ann.y + headerH + 6 + li * lineH);
        });
        ctx.restore();
      } else {
        if (!ann.strokes || ann.strokes.length === 0) return;
        ctx.save();
        ctx.strokeStyle = '#1a1d24';
        ctx.lineWidth = 2;
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';
        ann.strokes.forEach(stroke => {
          if (stroke.length < 2) return;
          ctx.beginPath();
          ctx.moveTo(ann.x + stroke[0].x, ann.y + headerH + stroke[0].y);
          for (let i = 1; i < stroke.length; i++) {
            ctx.lineTo(ann.x + stroke[i].x, ann.y + headerH + stroke[i].y);
          }
          ctx.stroke();
        });
        ctx.restore();
      }
    });

    const imgData = outCanvas.toDataURL('image/png');
    const pw = baseVP.width;
    const ph = baseVP.height;

    if (num === 1) {
      doc = new jsPDF({ unit: 'pt', format: [pw, ph] });
    } else {
      doc.addPage([pw, ph]);
    }
    doc.addImage(imgData, 'PNG', 0, 0, pw, ph);
  }

  if (doc) {
    const baseName = pdfFileName.replace(/\.pdf$/i, '');
    doc.save(baseName + '-signed.pdf');
    showToast('PDF downloaded');
  }
};

function wrapText(ctx, text, maxWidth) {
  const words = text.split(/\s+/);
  const lines = [];
  let line = '';
  words.forEach(word => {
    const test = line ? line + ' ' + word : word;
    if (ctx.measureText(test).width > maxWidth && line) {
      lines.push(line);
      line = word;
    } else {
      line = test;
    }
  });
  if (line) lines.push(line);
  return lines;
}

// ——— Keyboard Shortcuts ———
document.addEventListener('keydown', e => {
  const tag = document.activeElement.tagName.toLowerCase();
  const isEditing = document.activeElement.isContentEditable || tag === 'input' || tag === 'textarea';

  // Escape
  if (e.key === 'Escape') {
    closeModal();
    deselectAll();
    return;
  }

  // Ctrl/Cmd + D → duplicate
  if ((e.ctrlKey || e.metaKey) && e.key === 'd') {
    e.preventDefault();
    if (selectedId) duplicateAnnotation(selectedId);
    return;
  }

  if (isEditing) return;

  // Delete / Backspace
  if ((e.key === 'Delete' || e.key === 'Backspace') && selectedId) {
    e.preventDefault();
    deleteAnnotation(selectedId);
    return;
  }

  // P → preview
  if (e.key === 'p' || e.key === 'P') {
    togglePreview();
  }
});

// ——— Utility ———
function downloadFile(content, name, mime) {
  const blob = new Blob([content], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

// Initialize toolbar state
updateToolbar();
