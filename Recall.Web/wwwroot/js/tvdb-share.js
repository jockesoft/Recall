// Share in a detail page's action row (Pages/Shared/_TitleActions.cshtml):
// the page's canonical address and the title. The Web Share API where the
// browser has it; otherwise the address is copied and the "Link copied."
// toast the partial drew in a <template> is shown (RecallToasts, tvdb-toasts.js).
(function () {
    var buttons = document.querySelectorAll('[data-share]');
    if (buttons.length === 0) return;

    function address() {
        var canonical = document.querySelector('link[rel="canonical"]');
        return canonical ? canonical.href : location.href.split('#')[0];
    }

    function copied() {
        if (window.RecallToasts) window.RecallToasts.show('link-copied');
    }

    // Without the Clipboard API (an insecure context, an old browser): a
    // selected text field and the copy command.
    function copyByCommand(text) {
        var field = document.createElement('textarea');
        field.value = text;
        field.setAttribute('readonly', '');
        field.style.position = 'fixed';
        field.style.opacity = '0';
        document.body.appendChild(field);
        field.select();
        var ok = false;
        try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
        field.remove();
        return ok;
    }

    buttons.forEach(function (button) {
        button.addEventListener('click', function () {
            var url = address();
            var title = button.getAttribute('data-share-title') || document.title;

            if (navigator.share) {
                // A cancelled share sheet rejects; there is nothing to say about that.
                navigator.share({ title: title, url: url }).catch(function () { });
                return;
            }

            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(url).then(copied, function () {
                    if (copyByCommand(url)) copied();
                });
            } else if (copyByCommand(url)) {
                copied();
            }
        });
    });
})();
