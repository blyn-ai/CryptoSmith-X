/* Экран стратегии. Ничего не пишет сам: сохранение делает форма, как и делало.

   Задачи здесь три, и все они про то, что читатель видит:
     1. слово о состоянии и метка «Pakeista» — отличают «на экране то, что сохранено» от «ты
        что-то набрал»;
     2. подтверждение перед сохранением — перехватывает submit и показывает четыре числа,
        которые сейчас станут ботовскими;
     3. лист-подсказка, ползунки, сброс и списки — оформление макета.

   Со скриптами наотрез форма уходит напрямую, и сервер проверяет её точно так же: диалог
   охраняет работающую форму, а не является способом её отправить. */
(function () {
  var form = document.getElementById('limits');
  if (!form) { return; }

  var status = document.getElementById('status');
  var invalid = document.querySelector('[data-invalid]');
  var unsaved = document.querySelector('[data-unsaved]');
  var dialog = document.getElementById('confirm');
  var paused = document.querySelector('[data-paused]');
  var margin = form.querySelector('[name="positionMarginUsd"]');

  // ── 1. Состояние ─────────────────────────────────────────────────────────
  var idle = status ? (status.dataset.idle || 'Išsaugota') : '';

  function dirty() {
    var changed = false;
    Array.prototype.forEach.call(form.querySelectorAll('[data-number]'), function (input) {
      var moved = input.value !== input.dataset.saved;
      var card = input.closest('[data-field]');
      if (card) {
        card.classList.toggle('changed', moved);
        var line = card.querySelector('[data-changed]');
        if (line) { line.hidden = !moved; }
      }
      if (moved) { changed = true; }
    });

    if (unsaved) { unsaved.hidden = !changed; }
    if (status) {
      status.dataset.state = changed ? 'dirty' : 'saved';
      status.textContent = changed ? 'Neišsaugoti pakeitimai' : idle;
    }
    if (invalid) { invalid.hidden = form.checkValidity(); }

    // ПАУЗА — на набранном значении, а не на сохранённом. Ноль в марже это не размер, а
    // состояние бота, и узнать о нём после нажатия «Išsaugoti» поздно: человек должен видеть,
    // ЧТО он сейчас сохранит, пока курсор ещё в поле.
    if (paused && margin) {
      var typed = parseFloat(margin.value);
      paused.hidden = !(margin.value !== '' && typed === 0);
    }
  }

  form.addEventListener('input', dirty);

  // pageshow, а не load: возврат кнопкой «назад» восстанавливает поля из кеша браузера, и слово
  // «Saugoma…», оставшееся от прошлого визита, было бы ложью о давно законченном запросе.
  window.addEventListener('pageshow', dirty);

  // ── 2. Ползунок и число — одно значение ──────────────────────────────────
  Array.prototype.forEach.call(form.querySelectorAll('[data-field]'), function (card) {
    var number = card.querySelector('[data-number]');
    var range = card.querySelector('[data-range]');
    var revert = card.querySelector('[data-revert]');
    if (!number) { return; }

    if (range) {
      range.addEventListener('input', function () {
        number.value = range.value;
        number.dispatchEvent(new Event('input', { bubbles: true }));
      });
      number.addEventListener('input', function () { range.value = number.value; });
    }

    if (revert) {
      revert.addEventListener('click', function () {
        number.value = number.dataset.saved;
        if (range) { range.value = number.value; }
        number.dispatchEvent(new Event('input', { bubbles: true }));
      });
    }
  });

  // ── 3. Сброс всех значений к сохранённым ─────────────────────────────────
  var askReset = document.querySelector('[data-reset-ask]');
  var confirmReset = document.querySelector('[data-reset-confirm]');
  if (askReset && confirmReset) {
    askReset.addEventListener('click', function () {
      confirmReset.hidden = false;
      askReset.hidden = true;
    });
    confirmReset.querySelector('[data-reset-cancel]').addEventListener('click', function () {
      confirmReset.hidden = true;
      askReset.hidden = false;
    });
    confirmReset.querySelector('[data-reset-do]').addEventListener('click', function () {
      Array.prototype.forEach.call(form.querySelectorAll('[data-number]'), function (input) {
        input.value = input.dataset.saved;
        var range = input.closest('[data-field]').querySelector('[data-range]');
        if (range) { range.value = input.value; }
      });
      confirmReset.hidden = true;
      askReset.hidden = false;
      dirty();
    });
  }

  // ── 4. Внутренние параметры ──────────────────────────────────────────────
  var internalsToggle = document.querySelector('[data-internals-toggle]');
  var internals = document.querySelector('[data-internals]');
  if (internalsToggle && internals) {
    internalsToggle.addEventListener('click', function () {
      var open = internals.hidden;
      internals.hidden = !open;
      internalsToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      internalsToggle.textContent = open ? 'Slėpti vidinius parametrus' : 'Rodyti vidinius parametrus';
    });
  }

  // ── 5. Лист-подсказка ────────────────────────────────────────────────────
  var sheet = document.getElementById('sheet');
  if (sheet) {
    var tech = sheet.querySelector('[data-sheet-tech]');
    var techToggle = sheet.querySelector('[data-tech-toggle]');

    function fill(button) {
      sheet.querySelector('[data-sheet-title]').textContent = button.dataset.title || '';
      sheet.querySelector('[data-sheet-intro]').textContent = button.dataset.intro || '';

      var directions = sheet.querySelector('[data-sheet-directions]');
      var hasDirection = !!(button.dataset.down || button.dataset.up);
      directions.hidden = !hasDirection;
      if (hasDirection) {
        sheet.querySelector('[data-sheet-down]').textContent = button.dataset.down || '';
        sheet.querySelector('[data-sheet-up]').textContent = button.dataset.up || '';
      }

      // Строки листа: либо заданные кнопкой, либо «сейчас / сохранено» у обычного поля.
      var rows = sheet.querySelector('[data-sheet-rows]');
      rows.textContent = '';
      var pairs = button.dataset.rows
        ? button.dataset.rows.split('|').map(function (p) { return p.split('='); })
        : [['Dabar', button.dataset.now || ''], ['Išsaugota', button.dataset.saved || '']];

      pairs.forEach(function (pair) {
        if (!pair[0]) { return; }
        var row = document.createElement('span');
        row.className = 'sheet-row';
        var left = document.createElement('span');
        left.textContent = pair[0];
        var right = document.createElement('span');
        right.textContent = pair[1] || '';
        row.appendChild(left);
        row.appendChild(right);
        rows.appendChild(row);
      });

      var note = sheet.querySelector('[data-sheet-note]');
      note.textContent = button.dataset.note || '';
      note.hidden = !button.dataset.note;

      tech.textContent = button.dataset.tech || '';
      tech.hidden = true;
      techToggle.hidden = !button.dataset.tech;
      techToggle.setAttribute('aria-expanded', 'false');
    }

    if (techToggle) {
      techToggle.addEventListener('click', function () {
        var open = tech.hidden;
        tech.hidden = !open;
        techToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      });
    }

    document.addEventListener('click', function (e) {
      var button = e.target.closest ? e.target.closest('[data-sheet]') : null;
      if (!button) { return; }
      // «Сейчас» берётся в момент открытия: карточка живая, и значение в ней могло уехать.
      var card = button.closest('[data-field]');
      var number = card ? card.querySelector('[data-number]') : null;
      if (number) { button.dataset.now = number.value + ' ' + (button.dataset.now || '').split(' ').slice(1).join(' '); }
      fill(button);
      if (typeof sheet.showModal === 'function') { sheet.showModal(); }
    });
  }

  // Открытие и закрытие любого <dialog> по data-атрибутам, одним обработчиком.
  document.addEventListener('click', function (e) {
    var open = e.target.closest ? e.target.closest('[data-open]') : null;
    if (open) {
      var target = document.getElementById(open.dataset.open);
      if (target && typeof target.showModal === 'function') { target.showModal(); }
      return;
    }
    var close = e.target.closest ? e.target.closest('[data-close]') : null;
    if (close) {
      var owner = close.closest('dialog');
      if (owner) { owner.close(); }
    }
  });

  // Щелчок по затемнению закрывает лист — как в макете.
  Array.prototype.forEach.call(document.querySelectorAll('dialog.overlay'), function (d) {
    d.addEventListener('click', function (e) { if (e.target === d) { d.close(); } });
  });

  // ── 6. Подтверждение сохранения — механизм не менялся ────────────────────
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
  dirty();

  if (!dialog || typeof dialog.showModal !== 'function') { return; }

  var confirmed = false;
  var note = document.getElementById('revision-note');
  var noteTarget = document.getElementById('change-note');

  form.addEventListener('submit', function (e) {
    if (confirmed) { return; }
    // Пусть сначала скажут min/max/step самих полей: подтверждать значение, которое браузер
    // всё равно отвергнет, — значит просить одобрить то, что не уйдёт.
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
    // requestSubmit, а не submit(): submit() пропускает событие submit И кнопку, а заодно
    // проверку полей — и однажды это выстрелит.
    if (typeof form.requestSubmit === 'function') { form.requestSubmit(); } else { form.submit(); }
  });
})();
