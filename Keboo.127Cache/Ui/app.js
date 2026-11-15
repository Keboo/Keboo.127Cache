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
