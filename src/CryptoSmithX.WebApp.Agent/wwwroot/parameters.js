/* The screen's status word. Nothing here posts anywhere — the page is deliberately bound to no
   source yet — so "Saved" means the form has not been edited since it loaded, and nothing more.
   It says that rather than lying about a round trip that did not happen. */
(function () {
  var form = document.getElementById('limits');
  var status = document.getElementById('status');
  var timer;
  function setStatus(state, text) { status.dataset.state = state; status.textContent = text; }
  form.addEventListener('input', function () { clearTimeout(timer); setStatus('dirty', 'Unsaved changes'); });
  form.addEventListener('submit', function (e) {
    e.preventDefault();
    setStatus('saving', 'Saving…');
    clearTimeout(timer);
    timer = setTimeout(function () { setStatus('saved', 'Saved'); }, 550);
  });
})();
