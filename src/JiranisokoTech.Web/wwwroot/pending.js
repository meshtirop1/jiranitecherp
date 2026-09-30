// Telling somebody the machine heard them.
//
// THE FAULT THIS EXISTS FOR. This application had no acknowledgement of any kind: no aria-busy
// anywhere, no progress indication anywhere, no @keyframes in app.css, and none of its two
// hundred and fifteen submit buttons changed in any way when pressed. All three ways of acting
// on a page — following a link, posting a form, pressing a button on an interactive island —
// spent the entire round trip showing a screen byte-identical to the one before the click.
//
// From the user's side a working control and a dead one are then indistinguishable, so they
// press again. On several pages the second press writes the record twice.
//
// It is also the specific problem of this rendering model. Pages are statically rendered with
// interactive islands, so a page arrives finished-looking and stays dead until its circuit
// connects — CLAUDE.md records that as a trap that nearly had somebody conclude "interactivity
// does not work in this application". And an enhanced navigation is not a browser navigation, so
// there is not even a tab spinner to fall back on.
//
// THE CONTRACT, which is filters.js's and is stated there with the measurement behind it: every
// listener is delegated from `document` and attached exactly once; nothing here binds to an
// element, because a script that finds its elements at load works on the first page somebody
// opens and silently does nothing on every page they reach by clicking.
(() => {
    'use strict';

    const root = document.documentElement;

    /*
     * Long enough that a hop nobody waited for never flashes a bar, short enough that a wait
     * somebody notices is already acknowledged. Most navigations here are well under this and
     * will show nothing at all, which is the intent: a progress bar on every click is its own
     * kind of noise.
     */
    const BEFORE_SAYING_ANYTHING = 140;

    let waiting = null;

    const startWaiting = () => {
        clearTimeout(waiting);
        waiting = setTimeout(() => root.setAttribute('data-navigating', ''), BEFORE_SAYING_ANYTHING);
    };

    const stopWaiting = () => {
        clearTimeout(waiting);
        waiting = null;
        root.removeAttribute('data-navigating');

        for (const marked of document.querySelectorAll('[data-pending]')) {
            marked.removeAttribute('data-pending');
        }
    };

    /*
     * Blazor's own navigation events. They exist only when blazor.web.js has started, and it is
     * loaded before this file — but a page that failed to load it must still work, so this is
     * guarded rather than assumed.
     */
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancednavigationstart', startWaiting);
        window.Blazor.addEventListener('enhancedload', stopWaiting);
    }

    /*
     * The link that was clicked says so itself, which is the part a bar at the top of the screen
     * cannot do: it answers "did it take my click" at the place the eye already is.
     *
     * Delegated from the document and matched on the closest anchor, so it covers the sidebar,
     * a breadcrumb and a link in a table without knowing about any of them.
     *
     * IN THE CAPTURE PHASE, AND CHECKING NOTHING ABOUT defaultPrevented. Both halves of that were
     * found by driving a real click and watching, rather than by reading this file back.
     *
     * blazor.web.js registers its own click listener on the document when it starts, which is
     * before this file runs, and enhanced navigation works by calling preventDefault on the
     * anchor's click and fetching the page itself. So a bubbling listener here runs second and
     * sees defaultPrevented already true: the first version returned early on exactly the clicks
     * it existed to acknowledge — every internal link in the application — and read as completely
     * correct while doing it. The measurement said "linkMarkedAfterMs: never".
     *
     * Capture runs before any bubbling listener whatever the registration order. Nothing here
     * prevents anything; it marks the link and lets Blazor do the navigating.
     */
    document.addEventListener('click', event => {
        const link = event.target.closest('a[href]');

        if (link === null || event.button !== 0) {
            return;
        }

        // Modified clicks open a tab and leave this page alone; a download or a new target is
        // not a navigation of this document either.
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return;
        }

        if (link.target !== '' && link.target !== '_self') {
            return;
        }

        if (link.hasAttribute('download') || link.getAttribute('href').startsWith('#')) {
            return;
        }

        // Somewhere else entirely: the browser shows its own progress for that.
        if (link.origin !== window.location.origin) {
            return;
        }

        link.setAttribute('data-pending', '');
        startWaiting();
    }, true);

    /*
     * A form that has been submitted, on the button that submitted it.
     *
     * Disabled inside a timeout rather than immediately, and that is load-bearing: a disabled
     * control is not successful, so disabling it during the submit event removes its name and
     * value from what gets posted — which on a page with several forms is how the server stops
     * being told WHICH button was pressed. The timeout lands after serialisation.
     *
     * Capture and no defaultPrevented check, for the reason the click listener above gives: an
     * interactive EditForm's submit is handled by Blazor, which prevents the default — and a
     * button that shows nothing on the forms somebody uses most would be the same fault twice.
     *
     * The label is swapped rather than a spinner added, because this file appends nothing to the
     * DOM. Blazor's scoped CSS rewrites selectors against an attribute the compiler puts on
     * elements a component rendered, so anything invented here would be unstyled — the rule
     * filters.js states and follows.
     */
    document.addEventListener('submit', event => {
        const form = event.target;

        if (!(form instanceof HTMLFormElement)) {
            return;
        }

        const pressed = event.submitter;

        if (!(pressed instanceof HTMLButtonElement) || pressed.disabled) {
            return;
        }

        form.setAttribute('aria-busy', 'true');
        pressed.setAttribute('data-pending', '');

        if (pressed.dataset.saying) {
            pressed.dataset.was = pressed.textContent.trim();
            pressed.textContent = pressed.dataset.saying;
        }

        setTimeout(() => {
            pressed.disabled = true;
        }, 0);

        /*
         * And undone if the page is still here afterwards. A static form POST replaces the
         * document, so this usually never runs; an interactive form's submit does not, and a
         * button left disabled for ever on a page somebody stays on is worse than no feedback at
         * all. Also covers the browser restoring this page from its back-forward cache.
         */
        const release = () => {
            pressed.disabled = false;
            pressed.removeAttribute('data-pending');
            form.removeAttribute('aria-busy');

            if (pressed.dataset.was) {
                pressed.textContent = pressed.dataset.was;
                delete pressed.dataset.was;
            }
        };

        window.addEventListener('pageshow', release, { once: true });

        if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
            window.Blazor.addEventListener('enhancedload', release);
        }
    }, true);

    // An island finishing its handshake is the moment a finished-looking page becomes a working
    // one. Said once, on the root, so app.css can stop dimming what cannot yet be pressed.
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', () => root.removeAttribute('data-navigating'));
    }
})();
