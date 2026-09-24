/*
 * Narrowing a list without asking the server, and without a button.
 *
 * The complaint this answers, in the owner's words, is that the application is "select
 * everything and press a button". It is: People has Search, Department, Status and a Filter
 * button; Applications has Advert, State and a Show button; Holidays has a year and a Show
 * button. Every one of those is a full page load to look at fewer rows that were already on
 * the screen.
 *
 * ONE FILE RATHER THAN ONE PER PAGE. Six screens want the same dozen lines, and six copies is
 * five chances for them to drift. Everything here is driven by data- attributes that the Razor
 * writes, so a page opts in by describing itself rather than by importing anything.
 *
 * WHY THIS IS NOT A BLAZOR COMPONENT. An interactive component can only narrow rows it
 * rendered itself, so sharing one across People, Clients, Applications and the board would mean
 * making all four interactive — four circuits, four sets of state to lose on a dropped
 * connection — to hide rows that are already in the browser. There is nothing here a server
 * needs to be asked about.
 *
 * THE MECHANISM THAT DECIDES WHETHER THIS WORKS AT ALL, measured in Chrome against the running
 * container rather than assumed. Following a link in the sidebar is an ENHANCED navigation:
 * DOMContentLoaded fired zero times, Blazor's 'enhancedload' fired once, window globals
 * survived, and the navigation element was the same DOM object afterwards — blazor.web.js
 * patches the document rather than replacing it. So a script that found its fields at
 * DOMContentLoaded and bound to them would work on the first page somebody opened and do
 * nothing, silently, on every page they reached by clicking. nav.js survives that only by
 * accident, because it delegates from an element the diff happens to reuse.
 *
 * Hence the contract below: every listener is delegated from `document` and attached once; the
 * first paint runs on DOMContentLoaded AND on every 'enhancedload'; and painting is idempotent,
 * because it writes into elements the server rendered and never appends its own.
 *
 * WHAT IT DOES NOT DO. It adds no elements and invents no class names. Blazor's scoped CSS
 * rewrites selectors against an attribute the compiler puts on elements a component rendered,
 * so a class invented here could never be styled from a .razor.css — and a class invented here
 * is also invisible to StylesheetTests, which is how an unstyled page ships. It sets `hidden`,
 * `disabled`, `aria-pressed` and text content, and nothing else.
 */
(function () {
    'use strict';

    /*
     * Matching is one lowercased substring, deliberately, because that is exactly what the
     * server does — the searches behind these screens are EF.Functions.Like(…, "%term%"). A
     * cleverer match here would make the narrowed view disagree with what pressing the button
     * returns, and a preview that lies about the real answer is worse than no preview.
     */
    function matches(row, needle) {
        if (needle === '') {
            return true;
        }

        var text = row.getAttribute('data-narrow-text');

        if (text === null) {
            text = row.textContent;
        }

        return text.toLowerCase().indexOf(needle) !== -1;
    }

    function rowsFor(id) {
        return Array.prototype.slice.call(
            document.querySelectorAll('[data-narrow-row="' + id + '"]'));
    }

    function controlsFor(id) {
        return Array.prototype.slice.call(
            document.querySelectorAll('[data-narrow="' + id + '"]'));
    }

    /*
     * Read per paint rather than cached when the script loads. Enhanced navigation replaces the
     * rows with new nodes, so anything held from an earlier page points at detached elements
     * that will never be seen again — and the symptom of that is a filter that quietly stops
     * working after the first click, which is the hardest kind of fault to be told about.
     */
    function paint(id) {
        var needle = '';
        var wanted = [];

        controlsFor(id).forEach(function (control) {
            if (control.matches('input[type="search"], input[type="text"]')) {
                needle = control.value.trim().toLowerCase();
            }

            if (control.matches('[data-narrow-toggle]')
                && control.getAttribute('aria-pressed') === 'true') {
                wanted.push(control.getAttribute('data-narrow-toggle'));
            }
        });

        var showing = 0;

        rowsFor(id).forEach(function (row) {
            var flags = (row.getAttribute('data-narrow-flag') || '').split(/\s+/);

            var show = matches(row, needle) && wanted.every(function (flag) {
                return flags.indexOf(flag) !== -1;
            });

            if (show) {
                showing++;
            }

            if (row.hidden !== !show) {
                row.hidden = !show;
            }

            /*
             * A hidden row's controls are disabled as well as hidden. On the timesheet queue a
             * hidden row still carries a ticked checkbox, and a form posts what is in the DOM
             * rather than what is on the screen — so without this, narrowing to one person and
             * pressing Approve would approve everybody who happened to be ticked before.
             */
            Array.prototype.forEach.call(
                row.querySelectorAll('input, button, select, textarea'),
                function (field) {
                    field.disabled = !show;
                });
        });

        Array.prototype.forEach.call(
            document.querySelectorAll('[data-narrow-count="' + id + '"]'),
            function (element) {
                var of = element.getAttribute('data-narrow-of');

                element.textContent = of === null
                    ? String(showing)
                    : showing + ' of ' + of;
            });
    }

    function paintAll() {
        var seen = {};

        Array.prototype.forEach.call(
            document.querySelectorAll('[data-narrow]'),
            function (control) {
                var id = control.getAttribute('data-narrow');

                if (!seen[id]) {
                    seen[id] = true;
                    paint(id);
                }
            });
    }

    // ------------------------------------------------------------------ listeners

    document.addEventListener('input', function (event) {
        var id = event.target.getAttribute
            && event.target.getAttribute('data-narrow');

        if (id) {
            paint(id);
        }
    });

    document.addEventListener('click', function (event) {
        var toggle = event.target.closest && event.target.closest('[data-narrow-toggle]');

        if (!toggle) {
            return;
        }

        toggle.setAttribute(
            'aria-pressed',
            toggle.getAttribute('aria-pressed') === 'true' ? 'false' : 'true');

        paint(toggle.getAttribute('data-narrow'));
    });

    /*
     * A dropdown inside a filter form submits it, so the Show button stops being something
     * somebody has to find and press and becomes what it should always have been: the thing
     * that still works when this file does not.
     *
     * Selects only. A text box would submit on every keystroke, and a date field fires change
     * while somebody is still typing the year — a form that reloads halfway through a date is
     * worse than one with a button on it.
     */
    document.addEventListener('change', function (event) {
        var field = event.target;

        if (!field.matches || !field.matches('select')) {
            return;
        }

        var form = field.closest('form[data-narrow-submit]');

        if (form) {
            form.requestSubmit();
        }
    });

    /*
     * Enter in a narrowing box does nothing, rather than submitting the form it sits in. The
     * list on the screen is already the answer; reloading the page to be shown the same rows
     * again is the round trip this whole file exists to remove.
     */
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Enter'
            && event.target.getAttribute
            && event.target.getAttribute('data-narrow')
            && event.target.matches('input[type="search"], input[type="text"]')) {
            event.preventDefault();
            paint(event.target.getAttribute('data-narrow'));
        }
    });

    // First paint on a fresh document, and again on every page reached by a link.
    document.addEventListener('DOMContentLoaded', paintAll);

    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', paintAll);
    }
})();
