// Series Details (Pages/Series/Details.cshtml). Everything here improves on
// something that already works without script:
//  - a link to something inside a closed disclosure (the "Jump to next" link,
//    a redirect back to #episode-123, the "Rate it" link to #rating) opens the
//    disclosures around it first, so the browser can scroll to it;
//  - marking an unwatched episode first asks whether to mark the earlier
//    unwatched ones too (the row's form marks just this one without script).
(function () {
    function reveal(target) {
        for (var node = target; node; node = node.parentElement) {
            if (node.tagName === 'DETAILS') node.open = true;
        }
    }

    function revealHash() {
        if (!location.hash || location.hash.length < 2) return;
        var target = document.getElementById(decodeURIComponent(location.hash.slice(1)));
        if (!target) return;
        reveal(target);
        target.scrollIntoView({ block: 'start' });
    }

    revealHash();
    window.addEventListener('hashchange', revealHash);

    document.querySelectorAll('a[href^="#"]').forEach(function (link) {
        link.addEventListener('click', function () {
            var target = document.getElementById(link.getAttribute('href').slice(1));
            if (target) reveal(target);
        });
    });

    // "Mark earlier episodes too?" before marking an episode with unwatched ones before it.
    var modalElement = document.getElementById('seriesCatchUpModal');
    if (!modalElement || typeof bootstrap === 'undefined') return;

    var token = document.querySelector('input[name="__RequestVerificationToken"]');

    function copyFields(from, to) {
        ['episodeId', 'Season', 'Anchor'].forEach(function (name) {
            var source = from.querySelector('input[name="' + name + '"]');
            var target = to.querySelector('input[name="' + name + '"]');
            if (source && target) target.value = source.value;
        });
    }

    document.querySelectorAll('form[data-prior-check="true"]').forEach(function (form) {
        form.addEventListener('submit', function (event) {
            event.preventDefault();

            var data = new FormData();
            data.append('episodeId', form.getAttribute('data-episode-id'));
            if (token) data.append('__RequestVerificationToken', token.value);

            fetch('?handler=CheckPriorEpisodes', { method: 'POST', body: data })
                .then(function (response) {
                    if (!response.ok) throw new Error('prior episodes');
                    return response.json();
                })
                .then(function (result) {
                    var count = result.priorUnwatchedCount || 0;
                    if (count === 0) {
                        form.submit();
                        return;
                    }

                    document.getElementById('priorEpisodeCount').textContent = count;
                    document.getElementById('priorEpisodeCountPlural').textContent = count === 1 ? '' : 's';
                    document.getElementById('priorEpisodeCountText').textContent = count === 1 ? 'it' : 'those';
                    copyFields(form, modalElement.querySelector('[data-catch-up-just-this]'));
                    copyFields(form, modalElement.querySelector('[data-catch-up-through]'));
                    bootstrap.Modal.getOrCreateInstance(modalElement).show();
                })
                .catch(function () {
                    form.submit();
                });
        });
    });
})();
