/*
 * The command palette and the keyboard shortcuts.
 *
 * An external file rather than inline script, because the content security policy refuses
 * inline handlers — deliberately, and this application has already shipped one fault from
 * loosening it. Everything here attaches its listeners in code; there is not an onclick in
 * the markup anywhere.
 *
 * The destinations are read out of the navigation that the server already rendered. That
 * matters more than it looks: the sidebar is built from AuthorizeView blocks, so it
 * contains exactly the pages this person may open — and a palette built from a hard-coded
 * list would offer somebody a page that then refused them, while also going stale the first
 * time a screen was added.
 */
(function () {
    'use strict';

    var overlay = document.querySelector('[data-palette]');
    var input = document.querySelector('[data-palette-input]');
    var list = document.querySelector('[data-palette-list]');

    if (!overlay || !input || !list) {
        return;
    }

    /*
     * Read once, at load. The navigation does not change between renders of one page, and
     * re-reading it on every keystroke would mean a DOM query per character for no gain.
     */
    var destinations = Array.prototype.map
        .call(document.querySelectorAll('.sidebar a[href]'), function (link) {
            return { label: link.textContent.trim(), href: link.getAttribute('href') };
        })
        .filter(function (entry) {
            return entry.label.length > 0;
        });

    var chosen = 0;
    var showing = [];

    /*
     * Where focus was before the palette opened, so it can be put back.
     *
     * Not a nicety. Without it, closing leaves focus on an input inside a hidden dialog:
     * the next slash is read as typing and never reopens the palette, and a keyboard user
     * is left tabbing around inside something they cannot see.
     */
    var cameFrom = null;

    function draw(term) {
        var needle = term.trim().toLowerCase();

        showing = destinations.filter(function (entry) {
            return needle.length === 0 || entry.label.toLowerCase().indexOf(needle) >= 0;
        });

        chosen = 0;
        list.textContent = '';

        if (showing.length === 0) {
            var none = document.createElement('li');
            none.className = 'palette__none';
            none.textContent = 'Nothing here matches that.';
            list.appendChild(none);
            return;
        }

        showing.forEach(function (entry, index) {
            var row = document.createElement('li');
            var link = document.createElement('a');

            link.href = entry.href;
            link.textContent = entry.label;

            row.className = index === 0 ? 'palette__row palette__row--on' : 'palette__row';
            row.appendChild(link);
            list.appendChild(row);
        });
    }

    function highlight() {
        Array.prototype.forEach.call(list.children, function (row, index) {
            row.className = index === chosen ? 'palette__row palette__row--on' : 'palette__row';
        });

        if (list.children[chosen]) {
            list.children[chosen].scrollIntoView({ block: 'nearest' });
        }
    }

    function open() {
        cameFrom = document.activeElement;
        overlay.hidden = false;
        input.value = '';
        draw('');
        input.focus();
    }

    function close() {
        overlay.hidden = true;

        /*
         * Focus goes back where it came from, or to nothing. Leaving it on the hidden input
         * means the next slash is read as typing and the palette cannot be reopened with the
         * keyboard at all — which was found by driving the real script in a browser rather
         * than by any test.
         */
        if (cameFrom && typeof cameFrom.focus === 'function' && cameFrom !== input) {
            cameFrom.focus();
        } else {
            input.blur();
        }

        cameFrom = null;
    }

    /*
     * Ctrl+K, and also the plain slash — which is what every search box on the web has
     * trained people to press. The slash is ignored while a field has focus, because
     * otherwise typing a date into a form opens the palette instead.
     */
    document.addEventListener('keydown', function (event) {
        var typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName);

        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
            event.preventDefault();
            open();
            return;
        }

        if (event.key === '/' && !typing && overlay.hidden) {
            event.preventDefault();
            open();
            return;
        }

        if (overlay.hidden) {
            return;
        }

        if (event.key === 'Escape') {
            event.preventDefault();
            close();
            return;
        }

        if (event.key === 'ArrowDown') {
            event.preventDefault();
            chosen = Math.min(chosen + 1, showing.length - 1);
            highlight();
            return;
        }

        if (event.key === 'ArrowUp') {
            event.preventDefault();
            chosen = Math.max(chosen - 1, 0);
            highlight();
            return;
        }

        if (event.key === 'Enter' && showing[chosen]) {
            event.preventDefault();
            window.location.href = showing[chosen].href;
        }
    });

    input.addEventListener('input', function () {
        draw(input.value);
    });

    Array.prototype.forEach.call(
        document.querySelectorAll('[data-palette-open]'),
        function (button) {
            button.addEventListener('click', function (event) {
                event.preventDefault();
                open();
            });
        });

    /*
     * Clicking the backdrop closes it; clicking the box does not. Without the second half,
     * a click that lands on the padding between two rows dismisses the thing somebody is
     * reading.
     */
    overlay.addEventListener('click', function (event) {
        if (event.target === overlay) {
            close();
        }
    });
})();
