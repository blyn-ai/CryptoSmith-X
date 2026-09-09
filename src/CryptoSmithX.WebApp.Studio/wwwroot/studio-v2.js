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
