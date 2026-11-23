// Cast and crew lists (Pages/Shared/_CastList.cshtml). A list is collapsed
// until "Show all" is pressed: the stylesheet shows the first six or eight
// people and does not render the rest (by count, never by height, so nobody is
// ever half shown). The button is offered only when someone is hidden, which
// depends on the width (one column or two).
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
            var hidden = Array.prototype.some.call(list.children, function (item) {
                return getComputedStyle(item).display === 'none';
            });
            toggle.hidden = !expanded && !hidden;
        }

        toggle.addEventListener('click', function () {
            var expanded = section.classList.toggle('is-expanded');
            toggle.setAttribute('aria-expanded', expanded);
            label.textContent = expanded ? label.dataset.less : label.dataset.more;
            update();
        });

        update();
        window.addEventListener('resize', update);
    });
})();
