// Recall's service worker. It exists for push notifications only: it shows a
// push and opens the page a tapped notification points at. There is no fetch
// handler and nothing is cached, so the site works exactly as without it.
//
// Served from the site root so its scope is the whole site, with
// Cache-Control: no-cache (Program.cs) so a new version is picked up at once.

// One tag for every Recall notification: a device's subscription belongs to one
// user, so a newer push replaces the older one still in the tray instead of
// stacking up beside it.
const TAG = "recall-new-episodes";
const FALLBACK_URL = "/Account/Notifications";

self.addEventListener("push", (event) => {
    let data = {};
    try {
        data = event.data ? event.data.json() : {};
    } catch (e) {
        // A payload that is not ours: still show something rather than nothing,
        // since a push that shows no notification counts against the site.
    }

    event.waitUntil(self.registration.showNotification(data.title || "Recall", {
        body: data.body || "",
        icon: data.icon || "/images/icons/icon-192.png",
        tag: TAG,
        renotify: true,
        data: { url: data.url || FALLBACK_URL }
    }));
});

self.addEventListener("notificationclick", (event) => {
    event.notification.close();

    // Only ever open a page of this site, whatever the payload says.
    const target = new URL((event.notification.data && event.notification.data.url) || FALLBACK_URL, self.location.origin);
    if (target.origin !== self.location.origin)
        return;

    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        for (const client of windows) {
            if (new URL(client.url).origin !== self.location.origin)
                continue;
            // Reuse an open Recall window when there is one.
            try {
                await client.focus();
                if ("navigate" in client)
                    await client.navigate(target.href);
                return;
            } catch (e) {
                // A window this worker does not control cannot be navigated; open a new one.
            }
        }
        await self.clients.openWindow(target.href);
    })());
});
