// Drives the "All / Series / Movies" filter bar (Pages/Shared/_TypeFilterBar.cshtml)
// used on Library, Profile and Favorites. A no-op when the page has no filter
// buttons (e.g. a library/favorites list with only one content type).
(function () {
    var buttons = document.querySelectorAll('.tvdb-filter-btn');
    if (buttons.length === 0) return;

    var sections = document.querySelectorAll('[data-library-section]');
    var emptyNotice = document.getElementById('tvdb-library-empty-filter');

    function applyFilter(type) {
        var anyVisible = false;

        sections.forEach(function (section) {
            var cards = section.querySelectorAll('[data-content-type]');
            var sectionHasMatch = false;

            cards.forEach(function (card) {
                var matches = type === 'all' || card.dataset.contentType === type;
                card.hidden = !matches;
                if (matches) sectionHasMatch = true;
            });

            section.hidden = !sectionHasMatch;
            if (sectionHasMatch) anyVisible = true;
        });

        if (emptyNotice) emptyNotice.hidden = anyVisible;
    }

    buttons.forEach(function (button) {
        button.addEventListener('click', function () {
            buttons.forEach(function (b) {
                b.classList.remove('is-active', 'btn-dark');
                b.classList.add('btn-outline-dark');
            });
            button.classList.add('is-active', 'btn-dark');
            button.classList.remove('btn-outline-dark');

            applyFilter(button.dataset.filter);
        });
    });
})();
