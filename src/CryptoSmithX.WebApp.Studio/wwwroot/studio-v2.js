/* Раскрытие групп второй страницы актива.

   Одна кнопка на группу плюс «развернуть всё». Раскрытие идёт НА МЕСТЕ, в той же строке: модалка
   перекрыла бы таблицу, то есть ровно то сравнение площадок, ради которого по группе и кликают.

   Высота строки задаётся самой высокой открытой группой, поэтому открыть одну стоит почти столько
   же места, сколько открыть все — «развернуть всё» здесь не роскошь, а основной режим. */
/* Раскрытия групп больше нет: все колонки видны сразу, обычными колонками таблицы, и
   разворачивать нечего. Здесь стоял набор открытых групп, кнопка «развернуть всё» и
   восстановление их состояния после каждой посылки живого потока. */

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
      // P0-1: which population this cut ranks against — set from the button, same as the three
      // above, so it can never say something the table is not actually doing.
      var rank = document.getElementById('cut-now-rank');
      if (rank) { rank.textContent = pick.getAttribute('data-rank-note') || ''; }
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

  // ПОСЛЕ ПОСЫЛКИ НЕ СОРТИРУЕМ. Сортировка идёт по живому полю — по умолчанию по цене, — и
  // пересчёт порядка на каждой посылке переставлял строки местами каждые несколько секунд:
  // измерено на живой странице как 28 сдвигов раскладки за четырнадцать секунд и вся оставшаяся
  // CLS 0.098, при том что высота строк не менялась ни разу.
  //
  // Порядок отвечает на вопрос, заданный кликом, и держится до следующего клика. Таблица, которая
  // пересортировывает себя под руками у читателя, не отвечает ни на один вопрос: пока глаз идёт от
  // первой строки ко второй, первой там уже нет.

})();

// P0-3 (UX audit): hovering a gap row highlights the matrix cell it belongs to, and hovering a
// GAP/THROTTLED cell highlights the gap rows that explain it — one delegated pair of listeners,
// matching on data-cell ("{segment}:{dataset}", set server-side by both the matrix and the gap
// list from the same key), rather than two places independently deciding which gap a cell means.
(function () {
  function matches(key) {
    return document.querySelectorAll('[data-cell="' + key + '"]');
  }

  document.addEventListener('pointerover', function (e) {
    var el = e.target.closest('[data-cell]');
    if (!el) { return; }
    matches(el.getAttribute('data-cell')).forEach(function (m) {
      m.classList.add(m.classList.contains('v2-cov-cell') ? 'v2-cov-cell--hover' : 'v2-gap--hover');
    });
  });

  document.addEventListener('pointerout', function (e) {
    var el = e.target.closest('[data-cell]');
    if (!el) { return; }
    matches(el.getAttribute('data-cell')).forEach(function (m) {
      m.classList.remove('v2-cov-cell--hover', 'v2-gap--hover');
    });
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
/* Тень у правого края полосы 1 (Prompt 2, U-5).

   Сама колонка липнет CSS'ом (position:sticky) — этому файлу нужно только сказать, есть ли ещё
   что прокручивать. Порог в один пиксель против дробного scrollWidth/clientWidth: без него тень
   иногда не гасла на последнем пикселе прокрутки — сравнение "<" против почти равных чисел с
   плавающей точкой то было true, то false в зависимости от округления браузера.

   ResizeObserver, а не подписка на событие живого потока: этому файлу вообще нельзя знать о нём
   (см. A_push_changes_figures_and_never_the_order) — не потому что этой тени тот же тест не
   касается впрямую, а потому что правило для файла простое и его не стоит дырявить ради одного
   удобного случая. Наблюдатель ширины ловит ЛЮБУЮ смену scrollWidth — саму посылку, смену числа
   колонок где-то ещё, — без явной подписки на конкретное имя события. */
(function () {
  var scroll = document.querySelector('.v2-scroll');
  if (!scroll) { return; }

  function update() {
    var more = scroll.scrollWidth - scroll.clientWidth > 1 && scroll.scrollLeft < scroll.scrollWidth - scroll.clientWidth - 1;
    if (more) { scroll.setAttribute('data-scroll-more', ''); }
    else { scroll.removeAttribute('data-scroll-more'); }
  }

  scroll.addEventListener('scroll', update, { passive: true });
  if (window.ResizeObserver) {
    new ResizeObserver(update).observe(scroll);
  } else {
    window.addEventListener('resize', update);
  }
  update();
})();
