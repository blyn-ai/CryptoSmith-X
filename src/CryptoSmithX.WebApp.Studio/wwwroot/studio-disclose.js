/* Раскрытие на месте — общий механизм для полосы площадок (A1/A2), карточки актива (A3/A4) и
   теперь ячейки листинга на второй странице (B2).

   Ни один готовый паттерн раскрытия в этом приложении уже не существовал: строка про «a group
   opens in place» на второй странице актива была текстом без реализации. Этот файл — единственная
   реализация того, что раньше только обещали.

   Панель подгружается ОДИН РАЗ, при первом клике, и после этого просто прячется/показывается —
   не перезапрашивается. Данные под ней (режим сбора, состояние коллекторов, реестр алиасов, спека
   инструмента) не двигаются внутри одного захода на страницу быстрее, чем сама страница целиком,
   так что повторный запрос не покупает свежести, только лишний раунд-трип.

   СЛУШАТЕЛЬ ДЕЛЕГИРОВАН, А НЕ ПОВЕШЕН НА КАЖДЫЙ ТРИГГЕР. Полоса 1 второй страницы — один из живых
   регионов (LiveRegions): каждый проход коллектора заменяет всю таблицу целиком, включая любые
   триггеры внутри неё. Слушатель, повешенный на конкретный элемент при загрузке скрипта, перестаёт
   срабатывать молча в тот момент, когда этот элемент заменён, — один слушатель на document переживает
   любое число замен.

   Без скрипта ссылка просто ведёт на partial напрямую — data-disclose-url всегда настоящий URL,
   не якорь. */
(function () {
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

  function toggle(trigger) {
    var panel = panelOf(trigger);
    if (!panel) { return; }
    if (panel.hidden) { open(trigger, panel); } else { close(trigger, panel); }
  }

  // Стартовое состояние для того, что уже на странице при загрузке скрипта — не обязательно для
  // работы (делегированный слушатель ниже всё равно откроет любой триггер), но так экран читалки
  // знает «свёрнуто» ДО первого клика, а не только после.
  document.querySelectorAll('[data-disclose-url]').forEach(function (trigger) {
    trigger.setAttribute('aria-expanded', 'false');
  });

  document.addEventListener('click', function (e) {
    var trigger = e.target.closest('[data-disclose-url]');
    if (!trigger) { return; }
    e.preventDefault();
    toggle(trigger);
  });

  // Клавиатура — только для триггеров, у которых нет своей нативной активации (кнопка/ссылка уже
  // шлют click по Enter/Space сами; вызвать toggle() ещё раз для них значило бы открыть и тут же
  // закрыть панель).
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Enter' && e.key !== ' ') { return; }
    var trigger = e.target.closest('[data-disclose-url]');
    if (!trigger || trigger.tagName === 'BUTTON' || trigger.tagName === 'A') { return; }
    e.preventDefault();
    toggle(trigger);
  });
})();
