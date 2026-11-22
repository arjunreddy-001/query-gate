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

    function closeMobileNav() {
        root.removeAttribute('data-mobile-nav');
        var sidebar = document.getElementById('app-navigation');
        var backdrop = document.querySelector('.mobile-nav-backdrop');
        var toggles = document.querySelectorAll('[data-mobile-nav-toggle]');
        if (sidebar) sidebar.classList.remove('mobile-open');
        if (backdrop) backdrop.classList.remove('mobile-open');
        toggles.forEach(function (btn) { btn.setAttribute('aria-expanded', 'false'); });
    }

    function toggleMobileNav() {
        var isOpen = root.getAttribute('data-mobile-nav') === 'open';
        var next = !isOpen;
        if (next) {
            root.setAttribute('data-mobile-nav', 'open');
        } else {
            root.removeAttribute('data-mobile-nav');
        }
        var sidebar = document.getElementById('app-navigation');
        var backdrop = document.querySelector('.mobile-nav-backdrop');
        var toggles = document.querySelectorAll('[data-mobile-nav-toggle]');
        if (sidebar) sidebar.classList.toggle('mobile-open', next);
        if (backdrop) backdrop.classList.toggle('mobile-open', next);
        toggles.forEach(function (btn) { btn.setAttribute('aria-expanded', next ? 'true' : 'false'); });
    }

    // Inline handlers are blocked by the CSP, so clicks are delegated from the document.
    document.addEventListener('click', function (e) {
        var navToggle = e.target.closest && e.target.closest('[data-mobile-nav-toggle]');
        if (navToggle) {
            toggleMobileNav();
            return;
        }

        var navClose = e.target.closest && e.target.closest('[data-mobile-nav-close],.mobile-nav-backdrop');
        if (navClose) {
            closeMobileNav();
            return;
        }

        var navLink = e.target.closest && e.target.closest('#app-navigation nav a');
        if (navLink) {
            closeMobileNav();
        }

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

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
            closeMobileNav();
        }
    });

    document.addEventListener('enhancedload', closeMobileNav);
    window.addEventListener('popstate', closeMobileNav);

    // Draggable splitter between the SQL editor and the results.
    var drag = null;
    document.addEventListener('mousedown', function (e) {
        var s = e.target.closest && e.target.closest('.ws-split');
        if (!s) return;
        var pane = s.closest('.ws-body') ? s.closest('.ws-body').querySelector('.ws-editor-pane') : document.getElementById('sql-editor');
        if (!pane) return;
        var isHoriz = !!s.closest('.ws-body-horizontal');
        drag = {
            isHoriz: isHoriz,
            x: e.clientX,
            y: e.clientY,
            w: pane.getBoundingClientRect().width,
            h: pane.getBoundingClientRect().height,
            pane: pane
        };
        document.body.style.userSelect = 'none';
        e.preventDefault();
    });
    document.addEventListener('mousemove', function (e) {
        if (!drag) return;
        if (drag.isHoriz) {
            var w = Math.max(160, drag.w + (e.clientX - drag.x));
            drag.pane.style.flex = '0 0 ' + w + 'px';
        } else {
            var h = Math.max(80, drag.h + (e.clientY - drag.y));
            drag.pane.style.flex = '0 0 ' + h + 'px';
        }
    });
    document.addEventListener('mouseup', function () {
        if (drag) { drag = null; document.body.style.userSelect = ''; }
    });
})();
