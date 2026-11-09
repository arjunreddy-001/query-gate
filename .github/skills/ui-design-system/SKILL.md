---
name: ui-design-system
description: 'Style and edit the Query Gate UI: app.css design system, compact sidebar, theme, density, query workspace layout, Monaco editor, favicon and branding. Use when changing CSS, colours, spacing, dark or light theme, sidebar behaviour, the results grid, or copy and wording shown to users.'
---

# UI design system

Custom CSS only (no new Bootstrap dependency). Files: `wwwroot\app.css`, `wwwroot\js\theme.js`, `wwwroot\js\app.js`, `wwwroot\favicon.svg`.

## Tokens and density

- Colours are CSS variables in `:root` and `[data-theme="dark"]` (`--bg`, `--bg-elev`, `--line`, `--text`, `--brand`, `--grad`, `--ok`, `--bad`, and so on). Reuse them and do not hard-code colours that break dark mode.
- Compact density: body 13px, buttons about 5px x 11px padding, inputs about 5px x 9px, table cells 6px x 10px. Keep new components at this density.
- Untyped `<input>` and `type=email|text|password|number|search` share one rule. Still give login fields explicit types.

## Theme and sidebar state

- `data-theme` and `data-nav` live on `<html>` and are saved in localStorage (`dbam-theme`, `dbam-nav`). `theme.js` applies them early and re-applies them with a MutationObserver, because enhanced navigation to static pages resets `<html>` attributes.
- The CSP in `Program.cs` allows `script-src 'self' 'unsafe-eval'` only. No inline scripts or inline event handlers. Use delegated listeners keyed on `data-theme-toggle`, `data-nav-toggle` and `.ws-split`.
- Sidebar: icon-only by default, expands on hover or keyboard focus, can be pinned. Every label must be wrapped in `<span class="lbl">`, which is hidden when compact.

## Query workspace

- `.ws` fills the viewport height. `.editor-box` and `.results` are flex children and the `.ws-split` bar resizes the editor.
- The results card has a Maximize and Restore toggle (`.results.maximized` is `position: fixed; inset: 0`).
- Monaco is created in `app.js` (`editorInit`) with `fixedOverflowWidgets: true` so suggestions are not clipped. Suggestion rows rely on Monaco's own class names, so avoid global CSS that targets generic names.
- Database selector (`.db-selector-*`): Custom trigger dropdown displaying database name with engine badge and environment badge. Badges use `flex-shrink: 0` so they are never truncated or hidden when long database names are selected.
- Object Explorer: Clicking a table or view defaults to `SELECT TOP 100` (or `LIMIT 100`). Contextual operations are accessed via right-click contextual menu (`.oe-context-menu` positioned at click coordinates). Do not place inline action buttons inside tree rows.
- Query tabs: Double-clicking an active tab switches the label to an inline `<input class="tab-rename-input">` allowing instant renaming. Enter or blur saves; Escape cancels. Tab state and custom names are persisted in `localStorage` under `dbam-tabs`.

## Wording and branding

- Product name: **Query Gate**. Internal namespaces and the project file keep the old `DbAccessManager` name on purpose.
- Write plain, human copy. Do not use em dashes or en dashes in UI text, docs or comments. Use a full stop, comma, colon or parentheses. Use `-` for an empty table value.
- Keep emoji and decorative symbols out of UI copy unless they carry meaning (for example the tick in the access matrix).

## Verify

Use the `run-and-verify` skill to run a scratch copy, then check screenshots in light and dark themes, compact and pinned sidebar, and a narrow width (under 860px the sidebar becomes static with visible labels).
