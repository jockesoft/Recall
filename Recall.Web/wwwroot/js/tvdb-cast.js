// Cast and crew lists (Pages/Shared/_CastList.cshtml). The list shows two rows
// until "Show all" is pressed; the button is only offered when there is more
// than that, which depends on how many cards fit in a row at this width.
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
