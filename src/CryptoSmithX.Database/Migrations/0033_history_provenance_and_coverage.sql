-- ============================================================================
-- CryptoSmith X — миграция 0033: провенанс у funding_rate_history, таблица
-- `coverage` (положительная половина «пропуск хранится как запись о
-- пропуске» — collector_gap уже отрицательная), окно бюджета запросов.
--
-- Часть фазы «только база» (см. 0030).
--
--
-- ----------------------------------------------------------------------------
-- funding_rate_history: инвариант «у каждого наблюдения есть received_at»
-- нарушен с рождения таблицы (0006) — funding_time там есть, а КОГДА МЫ ЭТО
-- ПОЛУЧИЛИ, не хранится вовсе. Три новые колонки чинят это на будущее, без
-- бэкфилла нового поля значением, которого не существует: received_at у
-- строк ДО этой миграции остаётся NULL честно — момент получения этих строк
-- не восстановить, придумывать его значило бы утверждать то, чего не знаем.
-- ----------------------------------------------------------------------------
alter table funding_rate_history
    add column received_at            timestamptz,
    add column known_from             timestamptz,
    add column source                 text check (source in ('rest', 'backfill')),
    add column funding_interval_hours smallint check (funding_interval_hours > 0);

comment on column funding_rate_history.received_at is
    'Когда МЫ получили эту строку. NULL у строк, записанных до 2026-09-08 (миграция 0033): '
    'таблица существовала без этого поля с 0006, момент получения этих строк не сохранён нигде '
    'и не восстановим — NULL здесь означает «неизвестно», не «совпадает с funding_time».';
comment on column funding_rate_history.source is
    '''rest'' — обычный проход коллектора; ''backfill'' — догоняющий запрос старой истории. NULL '
    'у строк до 0033 по той же причине, что и у received_at.';
comment on column funding_rate_history.funding_interval_hours is
    'Интервал ЭТОГО конкретного платежа, если известен. Заполнен миграцией там, где вычислен точно '
    'из разницы с предыдущим платежом того же инструмента (см. ниже) — измерение, не предположение. '
    'NULL — не вычислен: разница с соседним платежом не легла ровно в {1,2,4,8} часов (пропущенный '
    'платёж даёт удвоенный зазор — это дыра в истории, а не другой интервал) или соседа не было '
    '(первый платёж инструмента в таблице).';


-- Заполнение — только там, где интервал измерен ТОЧНО: разница funding_time с предыдущим платежом
-- того же инструмента раундится до целого числа часов из {1,2,4,8}. round(..., 3) до сравнения —
-- у Binance funding_time дрейфует на миллисекунды между платежами, точное равенство double дало бы
-- NULL почти везде вместо честного совпадения.
with d as (
  select exchange_instrument_id, funding_time,
         round((extract(epoch from funding_time - lag(funding_time)
                over (partition by exchange_instrument_id order by funding_time)) / 3600)::numeric, 3) as h
    from funding_rate_history)
update funding_rate_history f set funding_interval_hours = d.h::smallint
  from d
 where d.exchange_instrument_id = f.exchange_instrument_id
   and d.funding_time = f.funding_time
   and d.h in (1, 2, 4, 8);


-- ----------------------------------------------------------------------------
-- coverage — «мы спрашивали этот диапазон и вот что получили в ответ», для
-- каждого запроса истории (candles / trades / funding / oi / liquidations).
-- collector_gap — противоположный факт: коллектор упал и НЕ узнал, что там на
-- бирже. coverage — коллектор дошёл до биржи и получил ответ, который надо
-- запомнить как ответ, а не как молчание.
-- ----------------------------------------------------------------------------
create table coverage (
    id                     bigint      generated always as identity primary key,
    exchange_instrument_id integer     not null references exchange_instrument (id),
    dataset_code            text        not null references dataset (code),
    range_from               timestamptz not null,
    range_to                 timestamptz not null,
    returned                  integer     not null check (returned >= 0),
    requested_at               timestamptz not null,
    reason                      text        check (reason in ('throttled', 'error', 'timeout', 'limit_hit', 'beyond_history')),
    run_id                        bigint      references collector_run (id) on delete set null,
    check (range_to > range_from)
);

create index coverage_lookup on coverage (exchange_instrument_id, dataset_code, range_from);

comment on table coverage is
    'Положительная половина «пропуск хранится как запись о пропуске» — collector_gap отрицательная '
    '(коллектор упал). Правило чтения: нет строки внутри запрошенного диапазона = биржа ничего не '
    'вернула = ИЗВЕСТНО, что пусто; диапазон вне покрытия = не спрашивали, неизвестность честная, '
    'не «пусто». Диапазон старше history_depth площадки (segment_dataset_capability) НИКОГДА не '
    'пишется сюда как покрытый — иначе пустой ответ на запрос за пределами истории венью станет '
    'записью «сделок не было», хотя на самом деле «биржа дальше этой даты вообще не отвечает».';
comment on column coverage.range_to is
    'РЕАЛЬНО покрытый конец диапазона — не тот, что был запрошен, если биржа отдала меньше (limit_hit) '
    'или оборвала раньше (timeout/error): тогда range_to меньше запрошенного, и remainder остаётся '
    'непокрытым для следующей попытки.';
comment on column coverage.returned is
    'Сколько строк реально вернула биржа за [range_from, range_to). 0 — валидный, отличный от '
    'отсутствия строки: означает «спросили и биржа подтвердила, что там пусто», не «не спрашивали».';
comment on column coverage.reason is
    'Почему range_to меньше запрошенного (NULL, если получили весь запрошенный диапазон целиком). '
    'beyond_history — площадка ответила «дальше не храню» явно (не путать с limit_hit — тот про '
    'лимит строк за один вызов, а не про историческую глубину венью).';
comment on column coverage.run_id is
    'Проход коллектора, которым получена эта строка покрытия — for debugging, не для целостности: '
    'on delete set null, потому что ротация collector_run не должна утаскивать за собой факт о том, '
    'что диапазон был проверен.';


-- ----------------------------------------------------------------------------
-- exchange.request_budget_window_s — окна бюджета как понятия в схеме не было,
-- отсюда «200 запросов на 10 секунд» (гипотеза WEEX, не измеренная — см. 0021)
-- физически некуда было записать иначе как per_s.
-- ----------------------------------------------------------------------------
alter table exchange
    add column request_budget_window_s integer not null default 1 check (request_budget_window_s > 0);

comment on column exchange.request_budget_window_s is
    'Окно, за которое считается request_budget_per_s — секунд. Default 1 сохраняет сегодняшнюю '
    'семантику per_s без изменений ни у одной площадки; значения этой миграцией не трогаются нигде '
    '(гипотеза «200 на 10 с» для WEEX не подтверждена измерением — см. 0021, п. «чего не делаем» в '
    'плане фазы). Появление этой колонки делает окно ВЫРАЗИМЫМ в схеме; менять его для конкретной '
    'площадки — отдельное решение с измерением за ним, не эта миграция.';


-- ----------------------------------------------------------------------------
-- Гранты
-- ----------------------------------------------------------------------------
grant select on coverage to studio_reader;
