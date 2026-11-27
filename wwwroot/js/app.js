window.dbam = (function () {
  const MONACO_BASE = "/_content/BlazorMonaco/lib/monaco-editor/min/vs";
  const SIDE_WIDTH_KEY = "dbam-ws-side-w";
  let monacoPromise = null;
  let sqlCompletionPromise = null;
  let erDiagramPromise = null;
  let metadata = [];
  let providerRegistered = false;
  let sideResizeBound = false;
  let activeWorkspaceRef = null;
  let workspaceShortcutsBound = false;
  const tabFileHandles = new Map();
  let pendingDropHandle = null;

  function loadScript(src) {
    return new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = src;
      s.onload = resolve;
      s.onerror = reject;
      document.head.appendChild(s);
    });
  }

  function loadSqlCompletion() {
    if (window.queryGateCompletion) return Promise.resolve(window.queryGateCompletion);
    if (sqlCompletionPromise) return sqlCompletionPromise;
    const src = document.body?.dataset?.jsSqlCompletion || "/js/sql-completion.js";
    sqlCompletionPromise = loadScript(src).then(() => window.queryGateCompletion);
    return sqlCompletionPromise;
  }

  function loadErDiagram() {
    if (window.dbamEr) return Promise.resolve(window.dbamEr);
    if (erDiagramPromise) return erDiagramPromise;
    const src = document.body?.dataset?.jsErDiagram || "/js/er-diagram.js";
    erDiagramPromise = loadScript(src).then(() => window.dbamEr);
    return erDiagramPromise;
  }

  function loadMonaco() {
    if (monacoPromise) return monacoPromise;
    monacoPromise = (async () => {
      await loadScript(MONACO_BASE + "/loader.js");
      window.require.config({ paths: { vs: MONACO_BASE } });
      window.MonacoEnvironment = {
        getWorkerUrl: function () {
          const code =
            "self.MonacoEnvironment={baseUrl:'" +
            location.origin +
            MONACO_BASE.replace("/vs", "") +
            "/'};importScripts('" +
            location.origin +
            MONACO_BASE +
            "/base/worker/workerMain.js');";
          return (
            "data:text/javascript;charset=utf-8," + encodeURIComponent(code)
          );
        },
      };
      await new Promise((resolve, reject) =>
        window.require(["vs/editor/editor.main"], resolve, reject),
      );
      return window.monaco;
    })();
    return monacoPromise;
  }

  const KEYWORDS = [
    "SELECT",
    "FROM",
    "WHERE",
    "GROUP BY",
    "ORDER BY",
    "HAVING",
    "JOIN",
    "INNER JOIN",
    "LEFT JOIN",
    "RIGHT JOIN",
    "FULL JOIN",
    "ON",
    "AS",
    "AND",
    "OR",
    "NOT",
    "IN",
    "EXISTS",
    "LIKE",
    "BETWEEN",
    "IS NULL",
    "IS NOT NULL",
    "TOP",
    "DISTINCT",
    "INSERT INTO",
    "VALUES",
    "UPDATE",
    "SET",
    "DELETE FROM",
    "CASE",
    "WHEN",
    "THEN",
    "ELSE",
    "END",
    "UNION ALL",
    "COUNT",
    "SUM",
    "AVG",
    "MIN",
    "MAX",
    "CAST",
    "CONVERT",
    "ISNULL",
    "COALESCE",
    "GETDATE",
    "OFFSET",
    "FETCH NEXT",
    "WITH",
    "EXEC",
    "DECLARE",
    "BEGIN",
    "COMMIT",
    "ROLLBACK",
  ];
  const SNIPPETS = [
    {
      label: "SELECT query",
      text: "SELECT ${1:*}\nFROM ${2:dbo.TableName}\nWHERE ${3:condition};",
    },
    {
      label: "JOIN clause",
      text: "JOIN ${1:dbo.TableName} AS ${2:t}\n  ON ${3:condition}",
    },
    {
      label: "CASE expression",
      text: "CASE WHEN ${1:condition} THEN ${2:value} ELSE ${3:value} END",
    },
    {
      label: "CTE",
      text: "WITH ${1:cte} AS (\n    SELECT ${2:*} FROM ${3:dbo.TableName}\n)\nSELECT * FROM ${1:cte};",
    },
  ];

  function registerProvider(monaco) {
    if (providerRegistered) return;
    providerRegistered = true;
    monaco.languages.registerCompletionItemProvider("sql", {
      triggerCharacters: [".", " "],
      provideCompletionItems: function (model, position) {
        const word = model.getWordUntilPosition(position);
        const range = {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endColumn: word.endColumn,
        };
        const K = monaco.languages.CompletionItemKind;
        const result = window.queryGateCompletion.analyze(
          model.getValue(),
          model.getOffsetAt(position),
          metadata,
        );
        if (result.replacement) {
          const start = model.getPositionAt(result.replacement.start);
          const end = model.getPositionAt(result.replacement.end);
          range.startLineNumber = start.lineNumber;
          range.startColumn = start.column;
          range.endLineNumber = end.lineNumber;
          range.endColumn = end.column;
        }
        const suggestions = result.suggestions.map((item) => ({
          label: item.label,
          kind:
            item.kind === "column"
              ? K.Field
              : item.kind === "join"
                ? K.Reference
                : item.kind === "variable"
                  ? K.Variable
                  : K.Class,
          insertText: item.insertText,
          detail: item.detail,
          sortText: "0" + item.label,
          range,
        }));
        if (result.context === "expression")
          suggestions.push(
            ...KEYWORDS.map((k) => ({
              label: k,
              kind: K.Keyword,
              insertText: k,
              sortText: "1" + k,
              range,
            })),
          );
        if (result.context === "expression")
          suggestions.push(
            ...SNIPPETS.map((s) => ({
              label: s.label,
              kind: K.Snippet,
              insertText: s.text,
              insertTextRules:
                monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
              sortText: "2" + s.label,
              range,
            })),
          );
        return { suggestions };
      },
    });
    monaco.languages.registerHoverProvider("sql", {
      provideHover(model, position) {
        const word = model.getWordAtPosition(position);
        if (!word) return null;
        const end = { lineNumber: position.lineNumber, column: word.endColumn };
        const result = window.queryGateCompletion.analyze(
          model.getValue(),
          model.getOffsetAt(end),
          metadata,
        );
        const matches = result.suggestions.filter(
          (s) =>
            s.kind === "column" &&
            s.label.toLowerCase() === word.word.toLowerCase(),
        );
        let detail = matches.length === 1 ? matches[0].detail : null;
        if (!detail && matches.length === 0) {
          const prefix = model
            .getLineContent(position.lineNumber)
            .slice(0, word.startColumn - 1);
          const schema = /(?:\[([^\]]+)\]|([a-z_]\w*))\.\s*$/i.exec(prefix);
          const hits = metadata.filter(
            (o) =>
              o.name.toLowerCase() === word.word.toLowerCase() &&
              (!schema ||
                o.schema.toLowerCase() ===
                  (schema[1] || schema[2]).toLowerCase()),
          );
          if (hits.length === 1)
            detail = hits[0].schema + "." + hits[0].name + " / " + hits[0].type;
        }
        if (!detail) return null;
        detail = detail.replace(/[\\`*_{}\[\]()#+.!|>~-]/g, "\\$&");
        return {
          range: new monaco.Range(
            position.lineNumber,
            word.startColumn,
            position.lineNumber,
            word.endColumn,
          ),
          contents: [{ value: detail }],
        };
      },
    });
  }

  const editors = {};
  function scheduleDiagnostics(entry) {
    clearTimeout(entry.diagnosticTimer);
    if (!entry.diagnostics) return;
    const model = entry.editor.getModel();
    const version = model.getVersionId();
    entry.diagnosticTimer = setTimeout(async () => {
      try {
        const sql = model.getValue();
        const errors =
          sql.length > 4000
            ? [
                {
                  line: 1,
                  column: 1,
                  message:
                    "Live diagnostics are unavailable for SQL over 4,000 characters.",
                },
              ]
            : await entry.ref.invokeMethodAsync("GetSyntaxDiagnostics", sql);
        if (
          entry.editor.getModel() !== model ||
          model.isDisposed() ||
          model.getVersionId() !== version ||
          !entry.diagnostics
        )
          return;
        window.monaco.editor.setModelMarkers(
          model,
          "dbam-syntax",
          errors.map((e) => {
            const line = Math.max(1, Math.min(e.line, model.getLineCount()));
            const column = Math.max(
              1,
              Math.min(e.column, model.getLineMaxColumn(line)),
            );
            return {
              severity: window.monaco.MarkerSeverity.Warning,
              message: e.message,
              startLineNumber: line,
              endLineNumber: line,
              startColumn: column,
              endColumn: Math.min(column + 1, model.getLineMaxColumn(line)),
            };
          }),
        );
      } catch (error) {
        console.error("Could not check SQL syntax", error);
        entry.ref.invokeMethodAsync("OnDiagnosticsError").catch(console.error);
      }
    }, 1000);
  }

  function clampSideWidth(width) {
    return Math.max(180, Math.min(520, width));
  }

  function setupEditorFileDrop(el, ref) {
    if (!el || el._hasFileDrop) return;
    el._hasFileDrop = true;

    el.addEventListener("dragover", (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      el.classList.add("drag-over");
    });

    el.addEventListener("dragleave", (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      el.classList.remove("drag-over");
    });

    el.addEventListener("drop", async (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      el.classList.remove("drag-over");
      const item = ev.dataTransfer?.items?.[0];
      const handlePromise =
        item && typeof item.getAsFileSystemHandle === "function"
          ? item.getAsFileSystemHandle().catch(() => null)
          : null;
      const fallbackFile = ev.dataTransfer?.files?.[0] || null;

      if (handlePromise) {
        const handle = await handlePromise;
        if (handle && handle.kind === "file") {
          if (!handle.name.toLowerCase().endsWith(".sql")) {
            ref.invokeMethodAsync("OnDropFileRejected", handle.name);
            return;
          }
          const file = await handle.getFile();
          const content = await file.text();
          pendingDropHandle = handle;
          ref.invokeMethodAsync("OnSqlFileDropped", file.name, content, true);
          return;
        }
      }

      if (!fallbackFile) return;
      if (!fallbackFile.name.toLowerCase().endsWith(".sql")) {
        ref.invokeMethodAsync("OnDropFileRejected", fallbackFile.name);
        return;
      }
      const reader = new FileReader();
      reader.onload = (e) => {
        const content = e.target.result;
        pendingDropHandle = null;
        ref.invokeMethodAsync(
          "OnSqlFileDropped",
          fallbackFile.name,
          content,
          false,
        );
      };
      reader.readAsText(fallbackFile);
    });
  }

  function ensureSideResizeHandlers() {
    if (sideResizeBound) return;
    sideResizeBound = true;

    let drag = null;
    function startSideDrag(splitter, clientX) {
      const ws = splitter && splitter.closest(".ws");
      if (!splitter || !ws) return false;
      const bounds = ws.getBoundingClientRect();
      const current = parseInt(
        getComputedStyle(ws).getPropertyValue("--ws-side-w"),
        10,
      );
      drag = {
        ws,
        startX: clientX,
        startWidth: Number.isFinite(current)
          ? current
          : Math.round(bounds.width * 0.24),
      };
      document.body.style.userSelect = "none";
      return true;
    }
    function moveSideDrag(clientX) {
      if (!drag) return;
      const width = clampSideWidth(drag.startWidth + (clientX - drag.startX));
      drag.ws.style.setProperty("--ws-side-w", width + "px");
    }
    function endSideDrag() {
      if (!drag) return;
      const width = parseInt(
        getComputedStyle(drag.ws).getPropertyValue("--ws-side-w"),
        10,
      );
      try {
        localStorage.setItem(SIDE_WIDTH_KEY, String(clampSideWidth(width)));
      } catch (e) {}
      drag = null;
      document.body.style.userSelect = "";
    }

    document.addEventListener("mousedown", (ev) => {
      const splitter = ev.target.closest && ev.target.closest(".ws-side-split");
      if (splitter && startSideDrag(splitter, ev.clientX)) ev.preventDefault();
    });
    document.addEventListener("mousemove", (ev) => moveSideDrag(ev.clientX));
    document.addEventListener("mouseup", endSideDrag);

    document.addEventListener(
      "touchstart",
      (ev) => {
        const splitter =
          ev.target.closest && ev.target.closest(".ws-side-split");
        if (!splitter || !ev.touches || !ev.touches.length) return;
        if (startSideDrag(splitter, ev.touches[0].clientX)) ev.preventDefault();
      },
      { passive: false },
    );
    document.addEventListener(
      "touchmove",
      (ev) => {
        if (!drag || !ev.touches || !ev.touches.length) return;
        ev.preventDefault();
        moveSideDrag(ev.touches[0].clientX);
      },
      { passive: false },
    );
    document.addEventListener("touchend", endSideDrag);
    document.addEventListener("touchcancel", endSideDrag);
  }

  function handleWorkspaceShortcutEvent(e) {
    if (!activeWorkspaceRef || !e) return false;
    if (!document.getElementById("query-ws")) return false;

    if (
      e.ctrlKey &&
      !e.altKey &&
      !e.metaKey &&
      !e.shiftKey &&
      (e.code === "Backquote" || e.key === "`")
    ) {
      e.preventDefault();
      e.stopPropagation();
      activeWorkspaceRef
        .invokeMethodAsync("OnToggleResults")
        .catch(console.error);
      return true;
    }

    if (
      (e.ctrlKey || e.metaKey) &&
      !e.altKey &&
      !e.shiftKey &&
      (e.code === "Backslash" || e.key === "\\")
    ) {
      e.preventDefault();
      e.stopPropagation();
      activeWorkspaceRef.invokeMethodAsync("OnToggleSide").catch(console.error);
      return true;
    }

    if (
      (e.ctrlKey || e.metaKey) &&
      !e.altKey &&
      !e.shiftKey &&
      (e.code === "KeyI" || (e.key && e.key.toLowerCase() === "i"))
    ) {
      e.preventDefault();
      e.stopPropagation();
      activeWorkspaceRef.invokeMethodAsync("OnToggleAi").catch(console.error);
      return true;
    }

    if (
      (e.ctrlKey || e.metaKey) &&
      e.shiftKey &&
      !e.altKey &&
      (e.code === "KeyE" || (e.key && e.key.toLowerCase() === "e"))
    ) {
      e.preventDefault();
      e.stopPropagation();
      activeWorkspaceRef
        .invokeMethodAsync("OnToggleFocusMode")
        .catch(console.error);
      return true;
    }

    return false;
  }

  function ensureWorkspaceShortcuts() {
    if (workspaceShortcutsBound) return;
    workspaceShortcutsBound = true;
    document.addEventListener("keydown", (e) => {
      if (!activeWorkspaceRef || !document.getElementById("query-ws")) return;
      if (e.key === "Escape") {
        if (
          !e.defaultPrevented &&
          !document.querySelector(
            "#sql-editor .suggest-widget.visible, #sql-editor .find-widget.visible",
          )
        ) {
          activeWorkspaceRef.invokeMethodAsync("OnEscape").catch(() => {});
        }
        return;
      }
      if (e.target && e.target.closest && e.target.closest("#sql-editor")) {
        return;
      }
      handleWorkspaceShortcutEvent(e);
    });
  }

  return {
    isNarrowViewport: () =>
      window.matchMedia(
        "(max-width: 960px), (max-width: 1024px) and (pointer: coarse)",
      ).matches,
    clampMenuCoords: (x, y, w, h) => {
      const vw = window.innerWidth || 800;
      const vh = window.innerHeight || 600;
      const pad = 8;
      const cx = Math.max(pad, Math.min(x, vw - (w || 240) - pad));
      const cy = Math.max(pad, Math.min(y, vh - (h || 320) - pad));
      return [cx, cy];
    },
    getTheme: () =>
      window.dbamTheme
        ? window.dbamTheme.get()
        : document.documentElement.getAttribute("data-theme") || "light",
    toggleTheme: function () {
      if (window.dbamTheme) return window.dbamTheme.toggle();
      const next = this.getTheme() === "dark" ? "light" : "dark";
      document.documentElement.setAttribute("data-theme", next);
      try {
        localStorage.setItem("dbam-theme", next);
      } catch (e) {}
      if (window.monaco)
        window.monaco.editor.setTheme(next === "dark" ? "vs-dark" : "vs");
      return next;
    },
    download: function (name, content, mime) {
      const blob = new Blob(["\ufeff" + content], {
        type: mime || "text/csv;charset=utf-8",
      });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = name;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    downloadBase64: function (name, base64, mime) {
      const byteChars = atob(base64);
      const byteNumbers = new Array(byteChars.length);
      for (let i = 0; i < byteChars.length; i++) {
        byteNumbers[i] = byteChars.charCodeAt(i);
      }
      const byteArray = new Uint8Array(byteNumbers);
      const blob = new Blob([byteArray], {
        type: mime || "application/octet-stream",
      });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = name;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    downloadUrl: function (url, name) {
      const a = document.createElement("a");
      a.href = url;
      if (name) a.download = name;
      a.style.display = "none";
      document.body.appendChild(a);
      a.click();
      a.remove();
    },
    ensureErDiagram: () => loadErDiagram(),
    scrollHighlightedOption: function (target, index) {
      try {
        const menu =
          typeof target === "string" ? document.querySelector(target) : target;
        if (!menu) return;
        const option = menu.querySelector(`[data-index="${index}"]`);
        if (option) {
          option.scrollIntoView({ block: "nearest" });
        }
      } catch (e) {}
    },
    copy: (text) =>
      navigator.clipboard
        ? navigator.clipboard.writeText(text)
        : Promise.resolve(),
    submitForm: (id) => document.getElementById(id)?.submit(),
    triggerFileInput: (id) => document.getElementById(id)?.click(),
    openSqlFile: async function (tabId) {
      if (typeof window.showOpenFilePicker !== "function") {
        return {
          supported: false,
          cancelled: false,
          fileName: null,
          content: null,
        };
      }
      try {
        const [handle] = await window.showOpenFilePicker({
          types: [
            {
              description: "SQL Files",
              accept: { "application/sql": [".sql"], "text/plain": [".sql"] },
            },
          ],
          multiple: false,
        });
        if (!handle)
          return {
            supported: true,
            cancelled: true,
            fileName: null,
            content: null,
          };
        const file = await handle.getFile();
        const content = await file.text();
        if (tabId) tabFileHandles.set(tabId, handle);
        return {
          supported: true,
          cancelled: false,
          fileName: file.name,
          content,
        };
      } catch (e) {
        if (e && e.name === "AbortError") {
          return {
            supported: true,
            cancelled: true,
            fileName: null,
            content: null,
          };
        }
        return {
          supported: true,
          cancelled: false,
          error: e?.message || "Failed to open file.",
        };
      }
    },
    bindPendingDropHandle: function (tabId) {
      if (!tabId || !pendingDropHandle) return false;
      tabFileHandles.set(tabId, pendingDropHandle);
      pendingDropHandle = null;
      return true;
    },
    saveSqlFile: async function (tabId, content) {
      const handle = tabId ? tabFileHandles.get(tabId) : null;
      if (!handle) return { ok: false, reason: "no-handle" };
      try {
        let perm = await handle.queryPermission({ mode: "readwrite" });
        if (perm !== "granted") {
          perm = await handle.requestPermission({ mode: "readwrite" });
        }
        if (perm !== "granted")
          return { ok: false, reason: "permission-denied" };
        const writable = await handle.createWritable();
        await writable.write(content);
        await writable.close();
        return { ok: true, fileName: handle.name };
      } catch (e) {
        return {
          ok: false,
          reason: "error",
          error: e?.message || "Failed to write file.",
        };
      }
    },
    saveSqlFileAs: async function (tabId, content, suggestedName) {
      if (typeof window.showSaveFilePicker === "function") {
        try {
          const handle = await window.showSaveFilePicker({
            suggestedName: suggestedName || "query.sql",
            types: [
              {
                description: "SQL Files",
                accept: { "application/sql": [".sql"], "text/plain": [".sql"] },
              },
            ],
          });
          const writable = await handle.createWritable();
          await writable.write(content);
          await writable.close();
          if (tabId) tabFileHandles.set(tabId, handle);
          return {
            ok: true,
            cancelled: false,
            mode: "picker",
            fileName: handle.name,
          };
        } catch (e) {
          if (e && e.name === "AbortError") {
            return { ok: false, cancelled: true, mode: "picker" };
          }
        }
      }
      this.download(suggestedName || "query.sql", content, "application/sql");
      return {
        ok: true,
        cancelled: false,
        mode: "download",
        fileName: suggestedName || "query.sql",
      };
    },
    releaseTabFileHandle: function (tabId) {
      if (tabId) tabFileHandles.delete(tabId);
    },

    startIdle: function (ref, warnMs, expireMs) {
      let warned = false,
        last = Date.now();
      const reset = () => {
        last = Date.now();
        if (warned) {
          warned = false;
          ref.invokeMethodAsync("OnIdleReset");
        }
      };
      ["mousemove", "keydown", "click", "scroll"].forEach((e) =>
        window.addEventListener(e, reset, { passive: true }),
      );
      setInterval(() => {
        const idle = Date.now() - last;
        if (idle > expireMs) ref.invokeMethodAsync("OnIdleExpired");
        else if (idle > warnMs && !warned) {
          warned = true;
          ref.invokeMethodAsync(
            "OnIdleWarning",
            Math.round((expireMs - idle) / 60000),
          );
        }
      }, 15000);
    },

    workspaceInit: function (containerId) {
      const ws = document.getElementById(containerId);
      if (!ws) return;
      let width = null;
      try {
        width = parseInt(localStorage.getItem(SIDE_WIDTH_KEY) || "", 10);
      } catch (e) {}
      if (Number.isFinite(width))
        ws.style.setProperty("--ws-side-w", clampSideWidth(width) + "px");
      ensureSideResizeHandlers();
      ensureWorkspaceShortcuts();
    },

    toggleResults: function () {
      if (activeWorkspaceRef) {
        return activeWorkspaceRef
          .invokeMethodAsync("OnToggleResults")
          .catch(console.error);
      }
    },

    editorInit: async function (id, elementId, ref, value, preferences) {
      if (id === "main") {
        activeWorkspaceRef = ref;
        ensureWorkspaceShortcuts();
      }
      const el = document.getElementById(elementId);
      if (!el) return false;
      try {
        metadata = [];
        const [monaco] = await Promise.all([loadMonaco(), loadSqlCompletion()]);
        registerProvider(monaco);
        el.innerHTML = "";
        const editor = monaco.editor.create(el, {
          value: value || "",
          language: "sql",
          theme: this.getTheme() === "dark" ? "vs-dark" : "vs",
          automaticLayout: true,
          minimap: { enabled: false },
          fontSize: preferences.fontSize,
          quickSuggestions: preferences.automaticSuggestions,
          suggestOnTriggerCharacters: preferences.automaticSuggestions,
          fontFamily: "'Cascadia Code', Consolas, monospace",
          fontLigatures: true,
          scrollBeyondLastLine: false,
          padding: { top: 8 },
          smoothScrolling: true,
          renderLineHighlight: "all",
          tabSize: 4,
          fixedOverflowWidgets: true,
        });
        editor.addCommand(monaco.KeyCode.F5, () =>
          ref.invokeMethodAsync("OnRun").catch(console.error),
        );
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () =>
          ref.invokeMethodAsync("OnRun").catch(console.error),
        );
        editor.addCommand(
          monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF,
          () => ref.invokeMethodAsync("OnFormat").catch(console.error),
        );
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () =>
          ref.invokeMethodAsync("OnSave").catch(console.error),
        );
        editor.onKeyDown((ev) => {
          if (ev.keyCode === monaco.KeyCode.F5) {
            ev.preventDefault();
            ev.stopPropagation();
            ref.invokeMethodAsync("OnRun").catch(console.error);
            return;
          }
          if (handleWorkspaceShortcutEvent(ev.browserEvent)) {
            ev.preventDefault();
            ev.stopPropagation();
          }
        });
        const entry = {
          kind: "monaco",
          editor,
          ref,
          diagnostics: preferences.liveDiagnostics,
          cursorTimer: null,
          changeTimer: null,
        };
        editors[id] = entry;
        editor.onDidChangeModelContent(() => {
          clearTimeout(entry.changeTimer);
          entry.changeTimer = setTimeout(() => {
            if (editors[id] === entry) {
              ref.invokeMethodAsync("OnChanged", editor.getValue()).catch(() => {});
            }
          }, 250);
          scheduleDiagnostics(entry);
        });
        editor.onDidChangeCursorPosition((ev) => {
          clearTimeout(entry.cursorTimer);
          entry.cursorTimer = setTimeout(() => {
            if (editors[id] === entry) {
              const elCursor = document.getElementById("ws-status-cursor");
              if (elCursor) {
                elCursor.textContent =
                  "Ln " +
                  ev.position.lineNumber +
                  ", Col " +
                  ev.position.column;
              }
            }
          }, 50);
        });
        scheduleDiagnostics(entry);
        setupEditorFileDrop(el, ref);
        return true;
      } catch (e) {
        console.warn("Monaco unavailable, using plain editor", e);
        el.innerHTML = "";
        const ta = document.createElement("textarea");
        ta.className = "fallback";
        ta.value = value || "";
        ta.spellcheck = false;
        const plainEntry = { kind: "plain", ta, changeTimer: null };
        ta.addEventListener("input", () => {
          clearTimeout(plainEntry.changeTimer);
          plainEntry.changeTimer = setTimeout(() => {
            if (editors[id] === plainEntry) {
              ref.invokeMethodAsync("OnChanged", ta.value).catch(() => {});
            }
          }, 250);
        });
        ta.addEventListener("keydown", (ev) => {
          if (ev.key === "F5" || (ev.ctrlKey && ev.key === "Enter")) {
            ev.preventDefault();
            ref.invokeMethodAsync("OnRun").catch(console.error);
            return;
          }
          handleWorkspaceShortcutEvent(ev);
        });
        el.appendChild(ta);
        editors[id] = plainEntry;
        setupEditorFileDrop(el, ref);
        return false;
      }
    },
    editorGet: function (id) {
      const e = editors[id];
      return !e ? "" : e.kind === "monaco" ? e.editor.getValue() : e.ta.value;
    },
    editorSelection: function (id) {
      const e = editors[id];
      if (!e) return "";
      if (e.kind === "monaco") {
        const sel = e.editor.getSelection();
        return sel && !sel.isEmpty()
          ? e.editor.getModel().getValueInRange(sel)
          : "";
      }
      return e.ta.value.substring(e.ta.selectionStart, e.ta.selectionEnd);
    },
    editorSet: function (id, value) {
      const e = editors[id];
      if (!e) return;
      if (e.kind === "monaco") e.editor.setValue(value);
      else e.ta.value = value;
    },
    editorMarker: function (id, line, message) {
      const e = editors[id];
      if (!e || e.kind !== "monaco" || !window.monaco) return;
      const model = e.editor.getModel();
      window.monaco.editor.setModelMarkers(
        model,
        "dbam",
        line
          ? [
              {
                severity: window.monaco.MarkerSeverity.Error,
                message,
                startLineNumber: line,
                endLineNumber: line,
                startColumn: 1,
                endColumn: model.getLineMaxColumn(
                  Math.min(line, model.getLineCount()),
                ),
              },
            ]
          : [],
      );
    },
    editorDispose: function (id) {
      if (id === "main") {
        activeWorkspaceRef = null;
      }
      const e = editors[id];
      if (e) {
        clearTimeout(e.diagnosticTimer);
        clearTimeout(e.cursorTimer);
        clearTimeout(e.changeTimer);
        if (e.kind === "monaco") {
          e.editor.dispose();
        }
      }
      delete editors[id];
      metadata = [];
    },
    editorPreferences: function (id, preferences) {
      const e = editors[id];
      if (!e || e.kind !== "monaco") return;
      e.diagnostics = preferences.liveDiagnostics;
      e.editor.updateOptions({
        fontSize: preferences.fontSize,
        quickSuggestions: preferences.automaticSuggestions,
        suggestOnTriggerCharacters: preferences.automaticSuggestions,
      });
      if (!e.diagnostics) {
        clearTimeout(e.diagnosticTimer);
        window.monaco.editor.setModelMarkers(
          e.editor.getModel(),
          "dbam-syntax",
          [],
        );
      } else scheduleDiagnostics(e);
    },
    editorLayout: function (id) {
      const e = editors[id];
      if (e && e.kind === "monaco") {
        setTimeout(() => e.editor.layout(), 10);
      }
    },
    syncSplitLayout: function (isHoriz) {
      const panes = document.querySelectorAll(".ws-body .ws-editor-pane");
      panes.forEach((pane) => {
        if (isHoriz) {
          pane.style.flex = pane.dataset.hFlex
            ? "0 0 " + pane.dataset.hFlex
            : "";
        } else {
          pane.style.flex = pane.dataset.vFlex
            ? "0 0 " + pane.dataset.vFlex
            : "";
        }
      });
      setTimeout(() => {
        const e = editors["main"];
        if (e && e.kind === "monaco") e.editor.layout();
      }, 20);
    },
    setMetadata: function (objects) {
      metadata = objects || [];
    },
  };
})();

/* ── Password-peek toggle (SSR-safe, auto-attaches) ── */
(function () {
  function attachPasswordToggles() {
    document
      .querySelectorAll(".btn-toggle-password[data-password-input]")
      .forEach(function (btn) {
        if (btn._pwAttached) return;
        btn._pwAttached = true;

        var inputId = btn.getAttribute("data-password-input");
        var input = document.getElementById(inputId);
        if (!input) return;

        function setVisibility(show) {
          input.type = show ? "text" : "password";
          if (show) {
            btn.classList.add("is-visible");
            btn.setAttribute("aria-label", "Hide password");
            btn.setAttribute("title", "Hide password");
            btn.setAttribute("aria-pressed", "true");
          } else {
            btn.classList.remove("is-visible");
            btn.setAttribute("aria-label", "Show password");
            btn.setAttribute("title", "Show password");
            btn.setAttribute("aria-pressed", "false");
          }
        }

        btn.addEventListener("click", function (e) {
          e.preventDefault();
          e.stopPropagation();
          var isShowing = input.type === "text";
          setVisibility(!isShowing);
        });
      });
  }

  /* ── Show passkey buttons only when WebAuthn is supported ── */
  function revealPasskeyButtons() {
    if (
      typeof navigator.credentials !== "undefined" &&
      typeof window.PublicKeyCredential !== "undefined"
    ) {
      document
        .querySelectorAll("[data-passkey-btn], [data-passkey-section]")
        .forEach(function (btn) {
          btn.style.display = "";
        });
    }
  }

  /* ── Documentation navigation scroll highlighting ── */
  function attachDocsNavHighlight() {
    // If a previous scroll handler was attached, clean it up first
    if (window._docsScrollHandler) {
      window.removeEventListener("scroll", window._docsScrollHandler);
      window._docsScrollHandler = null;
    }

    var docsContent = document.querySelector(".docs-content");
    if (!docsContent) return;

    var allLinks = document.querySelectorAll(
      '.docs-nav a[href*="#"], #docsMobileDropdown a[href*="#"]',
    );
    if (!allLinks.length) return;

    // Collect all target section elements in actual DOM order
    var candidateElements = docsContent.querySelectorAll(
      "section[id], .docs-subsection[id]",
    );
    var sections = [];

    candidateElements.forEach(function (el) {
      var id = el.id;
      if (!id) return;
      var matchingLinks = Array.from(
        document.querySelectorAll(
          '.docs-nav a[href$="#' +
            id +
            '"], #docsMobileDropdown a[href$="#' +
            id +
            '"]',
        ),
      );
      if (matchingLinks.length) {
        var heading = el.querySelector("h2, h3");
        var title = (matchingLinks[0].textContent || "")
          .replace(/[\n\r]+/g, " ")
          .replace(/✓/g, "")
          .trim();
        title = title.replace(/\b(Fast|Recommended)\b/gi, "").trim();
        if (!title && heading) {
          title = (heading.textContent || "").trim();
        }
        sections.push({
          id: id,
          el: el,
          title: title,
          links: matchingLinks,
        });
      }
    });

    if (!sections.length) return;

    var currentTitleEl = document.querySelector(".docs-mobile-current-title");
    var backToTopBtn = document.getElementById("docsBackToTop");

    function updateActiveSection(activeSection) {
      if (!activeSection) return;
      sections.forEach(function (item) {
        var isActive = item.id === activeSection.id;
        item.links.forEach(function (link) {
          if (isActive) {
            link.classList.add("active");
          } else {
            link.classList.remove("active");
          }
        });
      });
      if (currentTitleEl) {
        currentTitleEl.textContent = activeSection.title;
      }
    }

    function onScroll() {
      var isBottom =
        window.innerHeight + window.scrollY >=
        document.documentElement.scrollHeight - 60;
      var activeSection = sections[0];

      if (isBottom) {
        activeSection = sections[sections.length - 1];
      } else {
        var threshold = 140;
        for (var i = 0; i < sections.length; i++) {
          var rect = sections[i].el.getBoundingClientRect();
          if (rect.top <= threshold) {
            activeSection = sections[i];
          }
        }
      }

      updateActiveSection(activeSection);

      if (backToTopBtn) {
        if (window.scrollY > 350) {
          backToTopBtn.classList.add("visible");
        } else {
          backToTopBtn.classList.remove("visible");
        }
      }
    }

    window._docsScrollHandler = onScroll;
    window.addEventListener("scroll", onScroll, { passive: true });
    window.addEventListener("resize", onScroll, { passive: true });
    onScroll();

    window._updateActiveDocsSectionById = function (id) {
      var found = sections.find(function (s) {
        return s.id === id;
      });
      if (found) {
        updateActiveSection(found);
      }
    };
  }

  // Export globally for Blazor component invocations
  window.initDocumentationPage = function () {
    attachDocsNavHighlight();
    setupMobileToc();
    attachCodeCopyButtons();
  };

  /* ── Mobile TOC Dropdown Toggle and Auto-Close ── */
  let mobileTocHandlerAttached = false;
  function setupMobileToc() {
    if (mobileTocHandlerAttached) return;
    mobileTocHandlerAttached = true;

    document.addEventListener("click", function (e) {
      var mobileToc = document.getElementById("docsMobileToc");
      if (!mobileToc) return;
      var trigger = document.getElementById("docsMobileTocTrigger");

      // Tapping the dropdown trigger button
      if (trigger && (trigger === e.target || trigger.contains(e.target))) {
        var isOpen = mobileToc.classList.toggle("is-open");
        trigger.setAttribute("aria-expanded", isOpen ? "true" : "false");
        return;
      }

      // Tapping any option inside the dropdown immediately closes it and updates active selection
      var link = e.target.closest("#docsMobileDropdown a");
      if (link) {
        var href = link.getAttribute("href") || "";
        var hash = href.split("#")[1];
        if (hash && window._updateActiveDocsSectionById) {
          window._updateActiveDocsSectionById(hash);
        }
        if (mobileToc.classList.contains("is-open")) {
          mobileToc.classList.remove("is-open");
          if (trigger) trigger.setAttribute("aria-expanded", "false");
        }
        return;
      }

      // Tapping outside closes the dropdown
      if (
        mobileToc.classList.contains("is-open") &&
        !mobileToc.contains(e.target)
      ) {
        mobileToc.classList.remove("is-open");
        if (trigger) trigger.setAttribute("aria-expanded", "false");
      }
    });
  }

  /* ── Command and Code Copy Handlers ── */
  let copyHandlerAttached = false;
  function attachCodeCopyButtons() {
    if (copyHandlerAttached) return;
    copyHandlerAttached = true;

    document.addEventListener("click", function (e) {
      var btn = e.target.closest(".btn-code-copy, .btn-inline-copy");
      if (!btn) return;
      var textToCopy = btn.getAttribute("data-copy");
      if (!textToCopy) {
        var pre = btn.closest(".docs-code-block")
          ? btn.closest(".docs-code-block").querySelector("pre code")
          : null;
        textToCopy = pre ? pre.innerText.trim() : "";
      }
      if (!textToCopy) return;

      navigator.clipboard
        .writeText(textToCopy)
        .then(function () {
          btn.classList.add("copied");
          var span = btn.querySelector("span");
          var originalText = span ? span.innerText : "";
          if (span) span.innerText = "Copied!";
          setTimeout(function () {
            btn.classList.remove("copied");
            if (span) span.innerText = originalText || "Copy";
          }, 2000);
        })
        .catch(function (err) {
          console.error("Copy failed", err);
        });
    });
  }

  let comboboxHandlerAttached = false;
  function attachComboboxKeydownHandler() {
    if (comboboxHandlerAttached) return;
    comboboxHandlerAttached = true;
    document.addEventListener("keydown", function (e) {
      if (
        e.target &&
        e.target.classList &&
        e.target.classList.contains("combobox-input")
      ) {
        if (e.key === "Enter" || e.key === "ArrowDown" || e.key === "ArrowUp") {
          e.preventDefault();
        }
      }
    });
  }

  window.registerGlobalCommandPalette = function (ref) {
    window._commandPaletteRef = ref;
    if (!window._commandPaletteListenerAttached) {
      window._commandPaletteListenerAttached = true;
      document.addEventListener("keydown", function (e) {
        if ((e.metaKey || e.ctrlKey) && (e.key === "k" || e.key === "K")) {
          e.preventDefault();
          if (window._commandPaletteRef) {
            window._commandPaletteRef.invokeMethodAsync("OpenPalette");
          }
        }
      });
    }
  };

  window.toggleTheme = function () {
    if (window.dbamTheme) return window.dbamTheme.toggle();
    const root = document.documentElement;
    const current = root.getAttribute("data-theme") || "light";
    const next = current === "dark" ? "light" : "dark";
    root.setAttribute("data-theme", next);
    try {
      localStorage.setItem("dbam-theme", next);
    } catch (e) {}
    if (window.monaco) {
      window.monaco.editor.setTheme(next === "dark" ? "vs-dark" : "vs");
    }
    return next;
  };

  window.initTableColumnResizing = function (tableContainerId) {
    const wrap = document.getElementById(tableContainerId);
    if (!wrap) return;
    const headers = wrap.querySelectorAll("th.th-resizable");
    headers.forEach(function (th) {
      let resizer = th.querySelector(".th-resizer");
      if (!resizer) {
        resizer = document.createElement("div");
        resizer.className = "th-resizer";
        th.appendChild(resizer);
      }

      function beginResize(startX) {
        const startWidth = th.offsetWidth;
        resizer.classList.add("resizing");
        return function applyWidth(pageX) {
          const newWidth = Math.max(50, startWidth + (pageX - startX));
          th.style.width = newWidth + "px";
          th.style.minWidth = newWidth + "px";
        };
      }

      resizer.onmousedown = function (e) {
        e.preventDefault();
        e.stopPropagation();
        const applyWidth = beginResize(e.pageX);

        function onMouseMove(ev) {
          applyWidth(ev.pageX);
        }

        function onMouseUp() {
          resizer.classList.remove("resizing");
          document.removeEventListener("mousemove", onMouseMove);
          document.removeEventListener("mouseup", onMouseUp);
        }

        document.addEventListener("mousemove", onMouseMove);
        document.addEventListener("mouseup", onMouseUp);
      };

      resizer.ontouchstart = function (e) {
        if (!e.touches || !e.touches.length) return;
        e.preventDefault();
        e.stopPropagation();
        const applyWidth = beginResize(e.touches[0].pageX);

        function onTouchMove(ev) {
          if (!ev.touches || !ev.touches.length) return;
          ev.preventDefault();
          applyWidth(ev.touches[0].pageX);
        }

        function onTouchEnd() {
          resizer.classList.remove("resizing");
          document.removeEventListener("touchmove", onTouchMove);
          document.removeEventListener("touchend", onTouchEnd);
          document.removeEventListener("touchcancel", onTouchEnd);
        }

        document.addEventListener("touchmove", onTouchMove, { passive: false });
        document.addEventListener("touchend", onTouchEnd);
        document.addEventListener("touchcancel", onTouchEnd);
      };
    });
  };

  window.renderQueryChart = function (containerId, config) {
    const container = document.getElementById(containerId);
    if (!container) return;

    container._lastChartConfig = config;
    if (!container._chartObserver && typeof ResizeObserver !== "undefined") {
      let resizeTimer = null;
      container._chartObserver = new ResizeObserver(() => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => {
          if (container._lastChartConfig && document.body.contains(container)) {
            window.renderQueryChart(containerId, container._lastChartConfig);
          }
        }, 120);
      });
      container._chartObserver.observe(container);
    }

    container.innerHTML = "";

    const labels = config.labels || [];
    const values = config.values || [];
    const chartType = config.chartType || "bar";
    const colors = [
      "#5b5bf0",
      "#18b6e8",
      "#12a36b",
      "#d98a06",
      "#e03e52",
      "#8b5cf6",
      "#ec4899",
      "#f97316",
    ];

    if (!labels.length || !values.length) {
      container.innerHTML =
        "<div class='muted small' style='padding:20px;text-align:center'>No numeric data available to chart.</div>";
      return;
    }

    const width = Math.max(240, container.clientWidth || 600);
    const height = Math.max(150, container.clientHeight || 260);
    const isNarrowChart = width < 420;
    const isShortChart = height < 210;
    const padding = isNarrowChart
      ? {
          top: isShortChart ? 12 : 20,
          right: 16,
          bottom: isShortChart ? 28 : 38,
          left: 40,
        }
      : {
          top: isShortChart ? 16 : 30,
          right: 30,
          bottom: isShortChart ? 32 : 48,
          left: 56,
        };
    const chartWidth = Math.max(120, width - padding.left - padding.right);
    const chartHeight = Math.max(120, height - padding.top - padding.bottom);

    const maxVal = Math.max(...values, 0) || 1;
    const minVal = Math.min(...values, 0);

    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("width", "100%");
    svg.setAttribute("height", "100%");
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svg.style.overflow = "visible";

    if (chartType === "donut" || chartType === "pie") {
      const centerX = width / 2;
      const centerY = height / 2;
      const radius = Math.min(chartWidth, chartHeight) / 2.2;
      const innerRadius = chartType === "donut" ? radius * 0.55 : 0;
      const total = values.reduce((a, b) => a + (b > 0 ? b : 0), 0) || 1;

      let startAngle = 0;
      values.forEach((val, i) => {
        if (val <= 0) return;
        const sliceAngle = (val / total) * 2 * Math.PI;
        const endAngle = startAngle + sliceAngle;

        const x1 = centerX + radius * Math.cos(startAngle);
        const y1 = centerY + radius * Math.sin(startAngle);
        const x2 = centerX + radius * Math.cos(endAngle);
        const y2 = centerY + radius * Math.sin(endAngle);

        const ix1 = centerX + innerRadius * Math.cos(endAngle);
        const iy1 = centerY + innerRadius * Math.sin(endAngle);
        const ix2 = centerX + innerRadius * Math.cos(startAngle);
        const iy2 = centerY + innerRadius * Math.sin(startAngle);

        const largeArc = sliceAngle > Math.PI ? 1 : 0;
        const d =
          innerRadius > 0
            ? `M ${x1} ${y1} A ${radius} ${radius} 0 ${largeArc} 1 ${x2} ${y2} L ${ix1} ${iy1} A ${innerRadius} ${innerRadius} 0 ${largeArc} 0 ${ix2} ${iy2} Z`
            : `M ${centerX} ${centerY} L ${x1} ${y1} A ${radius} ${radius} 0 ${largeArc} 1 ${x2} ${y2} Z`;

        const path = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "path",
        );
        path.setAttribute("d", d);
        path.setAttribute("fill", colors[i % colors.length]);
        path.setAttribute("stroke", "var(--bg-elev)");
        path.setAttribute("stroke-width", "2");

        const title = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "title",
        );
        title.textContent = `${labels[i] || ""}: ${val} (${((val / total) * 100).toFixed(1)}%)`;
        path.appendChild(title);

        svg.appendChild(path);
        startAngle = endAngle;
      });
    } else if (chartType === "line" || chartType === "area") {
      // Y Grid lines
      for (let s = 0; s <= 4; s++) {
        const y = padding.top + (chartHeight * s) / 4;
        const val = maxVal - (maxVal / 4) * s;
        const line = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "line",
        );
        line.setAttribute("x1", padding.left);
        line.setAttribute("x2", width - padding.right);
        line.setAttribute("y1", y);
        line.setAttribute("y2", y);
        line.setAttribute("stroke", "var(--line)");
        line.setAttribute("stroke-dasharray", "3,3");
        svg.appendChild(line);

        const txt = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "text",
        );
        txt.setAttribute("x", padding.left - 8);
        txt.setAttribute("y", y + 4);
        txt.setAttribute("text-anchor", "end");
        txt.setAttribute("fill", "var(--text-faint)");
        txt.setAttribute("font-size", "11px");
        txt.textContent = Math.round(val);
        svg.appendChild(txt);
      }

      const points = values.map((val, i) => {
        const x = padding.left + (chartWidth * i) / (values.length - 1 || 1);
        const y = padding.top + chartHeight - (val / maxVal) * chartHeight;
        return { x, y, val, label: labels[i] };
      });

      const pathData = points
        .map((p, i) => `${i === 0 ? "M" : "L"} ${p.x} ${p.y}`)
        .join(" ");

      if (chartType === "area") {
        const areaData = `${pathData} L ${points[points.length - 1].x} ${padding.top + chartHeight} L ${points[0].x} ${padding.top + chartHeight} Z`;
        const areaPath = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "path",
        );
        areaPath.setAttribute("d", areaData);
        areaPath.setAttribute("fill", "color-mix(in srgb, var(--brand) 18%, transparent)");
        svg.appendChild(areaPath);
      }

      const linePath = document.createElementNS(
        "http://www.w3.org/2000/svg",
        "path",
      );
      linePath.setAttribute("d", pathData);
      linePath.setAttribute("fill", "none");
      linePath.setAttribute("stroke", colors[0]);
      linePath.setAttribute("stroke-width", "3");
      svg.appendChild(linePath);

      // Points
      points.forEach((p) => {
        const circle = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "circle",
        );
        circle.setAttribute("cx", p.x);
        circle.setAttribute("cy", p.y);
        circle.setAttribute("r", "4");
        circle.setAttribute("fill", colors[0]);
        circle.setAttribute("stroke", "var(--bg-elev)");
        circle.setAttribute("stroke-width", "2");

        const title = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "title",
        );
        title.textContent = `${p.label}: ${p.val}`;
        circle.appendChild(title);
        svg.appendChild(circle);
      });
    } else {
      // Default: Bar Chart
      const barWidth = Math.min(
        48,
        Math.max(12, (chartWidth / values.length) * 0.7),
      );

      // Y Grid lines
      for (let s = 0; s <= 4; s++) {
        const y = padding.top + (chartHeight * s) / 4;
        const val = maxVal - (maxVal / 4) * s;
        const line = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "line",
        );
        line.setAttribute("x1", padding.left);
        line.setAttribute("x2", width - padding.right);
        line.setAttribute("y1", y);
        line.setAttribute("y2", y);
        line.setAttribute("stroke", "var(--line)");
        line.setAttribute("stroke-dasharray", "3,3");
        svg.appendChild(line);

        const txt = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "text",
        );
        txt.setAttribute("x", padding.left - 8);
        txt.setAttribute("y", y + 4);
        txt.setAttribute("text-anchor", "end");
        txt.setAttribute("fill", "var(--text-faint)");
        txt.setAttribute("font-size", "11px");
        txt.textContent = Math.round(val);
        svg.appendChild(txt);
      }

      values.forEach((val, i) => {
        const barHeight = (val / maxVal) * chartHeight;
        const x =
          padding.left +
          (chartWidth / values.length) * i +
          (chartWidth / values.length - barWidth) / 2;
        const y = padding.top + chartHeight - barHeight;

        const rect = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "rect",
        );
        rect.setAttribute("x", x);
        rect.setAttribute("y", y);
        rect.setAttribute("width", barWidth);
        rect.setAttribute("height", barHeight);
        rect.setAttribute("rx", "3");
        rect.setAttribute("fill", colors[i % colors.length]);

        const title = document.createElementNS(
          "http://www.w3.org/2000/svg",
          "title",
        );
        title.textContent = `${labels[i] || ""}: ${val}`;
        rect.appendChild(title);
        svg.appendChild(rect);

        // X Axis labels (thin dynamically on narrow containers to prevent overlap)
        const maxLabels = Math.max(4, Math.floor(chartWidth / 56));
        const labelStep = Math.max(1, Math.ceil(values.length / maxLabels));
        if (i % labelStep === 0) {
          const lbl = document.createElementNS(
            "http://www.w3.org/2000/svg",
            "text",
          );
          lbl.setAttribute("x", x + barWidth / 2);
          lbl.setAttribute("y", height - padding.bottom + 16);
          lbl.setAttribute("text-anchor", "middle");
          lbl.setAttribute("fill", "var(--text-dim)");
          lbl.setAttribute("font-size", isNarrowChart ? "9.5px" : "10.5px");
          lbl.textContent = (labels[i] || "").substring(
            0,
            isNarrowChart ? 7 : 10,
          );
          svg.appendChild(lbl);
        }
      });
    }

    container.appendChild(svg);
  };

  function init() {
    attachPasswordToggles();
    revealPasskeyButtons();
    attachDocsNavHighlight();
    setupMobileToc();
    attachCodeCopyButtons();
    attachComboboxKeydownHandler();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
  document.addEventListener("blazor:enhancedload", init);

  // Auto-detect when docs content is rendered by client-side Blazor routing
  try {
    var observer = new MutationObserver(function () {
      if (
        document.querySelector(".docs-content") &&
        !window._docsScrollHandler
      ) {
        init();
      }
    });
    if (document.body) {
      observer.observe(document.body, { childList: true, subtree: true });
    }
  } catch (e) {
    // Ignore in unsupported environments
  }
})();
