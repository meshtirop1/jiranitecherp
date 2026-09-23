// Closing the navigation drawer when a link inside it is followed.
//
// This was an inline onclick attribute on the drawer, which came from the
// project template. Inline handlers are exactly what a script-src content
// security policy exists to refuse, so it lives in a file: the policy this
// application ships does not restrict scripts yet, and leaving the handler
// inline would have meant it could never start.
//
// Delegated from the drawer rather than bound to each link, because the links
// are rendered per permission and change as somebody's roles do.
document.addEventListener('DOMContentLoaded', () => {
    const drawer = document.querySelector('.nav-scrollable');
    const toggler = document.querySelector('.navbar-toggler');

    if (drawer === null || toggler === null) {
        return;
    }

    drawer.addEventListener('click', event => {
        if (event.target.closest('a') !== null) {
            toggler.click();
        }
    });
});
