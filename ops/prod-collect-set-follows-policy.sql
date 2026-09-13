-- Приводит набор собираемых инструментов на ПРОДЕ к политике, по которой уже
-- живёт тест.
--
-- ПОЛИТИКА (0029, plans/collection-policy.md): весь рынок собирается только у
-- Kraken Futures — «280 инструментов kraken-futures, оставленных включёнными по
-- прямому указанию». У остальных площадок собирается инструмент, чья базовая
-- валюта входит в 25 авто-одобренных активов (asset.auto_collect).
--
-- ТЕСТ ей следует точно: binance-usdm 44, hyperliquid 25, weex-futures 25 — и у
-- всех трёх собираемое совпадает с правилом без единого расхождения.
--
-- ПРОД — нет, замерено 2026-09-13:
--
--     binance-usdm    566 собирается, по правилу 44   → лишних 522
--     weex-futures    998 собирается, по правилу 25   → лишних 973
--     hyperliquid     178 собирается, по правилу 25   → лишних 153
--     kraken-futures  275 — полный рынок, как и задумано, не трогается
--
-- Цена этого расхождения видна в журнале: binance-usdm получил 55 ответов 429 за
-- 12 минут, глубина не обновлялась два часа; weex-futures/discovery — тоже 429.
-- Бюджет у прода и теста одинаковый; на тесте отказов нет.
--
-- Binance: 566 строк без отметки — на проде их просто никогда не выключали.
-- WEEX и Hyperliquid: 960 и 152 строки включены 2026-09-07 с заметкой «Вне
-- списка авто-одобрения (25 активов), решение владельца» — заметка обосновывает
-- ВЫКЛЮЧЕНИЕ, а значение стоит «включено». На тесте те же строки выключены.
--
-- Скрипт выключает только лишнее по правилу и ничего не включает. Kraken не
-- трогает. Прошлые наблюдения не удаляет.
--
-- HOW TO RUN IT (только прод; тест уже в этом состоянии)
--
--   ssh -F .local/ssh-config csx-datahub-jump \
--       'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/prod-collect-set-follows-policy.sql

begin;

create temporary table off_policy on commit drop as
select ei.id, ei.segment_code
  from exchange_instrument ei
  left join asset a on a.code = ei.base_asset
 where ei.segment_code in ('binance-usdm', 'weex-futures', 'hyperliquid')
   and ei.collect
   and not coalesce(a.auto_collect, false);

select 'будет выключено', segment_code, count(*) from off_policy group by 2 order by 2;

update exchange_instrument ei
   set collect = false,
       collect_note = 'Прод приведён к политике 0029: вне 25 авто-одобренных активов; полный рынок только у Kraken Futures.',
       collect_changed_at = now(),
       collect_changed_by = 'ops/prod-collect-set-follows-policy.sql'
  from off_policy o
 where ei.id = o.id;

select 'после', ei.segment_code, count(*) filter (where ei.collect), count(*)
  from exchange_instrument ei
 where ei.segment_code in ('binance-usdm', 'weex-futures', 'hyperliquid', 'kraken-futures')
 group by 2 order by 2;

commit;
