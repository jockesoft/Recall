// Toasts (Pages/Shared/_ToastMessages.cshtml): the countdown that closes them,
// its pausing, Escape, and the smooth reflow of the stack. Loaded by the layout
// on every page, after Bootstrap.
//
// A toast with data-toast-duration (milliseconds, decided on the server by
// ToastDurations) closes itself; one without stays until it is closed. The
// timer is the CSS animation of the toast's countdown bar (.tvdb-toast-timer):
// when it ends, the toast closes. So pausing is animation-play-state, in the
// stylesheet: while the toast is hovered or has focus inside it, and while the
// stack carries data-toast-paused, which this script sets when the tab is
// hidden. With reduced motion the bar is invisible and still keeps the time.
(function () {
    var stack = document.querySelector('.tvdb-toast-stack');
    if (!stack || typeof bootstrap === 'undefined') return;

    var reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

    function toasts() {
        return Array.prototype.slice.call(stack.querySelectorAll('.tvdb-toast'));
    }

    function close(toast) {
        bootstrap.Alert.getOrCreateInstance(toast).close();
    }

    function start(toast) {
        var duration = parseInt(toast.getAttribute('data-toast-duration'), 10);
        var timer = toast.querySelector('.tvdb-toast-timer');
        if (!timer || !(duration > 0)) return;

        timer.addEventListener('animationend', function (event) {
            if (event.animationName === 'tvdb-toast-countdown') close(toast);
        });

        toast.style.setProperty('--tvdb-toast-duration', duration + 'ms');
        toast.setAttribute('data-toast-running', '');
    }

    // Each toast has its own timer; they close independently.
    toasts().forEach(start);

    // A hidden tab reads nothing: every countdown waits until it is visible again.
    function syncVisibility() {
        stack.toggleAttribute('data-toast-paused', document.hidden);
    }

    document.addEventListener('visibilitychange', syncVisibility);
    syncVisibility();

    // Escape closes the toast that has focus, else the newest one. A modal or
    // an open menu gets its Escape first.
    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape' || event.defaultPrevented) return;
        if (document.querySelector('.modal.show, .dropdown-menu.show')) return;

        var open = toasts().filter(function (toast) { return toast.classList.contains('show'); });
        if (open.length === 0) return;

        var focused = document.activeElement && document.activeElement.closest('.tvdb-toast');
        close(focused && open.indexOf(focused) !== -1 ? focused : open[open.length - 1]);
    });

    // When a toast leaves, the others slide to their new place instead of
    // jumping: note where each is just before the removal, and animate from
    // there once it has happened.
    stack.addEventListener('closed.bs.alert', function () {
        if (reducedMotion.matches) return;

        var before = toasts().map(function (toast) {
            return { toast: toast, top: toast.getBoundingClientRect().top };
        });

        requestAnimationFrame(function () {
            before.forEach(function (entry) {
                if (!entry.toast.isConnected || typeof entry.toast.animate !== 'function') return;

                var moved = entry.top - entry.toast.getBoundingClientRect().top;
                if (moved === 0) return;

                entry.toast.animate(
                    [{ transform: 'translateY(' + moved + 'px)' }, { transform: 'none' }],
                    { duration: 200, easing: 'ease-out' });
            });
        });
    });
})();
