-- ============================================================================
-- CryptoSmith X — миграция 0031: `instrument_spec` — типизированная история
-- спецификации инструмента (SCD2), вместо единственной актуальной строки,
-- которую `exchange_instrument` держит сегодня.
--
-- Часть фазы «только база» (см. 0030 для контекста задачи и инвариантов).
--
--
-- ----------------------------------------------------------------------------
-- Зачем версии, а не только текущая строка
-- ----------------------------------------------------------------------------
-- `exchange_instrument` сегодня держит РОВНО ОДНУ спецификацию на инструмент —
-- ту, что увидел последний проход discovery. Если биржа поменяла price_step
-- или funding_interval_hours месяц назад, эта миграция об этом уже не знает:
-- discovery перезаписывает поле на месте (UPDATE), история переписана не
-- хранится нигде. Свеча или сделка, случившаяся ДО правки биржи, при чтении
-- сегодняшним контрактом молча получает СЕГОДНЯШНИЙ шаг цены — расхождение,
-- которое ничем не видно, пока кто-то не попробует воспроизвести бэктест на
-- старых данных и не получит другие числа.
--
-- `instrument_spec` — append-only список версий с полуоткрытым интервалом
-- действия. Резолв («какая спека действовала в момент t») — не хранимое поле,
-- а вьюха ниже, потому что интервал зависит от ДВУХ разных источников
-- времени одновременно (когда МЫ увидели новое значение и когда РЕАЛЬНО
-- поменяла биржа), и это единственное место, где их положено путать.
--
--
-- ----------------------------------------------------------------------------
-- Времена — три штуки, разные вопросы
-- ----------------------------------------------------------------------------
--   valid_from         — когда МЫ впервые увидели эти значения (проход discovery).
--   last_seen_at        — когда МЫ последний раз подтвердили те же значения
--                          (тот же spec_hash). Обновляется каждым проходом,
--                          пока ничего не поменялось; новая версия не заводится.
--   venue_effective_at  — когда биржа РЕАЛЬНО поменяла контракт. NULL по
--                          умолчанию — «не знаем», заполняется вручную, если
--                          площадка это где-то объявляет (changelog, форум).
--
-- Между valid_from (мы заметили) и venue_effective_at (биржа поменяла) обычно
-- есть зазор — discovery опрашивает раз в час, а правка биржи не ждёт
-- дискавери. Вьюха ниже называет это честно: eff_from = venue_effective_at,
-- если известен, иначе valid_from — с явной пометкой uncertain_after там, где
-- граница приблизительная.
--
--
-- ----------------------------------------------------------------------------
-- Хэш вместо построчного сравнения
-- ----------------------------------------------------------------------------
-- Новая версия заводится, только если значения РЕАЛЬНО отличаются от
-- последней — иначе каждый час дискавери плодил бы новую строку без
-- изменений. Сравнение — по хэшу, а не по восьми отдельным `is distinct
-- from`, чтобы discovery (следующая фаза) считал его ОДНИМ И ТЕМ ЖЕ способом,
-- что и миграция здесь: расхождение в реализации хэша между сидом и
-- сборщиком означало бы, что после первого же прохода discovery на каждом
-- инструменте заводится лишняя версия «изменилось», хотя ничего не менялось.
-- `trim_scale` убирает разницу представления numeric (1.0 vs 1 — тот же шаг
-- цены, разное количество знаков после запятой в буквальном виде).
--
--
-- ----------------------------------------------------------------------------
-- Чего здесь нет и почему
-- ----------------------------------------------------------------------------
--   * `spec jsonb` вместо типизированных колонок (была в задаче Kraken 1.8).
--     Резолв единицы измерения через `spec->>'contract_multiplier'` — это
--     `segment_dataset_capability` (EAV) в другом пальто: тот же класс
--     проблемы, который 0014 уже решил типизированными колонками там, где
--     форма известна заранее. Здесь форма известна — она совпадает с
--     `exchange_instrument` один в один.
--   * Штамп версии на строках фактов (свеча несёт instrument_spec_version_id).
--     Факты хранят СЫРЫЕ измерения; ничто в их записи не зависело от версии
--     спеки на момент записи — цена сделки одна и та же вне зависимости от
--     того, знаем мы шаг цены или нет. Резолв воспроизводим временем факта
--     без денормализации, и добавление version_id в 26M существующих строк
--     свечей было бы правкой ради удобства чтения, а не новым фактом.
--   * `funding_interval_source not null default 'assumed'` на всех строках
--     (была в задаче Kraken как требование). Это подставное значение — ровно
--     то, что 0028 только что убрал из этой же колонки на `exchange_instrument`.
--     NULL здесь значит «источник не записан», не «предположили».
--   * Дискавери, который пишет в эту таблицу. Сиды 0031 создают ровно одну
--     версию на инструмент из сегодняшнего `exchange_instrument`; дальше
--     таблица не растёт, пока не переписан DiscoveryCollector — фаза 3.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- 1. Функция хэша — SQL, immutable, чтобы discovery считал его так же
-- ----------------------------------------------------------------------------
create function instrument_spec_hash(
    p_status text, p_price_step numeric, p_qty_step numeric, p_min_qty numeric,
    p_min_notional numeric, p_contract_multiplier numeric, p_funding_interval_hours smallint)
returns text language sql immutable as $$
    select md5(concat_ws('|', p_status,
        trim_scale(p_price_step)::text, trim_scale(p_qty_step)::text, trim_scale(p_min_qty)::text,
        coalesce(trim_scale(p_min_notional)::text, ''), trim_scale(p_contract_multiplier)::text,
        coalesce(p_funding_interval_hours::text, '')))
$$;

comment on function instrument_spec_hash is
    'Определяет «те же значения спеки» для instrument_spec: новая версия заводится только когда '
    'хэш меняется. Discovery (следующая фаза) обязан считать ЭТОЙ ЖЕ функцией, не переизобретать '
    'сравнение в коде — иначе первый же проход после расхождения реализаций заведёт лишние версии '
    'на каждом инструменте.';


-- ----------------------------------------------------------------------------
-- 2. instrument_spec — сама таблица
-- ----------------------------------------------------------------------------
create table instrument_spec (
    exchange_instrument_id  integer     not null references exchange_instrument (id),
    valid_from              timestamptz not null,
    valid_to                timestamptz,
    last_seen_at            timestamptz not null,
    venue_effective_at      timestamptz,
    status                  text        not null check (status in ('trading','post_only','reduce_only','halted','delisted')),
    price_step              numeric     not null check (price_step > 0),
    qty_step                numeric     not null check (qty_step > 0),
    min_qty                 numeric     not null check (min_qty > 0),
    min_notional            numeric              check (min_notional > 0),
    contract_multiplier     numeric     not null check (contract_multiplier > 0),
    funding_interval_hours  smallint             check (funding_interval_hours > 0),
    funding_interval_source text                 check (funding_interval_source in ('venue','measured','assumed')),
    spec_hash               text        not null,
    raw_json                jsonb       not null,
    written_by              text        not null,
    primary key (exchange_instrument_id, valid_from),
    check (valid_to is null or valid_to > valid_from),
    check (last_seen_at >= valid_from)
);

-- Ровно одна открытая версия (valid_to is null) на инструмент — «текущая спека» без агрегата.
create unique index instrument_spec_open on instrument_spec (exchange_instrument_id) where valid_to is null;

comment on table instrument_spec is
    'Типизированная история спецификации инструмента, append-only, SCD2. Колонки — ровно как в '
    'exchange_instrument, чтобы не заводить маппинг между двумя разными наборами имён одного и того '
    'же факта. Читать не построчно, а через instrument_spec_effective — она одна знает, как сводить '
    'valid_from (когда увидели МЫ) и venue_effective_at (когда поменяла БИРЖА, если известно).';
comment on column instrument_spec.valid_from is
    'Когда МЫ впервые увидели эти значения — момент прохода discovery (или, для строк 0031-сида, '
    'последний ранее известный last_seen_at). Не путать с venue_effective_at.';
comment on column instrument_spec.last_seen_at is
    'Последнее подтверждение ТЕХ ЖЕ значений (тот же spec_hash) очередным проходом. Растёт, пока '
    'ничего не меняется; новая версия не заводится.';
comment on column instrument_spec.venue_effective_at is
    'Когда биржа РЕАЛЬНО поменяла контракт, если это где-то заявлено (changelog, форум) — руками. '
    'NULL = не знаем точный момент, известно только «увидели между valid_from предыдущей версии и '
    'этой». Отличие от valid_from — единственное, что делает границу версии точной, а не оценочной.';
comment on column instrument_spec.funding_interval_source is
    'venue — площадка сообщила прямо; measured — вычислено из истории платежей; assumed — '
    'предположение. NULL = источник не записан (не «предположили» — 0028 уже отказался от '
    'подставного значения на этом же факте у exchange_instrument).';
comment on column instrument_spec.written_by is
    '''0031 seed'' — начальная версия, заведённая этой миграцией из состояния exchange_instrument '
    'на момент миграции; ''discovery'' — версия, которую завёл сборщик (следующая фаза).';


-- ----------------------------------------------------------------------------
-- 3. Посев — одна версия на существующий инструмент
-- ----------------------------------------------------------------------------
-- valid_from = last_seen_at инструмента: последний проход discovery, когда эти значения
-- РЕАЛЬНО наблюдались. Не now() — миграция сама ничего не наблюдает, и now() сломал бы
-- check (last_seen_at >= valid_from), потому что last_seen_at инструмента почти всегда в
-- прошлом относительно момента накатки. Не first_seen_at — с каких пор действуют именно
-- ЭТИ значения, а не какие-нибудь более ранние, неизвестно; факты старше этой версии
-- резолвятся на чтении как assumed_first (см. вьюху ниже), а не притворяются измеренными.
insert into instrument_spec (
    exchange_instrument_id, valid_from, valid_to, last_seen_at, venue_effective_at,
    status, price_step, qty_step, min_qty, min_notional, contract_multiplier, funding_interval_hours,
    funding_interval_source, spec_hash, raw_json, written_by)
select id, last_seen_at, null, last_seen_at, null,
       status, price_step, qty_step, min_qty, min_notional, contract_multiplier, funding_interval_hours,
       null,
       instrument_spec_hash(status, price_step, qty_step, min_qty, min_notional, contract_multiplier, funding_interval_hours),
       raw_json, '0031 seed'
  from exchange_instrument;


-- ----------------------------------------------------------------------------
-- 4. exchange_instrument — источник провенанса интервала фандинга
-- ----------------------------------------------------------------------------
alter table exchange_instrument
    add column funding_interval_source text check (funding_interval_source in ('venue','measured','assumed'));

comment on column exchange_instrument.funding_interval_source is
    'venue — площадка сообщила прямо; measured — вычислено из истории платежей; assumed — '
    'предположение. NULL = источник не записан. Nullable по той же причине, по которой 0028 снял '
    'NOT NULL с funding_interval_hours на этой же таблице: задача Kraken просила NOT NULL с '
    'дефолтом ''assumed'' для всех — это подставное значение, которое 0028 уже отверг для соседнего '
    'поля этой же строки.';


-- ----------------------------------------------------------------------------
-- 5. instrument_spec_effective — резолв «что действовало в момент t»
-- ----------------------------------------------------------------------------
create view instrument_spec_effective as
select s.*,
       coalesce(s.venue_effective_at, s.valid_from) as eff_from,
       lead(coalesce(s.venue_effective_at, s.valid_from)) over w as eff_to,
       case when lead(s.valid_from) over w is not null
             and lead(s.venue_effective_at) over w is null
            then s.last_seen_at end as uncertain_after
  from instrument_spec s
window w as (partition by s.exchange_instrument_id order by s.valid_from);

comment on view instrument_spec_effective is
    'Резолв версии спеки на момент t: факт с временем t берёт строку, где eff_from <= t < eff_to '
    '(eff_to null у текущей версии — действует по сей день). t > uncertain_after этой версии — '
    'единицы следующей версии, но граница ПРИБЛИЗИТЕЛЬНАЯ (venue_effective_at следующей версии '
    'неизвестен) — используется потому что ничего точнее нет, не потому что она точна. t раньше '
    'первой версии инструмента — берётся первая версия, читать как assumed_first: мы утверждаем '
    'спеку, которую не наблюдали в этот момент, за неимением лучшего. Время факта берётся из '
    'СОБЫТИЯ, не из момента записи: event_time / observed_at / open_time / funding_time / '
    'bucket_time у новых таблиц (0032), received_at — у market_snapshot.';


-- ----------------------------------------------------------------------------
-- 6. Гранты
-- ----------------------------------------------------------------------------
grant select on instrument_spec           to studio_reader;
grant select on instrument_spec_effective to studio_reader;
