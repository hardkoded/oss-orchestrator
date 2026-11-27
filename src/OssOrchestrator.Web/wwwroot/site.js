// The server renders the whole page. This script switches views, shows the details of the chosen session, and
// replaces the page content every 15 seconds and after a button, without losing the reader's place.
(function () {
    var phone = matchMedia("(max-width: 719.98px)");
    var wide = matchMedia("(min-width: 1100px)");
    var toast = document.getElementById("toast");
    var sheet = document.getElementById("sheet");
    var filters = { q: "", repo: "all", state: "free" };
    var busyOpen = false, posting = 0, sending = Promise.resolve(), openedHere = false, touched = 0, poll, toastTimer, pointer;

    function all(selector, root) { return Array.prototype.slice.call((root || document).querySelectorAll(selector)); }

    // "#view" or "#sessions/owner/repo/issue".
    function route() {
        var hash = decodeURIComponent(location.hash.slice(1));
        var slash = hash.indexOf("/");
        var view = slash < 0 ? hash : hash.slice(0, slash);
        if (["backlog", "activity", "history", "repos"].indexOf(view) < 0 || (view === "repos" && wide.matches)) view = "sessions";
        return { view: view, key: view === "sessions" && slash >= 0 ? hash.slice(slash + 1) : null };
    }

    function say(text) {
        toast.textContent = text;
        toast.classList.add("show");
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { toast.classList.remove("show"); toast.textContent = ""; }, 6000);
    }

    function age(since) {
        var minutes = Math.floor((Date.now() - since) / 60000);
        if (minutes < 1) return "<1m";
        if (minutes < 60) return minutes + "m";
        if (minutes < 1440) return Math.floor(minutes / 60) + "h " + (minutes % 60) + "m";
        return Math.floor(minutes / 1440) + "d " + Math.floor(minutes % 1440 / 60) + "h";
    }

    // Times are shown in the reader's time zone; the server only knows its own.
    function times() {
        all("time[data-age]").forEach(function (t) { t.textContent = age(new Date(t.dateTime)); });
        all("time[data-since]").forEach(function (t) {
            var seconds = Math.max(0, Math.round((Date.now() - new Date(t.dateTime)) / 1000));
            t.textContent = seconds < 60 ? seconds + "s" : age(new Date(t.dateTime));
        });
        all("time[data-local]").forEach(function (t) {
            t.textContent = new Date(t.dateTime).toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
        });
    }

    function filter() {
        ["q", "repo", "state"].forEach(function (id) {
            var control = document.getElementById(id);
            if (control.value !== filters[id]) control.value = filters[id];
        });
        var text = filters.q.trim().toLowerCase(), shown = 0;
        all(".issue").forEach(function (row) {
            var match = (filters.repo === "all" || row.dataset.repo === filters.repo)
                && (filters.state === "all" || row.dataset.state.split(" ").indexOf(filters.state) >= 0)
                && row.querySelector(".pick").textContent.toLowerCase().indexOf(text) >= 0;
            row.hidden = !match;
            if (match) shown++;
        });
        document.getElementById("none").hidden = shown > 0;
    }

    function render() {
        var at = route();
        document.body.dataset.view = at.view;
        all("[data-tab]").forEach(function (tab) {
            if (tab.dataset.tab === at.view) tab.setAttribute("aria-current", "page"); else tab.removeAttribute("aria-current");
        });

        var cards = all(".detail .card");
        var card = cards.find(function (c) { return c.dataset.key === at.key; });
        if (at.key && !card) {
            say("That session is no longer open.");
            history.replaceState(null, "", "#sessions");
        }
        // Wider screens always show one session; the phone shows one only after a tap.
        if (!card && !phone.matches) card = cards.find(function (c) {
            var first = document.querySelector("[data-pane=sessions] .row");
            return first && c.dataset.key === first.dataset.key;
        });
        cards.forEach(function (c) { c.hidden = c !== card; });
        all(".row[data-key]").forEach(function (row) {
            row.setAttribute("aria-current", String(!!card && row.dataset.key === card.dataset.key));
        });

        if (phone.matches && card) {
            sheet.innerHTML = '<div class="card sheet-body"><button type="button" class="quiet close" aria-label="Close">✕</button>' + card.innerHTML + "</div>";
            if (!sheet.open) sheet.showModal();
        } else if (sheet.open) {
            sheet.close();
            openedHere = false;
        }

        var busy = document.getElementById("busy");
        if (busy) busy.open = !phone.matches || busyOpen;
        filter();
        times();
    }

    // A history chart shows the values nearest to the pointer. The chart is looked up by position each time, so the
    // values stay after a refresh replaces it.
    function inspect() {
        all(".cursor, .tip").forEach(function (el) { el.hidden = true; });
        var under = pointer && document.elementFromPoint(pointer.x, pointer.y);
        var area = under && under.closest(".area");
        if (!area) return;
        var box = area.getBoundingClientRect(), width = area.querySelector("svg").viewBox.baseVal.width;
        var x = (pointer.x - box.left) / box.width * width;
        var readings = JSON.parse(area.dataset.readings);
        if (!readings.length) return;
        var near = readings.reduce(function (a, b) { return Math.abs(b[0] - x) < Math.abs(a[0] - x) ? b : a; });
        var cursor = area.querySelector(".cursor"), tip = area.querySelector(".tip"), at = near[0] / width * 100 + "%";
        var when = document.createElement("span");
        // In UTC, like the dates under the chart.
        when.textContent = new Date(near[1]).toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "UTC", timeZoneName: "short" });
        tip.replaceChildren(when);
        all(".legend span", area.closest(".chart")).forEach(function (name, i) {
            var line = name.cloneNode(true), value = near[i + 2];
            line.querySelector("b").textContent = value === null ? "–" : value;
            tip.appendChild(line);
        });
        cursor.style.left = tip.style.left = at;
        // The box moves left by the same share of its width, so it never leaves the chart.
        tip.style.translate = "-" + at;
        cursor.hidden = tip.hidden = false;
    }

    function point(e) {
        pointer = e && { x: e.clientX, y: e.clientY };
        inspect();
    }

    // The message of the last command comes with the page; it moves to the live region, which is never replaced.
    function flash() {
        var message = document.querySelector(".flash");
        if (!message) return;
        say(message.textContent);
        message.remove();
    }

    // A selector that finds the same control again in new content.
    function selectorOf(el) {
        if (!el || el === document.body) return null;
        if (el.id) return "#" + CSS.escape(el.id);
        if (el.name === "cmd") return 'button[value="' + CSS.escape(el.value) + '"]';
        if (el.tagName === "SUMMARY") return "summary";
        if (el.classList.contains("close")) return ".close";
        var href = el.getAttribute("href");
        return href ? "a" + (el.classList[0] ? "." + el.classList[0] : "") + '[href="' + CSS.escape(href) + '"]' : null;
    }

    function swap(next) {
        var focused = document.activeElement;
        var selector = selectorOf(focused);
        var root = sheet.contains(focused) ? sheet : document;
        var y = scrollY, sheetY = sheet.scrollTop, card = shownCard(), cardY = card ? card.scrollTop : 0;
        document.getElementById("app").replaceWith(next);
        render();
        flash();
        var again = selector && root.querySelector(selector);
        if (again) again.focus({ preventScroll: true });
        sheet.scrollTop = sheetY;
        var shown = shownCard();
        if (card && shown && shown.dataset.key === card.dataset.key) shown.scrollTop = cardY;
        scrollTo(0, y);
        inspect();
    }

    function shownCard() { return document.querySelector(".detail .card:not([hidden])"); }

    // Reads the page again, or posts a command and shows the page it redirects to. On any problem the old content stays.
    async function load(body) {
        // A command makes a running refresh out of date. A refresh never cancels another, so a slow server still answers.
        if (body && poll) poll.abort();
        else if (poll) return;
        var control = new AbortController(), slow = false;
        var timer = setTimeout(function () { slow = true; control.abort(); }, 60000);
        if (!body) poll = control;
        try {
            var response = await fetch(body ? document.getElementById("act").action : location.pathname, {
                method: body ? "POST" : "GET",
                body: body,
                headers: { "X-Requested-With": "fetch" },
                cache: "no-store",
                signal: control.signal,
            });
            // 401: the sign-in expired. 400: the page's antiforgery token is no longer valid.
            if (response.status === 401 || response.status === 400) {
                if (body) sessionStorage.setItem("lost", "1");
                return location.reload();
            }
            if (!response.ok) throw new Error(response.status);
            var next = new DOMParser().parseFromString(await response.text(), "text/html").getElementById("app");
            if (!next) throw new Error("not the dashboard");
            // A refresh never replaces a number that is typed but not sent yet.
            if (!body && all("input[data-cmd]").some(function (box) { return box.value !== box.defaultValue; })) return;
            swap(next);
        } catch (e) {
            if (e.name !== "AbortError" || slow) say(body ? "Could not confirm the command. Check Activity." : "Could not update. Showing old data.");
        } finally {
            clearTimeout(timer);
            if (poll === control) poll = null;
        }
    }

    function refresh() {
        // A replaced input closes the phone keyboard or an open list, so the refresh waits while one was just used.
        var using = document.activeElement.matches("input, select") && Date.now() - touched < 30000;
        if (!document.hidden && !posting && !using) load();
    }

    // Commands are sent one after the other, each with the antiforgery token that the form holds.
    function send(command) {
        posting++;
        var body = new FormData(document.getElementById("act"));
        body.set("cmd", command);
        sending = sending.then(function () { return load(body); }).finally(function () { posting--; });
        return sending;
    }

    function closeSheet() {
        if (openedHere) {
            openedHere = false;
            history.back();
        } else {
            history.replaceState(null, "", "#sessions");
            render();
        }
    }

    document.addEventListener("click", function (e) {
        if (e.target === sheet || e.target.closest(".close")) return closeSheet();
        var link = e.target.closest('a[href^="#sessions/"]');
        if (!link || e.metaKey || e.ctrlKey || e.shiftKey) return;
        // On the phone the first tap adds a history entry, so Back closes the sheet. Every other choice replaces it.
        if (phone.matches && !sheet.open) {
            openedHere = true;
            return;
        }
        e.preventDefault();
        history.replaceState(null, "", link.getAttribute("href"));
        render();
    });

    document.addEventListener("submit", async function (e) {
        if (e.target.id !== "act") return;
        e.preventDefault();
        var button = e.submitter;
        if (!button || (button.dataset.confirm && !confirm(button.dataset.confirm.trim()))) return;
        // While a command is on its way, − and + still count from the old number, so they are left out.
        if (posting && /^(limit|sessions)\|/.test(button.value)) return;
        if (sheet.contains(button)) closeSheet();
        button.disabled = true;
        await send(button.value);
        button.disabled = false;
    });

    // A typed number is sent on Enter, or when the reader leaves the box.
    document.addEventListener("change", function (e) {
        var box = e.target;
        if (!box.dataset.cmd) return;
        // The old number comes back when the new one is wrong or could not be sent.
        var restore = function () { box.value = box.defaultValue; };
        var number = /^\d+$/.test(box.value) ? Number(box.value) : 0;
        if (number >= 1 && number <= Number(box.dataset.max)) return send(box.dataset.cmd + number).then(restore);
        say(box.getAttribute("aria-label") + " must be a number from 1 to " + box.dataset.max + ".");
        restore();
    });

    // Enter ends the typing: the reader leaves the box, which sends the number.
    document.addEventListener("keydown", function (e) {
        if (e.key === "Enter" && e.target.dataset.cmd) e.target.blur();
    });

    document.addEventListener("focusin", function () { touched = Date.now(); });

    document.addEventListener("input", function (e) {
        touched = Date.now();
        if (!(e.target.id in filters)) return;
        filters[e.target.id] = e.target.value;
        filter();
    });

    // "toggle" does not bubble, so it is caught on the way down.
    document.addEventListener("toggle", function (e) {
        if (e.target.id === "busy" && phone.matches) busyOpen = e.target.open;
    }, true);

    document.addEventListener("keydown", function (e) {
        var step = e.key === "ArrowDown" ? 1 : e.key === "ArrowUp" ? -1 : 0;
        var focused = document.activeElement;
        if (!step || phone.matches || route().view !== "sessions" || !(focused === document.body || focused.matches("a.pick"))) return;
        var rows = all("[data-pane=sessions] a.pick");
        var current = rows.findIndex(function (a) { return a.parentElement.getAttribute("aria-current") === "true"; });
        var next = rows[current + step];
        if (!next) return;
        e.preventDefault();
        history.replaceState(null, "", next.getAttribute("href"));
        render();
        next.focus();
    });

    // A mouse shows values as it moves; a finger shows them with a tap or a sideways drag.
    document.addEventListener("pointermove", point);
    document.addEventListener("pointerdown", point);
    document.documentElement.addEventListener("pointerleave", function (e) { if (e.pointerType === "mouse") point(null); });
    addEventListener("scroll", function () { point(null); }, { passive: true });

    sheet.addEventListener("cancel", function (e) { e.preventDefault(); closeSheet(); });
    addEventListener("hashchange", render);
    addEventListener("popstate", render);
    phone.addEventListener("change", render);
    wide.addEventListener("change", render);
    document.addEventListener("visibilitychange", refresh);
    setInterval(refresh, 15000);
    setInterval(times, 5000);
    render();
    flash();
    if (sessionStorage.getItem("lost")) {
        sessionStorage.removeItem("lost");
        say("Your command was not sent. Try again.");
    }
})();
