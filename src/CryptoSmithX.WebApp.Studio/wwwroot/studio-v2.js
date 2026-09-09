/* Раскрытие групп второй страницы актива.

   Одна кнопка на группу плюс «развернуть всё». Раскрытие идёт НА МЕСТЕ, в той же строке: модалка
   перекрыла бы таблицу, то есть ровно то сравнение площадок, ради которого по группе и кликают.

   Высота строки задаётся самой высокой открытой группой, поэтому открыть одну стоит почти столько
   же места, сколько открыть все — «развернуть всё» здесь не роскошь, а основной режим. */
(function () {
  var open = new Set();

  function apply() {
    document.querySelectorAll('[data-group]').forEach(function (el) {
      var on = open.has(el.getAttribute('data-group'));
      if (el.classList.contains('v2-cell')) {
        var fields = el.querySelector('.v2-fields');
        if (fields) { fields.hidden = !on; }
      } else if (el.tagName === 'BUTTON') {
        el.setAttribute('aria-expanded', on ? 'true' : 'false');
      }
    });
    var all = document.getElementById('expand-all');
    var total = document.querySelectorAll('.v2-gtoggle').length;
    if (all) {
      // Только состояние, подпись не трогаем: у тумблера состояние показывает он сам, а
      // переписывание надписи на «Collapse all» заставляет читать текст, чтобы понять,
      // включено сейчас или нет.
      all.setAttribute('aria-pressed', open.size === total ? 'true' : 'false');
    }
  }

  document.querySelectorAll('.v2-gtoggle').forEach(function (b) {
    b.addEventListener('click', function () {
      var k = b.getAttribute('data-group');
      if (open.has(k)) { open.delete(k); } else { open.add(k); }
      apply();
    });
  });

  var all = document.getElementById('expand-all');
  if (all) {
    all.addEventListener('click', function () {
      var keys = Array.prototype.map.call(
        document.querySelectorAll('.v2-gtoggle'),
        function (b) { return b.getAttribute('data-group'); });
      if (open.size === keys.length) { open.clear(); } else { keys.forEach(function (k) { open.add(k); }); }
      apply();
    });
  }

  apply();
})();

/* Выбор измерения: один селектор на полосы 2 и 3.

   Оба варианта каждой полосы отрисованы сервером и просто скрыты — рядов тут пять площадок на
   шесть измерений по двадцать четыре точки, это килобайты, а не мегабайты. Взамен страница
   переключается мгновенно, работает при выключенном скрипте (виден вариант по умолчанию) и не
   заводит второго источника данных, который мог бы разойтись с таблицей выше. */
(function () {
  var picks = document.querySelectorAll('.v2-cutpick');
  if (!picks.length) { return; }

  function show(key) {
    document.querySelectorAll('.v2-nows, .v2-cuts').forEach(function (el) {
      el.hidden = el.getAttribute('data-cut') !== key;
    });
    picks.forEach(function (b) {
      b.setAttribute('aria-pressed', b.getAttribute('data-cut') === key ? 'true' : 'false');
    });
    // Свечи меряют себя при вставке; если их полоса была скрыта в этот момент, ширина вышла
    // нулевой и график остался невидим. Библиотека сама этого не замечает — просим пересчитать.
    window.dispatchEvent(new Event('resize'));
  }

  picks.forEach(function (b) {
    b.addEventListener('click', function () { show(b.getAttribute('data-cut')); });
  });

  // Ссылки из полосы 5 («The cut → Funding») ведут не просто к якорю: они переключают срез,
  // иначе читатель приезжает к селектору и видит на нём то же, что видел до клика.
  document.querySelectorAll('[data-goto-cut]').forEach(function (a) {
    var key = a.getAttribute('data-goto-cut');
    if (!key) { return; }
    a.addEventListener('click', function () { show(key); });
  });
})();

/* Лента, которая идёт сама.

   FOLLOW — единственный тумблер на странице, за которым стоит запрос: всё остальное здесь
   снимок одного мгновения, а лента поток. Раз в пять секунд просим те же восемнадцать сделок
   и подставляем их целиком; свои строки страница не склеивает и не докладывает — ответ и есть
   лента, а склейка двух ответов родила бы ленту, которой не было ни в одном.

   Выключенный тумблер не «замораживает данные», а перестаёт спрашивать: то, что вы читаете,
   остаётся ровно тем, чем было, и это честнее паузы поверх идущего потока. */
(function () {
  var btn = document.getElementById('tape-follow');
  var body = document.getElementById('tape-body');
  if (!btn || !body) { return; }

  var url = btn.getAttribute('data-url');
  var timer = null;

  function cell(cls, text) {
    var el = document.createElement('span');
    if (cls) { el.className = cls; }
    el.textContent = text;
    return el;
  }

  function draw(rows) {
    var frag = document.createDocumentFragment();
    if (!rows.length) {
      var p = document.createElement('p');
      p.className = 'v2-empty';
      p.textContent = 'No fills recorded in the last hour.';
      frag.appendChild(p);
    }
    rows.forEach(function (t) {
      var row = document.createElement('div');
      row.className = 'v2-tape-row' + (t.loud ? ' v2-tape-row--loud' : '');
      row.appendChild(cell(null, t.at));
      row.appendChild(cell(null, t.venue));
      row.appendChild(cell('v2-side v2-side--' + t.side, t.side));
      row.appendChild(cell('v2-r', t.price));
      row.appendChild(cell('v2-r', t.qty));
      row.appendChild(cell('v2-kind', t.kind));
      frag.appendChild(row);
    });
    body.replaceChildren(frag);
  }

  function poll() {
    fetch(url, { headers: { 'Accept': 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (rows) { if (rows) { draw(rows); } })
      .catch(function () { /* сеть моргнула — следующий круг через пять секунд */ });
  }

  function set(on) {
    btn.setAttribute('aria-checked', on ? 'true' : 'false');
    if (on) { poll(); timer = window.setInterval(poll, 5000); return; }
    if (timer) { window.clearInterval(timer); timer = null; }
  }

  btn.addEventListener('click', function () {
    set(btn.getAttribute('aria-checked') !== 'true');
  });

  // Вкладка в фоне не смотрит на ленту, а запросы шлёт. Останавливаемся и догоняем одним
  // запросом при возврате — иначе открытая на ночь вкладка это 17 280 обращений ни за чем.
  document.addEventListener('visibilitychange', function () {
    if (btn.getAttribute('aria-checked') !== 'true') { return; }
    if (document.hidden) {
      if (timer) { window.clearInterval(timer); timer = null; }
    } else if (!timer) {
      poll();
      timer = window.setInterval(poll, 5000);
    }
  });
})();

