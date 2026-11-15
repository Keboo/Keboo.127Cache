// Progressive enhancement only: every page is fully usable without this script.
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
