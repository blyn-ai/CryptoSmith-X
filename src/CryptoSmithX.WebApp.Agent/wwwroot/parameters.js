/* Two jobs on this screen, and neither of them writes anything: the status word, and the
   confirmation in front of Save.

   The status word only distinguishes "what you see is what is stored" from "you have typed
   something that is not". Its idle text comes from the server (data-idle), because only the server
   knows whether these numbers are the owner's overrides or the bot's own deployed defaults.

   The confirmation intercepts submit and shows the four numbers that are about to become the
   bot's. With scripting off the form posts straight through and the server validates it exactly
   the same — the dialog guards a working form, it is not the way the form works. */
(function () {
  var form = document.getElementById('limits');
  var status = document.getElementById('status');
  var dialog = document.getElementById('confirm');
  if (!form) { return; }

  if (status) {
    var idle = status.dataset.idle || 'Išsaugota';
    form.addEventListener('input', function () {
      status.dataset.state = 'dirty';
      status.textContent = 'Neišsaugoti pakeitimai';
    });
    // pageshow rather than load: coming back with the Back button restores the fields from the
    // browser's cache, and a status left reading "Saving…" from the visit before would be a lie
    // about a request that is long over.
    window.addEventListener('pageshow', function () {
      status.dataset.state = 'saved';
      status.textContent = idle;
    });
  }

  if (!dialog || typeof dialog.showModal !== 'function') { return; }

  var confirmed = false;
  var note = document.getElementById('revision-note');
  var noteTarget = document.getElementById('change-note');
  var riskPanel = document.querySelector('[data-risk-panel]');
  var riskLevel = document.querySelector('[data-risk-level]');
  var riskText = document.querySelector('[data-risk-text]');

  function numberValue(name) {
    var field = form.elements[name];
    return field ? Number(field.value) : NaN;
  }

  function updateRisk() {
    if (!riskPanel || !riskLevel || !riskText) { return; }
    var margin = numberValue('positionMarginUsd');
    var leverage = numberValue('leverage');
    var positions = numberValue('maxOpenPositions');
    if (!Number.isFinite(margin) || !Number.isFinite(leverage) || !Number.isFinite(positions)) {
      riskLevel.textContent = '—';
      riskText.textContent = 'Užpildyk visus laukus, kad būtų galima suskaičiuoti bendrą ekspoziciją.';
      riskPanel.classList.remove('elevated');
      return;
    }
    var exposure = margin * leverage * positions;
    var elevated = exposure >= 10000 || leverage >= 8;
    riskLevel.textContent = elevated ? 'Padidintas' : 'Įprastas';
    riskText.textContent = margin.toLocaleString('en-US', { maximumFractionDigits: 2 }) + ' USD × ' + leverage.toLocaleString('en-US', { maximumFractionDigits: 1 }) + ' svertas × iki ' + positions.toLocaleString('en-US', { maximumFractionDigits: 0 }) + ' pozicijų gali reikšti iki ' + exposure.toLocaleString('en-US', { maximumFractionDigits: 2 }) + ' USD bendros ekspozicijos rinkoje.';
    riskPanel.classList.toggle('elevated', elevated);
  }

  updateRisk();
  form.addEventListener('input', updateRisk);

  form.addEventListener('submit', function (e) {
    if (confirmed) { return; }
    // Let the fields' own min/max/step speak first: confirming a value the browser is about to
    // refuse would ask the owner to approve something that never gets sent.
    if (form.noValidate !== true && typeof form.reportValidity === 'function' && !form.reportValidity()) {
      e.preventDefault();
      return;
    }
    e.preventDefault();
    Array.prototype.forEach.call(dialog.querySelectorAll('[data-for]'), function (cell) {
      var field = form.elements[cell.getAttribute('data-for')];
      cell.textContent = field ? field.value : '—';
    });
    dialog.showModal();
  });

  dialog.addEventListener('close', function () {
    if (dialog.returnValue !== 'save') { return; }
    confirmed = true;
    if (noteTarget) { noteTarget.value = note ? note.value : ''; }
    if (status) { status.dataset.state = 'saving'; status.textContent = 'Saugoma…'; }
    // requestSubmit, not submit(): submit() skips the submit event AND the submit button, and this
    // form has no name on its button to lose — but it also skips validation, and one day it will.
    if (typeof form.requestSubmit === 'function') { form.requestSubmit(); } else { form.submit(); }
  });
})();
