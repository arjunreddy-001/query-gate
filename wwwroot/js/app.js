window.dbam = (function () {
    const MONACO_BASE = '/_content/BlazorMonaco/lib/monaco-editor/min/vs';
    let monacoPromise = null;
    let metadata = [];
    let providerRegistered = false;

    function loadScript(src) {
        return new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = src; s.onload = resolve; s.onerror = reject;
            document.head.appendChild(s);
        });
    }

    function loadMonaco() {
        if (monacoPromise) return monacoPromise;
        monacoPromise = (async () => {
            await loadScript(MONACO_BASE + '/loader.js');
            window.require.config({ paths: { vs: MONACO_BASE } });
            window.MonacoEnvironment = {
                getWorkerUrl: function () {
                    const code = "self.MonacoEnvironment={baseUrl:'" + location.origin + MONACO_BASE.replace('/vs', '') + "/'};importScripts('" + location.origin + MONACO_BASE + "/base/worker/workerMain.js');";
                    return 'data:text/javascript;charset=utf-8,' + encodeURIComponent(code);
                }
            };
            await new Promise((resolve, reject) => window.require(['vs/editor/editor.main'], resolve, reject));
            return window.monaco;
        })();
        return monacoPromise;
    }

    const KEYWORDS = ['SELECT', 'FROM', 'WHERE', 'GROUP BY', 'ORDER BY', 'HAVING', 'JOIN', 'INNER JOIN', 'LEFT JOIN', 'RIGHT JOIN', 'FULL JOIN', 'ON', 'AS', 'AND', 'OR', 'NOT', 'IN', 'EXISTS', 'LIKE', 'BETWEEN', 'IS NULL', 'IS NOT NULL', 'TOP', 'DISTINCT', 'INSERT INTO', 'VALUES', 'UPDATE', 'SET', 'DELETE FROM', 'CASE', 'WHEN', 'THEN', 'ELSE', 'END', 'UNION ALL', 'COUNT', 'SUM', 'AVG', 'MIN', 'MAX', 'CAST', 'CONVERT', 'ISNULL', 'COALESCE', 'GETDATE', 'OFFSET', 'FETCH NEXT', 'WITH', 'EXEC', 'DECLARE', 'BEGIN', 'COMMIT', 'ROLLBACK'];

    function aliasMap(text) {
        const map = {};
        const re = /\b(?:from|join)\s+((?:\[?\w+\]?\.)?\[?\w+\]?)(?:\s+(?:as\s+)?(\[?\w+\]?))?/gi;
        let m;
        const reserved = new Set(['where', 'on', 'inner', 'left', 'right', 'full', 'cross', 'join', 'group', 'order', 'having', 'union', 'set', 'with']);
        while ((m = re.exec(text)) !== null) {
            const table = m[1].replace(/[\[\]]/g, '').toLowerCase();
            const alias = m[2] ? m[2].replace(/[\[\]]/g, '').toLowerCase() : null;
            if (alias && !reserved.has(alias)) map[alias] = table;
            map[table.split('.').pop()] = table;
        }
        return map;
    }

    function registerProvider(monaco) {
        if (providerRegistered) return;
        providerRegistered = true;
        monaco.languages.registerCompletionItemProvider('sql', {
            triggerCharacters: ['.', ' '],
            provideCompletionItems: function (model, position) {
                const word = model.getWordUntilPosition(position);
                const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn };
                const line = model.getLineContent(position.lineNumber).substring(0, position.column - 1);
                const K = monaco.languages.CompletionItemKind;
                const dot = /([\w\[\]]+)\.\s*\w*$/.exec(line);
                if (dot) {
                    const key = dot[1].replace(/[\[\]]/g, '').toLowerCase();
                    const full = aliasMap(model.getValue())[key] || key;
                    const hit = metadata.find(o => (o.schema + '.' + o.name).toLowerCase() === full || o.name.toLowerCase() === full);
                    if (hit) return { suggestions: hit.columns.map(c => ({ label: c, kind: K.Field, insertText: c, range })) };
                    const schemaObjs = metadata.filter(o => o.schema.toLowerCase() === key);
                    return { suggestions: schemaObjs.map(o => ({ label: o.name, kind: K.Class, insertText: o.name, detail: o.type, range })) };
                }
                const suggestions = KEYWORDS.map(k => ({ label: k, kind: K.Keyword, insertText: k, range }));
                metadata.forEach(o => suggestions.push({
                    label: o.schema + '.' + o.name, kind: o.type === 'Table' ? K.Class : (o.type === 'View' ? K.Interface : K.Function),
                    insertText: o.schema + '.' + o.name, detail: o.type, range
                }));
                return { suggestions };
            }
        });
    }

    const editors = {};

    return {
        getTheme: () => document.documentElement.getAttribute('data-theme') || 'light',
        toggleTheme: function () {
            const next = this.getTheme() === 'dark' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-theme', next);
            try { localStorage.setItem('dbam-theme', next); } catch (e) { }
            if (window.monaco) window.monaco.editor.setTheme(next === 'dark' ? 'vs-dark' : 'vs');
            return next;
        },
        download: function (name, content, mime) {
            const blob = new Blob(['\ufeff' + content], { type: mime || 'text/csv;charset=utf-8' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        },
        copy: (text) => navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.resolve(),
        submitForm: (id) => document.getElementById(id)?.submit(),

        startIdle: function (ref, warnMs, expireMs) {
            let warned = false, last = Date.now();
            const reset = () => { last = Date.now(); if (warned) { warned = false; ref.invokeMethodAsync('OnIdleReset'); } };
            ['mousemove', 'keydown', 'click', 'scroll'].forEach(e => window.addEventListener(e, reset, { passive: true }));
            setInterval(() => {
                const idle = Date.now() - last;
                if (idle > expireMs) ref.invokeMethodAsync('OnIdleExpired');
                else if (idle > warnMs && !warned) { warned = true; ref.invokeMethodAsync('OnIdleWarning', Math.round((expireMs - idle) / 60000)); }
            }, 15000);
        },

        editorInit: async function (id, elementId, ref, value) {
            const el = document.getElementById(elementId);
            if (!el) return false;
            try {
                const monaco = await loadMonaco();
                registerProvider(monaco);
                el.innerHTML = '';
                const editor = monaco.editor.create(el, {
                    value: value || '', language: 'sql', theme: this.getTheme() === 'dark' ? 'vs-dark' : 'vs', automaticLayout: true,
                    minimap: { enabled: false }, fontSize: 14, fontFamily: "'Cascadia Code', Consolas, monospace", fontLigatures: true,
                    scrollBeyondLastLine: false, padding: { top: 8 }, smoothScrolling: true, renderLineHighlight: 'all', tabSize: 4, fixedOverflowWidgets: true
                });
                editor.addCommand(monaco.KeyCode.F5, () => ref.invokeMethodAsync('OnRun'));
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => ref.invokeMethodAsync('OnRun'));
                editor.addCommand(monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF, () => ref.invokeMethodAsync('OnFormat'));
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => ref.invokeMethodAsync('OnSave'));
                editor.onDidChangeModelContent(() => ref.invokeMethodAsync('OnChanged', editor.getValue()));
                editors[id] = { kind: 'monaco', editor };
                return true;
            } catch (e) {
                console.warn('Monaco unavailable, using plain editor', e);
                el.innerHTML = '';
                const ta = document.createElement('textarea');
                ta.className = 'fallback'; ta.value = value || ''; ta.spellcheck = false;
                ta.addEventListener('input', () => ref.invokeMethodAsync('OnChanged', ta.value));
                ta.addEventListener('keydown', (ev) => {
                    if (ev.key === 'F5' || (ev.ctrlKey && ev.key === 'Enter')) { ev.preventDefault(); ref.invokeMethodAsync('OnRun'); }
                });
                el.appendChild(ta);
                editors[id] = { kind: 'plain', ta };
                return false;
            }
        },
        editorGet: function (id) { const e = editors[id]; return !e ? '' : e.kind === 'monaco' ? e.editor.getValue() : e.ta.value; },
        editorSelection: function (id) {
            const e = editors[id]; if (!e) return '';
            if (e.kind === 'monaco') { const sel = e.editor.getSelection(); return sel && !sel.isEmpty() ? e.editor.getModel().getValueInRange(sel) : ''; }
            return e.ta.value.substring(e.ta.selectionStart, e.ta.selectionEnd);
        },
        editorSet: function (id, value) { const e = editors[id]; if (!e) return; if (e.kind === 'monaco') e.editor.setValue(value); else e.ta.value = value; },
        editorMarker: function (id, line, message) {
            const e = editors[id]; if (!e || e.kind !== 'monaco' || !window.monaco) return;
            const model = e.editor.getModel();
            window.monaco.editor.setModelMarkers(model, 'dbam', line ? [{
                severity: window.monaco.MarkerSeverity.Error, message, startLineNumber: line, endLineNumber: line, startColumn: 1, endColumn: model.getLineMaxColumn(Math.min(line, model.getLineCount()))
            }] : []);
        },
        editorDispose: function (id) { const e = editors[id]; if (e && e.kind === 'monaco') e.editor.dispose(); delete editors[id]; },
        setMetadata: function (objects) { metadata = objects || []; }
    };
})();
