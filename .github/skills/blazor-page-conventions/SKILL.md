---
name: blazor-page-conventions
description: 'Add or change Blazor pages, admin pages, services and navigation in Query Gate. Use when creating a new .razor page, adding a menu item, a setting, an icon, or fixing first-render errors such as KeyNotFoundException or components that never initialise.'
---

# Blazor page conventions

Interactive Server rendering. Pages are in `Components\Pages` (user) and `Components\Pages\Admin` (admin). Account pages in `Components\Account` use static SSR.

## New page checklist

1. Start with `@page "/route"`, then `@attribute [Authorize]` (admin pages: `@attribute [Authorize(Roles = Roles.Admin)]`), then `@inherits PageBase`.
2. `PageBase` provides `UserId`, `UserName`, `IsAdmin`, `Me` (an `Actor` for audit), `Flash` with `Say(...)`, `OnLoadAsync()`, `EnvClass`, `OutcomeClass`, `Ago` and `Local`. Load data in `OnLoadAsync`.
3. `<PageTitle>Page name · Query Gate</PageTitle>`.
4. Services are singletons registered in `Program.cs`. Use `IDbContextFactory<ApplicationDbContext>` with `await using var db = await factory.CreateDbContextAsync()`. Never hold a DbContext across renders.
5. Add the nav link in `Components\Layout\MainLayout.razor` as `<Icon Name="..." /><span class="lbl">Label</span>`. The `lbl` span is required so the compact sidebar can hide the text.
6. Add new icons to the dictionary in `Components\Shared\Icon.razor`. An unknown name renders blank (the database icon is `db`, not `database`). Every icon used in `<Icon Name="..." />` must exist in `Icon.razor`.
7. Add a setting by adding a key to `SettingsService.Definitions` (default and label). A default of `"true"` or `"false"` renders as a Yes/No select on the Settings page.
8. Add new routes, pages, and major actions to `Components\Shared\CommandPalette.razor` so users can access them via `Cmd+K` / `Ctrl+K`.
9. Update `docs\USER-GUIDE.md` and `docs\ADMIN-CHEATSHEET.md` for user-facing changes.

## Gotchas that have already bitten this project

- **The first render happens before async loading finishes.** Blazor renders once when `OnInitializedAsync` returns an incomplete task, so `firstRender` in `OnAfterRenderAsync` sees empty data. Guard markup with a `ready` flag, use `GetValueOrDefault`, and for JS setup use an `initialized` flag checked after the data has loaded (see `Query.razor`).
- **Icon registry check**: Every `<Icon Name="..." />` renders an SVG looked up in `Components/Shared/Icon.razor`'s `Paths` dictionary. If an icon name does not exist, an empty `<svg>` tag is rendered, which inside button styling appears as an empty square box or mystery checkbox. Always verify the icon name exists in `Icon.razor` or add its Feather SVG path.
- **Button hover expansion (`.btn-hover-expand`)**: `.btn-hover-expand` is designed for short action words in dense toolbars (e.g. Save, Format, Explain). Never use `.btn-hover-expand` for long labels or inside standard `.actions` button groups, because the CSS width transition clips the label (e.g. "Scan Schema for I"). Use standard `.btn.btn-sm` instead.
- **Layout split containerization**: When implementing split views in the Query workspace, never apply `flex-direction: row` to the outer `.ws-main` container, because doing so causes top navigation tabs, the toolbar, and prompt bars to become side-by-side columns. Wrap only the editor/form pane, splitter divider (`.ws-split`), and results card inside `.ws-body` (with `.ws-body-horizontal`). The splitter drag handler in `theme.js` must support both `clientX` (width) and `clientY` (height) resizing, and call `dbam.editorLayout` to reflow Monaco.
- **Form Mode & alternative editor views**: When an alternative view (such as Form Mode / No-Code Parameterized Runner) replaces the editor, do not stack both the form and the full Monaco editor simultaneously. Hide the Monaco editor container with `display: none` (preserving the instance in memory) and render the dedicated view in `.ws-editor-pane`. When exiting back to the editor, trigger `dbam.editorLayout` to reflow Monaco immediately.
- **Plain human copy rule**: Never use em dashes or en dashes in any UI copy, placeholders, tooltips, documentation, or code comments. Use full stops, commas, colons, or parentheses.
- A member cannot share its class name. Injected services named `Audit`, `Settings` or `Permissions` clash with pages of those names, so those pages use `AuditSvc` and `Cfg`.
- Razor lambdas cannot contain `\"` escapes. Use `string.Empty` or single-quoted attributes.
- Use `@onclick:stopPropagation` on buttons nested inside clickable rows.
- Inline `onclick="..."` attributes are blocked by the CSP. Use `@onclick`, or `data-*` attributes with delegated handlers in `wwwroot\js\theme.js` or `app.js`.
- Avoid very generic CSS class names such as a bare `.main`. Scope them (`.shell > .main`), because Monaco and other widgets reuse such names.
