window.dbam = (function () {
  const MONACO_BASE = "/_content/BlazorMonaco/lib/monaco-editor/min/vs";
  const SIDE_WIDTH_KEY = "dbam-ws-side-w";
  let monacoPromise = null;
  let metadata = [];
  let providerRegistered = false;
  let sideResizeBound = false;

  function loadScript(src) {
    return new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = src;
      s.onload = resolve;
      s.onerror = reject;
      document.head.appendChild(s);
    });
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
    { label: "SELECT query", text: "SELECT ${1:*}\nFROM ${2:dbo.TableName}\nWHERE ${3:condition};" },
    { label: "JOIN clause", text: "JOIN ${1:dbo.TableName} AS ${2:t}\n  ON ${3:condition}" },
    { label: "CASE expression", text: "CASE WHEN ${1:condition} THEN ${2:value} ELSE ${3:value} END" },
    { label: "CTE", text: "WITH ${1:cte} AS (\n    SELECT ${2:*} FROM ${3:dbo.TableName}\n)\nSELECT * FROM ${1:cte};" },
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
          model.getValue(), model.getOffsetAt(position), metadata,
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
          kind: item.kind === "column" ? K.Field : item.kind === "join" ? K.Reference : item.kind === "variable" ? K.Variable : K.Class,
          insertText: item.insertText,
          detail: item.detail,
          sortText: "0" + item.label,
          range,
        }));
        if (result.context === "expression") suggestions.push(...KEYWORDS.map((k) => ({
          label: k,
          kind: K.Keyword,
          insertText: k,
          sortText: "1" + k,
          range,
        })));
        if (result.context === "expression") suggestions.push(...SNIPPETS.map((s) => ({
          label: s.label, kind: K.Snippet, insertText: s.text,
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
          sortText: "2" + s.label, range,
        })));
        return { suggestions };
      },
    });
    monaco.languages.registerHoverProvider("sql", {
      provideHover(model, position) {
        const word = model.getWordAtPosition(position);
        if (!word) return null;
        const end = { lineNumber: position.lineNumber, column: word.endColumn };
        const result = window.queryGateCompletion.analyze(model.getValue(), model.getOffsetAt(end), metadata);
        const matches = result.suggestions.filter(s => s.kind === "column" && s.label.toLowerCase() === word.word.toLowerCase());
        let detail = matches.length === 1 ? matches[0].detail : null;
        if (!detail && matches.length === 0) {
          const prefix = model.getLineContent(position.lineNumber).slice(0, word.startColumn - 1);
          const schema = /(?:\[([^\]]+)\]|([a-z_]\w*))\.\s*$/i.exec(prefix);
          const hits = metadata.filter(o => o.name.toLowerCase() === word.word.toLowerCase() &&
            (!schema || o.schema.toLowerCase() === (schema[1] || schema[2]).toLowerCase()));
          if (hits.length === 1) detail = hits[0].schema + "." + hits[0].name + " / " + hits[0].type;
        }
        if (!detail) return null;
        detail = detail.replace(/[\\`*_{}\[\]()#+.!|>~-]/g, "\\$&");
        return { range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
          contents: [{ value: detail }] };
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
        const errors = sql.length > 4000
          ? [{ line: 1, column: 1, message: "Live diagnostics are unavailable for SQL over 4,000 characters." }]
          : await entry.ref.invokeMethodAsync("GetSyntaxDiagnostics", sql);
        if (entry.editor.getModel() !== model || model.isDisposed() || model.getVersionId() !== version || !entry.diagnostics) return;
        window.monaco.editor.setModelMarkers(model, "dbam-syntax", errors.map(e => {
          const line = Math.max(1, Math.min(e.line, model.getLineCount()));
          const column = Math.max(1, Math.min(e.column, model.getLineMaxColumn(line)));
          return { severity: window.monaco.MarkerSeverity.Warning, message: e.message,
            startLineNumber: line, endLineNumber: line, startColumn: column,
            endColumn: Math.min(column + 1, model.getLineMaxColumn(line)) };
        }));
      } catch (error) {
        console.error("Could not check SQL syntax", error);
        entry.ref.invokeMethodAsync("OnDiagnosticsError").catch(console.error);
      }
    }, 1000);
  }

  function clampSideWidth(width) {
    return Math.max(180, Math.min(520, width));
  }

  function ensureSideResizeHandlers() {
    if (sideResizeBound) return;
    sideResizeBound = true;

    let drag = null;
    document.addEventListener("mousedown", (ev) => {
      const splitter = ev.target.closest && ev.target.closest(".ws-side-split");
      const ws = splitter && splitter.closest(".ws");
      if (!splitter || !ws) return;
      const bounds = ws.getBoundingClientRect();
      const current = parseInt(
        getComputedStyle(ws).getPropertyValue("--ws-side-w"),
        10,
      );
      drag = {
        ws,
        startX: ev.clientX,
        startWidth: Number.isFinite(current)
          ? current
          : Math.round(bounds.width * 0.24),
      };
      document.body.style.userSelect = "none";
      ev.preventDefault();
    });

    document.addEventListener("mousemove", (ev) => {
      if (!drag) return;
      const width = clampSideWidth(
        drag.startWidth + (ev.clientX - drag.startX),
      );
      drag.ws.style.setProperty("--ws-side-w", width + "px");
    });

    document.addEventListener("mouseup", () => {
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
    });
  }

  return {
    isNarrowViewport: () => window.matchMedia("(max-width: 860px)").matches,
    getTheme: () =>
      document.documentElement.getAttribute("data-theme") || "light",
    toggleTheme: function () {
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
    copy: (text) =>
      navigator.clipboard
        ? navigator.clipboard.writeText(text)
        : Promise.resolve(),
    submitForm: (id) => document.getElementById(id)?.submit(),

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
    },

    editorInit: async function (id, elementId, ref, value, preferences) {
      const el = document.getElementById(elementId);
      if (!el) return false;
      try {
        metadata = [];
        const monaco = await loadMonaco();
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
          ref.invokeMethodAsync("OnRun"),
        );
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () =>
          ref.invokeMethodAsync("OnRun"),
        );
        editor.addCommand(
          monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF,
          () => ref.invokeMethodAsync("OnFormat"),
        );
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () =>
          ref.invokeMethodAsync("OnSave"),
        );
        editor.onKeyDown((ev) => {
          if (ev.keyCode === monaco.KeyCode.F5) {
            ev.preventDefault();
            ev.stopPropagation();
            ref.invokeMethodAsync("OnRun");
          }
        });
        const entry = { kind: "monaco", editor, ref, diagnostics: preferences.liveDiagnostics };
        editors[id] = entry;
        editor.onDidChangeModelContent(() => {
          ref.invokeMethodAsync("OnChanged", editor.getValue());
          scheduleDiagnostics(entry);
        });
        scheduleDiagnostics(entry);
        return true;
      } catch (e) {
        console.warn("Monaco unavailable, using plain editor", e);
        el.innerHTML = "";
        const ta = document.createElement("textarea");
        ta.className = "fallback";
        ta.value = value || "";
        ta.spellcheck = false;
        ta.addEventListener("input", () =>
          ref.invokeMethodAsync("OnChanged", ta.value),
        );
        ta.addEventListener("keydown", (ev) => {
          if (ev.key === "F5" || (ev.ctrlKey && ev.key === "Enter")) {
            ev.preventDefault();
            ref.invokeMethodAsync("OnRun");
          }
        });
        el.appendChild(ta);
        editors[id] = { kind: "plain", ta };
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
      const e = editors[id];
      if (e && e.kind === "monaco") { clearTimeout(e.diagnosticTimer); e.editor.dispose(); }
      delete editors[id];
      metadata = [];
    },
    editorPreferences: function (id, preferences) {
      const e = editors[id];
      if (!e || e.kind !== "monaco") return;
      e.diagnostics = preferences.liveDiagnostics;
      e.editor.updateOptions({ fontSize: preferences.fontSize, quickSuggestions: preferences.automaticSuggestions,
        suggestOnTriggerCharacters: preferences.automaticSuggestions });
      if (!e.diagnostics) {
        clearTimeout(e.diagnosticTimer);
        window.monaco.editor.setModelMarkers(e.editor.getModel(), "dbam-syntax", []);
      } else scheduleDiagnostics(e);
    },
    setMetadata: function (objects) {
      metadata = objects || [];
    },
  };
})();
