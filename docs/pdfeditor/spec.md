# DocuSign PDF Annotator — Detailed SPA Specification

---

## Overview

A single-page, zero-backend, client-side-only PDF signing and annotation tool. Users open a local PDF, place text and signature annotations on top of it, then either download the flattened PDF or export/import annotations as a separate YAML file. No server, no accounts, no uploads.

---

## Technology Stack

**Runtime dependencies (CDN):**
- PDF.js 3.11.174 — PDF rendering
- jsPDF 2.5.1 — PDF generation/download
- Font Awesome 6.5.1 — icons
- Google Fonts: Inter (UI), Merriweather (brand)

**No framework.** Vanilla HTML/CSS/JS only. Single self-contained `.html` file.

---

## Layout & Chrome

### Toolbar (fixed, top, 60px tall)
Left to right:
1. **Brand** — "DocuSign" wordmark, gradient text (indigo → violet), Merriweather serif
2. Divider
3. **Open PDF** button — accent/filled style; triggers hidden `<input type="file" accept=".pdf">`
4. Divider
5. **Text** button — disabled until PDF loaded
6. **Sign** button — disabled until PDF loaded
7. **Date** button — disabled until PDF loaded
8. Divider
9. **Duplicate** button — disabled until PDF loaded
10. **Save** button (save selected to library) — disabled until PDF loaded
11. **Library** button — always enabled
12. Divider
13. **Export Ann.** button — disabled until PDF loaded; exports YAML
14. **Import Ann.** button — always enabled; triggers hidden `<input type="file" accept=".yaml,.yml">`
15. Divider
16. **Preview** toggle button — always enabled; eye icon, toggles to eye-slash when active
17. Spacer (flex: 1)
18. **Download** button — disabled until PDF loaded; downloads flattened PDF

### Main Area (fills remaining viewport below toolbar)
- Single `div.pdf-viewer` — scrollable, canvas-colored background (`#e8eaee` light / `#13151c` dark)
- Contains a `div.pdf-container` (flex column, centered) when PDF is loaded
- Shows a centered empty state illustration/text when no PDF is loaded

### Empty State
Centered vertically and horizontally. Large faded PDF icon, "No PDF Loaded" heading, one line of instructional text.

---

## Theming

### CSS Custom Properties
Defined on `:root` (light) and `.dark` (dark). The `.dark` class is toggled on `<html>`.

| Variable | Light | Dark |
|---|---|---|
| `--bg-primary` | `#f8f9fb` | `#0f1117` |
| `--bg-secondary` | `#ffffff` | `#1a1d24` |
| `--bg-canvas` | `#e8eaee` | `#13151c` |
| `--text-primary` | `#1a1d24` | `#f0f2f5` |
| `--text-secondary` | `#4a5263` | `#b4b9c5` |
| `--text-muted` | `#8891a8` | `#6b7280` |
| `--accent` | `#4f46e5` | `#6366f1` |
| `--accent-hover` | `#4338ca` | `#818cf8` |
| `--accent-light` | `rgba(79,70,229,0.1)` | `rgba(99,102,241,0.15)` |
| `--border` | `#d1d5db` | `#2d3139` |
| `--border-light` | `#e5e7eb` | `#23262e` |
| `--danger` | `#dc2626` | `#ef4444` |
| `--danger-hover` | `#b91c1c` | `#f87171` |
| `--overlay` | `rgba(26,29,36,0.5)` | `rgba(0,0,0,0.7)` |

Dark mode is detected automatically via `prefers-color-scheme` media query on page load and updated on change.

---

## PDF Rendering

- `RENDER_SCALE = 1.5` — all pages rendered at 1.5× their native size
- Store the `scale=1` viewport for each page in `pdfPageViewports[pageNum]` for later use in PDF export
- Each page renders as:
  ```
  div.pdf-page-wrapper
    div.pdf-page#page-{N}  (width/height set to viewport.width/height px)
      canvas.pdf-canvas    (same dimensions)
      div.annotation-layer#ann-layer-{N}  (absolute, fills page, pointer-events:none)
    div.page-label         ("Page N of Total", bottom-center, pill badge)
  ```
- Pages are rendered sequentially (promise chain), not in parallel
- Loading a new PDF clears all existing annotations and page viewports

---

## Annotation Data Model

Each annotation is a plain JS object:

```js
{
  id: Number,          // auto-increment, unique
  type: 'text'|'sign',
  page: Number,        // 1-indexed
  x: Number,           // CSS px from left edge of page div
  y: Number,           // CSS px from top edge of page div
  w: Number,           // width in CSS px
  h: Number,           // height in CSS px
  content: String|null,   // HTML string for text type; null for sign
  strokes: Array|null     // array of stroke arrays [{x,y},...] for sign; null for text
}
```

Global state: `annotations[]`, `library[]`, `selectedId`, `nextId`, `pdfDoc`, `pdfFileName`, `pdfPageViewports[]`, `previewMode`.

---

## Annotation Types

### Text Annotation
- Default size: 200×50px
- Contains a `contenteditable="true"` div with `background: transparent`
- Placeholder text "Type here…" via CSS `::before` when empty
- `ann.content` stores raw `innerHTML`; synced on `input` event
- Text color: `#1a1d24` (always dark, readable on white PDF backgrounds)
- Font: Inter 16px, line-height 1.4

### Signature Annotation
- Default size: 240×110px
- Contains a `<canvas>` inside a `div.sign-area` (both `background: transparent`)
- **Drawing:** freehand strokes captured as `[{x,y},...]` arrays using mouse and touch events
- **Rendering:** `ctx.strokeStyle = '#1a1d24'`, lineWidth 2, lineCap/lineJoin round
- **ResizeObserver** watches the sign-area and re-sizes the canvas (with DPR scaling) and redraws whenever dimensions change
- "Draw signature" placeholder centered, hidden once any stroke exists
- "Clear" button (bottom-right corner) resets `ann.strokes = []` and redraws

### Date Annotation
- Implemented as a text annotation pre-filled with `M/D/YYYY` format
- Default size: 140×44px

---

## Annotation DOM Structure

```html
div.annotation#ann-{id}  (position:absolute, border:2px solid accent, background:transparent)
  div.annotation-header  (background: accent-light, cursor:grab)
    span  (icon + "Text"/"Signature" label)
    div.annotation-actions
      button[data-action="dup"]   <!-- clone icon -->
      button[data-action="save"]  <!-- bookmark icon -->
      button[data-action="del"]   <!-- x icon, red hover -->
  div.text-content[contenteditable]   OR   div.sign-area > canvas + span.sign-placeholder + button.sign-clear
  div.resize-handle  (bottom-right, nwse-resize cursor, decorative chevron ::after)
```

---

## Annotation Interaction

### Placement
New annotations are placed centered on the visible portion of the currently most-centered page. A small random jitter (±10px) prevents stacking. Position clamped to `x >= 4, y >= 4`.

### Selection
Clicking an annotation sets `selectedId` and adds `.selected` class (adds accent glow ring). Clicking empty PDF area deselects.

### Dragging
`mousedown`/`touchstart` on the annotation (excluding text-content, sign-canvas, sign-clear, ann-btn) initiates drag. Position is clamped within the annotation layer bounds. Adds `.dragging` class (cursor:grabbing, opacity 0.88).

### Resizing
`mousedown`/`touchstart` on `.resize-handle` initiates resize. Minimum width 100px, minimum height 44px. Resizes annotation box; sign canvas auto-redraws via ResizeObserver.

### Deletion
- Via header delete button (`mousedown` to avoid interaction race)
- Via keyboard `Delete`/`Backspace` when annotation is selected and no text input is focused

### Duplication
- Via header clone button
- Via toolbar Duplicate button (requires selection)
- Via `Ctrl/Cmd+D` keyboard shortcut
- Offset: +18px x, +18px y from original

---

## Preview Mode

Toggled by the Preview button or by pressing `P` (when not editing text).

When active, `.preview-mode` class is added to `div.pdf-viewer`. CSS rules under this class:
- `.annotation` — `border-color: transparent`, `box-shadow: none`, `cursor: default`, `pointer-events: none`
- `.annotation-header` — `display: none`
- `.resize-handle` — `display: none`
- `.sign-clear` — `display: none`
- `.sign-placeholder` — `display: none`
- `.text-content::before` — suppressed (content: '')

Preview mode state persists across PDF re-renders (reapplied after `viewer.innerHTML = ''`).

Button shows active state (filled accent) and icon changes to eye-slash when preview is on.

---

## Library

In-memory array of serialized annotation objects. Each item stores `type`, `w`, `h`, `content` or `strokes`, and `savedAt` timestamp.

**Save:** Via toolbar Save button or per-annotation header save button. Requires selection.

**Library Modal:** Lists all saved items. Each row shows type badge, preview text (for text: first 50 chars of plain text; for sign: N strokes), Place button, and Delete button.

**Place:** Creates a new annotation from the library item on the current visible page, centered in view.

**Export Library:** Serializes `library[]` to JSON and triggers download as `docusign-library.json`.

**Import Library:** Modal with textarea; accepts pasted JSON; merges items into existing library.

---

## YAML Annotation Export/Import

### Export Format

File name: `{pdfBaseName}-annotations.yaml`

```yaml
# DocuSign Annotation File
# Generated: {ISO timestamp}
version: 1
source_pdf: {filename}
annotations:
  - type: text
    page: 1
    x: 123.45
    y: 67.89
    w: 200.00
    h: 50.00
    content: "Hello World"
  - type: sign
    page: 1
    x: 200.00
    y: 300.00
    w: 240.00
    h: 110.00
    signature_svg: "<svg xmlns=...><path d=\"M 10.00 20.00 L 30.00 40.00\" .../></svg>"
```

### SVG Signature Encoding
- Each stroke becomes one `<path>` element
- Path `d` attribute uses `M x y` for first point then `L x y` for subsequent points
- SVG `viewBox` and `width`/`height` set to the sign-area's pixel dimensions at time of export
- Stroke style baked into path attributes: `fill="none" stroke="#1a1d24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"`

### YAML String Escaping
Custom `yamlEscapeStr()` function:
- Empty string → `""`
- Strings containing `: { } [ ] , & * ? | > < = % @ \` # ' " ! \n \r \t` or leading/trailing whitespace → wrapped in double quotes with backslash escaping of `\`, `"`, `\n`, `\r`, `\t`
- Safe strings → bare (unquoted)

### Import Behavior
- Accepts `.yaml` / `.yml` files
- Custom lightweight line-by-line YAML parser (handles only the exact schema generated above)
- SVG parsed back to strokes via regex matching `<path d="...">` then `M x y` / `L x y` commands
- If existing annotations are present: shows Replace/Merge dialog
  - **Replace** (Confirm button): clears all existing annotations, loads file
  - **Merge** (dynamically injected button): appends to existing annotations
- Page numbers clamped to `[1, pdfDoc.numPages]`
- Works on any loaded PDF (annotations are not locked to the source PDF name)
- Import Ann. button is always enabled; prompts to load a PDF first if none is loaded

---

## PDF Download (Flatten)

Produces a rasterized PDF where annotations are burned into the page pixels.

**Process:**
1. For each page, clone the PDF canvas into a new `<canvas>` (same pixel dimensions)
2. For each annotation on that page:
   - **Text:** measure and word-wrap `ann.content` (HTML stripped to plain text), draw with `ctx.fillText` at Inter 16px, no background fill (transparent over PDF)
   - **Sign:** replay strokes offset by annotation position + header height, no background fill
3. Convert canvas to PNG data URL
4. Assemble pages into jsPDF, using `baseVP.width` × `baseVP.height` as page dimensions in points
5. Save as `{pdfBaseName}-signed.pdf`

**Coordinate mapping:** CSS px on the page div maps 1:1 to canvas pixels (since page div CSS size equals canvas pixel size). Header height is read from `header.offsetHeight` to correctly offset the content area.

---

## Modal System

Single reusable `openModal(title, bodyHTML, footerButtons[])` function.

`footerButtons` items: `{ label, id, cls, onclick }`. Button classes combine `modal-btn` with any additional class string (e.g. `modal-btn-primary`, `modal-btn-danger`).

Backdrop click and × button close the modal. `Escape` key also closes.

Only one modal can be open at a time (opening a new one calls `closeModal()` first).

`showConfirm(message, onConfirm)` is a convenience wrapper producing Cancel + Confirm (red) buttons.

---

## Toast Notification

Fixed bottom-center pill. Slides up with opacity transition on `.show` class. Auto-dismisses after 2500ms. Subsequent calls reset the timer.

---

## Keyboard Shortcuts

| Key | Action | Condition |
|---|---|---|
| `Delete` / `Backspace` | Delete selected annotation | Not editing text/textarea |
| `Ctrl/Cmd + D` | Duplicate selected annotation | Always |
| `Escape` | Deselect + close modal | Always |
| `P` | Toggle preview mode | Not editing text/textarea |

---

## Responsive Behavior

At `max-width: 768px`:
- Toolbar padding reduced, gap reduced
- Brand font-size reduced to 16px
- Button text labels (`.label` spans) hidden — icons only
- PDF viewer padding reduced to 10px
- Toolbar scrolls horizontally if needed

---

## Scrollbar Styling (WebKit)

Custom thin scrollbars using `--border` color thumb, transparent track, 10px width/height.

---

## Constraints & Edge Cases

- Same PDF file can be re-loaded (input `value` reset after each load)
- Same annotation YAML file can be re-imported (input `value` reset after each import)
- Sign canvas resizes gracefully — ResizeObserver fires `resizeCanvas()` which preserves strokes by calling `redraw()`
- Sign canvas initialization is deferred 60ms (`setTimeout`) to allow layout to settle before first `getBoundingClientRect()`
- Annotation positions clamped to layer bounds during drag
- Text annotation `mousedown` and `touchstart` stop propagation to prevent accidental drag initiation
- Sign canvas events stop propagation and prevent default to avoid interfering with annotation drag and page scroll
- `previewMode` state survives PDF re-renders