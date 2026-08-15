// Timestamps are rendered on the server, where local time is the host's time zone
// (UTC on App Service) rather than the reader's. The markup therefore carries the
// instant in ISO-8601 and the browser formats it for whoever is looking.
(function () {
  "use strict";

  const displayCulture = document.documentElement.lang || "en-GB";

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
      element.textContent = new Intl.DateTimeFormat(displayCulture, options).format(instant);
    } catch {
      element.textContent = instant.toLocaleString(displayCulture);
    }
  }

  // A <select> option cannot hold a <time>, so a range of instants travels in data
  // attributes instead and the server-rendered text stays as the no-script fallback.
  function formatRange(element) {
    const from = new Date(element.dataset.rangeStart || "");
    const to = new Date(element.dataset.rangeEnd || "");
    if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
      return;
    }
    const options = FORMATS[element.dataset.localRange] || FORMATS.time;
    try {
      const formatter = new Intl.DateTimeFormat(displayCulture, options);
      const label = element.dataset.rangeLabel;
      const range = `${formatter.format(from)}–${formatter.format(to)}`;
      element.textContent = label ? `${label} · ${range}` : range;
    } catch {
      // Keep the server text rather than replacing it with something worse.
    }
  }

  function formatLocalTimes(root) {
    const scope = root && typeof root.querySelectorAll === "function" ? root : document;
    scope.querySelectorAll("time[data-local][datetime]").forEach(format);
    scope.querySelectorAll("[data-local-range][data-range-start][data-range-end]").forEach(formatRange);
  }

  window.glosifyFormatLocalTimes = formatLocalTimes;

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => formatLocalTimes(document));
  } else {
    formatLocalTimes(document);
  }
})();
