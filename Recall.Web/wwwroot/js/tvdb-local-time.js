// Release times in the viewer's own zone. The server writes each one as
// <time datetime="2026-10-03T00:00:00Z" data-local-release="datetime">Fri, Oct 2 · 8:00 PM ET</time>
// (TheTVDB's date and the series' own time zone); this rewrites the text into
// the viewer's local date and time, in the same English style as the server's
// DisplayDate: "Sat, Oct 3 · 2:00 AM", "Oct 3, 2027 · 2:00 AM" in another year,
// or just "2:00 AM" for data-local-release="time". Only elements whose datetime
// has a time are touched; a date-only one (no air time known) stays as it is.
// Nothing is stored. The original text stays as the element's title.
(function () {
    'use strict';

    var day = new Intl.DateTimeFormat('en-US', { weekday: 'short', month: 'short', day: 'numeric' });
    var dayWithYear = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    var clock = new Intl.DateTimeFormat('en-US', { hour: 'numeric', minute: '2-digit', hour12: true });

    // Some engines put a narrow no-break space before AM/PM; the server writes a plain one.
    function plain(text) { return text.replace(/[  ]/g, ' '); }

    function rewrite() {
        var now = new Date();
        var elements = document.querySelectorAll('time[data-local-release]');

        for (var i = 0; i < elements.length; i++) {
            var element = elements[i];
            var iso = element.getAttribute('datetime') || '';
            if (iso.indexOf('T') < 0) continue;

            var moment = new Date(iso);
            if (isNaN(moment.getTime())) continue;

            var time = plain(clock.format(moment));
            var text = element.getAttribute('data-local-release') === 'time'
                ? time
                : plain((moment.getFullYear() === now.getFullYear() ? day : dayWithYear).format(moment)) + ' · ' + time;

            if (!element.title) element.title = element.textContent.trim();
            element.textContent = text;
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', rewrite);
    } else {
        rewrite();
    }
})();
