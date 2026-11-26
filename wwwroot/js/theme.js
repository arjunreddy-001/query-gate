(function () {
    var root = document.documentElement;
    var store = {
        get: function (k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
        set: function (k, v) { try { localStorage.setItem(k, v); } catch (e) { } }
    };
    function theme() {
        var t = store.get('dbam-theme');
        if (t === 'dark' || t === 'light') return t;
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }
    function nav() { return store.get('dbam-nav') === 'pinned' ? 'pinned' : 'compact'; }
    function syncMonaco(t) {
        if (window.monaco && window.monaco.editor) {
            window.monaco.editor.setTheme(t === 'dark' ? 'vs-dark' : 'vs');
        }
    }
    function apply() {
        var t = theme();
        if (root.getAttribute('data-theme') !== t) root.setAttribute('data-theme', t);
        if (root.getAttribute('data-nav') !== nav()) root.setAttribute('data-nav', nav());
    }
    function setTheme(next) {
        var normalized = next === 'dark' ? 'dark' : 'light';
        store.set('dbam-theme', normalized);
        apply();
        syncMonaco(normalized);
        return normalized;
    }
    function toggleTheme() {
        return setTheme(theme() === 'dark' ? 'light' : 'dark');
    }
    window.dbamTheme = {
        get: theme,
        set: setTheme,
        toggle: toggleTheme
    };
    apply();

    // Enhanced navigation to static pages can reset <html> attributes; re-apply the saved state.
    new MutationObserver(apply).observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-nav'] });

    if (window.matchMedia) {
        var mq = window.matchMedia('(prefers-color-scheme: dark)');
        var onSystemThemeChange = function () {
            if (!store.get('dbam-theme')) {
                apply();
                syncMonaco(theme());
            }
        };
        if (mq.addEventListener) mq.addEventListener('change', onSystemThemeChange);
        else if (mq.addListener) mq.addListener(onSystemThemeChange);
    }

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
        var exportMenuBtn = e.target.closest && e.target.closest('.export-dropdown > button');
        if (exportMenuBtn) {
            var dd = exportMenuBtn.closest('.export-dropdown');
            var willOpen = dd && !dd.classList.contains('is-open');
            document.querySelectorAll('.export-dropdown.is-open').forEach(function (el) {
                el.classList.remove('is-open');
            });
            if (dd && willOpen) dd.classList.add('is-open');
            return;
        }
        var exportItem = e.target.closest && e.target.closest('.export-dropdown-item');
        if (exportItem || !(e.target.closest && e.target.closest('.export-dropdown'))) {
            document.querySelectorAll('.export-dropdown.is-open').forEach(function (el) {
                el.classList.remove('is-open');
            });
        }

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
            toggleTheme();
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
    function startSplitDrag(s, clientX, clientY) {
        var pane = s.closest('.ws-body') ? s.closest('.ws-body').querySelector('.ws-editor-pane') : document.getElementById('sql-editor');
        if (!pane) return false;
        var isHoriz = !!s.closest('.ws-body-horizontal');
        drag = {
            isHoriz: isHoriz,
            x: clientX,
            y: clientY,
            w: pane.getBoundingClientRect().width,
            h: pane.getBoundingClientRect().height,
            pane: pane,
            moved: false
        };
        document.body.style.userSelect = 'none';
        return true;
    }
    function moveSplitDrag(clientX, clientY) {
        if (!drag) return;
        if (!drag.moved && Math.abs(clientX - drag.x) < 2 && Math.abs(clientY - drag.y) < 2) return;
        drag.moved = true;
        if (drag.isHoriz) {
            var parentW = drag.pane.parentElement ? drag.pane.parentElement.getBoundingClientRect().width : 800;
            var maxW = Math.max(160, parentW - 160);
            var w = Math.min(maxW, Math.max(160, drag.w + (clientX - drag.x)));
            drag.pane.dataset.hFlex = w + 'px';
            drag.pane.style.flex = '0 0 ' + w + 'px';
        } else {
            var parentH = drag.pane.parentElement ? drag.pane.parentElement.getBoundingClientRect().height : 600;
            var maxH = Math.max(80, parentH - 100);
            var h = Math.min(maxH, Math.max(80, drag.h + (clientY - drag.y)));
            drag.pane.dataset.vFlex = h + 'px';
            drag.pane.style.flex = '0 0 ' + h + 'px';
        }
    }
    function endSplitDrag() {
        if (drag) {
            var didMove = drag.moved;
            drag = null;
            document.body.style.userSelect = '';
            if (didMove && window.dbam && window.dbam.editorLayout) {
                window.dbam.editorLayout('main');
            }
        }
    }
    document.addEventListener('mousedown', function (e) {
        var s = e.target.closest && e.target.closest('.ws-split');
        if (s && startSplitDrag(s, e.clientX, e.clientY)) e.preventDefault();
    });
    document.addEventListener('mousemove', function (e) {
        moveSplitDrag(e.clientX, e.clientY);
    });
    document.addEventListener('mouseup', endSplitDrag);

    document.addEventListener('touchstart', function (e) {
        var s = e.target.closest && e.target.closest('.ws-split');
        if (!s || !e.touches || !e.touches.length) return;
        if (startSplitDrag(s, e.touches[0].clientX, e.touches[0].clientY)) e.preventDefault();
    }, { passive: false });
    document.addEventListener('touchmove', function (e) {
        if (!drag || !e.touches || !e.touches.length) return;
        e.preventDefault();
        moveSplitDrag(e.touches[0].clientX, e.touches[0].clientY);
    }, { passive: false });
    document.addEventListener('touchend', endSplitDrag);
    document.addEventListener('touchcancel', endSplitDrag);
    document.addEventListener('dblclick', function (e) {
        var s = e.target.closest && e.target.closest('.ws-split');
        if (!s) return;
        e.preventDefault();
        drag = null;
        document.body.style.userSelect = '';
        if (window.dbam && window.dbam.toggleResults) {
            window.dbam.toggleResults();
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
