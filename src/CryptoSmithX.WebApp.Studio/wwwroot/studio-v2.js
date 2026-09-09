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
})();

