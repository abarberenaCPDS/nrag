# UI Parity — React → Blazor

Source of truth for all Blazor frontend parity work. Written from systematic Playwright
review of `localhost:3000` (React) cross-referenced against the current Blazor implementation.

**Screenshots:** `docs/screenshots/react-review/` (53 captures, run `fixtures/review_react_ui.py`)  
**Spec detail:** `docs/ui-spec.md` (React-only, control-by-control)  
**Parity matrix:** `docs/blazor-parity-matrix.md` (feature table)

---

## Architecture Overview

| Aspect | React | Blazor |
|--------|-------|--------|
| Framework | React 18 + TypeScript + Zustand stores | Blazor Server + scoped state services |
| Component library | KUI (`@kui/react`) | MudBlazor + custom CSS |
| State management | 13 Zustand stores (client-side) | Scoped C# services per SignalR circuit |
| Routing | react-router-dom | Blazor routing (`@page`) |
| CSS | global `app.css` + CSS Modules | global `app.css` + `.razor.css` scoped files |
| Pages | 3: `/`, `/collections/new`, `/settings` | Same 3 routes + `/error`, `/` (404) |
| New Collection UX | **Single-page 2-column form** | **3-step wizard (Next/Next/Create)** ← major gap |

---

## Priority Legend

| Level | Meaning |
|-------|---------|
| **P0** | Broken or missing core flow — blocks users |
| **P1** | Visible gap — feature exists in React, missing/wrong in Blazor |
| **P2** | Polish / edge case — behavior differs but not blocking |
| **P3** | Deferred / won't fix for now |

---

## 1. Chat Page `/`

### Layout

```
┌─ Header ────────────────────────────────────────────────────────────┐
│  NVIDIA logo · "RAG Blueprint"          [🔔 1]  [⚙ Settings]       │
├─ Sidebar ──────────┬─ Main area ─────────────────────────────────────┤
│  [Search…]         │  (empty state or messages)                     │
│  collection card   │                                                  │
│  collection card   │                                                  │
│  [filter bar]      │                                                  │
│  + New Collection  │  ┌─ message input bar ──────────────────────┐  │
│                    │  │ [chips] textarea… [📎] [Standard ▾] [➤]  │  │
└────────────────────┴──┴──────────────────────────────────────────┴──┘
```

Right side: citations panel slides in on top of main area when a citation button is clicked.

### Parity gaps

| Feature | React | Blazor | Gap | P |
|---------|-------|--------|-----|---|
| Message input: collection chips | Chips appear inside input row for each selected collection | ✅ chips in input row | Match | — |
| Agentic mode selector | "Standard" / "Agentic" dropdown in the input bar | ✅ AgenticModeSelector.razor | Verify label text and options match | P2 |
| Filter bar with 1 collection | Slides in between collection list and footer | ✅ FilterBar.razor | Verify all operator types per field type | P1 |
| Multi-collection warning | Yellow banner inside sidebar | ✅ yellow banner | Match | — |
| Ingestion progress in card | While task is pending, ⋮ button replaced by "X/N 🔄" | ❌ not implemented — ⋮ always shows | P1 |
| Image attach in chat | Drag-drop or attach-menu button in input bar | ❓ not verified in Blazor | Verify image attach flow | P1 |
| ⋮ selected opacity | `opacity: 1` on hover only | `opacity: 1` on hover **and** when selected | Extra always-visible state in Blazor | P3 |

### Ingestion progress in card (P1)
React `CollectionItem.tsx` logic:
```tsx
// While any task for this collection is PENDING:
//   replace ⋮ button with:
<button onClick={openNotificationPanel} title="View upload progress">
  <Text>{completedCount}/{totalCount}</Text>
  <SpinnerIcon />
</button>
```
Blazor fix: in `CollectionSidebar.razor`, check `NotifState` for pending tasks for `col.CollectionName`; if found, replace `collection-more-btn` with a spinner + progress text that opens the notification bell.

---

## 2. Collection Sidebar

### Card layout

```
┌─────────────────────────────────────┐
│  collection_name                    │
│  1,234 entities               [⋮]  │  ← ⋮ hidden; appears on hover
└─────────────────────────────────────┘
```

During active ingestion (React only):
```
┌─────────────────────────────────────┐
│  collection_name                    │
│  1,234 entities        0/1 🔄      │  ← ⋮ replaced by progress
└─────────────────────────────────────┘
```

### Controls

| Control | React | Blazor | Notes |
|---------|-------|--------|-------|
| Single click | Selects/deselects (chip in input) | Toggles checkbox + selection | Match |
| Checkbox | Inside card, stopPropagation | Inside card, stopPropagation | Match |
| ⋮ button | `<Button kind="tertiary" size="tiny">` with MoreIcon SVG | Custom button with `.collection-more-btn` CSS | Functionally same |
| Search input | `input[type="search"]` | `input[type="search"]` | Match |
| Empty state text | "Create your first collection and add files to customize your model's response." | Should match | Verify exact text |
| Search no-results | "No collections match 'query'" | "No collections match '@SearchQuery'" | Match |
| + New Collection | Footer anchor → `/collections/new` | Footer anchor → `/collections/new` | Match |

---

## 3. Collection Drawer

**Trigger:** ⋮ button on collection card (single click, `stopPropagation`)  
**Component:** `SidePanel` (KUI) — slides in from right, dark overlay behind

### Layout

```
┌─ SidePanel ───────────────────────────────────────┐
│  collection_name                              [×] │
├───────────────────────────────────────────────────┤
│  CollectionCatalogInfo                            │  ← P1: missing in Blazor
│    Description · Tags · Owner · Domain · Status   │
├───────────────────────────────────────────────────┤
│  Documents                                        │
│    filename.pdf    2.1 MB    Jun 26    [🗑]       │
│    (empty state if no documents)                  │
├───────────────────────────────────────────────────┤
│  [Delete Collection]   [Add Source to Collection] │  ← footer actions
│                        (or [Close Uploader])      │
└───────────────────────────────────────────────────┘
```

When "Add Source to Collection" is clicked, uploader zone slides in above the footer:
```
├───────────────────────────────────────────────────┤
│  Add New Documents                                │
│  [drag-drop zone / file picker]                   │
│  staged file list                                 │
│  [Upload N File(s)]                               │
├───────────────────────────────────────────────────┤
│  [Delete Collection]   [Close Uploader]           │
└───────────────────────────────────────────────────┘
```

### Parity gaps

| Feature | React | Blazor | Gap | P |
|---------|-------|--------|-----|---|
| CollectionCatalogInfo section | ✅ description, tags, owner, domain, status | ❌ missing entirely | Add catalog info display to drawer | P1 |
| Individual document delete | ✅ per-row delete icon | ✅ implemented | Verify confirmation? | P2 |
| Document metadata edit | ✅ `PATCH /api/collections/{name}/documents/{doc}/metadata` | ❓ not verified | Check if inline edit exists | P1 |
| Delete confirmation modal | ✅ `ConfirmationModal` with Cancel / Delete | ✅ Blazor has inline confirm logic | Verify modal matches React | P1 |
| "Add Source to Collection" text | Exact label | ✅ matches | Match | — |
| "Close Uploader" text | Exact label | ✅ matches | Match | — |
| Auto-close uploader after upload | ✅ `showUploader = false` on success | ✅ `_showUploader = false` | Match | — |
| Delete error in drawer | `Notification` component in drawer | Toast notification | React shows error inline in drawer | P2 |
| Escape closes panel | ✅ | ✅ | Match | — |

### CollectionCatalogInfo (P1)
React renders these fields read-only inside the drawer:

| Field | Display |
|-------|---------|
| Description | Text paragraph |
| Tags | `<Tag>` chips (gray, outline) |
| Owner | Text with person icon |
| Business Domain | Text with domain icon |
| Status | Badge: Active (green) / Archived (gray) / Deprecated (yellow) |

Blazor implementation: add a section above the documents list in `CollectionDrawer.razor` that reads `Collection.CollectionInfo` (already on the `UploadedCollection` model) and displays these fields.

---

## 4. New Collection Page `/collections/new`

### ⚠ P0: Architecture Difference

React uses a **single-page 2-column form**. Blazor uses a **3-step wizard**.

| | React (single page) | Blazor (wizard) |
|--|---------------------|-----------------|
| Step 1 | — | File upload |
| Step 2 | — | Metadata schema |
| Step 3 | — | Collection info (name etc.) |
| All at once | Left: name + schema + config · Right: upload | No |

**Decision needed:** Migrate Blazor to the single-page layout (matches React exactly) or keep wizard (different UX, both functional). All other parity gaps in this section assume the wizard is kept.

### React single-page layout

```
┌──── Left column (6/12) ────────────────┐  ┌──── Right column (6/12) ───────────────┐
│  Collection Name *                      │  │  ╔══════════════════════════════╗      │
│  ┌──────────────────────────────┐       │  │  ║  Drag & drop files here      ║      │
│  │ my_collection                │       │  │  ║  or click to browse          ║      │
│  └──────────────────────────────┘       │  │  ╚══════════════════════════════╝      │
│  [error message if invalid]             │  │  Accepted: .pdf .docx .pptx …          │
│                                         │  │  Max 400 MB per file                   │
│  ▶ Data Catalog (click to expand)       │  │  ─────────────────────────────────     │
│    Description                          │  │  📄 charger.pdf    1.2 MB  [×]         │
│    Tags                                 │  │  📄 catalog.pdf    4.5 MB  [×]         │
│    Owner                                │  │                                         │
│    Business Domain                      │  └────────────────────────────────────────┘
│    Status                               │
│                                         │
│  ▶ Collection Configuration             │  ┌──── Bottom (12/12) ───────────────────┐
│    [Generate summaries toggle] ●        │  │    [Cancel]    [Create Collection]     │
│                                         │  └────────────────────────────────────────┘
│  Metadata Schema                        │
│    Add New Field                        │
│    Field name: [________] Type: [str ▾] │
│    [ ] Required                         │
│    [Add Field]                          │
└─────────────────────────────────────────┘
```

### Collection Name — validation rules

| Behavior | Detail |
|----------|--------|
| Auto-replace | Spaces → `_` on every keystroke (`onChange`) |
| Error on blur | Empty: "Collection name is required"; invalid chars: "must start with letter or underscore, contain only letters/numbers/underscores"; duplicate: "already exists" |
| Valid pattern | `^[_a-zA-Z][_a-zA-Z0-9]*$` |
| Create button | Disabled while name is blank, has error, or required metadata missing |

### Data Catalog (collapsible)
- **Collapsed by default** (ChevronDown icon in header)
- Click header to expand; ChevronDown rotates 180°
- Fields: Description (`TextInput`) · Tags (text input + Enter to add chips, click chip to remove) · Owner (`TextInput`) · Business Domain (`Select`) · Status (`Select`)
- Domain options: Engineering / Finance / Legal / Marketing / Operations / Product / Sales / Support / Other
- Status options: Active (default) / Archived / Deprecated

### Metadata Schema Editor
- Always visible (not collapsible)
- `NewFieldForm` always rendered; no "open form" toggle
- Add Field button enabled only when field name is non-empty
- Enter key in field name field also triggers add
- Field types: `string` / `integer` / `float` / `number` / `boolean` / `datetime` / `array`
- When type = `array`: sub-selector for element type appears (`string` / `number` / `integer` / `float`)
- Field cards show name, type, required flag, edit (pencil) + delete (×) icons

### File Upload
- Drag-drop zone + click-to-browse
- Accepted: `.pdf .docx .pptx .txt .md .json .html .png .jpg .jpeg .bmp .tiff .mp3 .wav .mp4 .mov .avi .mkv .sh`
- Max 400 MB/file
- Rejection: toast warning for invalid type or oversized file
- If schema has required fields: metadata form appears under each staged file

### Create Collection button
Disabled when: name is empty · name has error · any required metadata field is empty · `hasInvalidFiles` is true · `isLoading` is true

### Parity gaps (beyond architecture)

| Feature | React | Blazor | Gap | P |
|---------|-------|--------|-----|---|
| Data Catalog collapsed by default | ✅ | In wizard step 3 it's always expanded | Collapse it or move to match | P1 |
| Name auto-converts spaces to `_` | ✅ via `onChange` | ✅ similar | Verify on every keypress | P2 |
| Exact error messages | See above | Verify text matches | Verify | P2 |
| "Generate summaries" default on | ✅ default `true` | ✅ default `true` | Match | — |
| Per-file metadata form | ✅ when schema has required fields | ✅ | Match | — |
| File type rejection toast | ✅ | Verify | Verify | P2 |
| Array field element type selector | ✅ | ✅ | Match | — |

---

## 5. Settings Page `/settings`

### Section nav (vertical, left side)
5 sections clickable: RAG Configuration · Feature Toggles · Models · Endpoints · Other

### RAG Configuration

| Setting | Control | Range |
|---------|---------|-------|
| Temperature | Slider + number input | 0.0 – 1.0 |
| Top P | Slider + number input | 0.0 – 1.0 |
| Max Tokens | Number input | Integer > 0 |
| VDB Top K | Number input | Integer > 0 |
| Reranker Top K | Number input | Integer > 0 |
| Confidence Score Threshold | Slider + number input | 0.0 – 1.0 |

### Feature Toggles (on/off switches — `[role="switch"]`)
- Enable Query Rewriting
- Enable Reranker
- Use Guardrails
- Include Citations
- Enable VLM Inference
- Enable Filter Generator

**Feature Warning Modal:** Enabling certain toggles (e.g. VLM, Guardrails) shows a modal:
- Title: e.g. "Enable VLM Inference"
- Body: explanation of what's needed
- Checkbox: "Don't show this again"
- Buttons: Cancel · Confirm (primary/green)

### Models (text inputs)
LLM · Embedding · Reranker · VLM · Query Rewriter · Filter Generator · Reflection

### Endpoints (URL text inputs)
LLM · Embedding · Reranker · VLM · VDB · Query Rewriter · Filter Generator · Reflection

### Other
- Stop tokens (text input)
- Theme toggle (light/dark)
- Use localStorage for persistence (toggle)

### Parity gaps

| Feature | React | Blazor | Gap | P |
|---------|-------|--------|-----|---|
| Feature warning modal | ✅ shown on risky toggles | ❌ not implemented | Add modal for VLM/Guardrails toggles | P2 |
| "Don't show again" checkbox | ✅ in modal | N/A until modal added | Add with modal | P2 |
| Auto-save (no Save button) | ✅ | ✅ | Match | — |
| Slider behavior | Updates on drag release | Verify | Verify step/release behavior | P2 |

---

## 6. Notification Bell

**Trigger:** Bell button in header (no `aria-label`; first tertiary button before Settings)  
**Implementation:** KUI `<Popover>` opening bottom-end

### Task card layout
```
┌────────────────────────────────────────────────┐
│  [icon]  collection_name                       │
│          filename.pdf · 2 min ago              │
│  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  │
│  0/1 completed                                 │
│  [error message if FAILED]                     │
└────────────────────────────────────────────────┘
```

| State | Icon | Border | Bar |
|-------|------|--------|-----|
| PENDING / IN_PROGRESS | animated spinner | `#76B900` green | gray track (no fill) |
| FINISHED | ✓ checkmark | `#76B900` green | full green fill |
| FAILED | × | `#76B900` green | gray (no fill) + error text below |

**Rule: border is ALWAYS `#76B900` regardless of state.** Only the bar fill and icon change.

### Parity gaps

| Feature | React | Blazor | Gap | P |
|---------|-------|--------|-----|---|
| Always-green border | ✅ | ✅ (implemented last session) | Match | — |
| Error message below bar | ✅ | ✅ (implemented last session) | Match | — |
| Section label case | "Ingestion Tasks (N)" normal case | ✅ normal case | Match | — |
| Panel close on Escape/backdrop | ✅ | Blazor: click at (100,200) workaround | Verify proper backdrop close | P2 |
| Timestamp on completed tasks | ✅ relative timestamp | ✅ `CompletedAt` | Match | — |
| Task poller interval | 5 seconds | 5 seconds | Match | — |

---

## 7. Filter Bar

**Trigger:** Exactly 1 collection selected in sidebar  
**Requires:** Running ingestor backend

### Controls
| Control | Behavior |
|---------|----------|
| Add condition | Adds a filter row |
| Field selector | Dropdown populated by `GET /api/metadata-values` |
| Operator | Depends on field type |
| Value input | Type-appropriate (text / number / date / bool) |
| AND / OR | Logic between conditions |
| Remove (×) | Removes a row |

### Operators by field type

| Type | Operators |
|------|-----------|
| string | equals · contains · starts_with · ends_with |
| integer / float / number | = · ≠ · > · < · ≥ · ≤ |
| boolean | is true · is false |
| datetime | before · after · equals |
| array | contains |

### Parity gaps

| Feature | React | Blazor | Gap | P |
|---------|-------|--------|-----|---|
| Operators per field type | Full set above | Verify all implemented | Verify operator coverage | P1 |
| AI filter generator | `POST /api/generate-filter` when toggle on | ✅ | Verify toggle wires to feature flag | P2 |
| API-driven field values | `GET /api/metadata-values` for autocomplete | ✅ | Match | — |

---

## 8. Citations Panel

**Trigger:** "Citations" button on an assistant message (only appears when citations were returned)  
**Requires:** Successful RAG query with citations from live backend

Renders as a `<SidePanel>` sliding in from the right.

### Citation card layout
```
┌────────────────────────────────────────────────┐
│  document_name.pdf                 Score: 0.87  │
│  Stage: retrieval  ·  Type: text               │
│  Collection: collection_name                    │
│  ──────────────────────────────────────────     │
│  "...excerpt of text from the source doc..."   │
│  [image/table thumbnail if visual content]      │
└────────────────────────────────────────────────┘
```

### Parity gaps

| Feature | React | Blazor | Gap | P |
|---------|-------|--------|-----|---|
| Score display | Shown per citation | ✅ | Match | — |
| Stage/type metadata | Shown per citation | ✅ | Match | — |
| Visual content (images) | Inline base64 image in card | ✅ | Match | — |
| Table content | Rendered as HTML table | ✅ | Verify rendering | P2 |

---

## 9. Toast Notifications

| Type | Color | Icon | Auto-dismiss |
|------|-------|------|-------------|
| Success | Green | ✓ | 5 seconds |
| Error | Red | × | 5 seconds |
| Warning | Yellow | ! | 5 seconds |
| Info | Blue | ℹ | 5 seconds |

Position: bottom-right, stacked.

**Parity gaps:** None identified — `ToastContainer.razor` + `NotificationState` match.

---

## Implementation Roadmap

### P0 — Core architecture

**1. New Collection: single-page form vs wizard**

Options:
- **Option A (recommended):** Replace Blazor wizard with React's 2-column single-page layout. Files to touch: `NewCollection.razor` (full rewrite), possibly move step components into the single page.
- **Option B:** Keep wizard but reorder steps to match React intent (name first, schema second, files third) and add the Data Catalog collapsible section.

Both options require testing with `test4.py` after completion.

---

### P1 — Visible gaps (in suggested fix order)

**2. CollectionCatalogInfo in drawer**  
File: `src/dotnet_rag/blazor_frontend/Components/Collections/CollectionDrawer.razor`  
Add a read-only section above the documents list:
```razor
@if (Collection.CollectionInfo is { } info)
{
    <div style="padding:12px 16px;border-bottom:1px solid var(--border)">
        @if (!string.IsNullOrEmpty(info.Description))
        {
            <div style="font-size:13px;color:var(--text-muted);margin-bottom:8px;">@info.Description</div>
        }
        @if (info.Tags?.Count > 0)
        {
            <div style="display:flex;flex-wrap:wrap;gap:4px;margin-bottom:8px;">
                @foreach (var tag in info.Tags)
                { <span style="font-size:11px;padding:2px 8px;border:1px solid var(--border);border-radius:12px;color:var(--text-muted);">@tag</span> }
            </div>
        }
        @if (!string.IsNullOrEmpty(info.Owner))
        { <div style="font-size:12px;color:var(--text-muted);">Owner: @info.Owner</div> }
        @if (!string.IsNullOrEmpty(info.BusinessDomain))
        { <div style="font-size:12px;color:var(--text-muted);">Domain: @info.BusinessDomain</div> }
        @if (!string.IsNullOrEmpty(info.Status))
        { <div style="font-size:12px;">
              <span style="padding:2px 8px;border-radius:10px;font-size:11px;
                           background:@(info.Status=="Active"?"#76B900":"var(--border)");
                           color:@(info.Status=="Active"?"#000":"var(--text-muted)");">
                  @info.Status
              </span>
          </div> }
    </div>
}
```

**3. Ingestion progress in collection card**  
File: `src/dotnet_rag/blazor_frontend/Components/Collections/CollectionSidebar.razor`  
When a pending task exists for `col.CollectionName`, replace `collection-more-btn` with:
```razor
@if (NotifState.HasPendingTask(col.CollectionName))
{
    var task = NotifState.GetPendingTask(col.CollectionName);
    <button @onclick="() => { /* open bell panel */ }" class="collection-progress-btn" title="View upload progress">
        <span style="font-size:11px;color:var(--text-muted);">@task.DocumentsCompleted/@task.TotalDocuments</span>
        <!-- spinner SVG -->
    </button>
}
else
{
    <button class="collection-more-btn" ...><!-- existing ⋮ --></button>
}
```
Requires adding `HasPendingTask(string)` / `GetPendingTask(string)` to `NotificationState.cs`.

**4. Filter bar — verify operator coverage**  
File: `src/dotnet_rag/blazor_frontend/Components/Collections/FilterBar.razor`  
Compare the operator dropdown options against the table in section 7 above. Add any missing operators for string/number/datetime/array fields.

**5. Image attachment in chat**  
Verify `MessageInput.razor` supports image drag-drop and attach-menu. If not, add equivalent of React's `useImageAttachmentStore` and base64 image inclusion in `GenerateRequest`.

**6. Delete confirmation modal**  
File: `src/dotnet_rag/blazor_frontend/Components/Collections/CollectionDrawer.razor`  
React uses `<ConfirmationModal>` with explicit title, body text, Cancel and Delete buttons. Verify Blazor has equivalent — if it uses browser `confirm()` or inline conditional rendering, replace with a proper Blazor modal overlay matching React's design.

---

### P2 — Polish (in suggested order)

**7. Feature warning modal in Settings**  
When enabling VLM Inference or Guardrails, show a warning modal similar to React's `FeatureWarningModal`. Store "don't show again" preference in `SettingsState`.

**8. Notification panel backdrop close**  
React `<Popover>` closes on click-outside natively. Blazor currently works around this with a `(100, 200)` click in tests. Verify the notification panel actually closes properly when clicking the backdrop; fix if it doesn't.

**9. Selected-state ⋮ opacity**  
Blazor currently shows ⋮ at `opacity: 1` when a card is selected (`.collection-item.selected .collection-more-btn { opacity: 1 }`). React only shows it on hover. Decide: remove the selected-state rule to match React, or keep it (both work, just different UX).

**10. Data Catalog collapsible in New Collection (if keeping wizard)**  
Currently the Blazor wizard step 3 shows catalog fields always-expanded. Wrap them in a collapsible `<details>` or custom panel matching React's collapsed-by-default pattern.

**11. Validation error messages — exact text**  
Compare React's validation error strings against Blazor's. Align the exact wording for: name required, name invalid pattern, name duplicate, required fields missing.

---

## Running the Review Again

When backend services are running (ingestor, ChromaDB/Milvus), re-run the Playwright review with a backend-populated collection to capture drawer, filter bar, and citation states:

```bash
uv run python fixtures/review_react_ui.py \
  --url http://localhost:3000 \
  --headed
```

Screenshots saved to `docs/screenshots/react-review/`. The script is idempotent — re-running overwrites existing files with fresh captures.

For the cloud instance (requires NVIDIA auth — SSH tunnel needed):
```bash
# Establish tunnel first, then:
uv run python fixtures/review_react_ui.py \
  --url http://localhost:<tunnel-port> \
  --headed
```
