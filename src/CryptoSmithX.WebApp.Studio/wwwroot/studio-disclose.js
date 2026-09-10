/* Раскрытие на месте — общий механизм для полосы площадок (A1/A2) и карточки актива (A3/A4).

   Ни один готовый паттерн раскрытия в этом приложении уже не существует: строка про «a group
   opens in place» на второй странице актива — текст без реализации, разворачивать там больше
   нечего, все колонки видны сразу (см. studio-v2.js). Этот файл — не переиспользование, а
   единственная реализация того, что раньше только обещали.

   Панель подгружается ОДИН РАЗ, при первом клике, и после этого просто прячется/показывается —
   не перезапрашивается. Данные под ней (режим сбора, состояние коллекторов, реестр алиасов) не
   двигаются внутри одного захода на страницу быстрее, чем сама страница целиком, так что повторный
   запрос не покупает свежести, только лишний раунд-трип.

   Без скрипта ссылка просто ведёт на partial напрямую — data-disclose-url всегда настоящий URL,
   не якорь. */
(function () {
  var triggers = document.querySelectorAll('[data-disclose-url]');
  if (!triggers.length) { return; }

  function panelOf(trigger) {
    var id = trigger.getAttribute('data-disclose-target');
    return id ? document.getElementById(id) : null;
  }

  function open(trigger, panel) {
    var url = trigger.getAttribute('data-disclose-url');
    panel.hidden = false;
    trigger.setAttribute('aria-expanded', 'true');

    if (panel.getAttribute('data-loaded') === '1') { return; }

    panel.innerHTML = '<p class="a-note">Loading…</p>';
    fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
      .then(function (r) {
        if (!r.ok) { throw new Error('http ' + r.status); }
        return r.text();
      })
      .then(function (html) {
        panel.innerHTML = html;
        panel.setAttribute('data-loaded', '1');
      })
      .catch(function () {
        panel.innerHTML = '<p class="a-note">Could not load this panel. Reload the page to try again.</p>';
      });
  }

  function close(trigger, panel) {
    panel.hidden = true;
    trigger.setAttribute('aria-expanded', 'false');
  }

  triggers.forEach(function (trigger) {
    var panel = panelOf(trigger);
    if (!panel) { return; }

    trigger.setAttribute('aria-expanded', 'false');
    trigger.addEventListener('click', function (e) {
      e.preventDefault();
      if (panel.hidden) { open(trigger, panel); } else { close(trigger, panel); }
    });
  });
})();
