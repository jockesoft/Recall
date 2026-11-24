// Dashboard, "Continue watching" (Pages/Dashboard.cshtml). Every series is on
// the page; the stylesheet shows the first nine until the list has
// .is-expanded. Without script, "Show all N" is a GET form to ?all=true, and
// the server renders the list expanded. With script it toggles in place, keeps
// the address in step (so a reload keeps the state), and keeps the hidden
// "all" field of every mark and stop form in step, so the page comes back the
// way it was after a POST.
(function () {
    var list = document.querySelector('[data-catchup]');
    var form = document.querySelector('[data-catchup-toggle-form]');

    if (list && form) {
        var toggle = form.querySelector('button');

        form.addEventListener('submit', function (event) {
            event.preventDefault();
            var expanded = list.classList.toggle('is-expanded');
            toggle.setAttribute('aria-expanded', expanded ? 'true' : 'false');
            toggle.textContent = expanded ? toggle.dataset.less : toggle.dataset.more;

            list.querySelectorAll('input[data-catchup-all]').forEach(function (input) {
                input.value = expanded ? 'true' : 'false';
            });

            try {
                var url = new URL(window.location.href);
                if (expanded) {
                    url.searchParams.set('all', 'true');
                } else {
                    url.searchParams.delete('all');
                }
                url.hash = '';
                window.history.replaceState(window.history.state, '', url.toString());
            } catch (e) {
                // The address is a convenience; the toggle works without it.
            }

            if (!expanded) {
                toggle.scrollIntoView({ block: 'nearest' });
            }
        });
    }

    // After a mark the page returns at the card or row that was marked. A
    // marked series has just been watched, so it moves up the queue: on a
    // phone it may now be the card where it was a row (or, at another width,
    // the other way round), and the address names one that is gone or not
    // drawn. Then land on the series' other one; for a series that has just
    // been caught up and left the list, at the section.
    var match = /^#catchup-(card|row)-(\d+)$/.exec(window.location.hash);
    if (match) {
        var drawn = function (el) { return el && el.getClientRects().length > 0; };
        var named = document.getElementById(match[0].slice(1));
        if (!drawn(named)) {
            var other = document.getElementById('catchup-' + (match[1] === 'card' ? 'row' : 'card') + '-' + match[2]);
            var target = drawn(other) ? other : document.getElementById('continueWatching');
            if (target) target.scrollIntoView({ block: drawn(other) ? 'center' : 'start' });
        }
    }
})();
