// Timestamps are rendered on the server, where local time is the host's time zone
// (UTC on App Service) rather than the reader's. The markup therefore carries the
// instant in ISO-8601 and the browser formats it for whoever is looking.
(function () {
  "use strict";

  const FORMATS = {
    datetime: { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" },
    date: { year: "numeric", month: "short", day: "numeric" },
    time: { hour: "2-digit", minute: "2-digit", second: "2-digit" },
    weekday: {
      weekday: "long",
      year: "numeric",
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    },
  };

  function format(element) {
    const iso = element.getAttribute("datetime");
    if (!iso) {
      return;
    }
    const instant = new Date(iso);
    if (Number.isNaN(instant.getTime())) {
      // Leave the server-rendered fallback in place rather than showing "Invalid Date".
      return;
    }
    const options = FORMATS[element.dataset.local] || FORMATS.datetime;
    try {
      element.textContent = new Intl.DateTimeFormat(undefined, options).format(instant);
    } catch {
      element.textContent = instant.toLocaleString();
    }
  }

  function formatLocalTimes(root) {
    const scope = root && typeof root.querySelectorAll === "function" ? root : document;
    scope.querySelectorAll("time[data-local][datetime]").forEach(format);
  }

  window.glosifyFormatLocalTimes = formatLocalTimes;

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => formatLocalTimes(document));
  } else {
    formatLocalTimes(document);
  }
})();
