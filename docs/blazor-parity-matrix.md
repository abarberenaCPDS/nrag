# Blazor ↔ React Parity Matrix

Each row: React behavior → current Blazor state → gap description → priority.

**Priorities:** P0 = broken/missing core flow · P1 = visible gap · P2 = edge-case/polish · P3 = deferred

---

## Chat Page

| Feature | React | Blazor today | Gap | P |
|---------|-------|-------------|-----|---|
| Single-click selects collection | ✅ click → chip in input | ✅ click → checkbox | Match (different UI, same behavior) | — |
| ⋮ button opens drawer | ✅ opacity 0 → 1 on hover | ✅ implemented | Match | — |
| Agentic mode selector | ✅ Standard/Agentic dropdown in input bar | ✅ present | Verify options match | P2 |
| Filter bar (1 collection) | ✅ appears below collection list | ✅ `FilterBar.razor` | Verify operator options per field type | P2 |
| Multi-collection warning | ✅ banner inside sidebar | ✅ implemented | Match | — |
| Citations button on message | ✅ appears after RAG response | ✅ CitationsPanel.razor | Match | — |
| Reasoning panel (agentic) | ✅ collapsible per message | ✅ ReasoningPanel.razor | Match | — |
| Image attachment | ✅ drag-drop or menu button in input | ❓ not verified | Verify image attach flow in Blazor | P1 |
| Message streaming indicator | ✅ animated dots | ✅ StreamingIndicator.razor | Match | — |
| ⋮ selected-state opacity | React: opacity 1 on hover only | Blazor: opacity 1 on hover AND selected | Blazor adds extra always-visible state — remove? | P3 |

---

## Collection Sidebar

| Feature | React | Blazor today | Gap | P |
|---------|-------|-------------|-----|---|
| Empty state text | "Create your first collection and add files to customize your model's response." | Similar text | Verify exact wording | P2 |
| Search "No results" text | "No collections match 'query'" | "No collections match '@ColState.SearchQuery'" | Match | — |
| Card layout | Name · entities · ⋮ | Name · entities · ⋮ | Match | — |
| Ingestion progress in card | spinner + "X/N" replaces ⋮ while tasks pending | Not implemented | Blazor always shows ⋮ during ingestion | P1 |
| + New Collection button | Anchor → `/collections/new`, in sidebar footer | Anchor → `/collections/new` in sidebar footer | Match | — |

---

## Collection Drawer

| Feature | React | Blazor today | Gap | P |
|---------|-------|-------------|-----|---|
| Trigger | ⋮ button | ⋮ button | Match | — |
| Close | × button (top right) | `button[aria-label="Close"]` | Verify label matches | P2 |
| Overlay behind panel | dark semi-transparent overlay | overlay present | Match | — |
| Header title | collection name | collection name | Match | — |
| Catalog info section | description, tags, owner, domain, status badges | Not implemented | Blazor drawer has no `CollectionCatalogInfo` section | P1 |
| Documents list | table with name, size, date, delete icon | Implemented (list) | Verify individual doc delete & metadata edit | P1 |
| "Add Source to Collection" | footer button, primary/green | ✅ implemented | Match | — |
| "Close Uploader" | replaces Add Source when open | ✅ implemented | Match | — |
| Auto-close uploader after upload | ✅ `showUploader = false` after success | ✅ `_showUploader = false` | Match | — |
| Delete confirmation modal | KUI ConfirmationModal | Not using KUI; custom confirm | Add proper confirmation modal | P1 |
| Delete error display | Notification in drawer | toast notification | Verify drawer-level vs toast error | P2 |

---

## New Collection — **Major Architecture Gap**

| Feature | React | Blazor today | Gap | P |
|---------|-------|-------------|-----|---|
| **Page architecture** | **Single-page form (2 columns)** | **3-step wizard** | **Fundamental UX difference** | P0 |
| Collection name field | Left column, first field | Step 3 (last) | Field ordering reversed | P0 |
| File upload | Right column (always visible) | Step 1 (first) | Upload step position | P0 |
| Metadata schema editor | Left column (always visible) | Step 2 (middle) | Schema step position | P0 |
| Data Catalog section | Collapsible panel in left column | Inline fields in step 3 | Collapsible vs always-expanded | P1 |
| Name auto-converts spaces → underscores | ✅ via `onChange` | ✅ via `@oninput` | Match | — |
| Name validation error | Inline below field on blur | Inline below field | Verify exact error text | P2 |
| Collection Config (summaries) | Toggle in left panel | Toggle in step 3 | Position difference | P1 |
| Metadata field type options | string/integer/float/number/boolean/datetime/array | Same 7 types | Match | — |
| Array element type sub-selector | ✅ visible when type=array | ✅ present | Match | — |
| Add Field button | Enabled when name non-empty | Enabled when name non-empty | Match | — |
| Enter key adds field | ✅ Enter in name field triggers add | ✅ same | Match | — |
| File size limit | 400 MB, toast on rejection | 400 MB | Match | — |
| File type rejection | Toast warning | Toast? verify | Verify rejection UX | P2 |
| Accepted file types | .pdf .docx .pptx .txt .md .json .html .png .jpg .jpeg .bmp .tiff .mp3 .wav .mp4 .mov .avi .mkv .sh | Same list | Match | — |
| Per-file metadata form | Appears under each file when schema has required fields | Implemented | Verify required field enforcement | P2 |
| Cancel button | Returns to `/` | Returns to `/` | Match | — |
| Create button disabled state | name empty / error / required missing / invalid files / loading | Verify all conditions | Verify Blazor disables on all same conditions | P1 |

---

## Settings Page

| Feature | React | Blazor today | Gap | P |
|---------|-------|-------------|-----|---|
| 5-section nav | RAG Config / Feature Toggles / Models / Endpoints / Other | Same 5 sections | Match | — |
| Temperature slider | 0.0–1.0 | 0.0–1.0 | Match | — |
| Top P slider | 0.0–1.0 | 0.0–1.0 | Match | — |
| Max Tokens | integer input | integer input | Match | — |
| VDB Top K | integer input | integer input | Match | — |
| Reranker Top K | integer input | integer input | Match | — |
| Confidence threshold | slider 0.0–1.0 | slider 0.0–1.0 | Match | — |
| Feature warning modal | shown when enabling VLM/guardrails etc. | Not implemented | Blazor shows no warning modal for risky toggles | P2 |
| Auto-save (no save button) | ✅ immediate update | ✅ immediate update | Match | — |
| localStorage persistence | optional toggle | optional toggle | Match | — |
| Health status | banner/badge in settings | `HealthBadge.razor` in header | Match | — |
| "Don't show again" in modal | ✅ checkbox in warning modal | N/A | Only needed if modal implemented | P2 |

---

## Notification Bell

| Feature | React | Blazor today | Gap | P |
|---------|-------|-------------|-----|---|
| Bell position | Header right, before Settings | Header right | Match | — |
| Badge count | Numeric badge | Numeric badge | Match | — |
| Panel empty text | "No notifications" | Match | Match | — |
| Task section label | "Ingestion Tasks (N)" | "Ingestion Tasks (@visible.Count)" | Match | — |
| Card border always green | ✅ #76B900 regardless of state | ✅ implemented last session | Match | — |
| Progress bar green when FINISHED | ✅ green | ✅ green | Match | — |
| Progress bar gray when not done | ✅ gray track | ✅ gray | Match | — |
| Error message for FAILED | ✅ below progress bar | ✅ implemented last session | Match | — |
| Section label case | "Ingestion Tasks (N)" — normal case | Normal case | Match | — |
| Spinner icon while pending | ✅ animated | ✅ animated | Match | — |
| Timestamp on completed | ✅ shown | ✅ `task.CompletedAt` | Match | — |
| Panel closes on Escape/click-outside | ✅ | ✅ (click at 100,200 workaround) | Verify proper backdrop close | P2 |

---

## Toast Notifications

| Feature | React | Blazor today | Gap | P |
|---------|-------|-------------|-----|---|
| Success / Error / Warning / Info | 4 types with color coding | `ToastSeverity` enum | Match | — |
| Auto-dismiss | 5 seconds | 5 seconds | Match | — |
| Position | Bottom-right | Bottom-right | Match | — |
| Stack multiple | ✅ | ✅ | Match | — |

---

## Filter Bar

| Feature | React | Blazor today | Gap | P |
|---------|-------|-------------|-----|---|
| Appears with 1 collection | ✅ | ✅ `FilterBar.razor` | Match | — |
| Field selector | API-driven unique values | Implemented | Verify field options come from API | P2 |
| Operator options per type | string/number/bool/array operators | Verify | Verify all operator types implemented | P1 |
| AND/OR logic | ✅ | ✅ | Match | — |
| AI filter generator | ✅ when enabled in settings | ✅ | Verify toggle wires to feature flag | P2 |

---

## P0 Summary — Must Fix Before Claiming Parity

1. **New Collection page architecture** — React is a single-page 2-column form; Blazor is a 3-step wizard. This is the most significant UX difference. Decide: keep wizard (different-but-functional) or migrate to single-page layout.

## P1 Summary — Visible Gaps

2. **Collection card ingestion progress** — React shows "X/N + spinner" in card while task is active; Blazor always shows ⋮
3. **Catalog info in drawer** — React `CollectionCatalogInfo` shows description/tags/owner/domain/status; Blazor drawer has none
4. **Delete confirmation modal** — React uses a proper modal; verify Blazor has equivalent
5. **Image attachment in chat** — not yet verified in Blazor
6. **Filter bar operators** — not verified all field type operators are correctly implemented

## P2 Summary — Polish

7. Feature warning modal in Settings
8. Exact validation error messages
9. File type rejection UX (toast vs inline)
10. Backdrop/close behavior for notification panel
11. Selected-state ⋮ opacity (Blazor shows it always on selected, React doesn't)
