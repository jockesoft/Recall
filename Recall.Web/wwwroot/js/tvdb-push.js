// Profile's "Notifications" section: whether this browser can get push
// notifications, and turning them on or off for it. Loaded only on Profile and
// only when push is enabled on the server.
//
// The browser is asked for permission only from the "Turn on" button's click
// handler, never when the page loads: a permission prompt nobody asked for is
// what makes people block a site for good, and browsers penalise it too.
//
// The subscription is sent with an ordinary form post (pushSubscribeForm), so
// the antiforgery token, the redirect and the toast work as on every other page.
(function () {
    "use strict";

    var root = document.querySelector("[data-push]");
    if (!root) return;

    var errorLine = root.querySelector("[data-push-error]");

    function show(state) {
        root.querySelectorAll("[data-push-state]").forEach(function (el) {
            el.hidden = el.getAttribute("data-push-state") !== state;
        });
    }

    function fail(message) {
        errorLine.textContent = message;
        errorLine.hidden = false;
    }

    // iOS and iPadOS only offer push to a site added to the Home Screen and
    // opened from there. An iPad asks for the desktop site and says "Macintosh",
    // but has a touch screen.
    function isAppleMobile() {
        return /iPhone|iPad|iPod/.test(navigator.userAgent)
            || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
    }

    function isStandalone() {
        return window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
    }

    function supportsPush() {
        return "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
    }

    function keyBytes(base64Url) {
        var base64 = (base64Url + "===".slice((base64Url.length + 3) % 4)).replace(/-/g, "+").replace(/_/g, "/");
        var raw = atob(base64);
        var bytes = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        return bytes;
    }

    function sameKey(buffer, bytes) {
        if (!buffer) return false;
        var a = new Uint8Array(buffer);
        if (a.length !== bytes.length) return false;
        for (var i = 0; i < a.length; i++) if (a[i] !== bytes[i]) return false;
        return true;
    }

    // The page lists the devices by a SHA-256 of their endpoint, not the endpoint itself.
    async function hash(text) {
        var digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
        return Array.from(new Uint8Array(digest)).map(function (b) { return b.toString(16).padStart(2, "0"); }).join("");
    }

    async function currentSubscription() {
        var registration = await navigator.serviceWorker.getRegistration("/");
        return registration ? registration.pushManager.getSubscription() : null;
    }

    function deviceRow(endpointHash) {
        return Array.from(root.querySelectorAll("[data-endpoint-hash]")).find(function (row) {
            return row.getAttribute("data-endpoint-hash") === endpointHash;
        });
    }

    async function turnOn() {
        errorLine.hidden = true;
        var button = document.getElementById("pushSwitchOn");
        button.disabled = true;

        try {
            // The one place permission is asked for: the user has just pressed "Turn on".
            var permission = await Notification.requestPermission();
            if (permission !== "granted") {
                show(permission === "denied" ? "denied" : "off");
                return;
            }

            var key = keyBytes(root.getAttribute("data-push-key"));
            var registration = await navigator.serviceWorker.register("/sw.js", { scope: "/" });
            await navigator.serviceWorker.ready;

            // A subscription made with an older server key cannot receive pushes signed with the new one.
            var subscription = await registration.pushManager.getSubscription();
            if (subscription && !sameKey(subscription.options.applicationServerKey, key)) {
                await subscription.unsubscribe();
                subscription = null;
            }
            subscription = subscription || await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });

            var json = subscription.toJSON();
            var form = document.getElementById("pushSubscribeForm");
            form.elements.endpoint.value = json.endpoint;
            form.elements.p256dh.value = json.keys.p256dh;
            form.elements.auth.value = json.keys.auth;
            form.elements.timeZone.value = Intl.DateTimeFormat().resolvedOptions().timeZone || "";
            form.submit();
        } catch (e) {
            fail("Could not turn on notifications on this device. Try again in a moment.");
        } finally {
            button.disabled = false;
        }
    }

    // Unsubscribes the browser, then tells the server with the given form.
    async function unsubscribeAndSubmit(form) {
        try {
            var subscription = await currentSubscription();
            if (subscription) {
                if (form.elements.endpoint) form.elements.endpoint.value = subscription.endpoint;
                await subscription.unsubscribe();
            }
        } catch (e) {
            // The server side is what stops the pushes; go on and remove it there.
        }
        form.submit();
    }

    async function init() {
        if (isAppleMobile() && !isStandalone()) { show("ios"); return; }
        if (!supportsPush()) { show("unsupported"); return; }
        if (Notification.permission === "denied") { show("denied"); return; }

        show("checking");

        var on = false;
        try {
            var subscription = await currentSubscription();
            if (subscription) {
                var row = deviceRow(await hash(subscription.endpoint));
                if (row) {
                    on = Notification.permission === "granted";
                    row.querySelector("[data-this-device]").hidden = false;

                    // Removing this very device from the list also unsubscribes the browser.
                    var removeForm = row.querySelector("[data-remove-device]");
                    removeForm.addEventListener("submit", function (event) {
                        event.preventDefault();
                        unsubscribeAndSubmit(removeForm);
                    });
                }
            }
        } catch (e) {
            // Unknown: offer "Turn on", which sorts it out.
        }

        show(on ? "on" : "off");
    }

    document.getElementById("pushSwitchOn").addEventListener("click", turnOn);
    document.getElementById("pushSwitchOff").addEventListener("click", function () {
        this.disabled = true;
        unsubscribeAndSubmit(document.getElementById("pushUnsubscribeForm"));
    });

    init();
})();
