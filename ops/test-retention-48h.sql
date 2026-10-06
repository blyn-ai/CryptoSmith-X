-- Включение 48-часового ретеншена на ТЕСТЕ (миграция 0068).
--
-- Что делает: переводит нарезку партиций на сутки, включает удаление через 48 часов и сносит
-- старые МЕСЯЧНЫЕ партиции, которые иначе не дали бы дневным появиться — месяц уже покрывает
-- сегодняшний диапазон, и дневная партиция поверх него не создаётся (см. 0068, раздел 2).
--
-- ЭТО УДАЛЯЕТ ДАННЫЕ И ПРЕДНАЗНАЧЕНО ТОЛЬКО ДЛЯ ТЕСТА. На проде стоит обратное правило: снимок
-- рынка никто не продаст обратно, поэтому retention_delete_after_hours там остаётся 0. Чтобы
-- скрипт нельзя было запустить на проде по мышечной памяти, он требует явного подтверждения:
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1 -v contour=test' < ops/test-retention-48h.sql
--
-- Без `-v contour=test` скрипт отказывается работать.

\if :{?contour}
\else
  \echo 'ОТКАЗ: запусти с -v contour=test. Скрипт удаляет данные и предназначен только для теста.'
  \quit
\endif

select :'contour' = 'test' as contour_is_test \gset
\if :contour_is_test
\else
  \echo 'ОТКАЗ: contour не равен test. На проде ретеншен остаётся выключенным (0068).'
  \quit
\endif

begin;

-- ---------------------------------------------------------------------------
-- 1. Настройки. С этого момента хаб нарезает партиции по суткам и дропает те,
--    чей диапазон целиком старше 48 часов.
-- ---------------------------------------------------------------------------
update setting set value = 'day', updated_at = now(), updated_by = 'ops/test-retention-48h.sql'
 where key = 'partition_granularity';

update setting set value = '48', updated_at = now(), updated_by = 'ops/test-retention-48h.sql'
 where key = 'retention_delete_after_hours';

-- ---------------------------------------------------------------------------
-- 2. Старые месячные партиции. Пока они есть, дневные не создаются: их диапазон
--    уже покрыт. Дропаются ВСЕ месячные — включая текущий месяц, иначе до
--    первого числа следующего ничего не изменится.
-- ---------------------------------------------------------------------------
do $$
declare
    part record;
    freed text;
    total bigint := 0;
begin
    for part in
        select c.relname, pg_total_relation_size(c.oid) as bytes
          from pg_class c
          join pg_inherits h on h.inhrelid = c.oid
          join pg_class p on p.oid = h.inhparent
         where p.relname in ('market_snapshot', 'market_candle', 'trade', 'book_topn', 'market_price_candle')
           -- только месячные: parent_YYYY_MM. Дневные (parent_YYYY_MM_DD) не трогаем —
           -- за них отвечает ретеншен, и среди них есть сегодняшняя, в которую идёт запись.
           and c.relname ~ ('^' || p.relname || '_[0-9]{4}_[0-9]{2}$')
         order by pg_total_relation_size(c.oid) desc
    loop
        freed := pg_size_pretty(part.bytes);
        total := total + part.bytes;
        raise notice 'дроплю % (%)', part.relname, freed;
        execute format('drop table if exists %I', part.relname);
    end loop;

    raise notice 'всего освобождено: %', pg_size_pretty(total);
end $$;

-- ---------------------------------------------------------------------------
-- 3. Партиции на сейчас и на завтра, чтобы запись не встала до следующего
--    прохода хаба.
-- ---------------------------------------------------------------------------
select ensure_next_partitions(t::regclass, now())
  from unnest(array['market_snapshot', 'market_candle', 'trade', 'book_topn', 'market_price_candle']) as t;

select key, value from setting
 where key in ('partition_granularity', 'retention_delete_after_hours') order by 1;

select p.relname as tablica, count(*) as partitsij
  from pg_class c
  join pg_inherits h on h.inhrelid = c.oid
  join pg_class p on p.oid = h.inhparent
 where p.relname in ('market_snapshot', 'market_candle', 'trade', 'book_topn', 'market_price_candle')
 group by 1 order by 1;

commit;
