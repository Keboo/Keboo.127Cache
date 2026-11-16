// Bundled progressive enhancement; no external assets or runtime.
document.addEventListener("click", async (event) => {
  const button = event.target.closest("button[data-copy-target]");
  if (!button) return;
  const input = document.getElementById(button.dataset.copyTarget);
  if (!input) return;
  input.select();
  try {
    await navigator.clipboard.writeText(input.value);
    button.textContent = "Copied";
    setTimeout(() => { button.textContent = "Copy"; }, 2000);
  } catch {
    document.execCommand("copy");
  }
});

// Shows an in-progress state while a refresh runs; the server renders the result when the request returns.
document.addEventListener("submit", (event) => {
  const form = event.target;
  if (!form.dataset || !form.dataset.busy) return;
  const label = event.submitter && event.submitter.dataset.busyLabel;
  const status = document.getElementById(form.dataset.busy);
  if (status) { status.textContent = label || "Working…"; status.hidden = false; }
  document.querySelectorAll("button[form='" + form.id + "']").forEach((b) => { b.setAttribute("aria-busy", "true"); });
  // Disabling synchronously would drop the clicked button's value from the posted data.
  setTimeout(() => document.querySelectorAll("button[form='" + form.id + "'], #" + form.id + " button").forEach((b) => { b.disabled = true; }), 0);
});

(() => {
  if (!document.getElementById("overview")) return;
  let timer;
  let request;
  let disposed = false;
  let submitting = false;
  const error = document.getElementById("overview-connection-error");
  const announcement = document.getElementById("overview-announcement");

  function show(html) {
    const next = new DOMParser().parseFromString(html, "text/html").getElementById("overview");
    if (!next) throw new Error("Missing overview response");
    const current = document.getElementById("overview");
    if (next.dataset.revision !== current.dataset.revision || !error.hidden) {
      const focused = document.activeElement;
      const name = focused && focused.getAttribute("name");
      const value = focused && focused.getAttribute("value");
      const restoreFocus = current.contains(focused);
      current.replaceWith(next);
      if (restoreFocus && name) {
        const replacement = Array.from(next.querySelectorAll("button[name]"))
          .find(button => button.name === name && button.value === value);
        if (replacement && !replacement.disabled) replacement.focus({ preventScroll: true });
      }
    }
    if (announcement.textContent !== next.dataset.status) announcement.textContent = next.dataset.status;
    error.hidden = true;
    if (!disposed && next.dataset.busy === "true") timer = setTimeout(poll, 750);
  }

  async function update(url, options) {
    clearTimeout(timer);
    if (request) request.abort();
    const controller = new AbortController();
    request = controller;
    try {
      const response = await fetch(url, { ...options, signal: controller.signal, cache: "no-store" });
      if (!response.ok) throw new Error("Overview update failed");
      const html = await response.text();
      if (!disposed && !controller.signal.aborted) show(html);
    } catch (failure) {
      if (!disposed && failure.name !== "AbortError") {
        error.hidden = false;
        // The server may have accepted the POST. Reconnect to its shared state before allowing another refresh.
        document.querySelectorAll("[data-overview-refresh] button, button[form='refresh-form']")
          .forEach(button => { button.disabled = true; });
      }
    }
  }

  function poll() { return update("/_ui/overview"); }
  document.addEventListener("click", event => {
    if (event.target.closest("[data-overview-retry]")) poll();
  });
  document.addEventListener("submit", async event => {
    const form = event.target;
    if (!form.hasAttribute("data-overview-refresh")) return;
    event.preventDefault();
    if (submitting || document.getElementById("overview").dataset.busy === "true") return;
    submitting = true;
    const body = new FormData(form);
    if (event.submitter && event.submitter.name) body.append(event.submitter.name, event.submitter.value);
    document.querySelectorAll("[data-overview-refresh] button, button[form='refresh-form']")
      .forEach(button => { button.disabled = true; });
    announcement.textContent = "Starting refreshâ€¦";
    await update("/", { method: "POST", body });
    submitting = false;
  });
  window.addEventListener("pagehide", () => {
    disposed = true;
    clearTimeout(timer);
    if (request) request.abort();
  });
  // A page restored from the browser's back/forward cache needs a fresh shell and polling lifecycle.
  window.addEventListener("pageshow", event => { if (event.persisted) window.location.reload(); });
  if (document.getElementById("overview").dataset.busy === "true") poll();
})();
