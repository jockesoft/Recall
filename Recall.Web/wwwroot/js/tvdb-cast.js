// Cast and crew lists (Pages/Shared/_CastList.cshtml). A list is collapsed
// until "Show all" is pressed: the cast to one row of cards, the crew to its
// first few rows. The button is only offered when something is cut off, which
// depends on how many fit at this width.
(function () {
    var sections = document.querySelectorAll('[data-cast]');
    if (sections.length === 0) return;

    sections.forEach(function (section) {
        var list = section.querySelector('.tvdb-cast__list');
        var toggle = section.querySelector('[data-cast-toggle]');
        var label = section.querySelector('[data-cast-toggle-label]');
        if (!list || !toggle || !label) return;

        function update() {
            var expanded = section.classList.contains('is-expanded');
            // Collapsed, the list clips its extra rows: more content than box means there is more to show.
            var clipped = list.scrollHeight > list.clientHeight + 1;
            toggle.hidden = !expanded && !clipped;

            // People in a cut-off row are not on screen, so keep their links out
            // of the tab order too. (A sideways-scrolling row cuts nothing off.)
            var bottom = list.getBoundingClientRect().bottom;
            Array.prototype.forEach.call(list.children, function (item) {
                item.inert = !expanded && clipped && item.getBoundingClientRect().top >= bottom - 1;
            });
        }

        toggle.addEventListener('click', function () {
            var expanded = section.classList.toggle('is-expanded');
            toggle.setAttribute('aria-expanded', expanded);
            label.textContent = expanded ? label.dataset.less : label.dataset.more;
            update();
        });

        update();
        window.addEventListener('resize', update);
        window.addEventListener('load', update);
    });
})();
