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

    function closeTopbarDropdowns() {
        var openDropdowns = document.querySelectorAll('details.topbar-workspace[open], details.topbar-user[open]');
        openDropdowns.forEach(function (d) {
            d.removeAttribute('open');
        });
    }

    function handleTopbarDropdownOutside(e) {
        var openDropdowns = document.querySelectorAll('details.topbar-workspace[open], details.topbar-user[open]');
        if (!openDropdowns.length) return;
        var target = e.target;
        if (target && target.nodeType === 3) target = target.parentNode;
        openDropdowns.forEach(function (d) {
            var summary = d.querySelector('summary');
            if (summary && summary.contains(target)) {
                return;
            }
            if (!d.contains(target) || (target.closest && target.closest('.topbar-menu a, .topbar-menu button'))) {
                d.removeAttribute('open');
            }
        });
    }

    document.addEventListener('click', handleTopbarDropdownOutside, true);
    document.addEventListener('pointerdown', function (e) {
        var openDropdowns = document.querySelectorAll('details.topbar-workspace[open], details.topbar-user[open]');
        if (!openDropdowns.length) return;
        var target = e.target;
        if (target && target.nodeType === 3) target = target.parentNode;
        openDropdowns.forEach(function (d) {
            var summary = d.querySelector('summary');
            if (summary && summary.contains(target)) {
                return;
            }
            if (!d.contains(target)) {
                d.removeAttribute('open');
            }
        });
    }, true);

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
            closeTopbarDropdowns();
        }
    });

    document.addEventListener('enhancedload', function () {
        closeMobileNav();
        closeTopbarDropdowns();
    });
    window.addEventListener('popstate', function () {
        closeMobileNav();
        closeTopbarDropdowns();
    });

    // Detect OS for keyboard shortcuts (mac vs other)
    var isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad|iPod/.test(navigator.platform || navigator.userAgent);
    if (isMac) root.setAttribute('data-os', 'mac');
    else root.setAttribute('data-os', 'other');

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
            var parentW = drag.pane.parentElement ? drag.pane.parentElement.getBoundingClientRect().width : 800;
            var maxW = Math.max(160, parentW - 160);
            var w = Math.min(maxW, Math.max(160, drag.w + (e.clientX - drag.x)));
            drag.pane.dataset.hFlex = w + 'px';
            drag.pane.style.flex = '0 0 ' + w + 'px';
        } else {
            var parentH = drag.pane.parentElement ? drag.pane.parentElement.getBoundingClientRect().height : 600;
            var maxH = Math.max(80, parentH - 100);
            var h = Math.min(maxH, Math.max(80, drag.h + (e.clientY - drag.y)));
            drag.pane.dataset.vFlex = h + 'px';
            drag.pane.style.flex = '0 0 ' + h + 'px';
        }
    });
    document.addEventListener('mouseup', function () {
        if (drag) {
            drag = null;
            document.body.style.userSelect = '';
            if (window.dbam && window.dbam.editorLayout) {
                window.dbam.editorLayout('main');
            }
        }
    });

    // Keep split layout flex-basis synced when orientation toggles
    var lastHorizState = null;
    var splitLayoutObserver = new MutationObserver(function () {
        var body = document.querySelector('.ws-body');
        if (!body) return;
        var isHoriz = body.classList.contains('ws-body-horizontal');
        if (lastHorizState !== isHoriz) {
            lastHorizState = isHoriz;
            var pane = body.querySelector('.ws-editor-pane');
            if (pane) {
                if (isHoriz) {
                    pane.style.flex = pane.dataset.hFlex ? ('0 0 ' + pane.dataset.hFlex) : '';
                } else {
                    pane.style.flex = pane.dataset.vFlex ? ('0 0 ' + pane.dataset.vFlex) : '';
                }
            }
        }
    });
    splitLayoutObserver.observe(root, { subtree: true, attributes: true, attributeFilter: ['class'] });
})();
