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
    scrollHighlightedOption: function (target, index) {
      try {
        const menu = typeof target === "string" ? document.querySelector(target) : target;
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

/* ── Password-peek toggle (SSR-safe, auto-attaches) ── */
(function () {
  function attachPasswordToggles() {
    document.querySelectorAll('.btn-toggle-password[data-password-input]').forEach(function (btn) {
      if (btn._pwAttached) return;
      btn._pwAttached = true;

      var inputId = btn.getAttribute('data-password-input');
      var input = document.getElementById(inputId);
      if (!input) return;

      function setVisibility(show) {
        input.type = show ? 'text' : 'password';
        if (show) {
          btn.classList.add('is-visible');
          btn.setAttribute('aria-label', 'Hide password');
          btn.setAttribute('title', 'Hide password');
          btn.setAttribute('aria-pressed', 'true');
        } else {
          btn.classList.remove('is-visible');
          btn.setAttribute('aria-label', 'Show password');
          btn.setAttribute('title', 'Show password');
          btn.setAttribute('aria-pressed', 'false');
        }
      }

      btn.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        var isShowing = input.type === 'text';
        setVisibility(!isShowing);
      });
    });
  }

  /* ── Show passkey buttons only when WebAuthn is supported ── */
  function revealPasskeyButtons() {
    if (
      typeof navigator.credentials !== 'undefined' &&
      typeof window.PublicKeyCredential !== 'undefined'
    ) {
      document.querySelectorAll('[data-passkey-btn]').forEach(function (btn) {
        btn.style.display = '';
      });
    }
  }

  /* ── Documentation navigation scroll highlighting ── */
  function attachDocsNavHighlight() {
    // If a previous scroll handler was attached, clean it up first
    if (window._docsScrollHandler) {
      window.removeEventListener('scroll', window._docsScrollHandler);
      window._docsScrollHandler = null;
    }

    var docsContent = document.querySelector('.docs-content');
    if (!docsContent) return;

    var allLinks = document.querySelectorAll('.docs-nav a[href*="#"], #docsMobileDropdown a[href*="#"]');
    if (!allLinks.length) return;

    // Collect all target section elements in actual DOM order
    var candidateElements = docsContent.querySelectorAll('section[id], .docs-subsection[id]');
    var sections = [];

    candidateElements.forEach(function (el) {
      var id = el.id;
      if (!id) return;
      var matchingLinks = Array.from(document.querySelectorAll(
        '.docs-nav a[href$="#' + id + '"], #docsMobileDropdown a[href$="#' + id + '"]'
      ));
      if (matchingLinks.length) {
        var heading = el.querySelector('h2, h3');
        var title = (matchingLinks[0].textContent || '').replace(/[\n\r]+/g, ' ').replace(/✓/g, '').trim();
        title = title.replace(/\b(Fast|Recommended)\b/gi, '').trim();
        if (!title && heading) {
          title = (heading.textContent || '').trim();
        }
        sections.push({
          id: id,
          el: el,
          title: title,
          links: matchingLinks
        });
      }
    });

    if (!sections.length) return;

    var currentTitleEl = document.querySelector('.docs-mobile-current-title');
    var backToTopBtn = document.getElementById('docsBackToTop');

    function updateActiveSection(activeSection) {
      if (!activeSection) return;
      sections.forEach(function (item) {
        var isActive = (item.id === activeSection.id);
        item.links.forEach(function (link) {
          if (isActive) {
            link.classList.add('active');
          } else {
            link.classList.remove('active');
          }
        });
      });
      if (currentTitleEl) {
        currentTitleEl.textContent = activeSection.title;
      }
    }

    function onScroll() {
      var isBottom = (window.innerHeight + window.scrollY) >= (document.documentElement.scrollHeight - 60);
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
          backToTopBtn.classList.add('visible');
        } else {
          backToTopBtn.classList.remove('visible');
        }
      }
    }

    window._docsScrollHandler = onScroll;
    window.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('resize', onScroll, { passive: true });
    onScroll();

    window._updateActiveDocsSectionById = function (id) {
      var found = sections.find(function (s) { return s.id === id; });
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

    document.addEventListener('click', function (e) {
      var mobileToc = document.getElementById('docsMobileToc');
      if (!mobileToc) return;
      var trigger = document.getElementById('docsMobileTocTrigger');

      // Tapping the dropdown trigger button
      if (trigger && (trigger === e.target || trigger.contains(e.target))) {
        var isOpen = mobileToc.classList.toggle('is-open');
        trigger.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
        return;
      }

      // Tapping any option inside the dropdown immediately closes it and updates active selection
      var link = e.target.closest('#docsMobileDropdown a');
      if (link) {
        var href = link.getAttribute('href') || '';
        var hash = href.split('#')[1];
        if (hash && window._updateActiveDocsSectionById) {
          window._updateActiveDocsSectionById(hash);
        }
        if (mobileToc.classList.contains('is-open')) {
          mobileToc.classList.remove('is-open');
          if (trigger) trigger.setAttribute('aria-expanded', 'false');
        }
        return;
      }

      // Tapping outside closes the dropdown
      if (mobileToc.classList.contains('is-open') && !mobileToc.contains(e.target)) {
        mobileToc.classList.remove('is-open');
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
      }
    });
  }

  /* ── Command and Code Copy Handlers ── */
  let copyHandlerAttached = false;
  function attachCodeCopyButtons() {
    if (copyHandlerAttached) return;
    copyHandlerAttached = true;

    document.addEventListener('click', function (e) {
      var btn = e.target.closest('.btn-code-copy, .btn-inline-copy');
      if (!btn) return;
      var textToCopy = btn.getAttribute('data-copy');
      if (!textToCopy) {
        var pre = btn.closest('.docs-code-block') ? btn.closest('.docs-code-block').querySelector('pre code') : null;
        textToCopy = pre ? pre.innerText.trim() : '';
      }
      if (!textToCopy) return;

      navigator.clipboard.writeText(textToCopy).then(function () {
        btn.classList.add('copied');
        var span = btn.querySelector('span');
        var originalText = span ? span.innerText : '';
        if (span) span.innerText = 'Copied!';
        setTimeout(function () {
          btn.classList.remove('copied');
          if (span) span.innerText = originalText || 'Copy';
        }, 2000);
      }).catch(function (err) {
        console.error('Copy failed', err);
      });
    });
  }

  let comboboxHandlerAttached = false;
  function attachComboboxKeydownHandler() {
    if (comboboxHandlerAttached) return;
    comboboxHandlerAttached = true;
    document.addEventListener("keydown", function (e) {
      if (e.target && e.target.classList && e.target.classList.contains("combobox-input")) {
        if (e.key === "Enter" || e.key === "ArrowDown" || e.key === "ArrowUp") {
          e.preventDefault();
        }
      }
    });
  }

  function init() {
    attachPasswordToggles();
    revealPasskeyButtons();
    attachDocsNavHighlight();
    setupMobileToc();
    attachCodeCopyButtons();
    attachComboboxKeydownHandler();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
  document.addEventListener('blazor:enhancedload', init);

  // Auto-detect when docs content is rendered by client-side Blazor routing
  try {
    var observer = new MutationObserver(function () {
      if (document.querySelector('.docs-content') && !window._docsScrollHandler) {
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
