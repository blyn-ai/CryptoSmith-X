/* Раскрытие групп второй страницы актива.

   Одна кнопка на группу плюс «развернуть всё». Раскрытие идёт НА МЕСТЕ, в той же строке: модалка
   перекрыла бы таблицу, то есть ровно то сравнение площадок, ради которого по группе и кликают.

   Высота строки задаётся самой высокой открытой группой, поэтому открыть одну стоит почти столько
   же места, сколько открыть все — «развернуть всё» здесь не роскошь, а основной режим. */
(function () {
  var open = new Set();

  function apply() {
    // НА <body>, снаружи заменяемых полос: живой поток меняет таблицу целиком, и hidden,
    // проставленный внутри неё, уезжает вместе с разметкой — группы схлопывались на каждой
    // посылке, а скрипт открывал их обратно следующим кадром. Показывает состояние CSS.
    document.body.setAttribute('data-open', Array.from(open).join(' '));

    document.querySelectorAll('.v2-gtoggle[data-group]').forEach(function (el) {
      el.setAttribute('aria-expanded', open.has(el.getAttribute('data-group')) ? 'true' : 'false');
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

  // Восстанавливать после посылки больше нечего: состояние лежит на <body>, снаружи заменяемой
  // полосы, и показывает его CSS. Осталось одно — aria-expanded на кнопках, которые приехали
  // новыми вместе с разметкой.
  document.addEventListener('csx-studio-live', function () { apply(); });
})();

/* Выбор измерения: один селектор на полосы 2 и 3.

   Оба варианта каждой полосы отрисованы сервером и просто скрыты — рядов тут пять площадок на
   шесть измерений по двадцать четыре точки, это килобайты, а не мегабайты. Взамен страница
   переключается мгновенно, работает при выключенном скрипте (виден вариант по умолчанию) и не
   заводит второго источника данных, который мог бы разойтись с таблицей выше. */
(function () {
  var picks = document.querySelectorAll('.v2-cutpick');
  if (!picks.length) { return; }

  // Таблица сортируется по ВЫБРАННОМУ полю, и это не украшение: полоса 2 ниже уже стоит
  // отсортированной, и таблица над ней, идущая в другом порядке, заставляет искать одну и ту же
  // площадку дважды. Пустое значение уезжает вниз в обе стороны — «не измерено» не хуже и не
  // лучше, его не с чем сравнивать.
  var ASC = { spread: true };

  function sort(key) {
    var grid = document.querySelector('.v2-grid');
    if (!grid) { return; }

    var rows = Array.prototype.slice.call(grid.querySelectorAll('.v2-row'));
    if (rows.length < 2) { return; }

    var asc = ASC[key] === true;
    var ranked = rows.slice().sort(function (a, b) {
      var x = parseFloat(a.getAttribute('data-sort-' + key));
      var y = parseFloat(b.getAttribute('data-sort-' + key));
      var xn = isNaN(x), yn = isNaN(y);
      if (xn && yn) { return 0; }
      if (xn) { return 1; }
      if (yn) { return -1; }
      return asc ? x - y : y - x;
    });

    // ПОРЯДОК ЗАДАЁТСЯ СТИЛЕМ, А УЗЛЫ НЕ ДВИГАЮТСЯ. Это не оптимизация, это единственный
    // способ не подраться с живым потоком: morph в studio-live.js сопоставляет детей ПО
    // ИНДЕКСУ, и стоило переставить строки через appendChild, как порядок в DOM переставал
    // совпадать с порядком в приходящем фрагменте — тогда каждая посылка брала первую строку
    // сервера и переписывала ею первую строку экрана целиком, со всеми клетками, а сортировка
    // тащила строки обратно. Замерено на живой странице: перекладка каждые 0.3 с, 718 правок
    // атрибутов и 407 вставок узлов за четырнадцать секунд. Читателю это видно как таблицу,
    // которая мигает.
    //
    // order трогает только раскладку. DOM остаётся в серверном порядке, morph снова патчит
    // подобное подобным, и переписывать нечего.
    ranked.forEach(function (r, i) {
      var want = String(i);
      if (r.style.order !== want) { r.style.order = want; }
    });
  }

  function show(key, quiet) {
    document.body.setAttribute('data-cut', key);
    picks.forEach(function (b) {
      b.setAttribute('aria-pressed', b.getAttribute('data-cut') === key ? 'true' : 'false');
    });
    // Каретки в шапке таблицы показывают то же состояние: стрелка у группы, чьё поле сейчас
    // внизу, — «вы здесь», а не «сюда можно».
    document.querySelectorAll('.v2-caret[data-goto-cut]').forEach(function (c) {
      c.setAttribute('aria-pressed', c.getAttribute('data-goto-cut') === key ? 'true' : 'false');
    });
    // Колонка выбранного поля помечена в шапке таблицы. Стрелка, которая только приглашает и
    // никогда не говорит «вы здесь», — половина контрола.
    document.querySelectorAll('.v2-hcell[data-cut]').forEach(function (h) {
      h.classList.toggle('is-cut', h.getAttribute('data-cut') === key);
    });
    sort(key);
    // Заголовок и подписи обеих полос называют ВЫБРАННОЕ поле. Без этого селектор менял
    // содержимое, а шапка над ним продолжала описывать книги — то есть подпись противоречила
    // тому, что под ней нарисовано.
    var pick = document.querySelector('.v2-cutpick[data-cut="' + key + '"]');
    if (pick) {
      var title = document.getElementById('cut-now-title');
      var note = document.getElementById('cut-now-sub');
      var series = document.getElementById('cut-time-sub');
      if (title) { title.textContent = pick.getAttribute('data-title'); }
      if (note) { note.textContent = pick.getAttribute('data-note'); }
      if (series) { series.textContent = pick.getAttribute('data-series'); }
      var unit = document.getElementById('cut-now-unit');
      if (unit) { unit.textContent = pick.getAttribute('data-unit'); }
    }

    // Свечи меряют себя при вставке; если их полоса была скрыта в этот момент, ширина вышла
    // нулевой и график остался невидим. Библиотека сама этого не замечает — просим пересчитать.
    //
    // ТОЛЬКО ПО КЛИКУ. На каждой посылке живого потока это заставляло библиотеку перемерять и
    // перерисовать все графики раз в секунду, а ширина при этом не менялась ни разу.
    if (!quiet) { window.dispatchEvent(new Event('resize')); }
  }

  picks.forEach(function (b) {
    b.addEventListener('click', function () { show(b.getAttribute('data-cut')); });
  });

  // Пересортировка после посылки: строки приехали в серверном порядке, а выбранный срез лежит
  // на <body> и никуда не делся. Ни скрытий, ни resize — только порядок, и только если он
  // действительно другой.
  document.addEventListener('csx-studio-live', function () {
    sort(document.body.getAttribute('data-cut') || 'price');
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
      row.setAttribute('data-venue', t.listing);
      row.appendChild(cell(null, t.at));
      row.appendChild(cell(null, t.venue));
      row.appendChild(cell('v2-side v2-side--' + t.side, t.side));
      row.appendChild(cell('v2-r', t.price));
      row.appendChild(cell('v2-r', t.qty));
      row.appendChild(cell('v2-kind', t.loud ? t.kind : ''));
      frag.appendChild(row);
    });
    body.replaceChildren(frag);
    document.dispatchEvent(new CustomEvent('csx-tape-drawn'));
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


/* Колонка под курсором.

   Делегированно на документе, а не на клетках: живой поток заменяет полосу 1 целиком, и
   обработчики, повешенные на клетки, уехали бы вместе с ними при первой же посылке — молча, и
   ровно тогда, когда страница стала интереснее всего.

   Клавиатура получает то же самое: :focus-within в стилях сюда не годится, потому что подсветить
   надо не предка сфокусированного элемента, а СЕСТЁР в других строках. */
(function () {
  var current = null;

  function light(key) {
    if (key === current) { return; }
    current = key;
    document.querySelectorAll('.v2-cell.is-col,.v2-hcell.is-col').forEach(function (el) {
      el.classList.remove('is-col');
    });
    if (!key) { return; }
    document.querySelectorAll('.v2-cell[data-group="' + key + '"],.v2-hcell[data-group="' + key + '"]')
      .forEach(function (el) { el.classList.add('is-col'); });
  }

  function keyOf(target) {
    if (!target || !target.closest) { return null; }
    var cell = target.closest('.v2-cell[data-group],.v2-hcell[data-group]');
    return cell ? cell.getAttribute('data-group') : null;
  }

  // pointerover всплывает и приходит на КАЖДЫЙ элемент, в который вошёл курсор, поэтому гашение
  // отдельным обработчиком не нужно: ушёл из клетки в подпись или в другую полосу — keyOf вернул
  // null, и колонка погасла тем же вызовом, которым зажглась.
  document.addEventListener('pointerover', function (e) { light(keyOf(e.target)); });
  document.addEventListener('focusin', function (e) { light(keyOf(e.target)); });
})();

/* Режим на телефоне.

   По умолчанию — сравнение одного поля: на 390 px таблица семи групп на пять площадок это 950 px
   сетки, то есть горизонтальная прокрутка поверх вертикальной и ни одного сравнения, которое
   можно сделать глазами. Переключатель отдаёт карточку на листинг, когда нужна одна площадка
   целиком. Выбор держится в этой вкладке и не переживает её: это ответ на «как я сейчас держу
   телефон», а не настройка. */
(function () {
  var btn = document.getElementById('m-mode');
  if (!btn) { return; }

  var label = btn.querySelector('em');

  function set(listing) {
    if (listing) {
      document.body.setAttribute('data-mobile-mode', 'listing');
    } else {
      document.body.removeAttribute('data-mobile-mode');
    }
    btn.setAttribute('aria-pressed', listing ? 'true' : 'false');
    if (label) { label.textContent = listing ? 'By listing' : 'Compare one field'; }
  }

  btn.addEventListener('click', function () {
    set(btn.getAttribute('aria-pressed') !== 'true');
  });

  set(false);
})();

/* Фильтр ленты по площадке.

   Делегированно и по data-venue, потому что тело ленты переписывает опрос FOLLOW: обработчики,
   повешенные на строки, ушли бы с первой же заменой, а выбранный чип остался бы нажатым — то
   есть страница показывала бы фильтр, которого больше нет. */
(function () {
  var bar = document.querySelector('.v2-tape-filter');
  var body = document.getElementById('tape-body');
  if (!bar || !body) { return; }

  var chosen = '';

  function apply() {
    body.querySelectorAll('.v2-tape-row').forEach(function (row) {
      row.hidden = chosen !== '' && row.getAttribute('data-venue') !== chosen;
    });
    bar.querySelectorAll('.v2-chip').forEach(function (c) {
      c.setAttribute('aria-pressed', c.getAttribute('data-tape-venue') === chosen ? 'true' : 'false');
    });
  }

  bar.addEventListener('click', function (e) {
    var chip = e.target.closest ? e.target.closest('.v2-chip') : null;
    if (!chip) { return; }
    chosen = chip.getAttribute('data-tape-venue') || '';
    apply();
  });

  // Лента пришла заново — фильтр применяется к новым строкам, а не остаётся нажатым над
  // неотфильтрованным списком.
  document.addEventListener('csx-tape-drawn', apply);
})();

/* Переключатель числа колонок, один на полосу.

   Контрол знает, чем управляет, из разметки (data-cols-target) — так его можно поставить в любую
   полосу, где карточек больше одной, не трогая этот файл. Выбор помнится по ключу полосы: на
   странице их два, и общий ключ означал бы, что книги и графики нельзя разложить по-разному. */
(function () {
  document.querySelectorAll('.v2-cols[data-cols-target]').forEach(function (group) {
    var target = group.getAttribute('data-cols-target');
    var key = 'csx-v2-cols-' + group.getAttribute('data-cols-key');
    var band = group.closest('section') || document;

    function set(n, remember) {
      document.body.setAttribute('data-cols-' + group.getAttribute('data-cols-key'), n);
      group.querySelectorAll('.v2-cols-btn').forEach(function (b) {
        b.setAttribute('aria-pressed', b.getAttribute('data-cols') === n ? 'true' : 'false');
      });
      if (remember) {
        try { window.localStorage.setItem(key, n); } catch (e) { /* приватное окно */ }
      }
      // Свечи меряют себя при вставке и после смены ширины сами не пересчитываются.
      window.dispatchEvent(new Event('resize'));
    }

    group.addEventListener('click', function (e) {
      var btn = e.target.closest ? e.target.closest('.v2-cols-btn') : null;
      if (btn) { set(btn.getAttribute('data-cols'), true); }
    });

    var first = band.querySelector(target);
    var stored = null;
    try { stored = window.localStorage.getItem(key); } catch (e) { /* приватное окно */ }
    set(stored || (first && first.getAttribute('data-cols')) || '4', false);
  });
})();