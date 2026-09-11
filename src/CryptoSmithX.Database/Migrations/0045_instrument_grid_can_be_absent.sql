-- ============================================================================
-- CryptoSmith X — миграция 0045: у инструмента может не быть торговой сетки.
--
-- Продолжение 0030 на соседней таблице, и найдено ровно тем же способом — не
-- рассуждением, а падением на живом хосте. 0044 включила Avantis, discovery
-- пошёл и упал:
--
--   23514: new row for relation "exchange_instrument"
--          violates check constraint "exchange_instrument_min_qty_check"
--
-- price_step, qty_step и min_qty объявлены not null и `> 0` с 0001, когда все
-- площадки были книжными. У книжной площадки сетка есть всегда: цена ходит по
-- тику, количество по лоту, и меньше минимума ордер не принимают. Avantis —
-- первая, у которой её НЕТ: цену даёт оракул, а не сетка венью, экспозиция
-- задаётся ноционалом в USDC, и единственное ограничение, которое площадка
-- публикует, — минимальный ноционал (minLevPosUSDC = 100), то есть ровно
-- min_notional, который nullable с самого начала и уже пишется.
--
-- ЧТО ИМЕННО СНИМАЕТСЯ: только `not null`. Проверки `> 0` ОСТАЮТСЯ и не
-- переписываются: в SQL `NULL > 0` даёт NULL, а CHECK пропускает всё, кроме
-- явного false. То есть отсутствие проходит, а ноль и отрицательное по-прежнему
-- нет — что и требуется. Ноль здесь был бы хуже пустоты: он читается как
-- «шаг равен нулю», то есть как измерение, которого никто не делал.
--
-- ЧЕГО ЗДЕСЬ НЕТ: contract_multiplier остаётся not null. У Avantis он реально
-- равен 1 — это факт о контракте, а не пробел, и писать туда NULL значило бы
-- сказать «не знаем» про то, что знаем.
--
-- Обе таблицы, потому что instrument_spec версионирует ровно эти же поля и
-- разошедшиеся ограничения на паре «текущее состояние / его версия» — это
-- инструмент, который пишется, и версия, которая не может записаться.

alter table exchange_instrument alter column price_step drop not null;
alter table exchange_instrument alter column qty_step   drop not null;
alter table exchange_instrument alter column min_qty    drop not null;

alter table instrument_spec alter column price_step drop not null;
alter table instrument_spec alter column qty_step   drop not null;
alter table instrument_spec alter column min_qty    drop not null;

comment on column exchange_instrument.price_step is
    'Шаг цены на площадке. NULL = площадка сетки не публикует — так у рынков, '
    'где цену даёт оракул, а не собственный мэтчинг (segment.market_model = '
    '''oracle_vault''). Не ноль: ноль читался бы как измеренный шаг.';
comment on column exchange_instrument.qty_step is
    'Шаг количества. NULL = не публикуется; см. price_step.';
comment on column exchange_instrument.min_qty is
    'Минимальное количество в ордере. NULL = площадка ограничивает не количество, '
    'а НОЦИОНАЛ — тогда заполнен min_notional, и он единственный настоящий.';
