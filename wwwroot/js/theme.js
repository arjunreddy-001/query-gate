(function () {
    var root = document.documentElement;
    var store = {
        get: function (k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
        set: function (k, v) { try { localStorage.setItem(k, v); } catch (e) { } }
    };
    function theme() {
        var t = store.get('dbam-theme');
        if (!t) t = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
        return t;
    }
    function nav() { return store.get('dbam-nav') === 'pinned' ? 'pinned' : 'compact'; }
    function apply() {
        if (root.getAttribute('data-theme') !== theme()) root.setAttribute('data-theme', theme());
        if (root.getAttribute('data-nav') !== nav()) root.setAttribute('data-nav', nav());
    }
    apply();

    // Enhanced navigation to static pages can reset <html> attributes; re-apply the saved state.
    new MutationObserver(apply).observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-nav'] });

    // Inline handlers are blocked by the CSP, so clicks are delegated from the document.
    document.addEventListener('click', function (e) {
        var t = e.target.closest && e.target.closest('[data-theme-toggle],[data-nav-toggle]');
        if (!t) return;
        if (t.hasAttribute('data-theme-toggle')) {
            var next = theme() === 'dark' ? 'light' : 'dark';
            store.set('dbam-theme', next);
            apply();
            if (window.monaco) window.monaco.editor.setTheme(next === 'dark' ? 'vs-dark' : 'vs');
        } else {
            store.set('dbam-nav', nav() === 'pinned' ? 'compact' : 'pinned');
            apply();
            if (document.activeElement && document.activeElement.blur) document.activeElement.blur();
        }
    });

    // Draggable splitter between the SQL editor and the results.
    var drag = null;
    document.addEventListener('mousedown', function (e) {
        var s = e.target.closest && e.target.closest('.ws-split');
        var ed = document.getElementById('sql-editor');
        if (!s || !ed) return;
        drag = { y: e.clientY, h: ed.getBoundingClientRect().height, ed: ed };
        document.body.style.userSelect = 'none';
        e.preventDefault();
    });
    document.addEventListener('mousemove', function (e) {
        if (!drag) return;
        var h = Math.max(80, drag.h + (e.clientY - drag.y));
        drag.ed.style.flex = '0 0 ' + h + 'px';
    });
    document.addEventListener('mouseup', function () {
        if (drag) { drag = null; document.body.style.userSelect = ''; }
    });
})();
