# React Frontend UI Specification

Source of truth for Blazor parity. Screenshots captured from `http://localhost:3000` via
`fixtures/review_react_ui.py`. Sections needing a live ingestor backend are noted.

---

## Chat Page — `/`

**Screenshot:** `chat-01-initial-load.png`

### Layout
- Left sidebar (fixed width): collection search + collection grid + filter bar (conditional) + `+ New Collection` footer button
- Main area: chat messages (scroll) + message input bar (fixed bottom)
- Header (fixed top): NVIDIA logo · "RAG Blueprint" title · notification bell · Settings button
- Right side: citations panel (slide-in overlay, not always visible)

### Controls — Header
| Control | Type | Behavior |
|---------|------|----------|
| NVIDIA Logo | div (click) | Navigates to `/` |
| "RAG Blueprint" | text (click) | Navigates to `/` |
| Notification bell | tertiary button (no aria-label) | Opens popover panel below; shows count badge when tasks exist |
| Settings button | tertiary button | Navigates to `/settings` (toggles back to `/` if already on settings) |

### Controls — Message Input Bar
| Control | Type | Behavior |
|---------|------|----------|
| Textarea | `<textarea>` | Auto-resizing; Enter sends, Shift+Enter newlines; placeholder visible when empty |
| Collection chips | removable chips in input area | Shows which collections are selected; click × to deselect |
| Agentic mode selector | button/dropdown | Toggles between "Standard" (default) and "Agentic" |
| Image attach | icon button | Drag-drop or click to attach image (JPEG/PNG/GIF/WebP, max 10 MB) |
| Send button | icon button | Sends message; disabled while streaming |

**Screenshot:** `chat-04-message-input-unfocused.png`, `chat-05-message-input-focused.png`, `chat-06-message-input-with-text.png`

### Agentic Mode Selector
- Renders as a dropdown/button with "Standard" label when in standard mode
- Click opens options: Standard / Agentic
- **Screenshot:** `chat-07-agentic-selector-open.png`

### States
- **Empty (no messages):** Shows welcome/empty state in main area; sidebar shows empty state if no collections
- **With collections selected:** Chips appear in message input row
- **Multi-collection warning:** Yellow/info banner inside sidebar ("Filters are disabled when multiple collections are selected")
- **Streaming:** Streaming indicator (animated dots) appears inside assistant message bubble
- **Completed response:** Shows full text, "Citations" button visible on assistant bubble if citations returned
- **Filter bar:** Appears between collection list and sidebar footer when exactly 1 collection is selected

### API calls
- `POST /api/generate` — sends message; SSE stream for response chunks, citations, reasoning steps

---

## Collection Sidebar (always visible on chat page)

**Screenshot:** `sidebar-01-empty-state.png`

### Controls
| Control | Type | Behavior |
|---------|------|----------|
| Search input | `input[type="search"]` | Filters collection list; shows "No results" empty state when no match |
| Collection card | list item | Single click: selects/deselects collection (adds chip to message input); no double-click action |
| Collection checkbox | checkbox inside card | Toggles selection (same as card click) |
| ⋮ button (`MoreIcon`) | tertiary tiny KUI Button | Hidden (`opacity: 0`) by default; appears on card hover; click opens the Collection Drawer |
| + New Collection | footer anchor → `/collections/new` | Navigates to new collection page |

### Collection Card Layout
```
┌─────────────────────────────┐
│  collection_name            │
│  1,234 entities             │      ⋮
└─────────────────────────────┘
```
- ⋮ button is at the right edge; renders only when NOT ingesting (replaced by spinner + "X/N" progress during active tasks)
- Selected card: highlighted border/background (green accent)

### States
- **Empty:** "No collections" empty state with folder icon — "Create your first collection…"
- **Search no results:** magnifier icon — "No results — No collections match 'query'"
- **Filter bar (1 collection):** `SimpleFilterBar` slides in above the footer

### API calls
- `GET /api/collections` — on load and polling

---

## Collection Drawer

**Trigger:** ⋮ button on any collection card  
Renders as `<SidePanel>` (KUI component) sliding in from the right, with a dark overlay behind it.

### Layout (top to bottom)
1. **Header:** collection name as title + Close (×) button
2. **Catalog info:** `CollectionCatalogInfo` — shows description, tags, owner, domain, status badges
3. **Documents section:** `DocumentsList` — table of documents (name, size, date, delete icon per row)
4. **Footer actions:** `DrawerActions` — Delete Collection (secondary/danger) | Add Source to Collection (primary, green) — buttons are side by side
5. **Uploader section** (conditional): `UploaderSection` — appears above the footer when "Add Source to Collection" is clicked; footer button label changes to "Close Uploader"

### Controls — DrawerActions
| Control | Label | Kind | Behavior |
|---------|-------|------|----------|
| Delete button | "Delete Collection" | secondary (outlined) | Opens confirmation modal |
| Toggle button | "Add Source to Collection" / "Close Uploader" | primary/green | Toggles `showUploader` state |

### States
- **No documents:** Empty state "No documents yet" message in the documents section
- **With documents:** Table showing filename, type, size, date; per-row delete icon
- **Uploader hidden:** Footer shows [Delete Collection] [Add Source to Collection]
- **Uploader revealed:** Uploader zone appears with schema-based metadata fields; footer shows [Delete Collection] [Close Uploader]
- **Delete confirmation:** `ConfirmationModal` with title, body text, Cancel + Delete buttons

### API calls
- `GET /api/documents?collection_name=X` — on drawer open
- `POST /api/documents?collection_name=X` — file upload (multipart)
- `DELETE /api/documents?collection_name=X` — delete document
- `DELETE /api/collections` — delete collection (after confirmation)

---

## New Collection Page — `/collections/new`

**Architecture:** Single-page form (NOT a wizard). React uses a two-column layout. Blazor uses a 3-step wizard — **this is a known parity gap.**

**Screenshot:** `nc-01-initial-empty.png`

### Layout
```
┌──── Left (6 cols) ──────────────────┐  ┌──── Right (6 cols) ────────────────┐
│  Collection Name (required)          │  │  NvidiaUpload dropzone              │
│  Data Catalog (collapsible section)  │  │  File list (staged files)           │
│  Collection Configuration            │  │  Accepted types, size limits        │
│  Metadata Schema Editor              │  │                                     │
└──────────────────────────────────────┘  └────────────────────────────────────┘
         ┌──── Bottom (12 cols) ────────────────────────────────┐
         │  Cancel button  |  Create Collection button          │
         └────────────────────────────────────────────────────┘
```

### Collection Name field
| Behavior | Detail |
|----------|--------|
| Auto-replace | Spaces → underscores via `onChange`: `value.replace(/\s+/g, '_')` |
| Validation | On blur (`onBlur`): shows error if name is empty, duplicate, or contains invalid chars |
| Error display | Inline below the field |
| Valid pattern | `^[_a-zA-Z][_a-zA-Z0-9]*$` |
| Create button disabled | While name is empty, has error, or required metadata fields are missing |

**Screenshots:** `nc-02-name-focused.png`, `nc-03-name-valid.png`, `nc-04-name-invalid-error.png`, `nc-05-name-space-autoconvert.png`

### Data Catalog section (collapsible `<Panel>`)
- Collapsed by default; header click expands
- Collapsed header shows: "Data Catalog" + BookOpen icon + ChevronDown (rotates on expand)
- **Fields when expanded:**
  | Field | Type | Placeholder/Options |
  |-------|------|---------------------|
  | Description | TextInput | "e.g. Q4 2024 Financial Reports" |
  | Tags | TextInput + tag chips | Type then Enter to add; click tag to remove |
  | Owner | TextInput | "e.g. Finance Team" |
  | Business Domain | Select | Engineering / Finance / Legal / Marketing / Operations / Product / Sales / Support / Other |
  | Status | Select | Active / Archived / Deprecated (Active is default) |

**Screenshots:** `nc-07-catalog-section-expanded.png`, `nc-08-catalog-tags-added.png`, `nc-10-catalog-section-filled.png`

### Collection Configuration section
- Generate document summaries toggle (on by default)
- Shows description of what summaries do

### Metadata Schema Editor
- Always visible (not behind a toggle)
- Header: "Define metadata fields for this collection."
- NewFieldForm always rendered below existing fields:
  | Control | Type | Notes |
  |---------|------|-------|
  | Field name | TextInput | placeholder "e.g. category, author, department"; required for Add Field to enable |
  | Type | Select | string / integer / float / number / boolean / datetime / array |
  | Array element type | Select (conditional) | Appears when type = array; options: string / number / integer / float |
  | Required | Checkbox/switch | Marks field as required in metadata form |
  | Description | TextArea | Optional field description |
  | Add Field | KUI Button | Disabled until name is non-empty; Enter key also triggers |
- Added fields appear as cards above the form; each has Edit and Delete icons

**Screenshots:** `nc-12-schema-editor-empty-state.png`, `nc-13-field-name-filled.png`, `nc-16-schema-with-1field.png`

### File Upload (NvidiaUpload)
- Drag-drop zone with dashed border
- Accepted types: `.pdf .docx .pptx .txt .md .json .html .png .jpg .jpeg .bmp .tiff .mp3 .wav .mp4 .mov .avi .mkv .sh`
- Max size: 400 MB per file
- Invalid type: toast notification (warning)
- Staged files appear as a list with name, size, type icon; × to remove each
- If schema has required fields: per-file metadata form appears under each file

**Screenshots:** `nc-17-1file-staged.png`, `nc-18-2files-staged.png`, `nc-19-filelist-area.png`

### Buttons (bottom row, `NewCollectionButtons`)
| Button | Kind | Disabled when |
|--------|------|---------------|
| Cancel | secondary | Never |
| Create Collection | primary/green | Name empty, name error, required metadata missing, invalid files, or loading |

**Screenshots:** `nc-20-create-btn-enabled.png`, `nc-21-cancel-btn.png`

### API calls
- `POST /api/collection` — on Create Collection click
- `POST /api/documents` — file upload (after collection created)

---

## Settings Page — `/settings`

**Screenshot:** `settings-01-initial.png`

### Layout
- Vertical nav on left (5 sections)
- Content area on right

### Sections

#### RAG Configuration
**Screenshots:** `settings-02-rag-config.png`, `settings-02-rag-config-scrolled.png`

| Setting | Control | Range / Notes |
|---------|---------|---------------|
| Temperature | Slider + number | 0.0–1.0 |
| Top P | Slider + number | 0.0–1.0 |
| Max Tokens | Number input | Integer |
| VDB Top K | Number input | Integer (documents retrieved from vector DB) |
| Reranker Top K | Number input | Integer (documents after reranking) |
| Confidence Score Threshold | Slider + number | 0.0–1.0 |

#### Feature Toggles
**Screenshots:** `settings-03-feature-toggles.png`, `settings-03-feature-toggles-scrolled.png`, `settings-08-feature-warning-modal.png`

Toggles (on/off switches):
- Enable Query Rewriting
- Enable Reranker
- Use Guardrails
- Include Citations
- Enable VLM Inference
- Enable Filter Generator
- Agentic Mode (may be in a different section)

**Feature Warning Modal:** Clicking certain toggles (e.g., VLM) shows a modal warning that enabling this feature requires additional configuration. Has title, description, "Don't show again" checkbox, and Cancel/Confirm buttons.

#### Models
**Screenshots:** `settings-04-models.png`, `settings-04-models-scrolled.png`

Text inputs for model names:
- LLM Model
- Embedding Model
- Reranker Model
- VLM Model
- Query Rewriter Model
- Filter Generator Model
- Reflection Model

#### Endpoints
**Screenshots:** `settings-05-endpoints.png`, `settings-05-endpoints-scrolled.png`

URL inputs for:
- LLM Endpoint
- Embedding Endpoint
- Reranker Endpoint
- VLM Endpoint
- VDB (Vector DB) Endpoint
- Query Rewriter Endpoint
- Filter Generator Endpoint
- Reflection Endpoint

#### Other (Advanced)
**Screenshot:** `settings-06-other.png`

- Stop tokens text input
- Theme toggle (light/dark)
- Use localStorage for persistence toggle

### Behavior
- Settings update **immediately** on change (no Save button)
- Values sent in the next `POST /api/generate` request
- Settings persisted to `localStorage` (if "Use localStorage" toggle is on)
- Server defaults loaded from `GET /v1/configuration` on page load

### API calls
- `GET /v1/health` — backend service health check
- `GET /v1/configuration` — load server defaults

---

## Notification Bell

**Trigger:** Bell button in header (first button before Settings)  
**Implementation:** KUI `<Popover>` opening below, aligned to end

### States
| State | Description |
|-------|-------------|
| 0 count | Bell icon only, no badge |
| Unread tasks | Numeric badge on the bell |
| Panel empty | "No notifications" empty state |
| Panel with tasks | "Ingestion Tasks (N)" section heading + task cards |

**Screenshots:** `chat-08-bell-0-count.png`, `chat-09-bell-panel-empty.png`, `chat-10-bell-with-badge.png`, `chat-11-bell-panel-with-tasks.png`, `notif-01` through `notif-04`

### Task Card Layout
```
┌─────────────────────────────────────────────┐
│  [icon]  collection_name                    │
│  document_name.pdf · timestamp              │
│  ━━━━━━━━━━━━━━━━━━━━━━━━  (progress bar)   │
│  X/N completed                              │
│  [error message if FAILED]                  │
└─────────────────────────────────────────────┘
```

| State | Icon | Border | Bar color |
|-------|------|--------|-----------|
| PENDING / IN_PROGRESS | spinner | green (#76B900) | gray track, no fill |
| FINISHED | checkmark | green (#76B900) | green full fill |
| FAILED | × | green (#76B900) | gray (no fill); error message below |

**Note:** Border is ALWAYS green regardless of state. Only the bar fill changes.

### Behavior
- Popover closes on Escape or click outside
- Task poller runs every 5 seconds for pending tasks
- Tasks persist in localStorage

---

## Filter Bar (single collection selected)

**Trigger:** Selecting exactly 1 collection in the sidebar  
**Requires:** Running ingestor backend with at least 1 collection

When visible, appears between the collection list and the sidebar footer (+ New Collection).

### Controls
| Control | Behavior |
|---------|----------|
| Add condition button | Adds a new filter row |
| Field selector | Dropdown of metadata fields from the selected collection |
| Operator selector | Options depend on field type (string: equals/contains/starts_with; number: =/>/</>=/<=; boolean: is; array: contains) |
| Value input | Type-appropriate input (text / number / date picker / boolean toggle) |
| Logic selector | AND / OR between conditions |
| Remove condition | × button per row |

### API calls
- `GET /api/metadata-values?collection_name=X&field=Y` — fetches unique values for filter field autocomplete
- `POST /api/generate-filter` — AI-generated filter from natural language (if enabled in settings)

---

## Citations Panel

**Trigger:** "Citations" button on an assistant message bubble (only appears when citations were returned)  
**Requires:** RAG query that returned citations from the backend

Renders as a `<SidebarDrawer>` (KUI SidePanel) from the right.

### Layout
- Header: "Citations" + close button
- List of citation cards

### Citation Card
```
┌─────────────────────────────────────────────┐
│  document_name.pdf            Score: 0.87   │
│  Stage: retrieval · Type: text              │
│  Collection: collection_name                 │
│  ──────────────────────────────────────     │
│  Text excerpt from the source document...   │
│  [Image/table thumbnail if visual content]  │
└─────────────────────────────────────────────┘
```

---

## Toast Notifications

Global overlay, bottom-right corner. Auto-dismiss after 5 seconds.

| Type | Color | Icon |
|------|-------|------|
| Success | Green | checkmark |
| Error | Red | × |
| Warning | Yellow | ! |
| Info | Blue | ℹ |

---

## Summary: Key Behavioral Rules

1. **Collection name validation:** spaces auto-converted to underscores on every keystroke; error shown on blur; valid pattern `[_a-zA-Z][_a-zA-Z0-9]*`
2. **Create Collection disabled:** while name is blank/invalid, required metadata fields are missing, files are invalid, or upload is in progress
3. **⋮ button opacity:** `opacity: 0` normally; `opacity: 1` on card hover (no selected-state opacity rule in React — only Blazor adds that)
4. **Drawer via ⋮ only:** collection card single-click = selection only; no double-click; ⋮ = open drawer
5. **Add Source toggle:** in drawer footer, replaces itself with "Close Uploader" when uploader is shown; uploader auto-closes after successful upload
6. **Notification border always green:** PENDING/FINISHED/FAILED all use `#76B900` card border; only progress bar fill changes
7. **Settings auto-save:** no explicit save button; all settings write to state (and optionally localStorage) immediately
8. **Single-page collection form:** React NewCollection is NOT a wizard — it's a 2-column single page. Blazor's 3-step wizard is a UI parity gap.
9. **File size limit:** 400 MB per file, communicated via toast on rejection
10. **Data Catalog collapsed by default:** description/tags/owner/domain/status section is collapsed; click header to expand
