-- ============================================================================
-- CryptoSmith X — миграция 0006: справочник активов + данные, которые задним
-- числом не добрать.
--
-- Две причины, по одной на задачу:
--
--   A. Нормализация тикеров (XBT->BTC, 1000PEPE->PEPE) была зашита в код адаптера.
--      При одной фейковой бирже это терпимо, при четырёх реальных — разъедется:
--      каждый адаптер знал бы свой кусок маппинга, а «все листинги BTC по биржам»
--      требовали бы обхода кода. Делаем маппинг ДАННЫМИ: храним и оригинал биржи
--      (raw), и канон, а соответствие — в таблице, которую правит админ. Резолв
--      переезжает в Hub (discovery), адаптеры тупеют — отдают строки биржи как есть.
--
--   B. Свечи и funding добираются задним числом (биржи отдают историю), а
--      open interest, глубина стакана и bid/ask — НЕТ: они существуют только с
--      момента, когда мы начали их писать, и умирают вместе с ротацией
--      market_snapshot (90 дней). Значит их надо сохранять в неротируемую форму
--      по мере поступления: почасовой срез микроструктуры и история funding.
--
-- Соглашения те же, что в 0001: timestamptz (UTC); статусы text + CHECK; updated_at
-- выставляет приложение; комментарии на таблицах и ключевых колонках; без триггеров.
--
-- Что НЕ вошло и почему:
--   * feed (mark/index/premium как отдельные серии свечей) — бот торгует по trade;
--     текущие mark/index уже лежат в снапшоте; колонка без потребителя.
--   * tags биржи (ярлыки Kraken) — нет потребителя.
--   * Справочник/FK для quote_asset — в V1 котировка это фиксированное USD-семейство
--     (USD/USDT/USDC), канон = raw, отдельной таблицы активов-котировок никто не
--     спрашивает. quote_asset остаётся text-каноном без FK.
--   * 10/50 bps в market_metric_hour — 25 bps достаточно как мера ликвидности;
--     10 и 50 живут свои 90 дней в снапшотах, дублировать их навсегда незачем.
--   * Бэкфилл исторического дампа Kraken — отдельная задача. Проверено мысленно:
--     symbol дампа = exchange_symbol, маппится через этот справочник без правок схемы.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- ЗАДАЧА A — справочник активов и нормализация
-- ----------------------------------------------------------------------------

-- asset — канонические активы. Строка появляется автоматически при первом
-- discovery неизвестного алиаса (code = алиас как есть, note = 'auto-registered');
-- админ потом может слить дубль правкой asset_alias, discovery починит инструменты
-- следующим проходом.
create table asset (
    code        text        primary key,   -- 'BTC', 'PEPE', 'SOL' — канон
    name        text,
    note        text,
    created_at  timestamptz not null default now()
);

comment on table asset is
    'Канонические активы. Пополняется сидами и автоматически при discovery неизвестного '
    'алиаса. code — каноническое имя, на которое ссылается exchange_instrument.base_asset.';

-- asset_alias — как биржи называют актив. exchange_code null = глобальный алиас
-- (действует для всех бирж). multiplier: '1000PEPE' -> PEPE с множителем 1000 —
-- при резолве перемножается с contract_multiplier инструмента; в ценах/объёмах
-- НИЧЕГО не пересчитываем, множитель только описывает единицу количества.
create table asset_alias (
    exchange_code text    references exchange (code),   -- null = для всех бирж
    alias         text    not null,
    asset_code    text    not null references asset (code),
    multiplier    numeric not null default 1 check (multiplier > 0),
    note          text,
    unique nulls not distinct (exchange_code, alias)     -- PG16: null трактуется как значение
);

comment on table asset_alias is
    'Соответствие «строка биржи -> канонический актив». Данные, не код: discovery резолвит '
    'raw биржи через биржевой алиас, затем глобальный, затем identity. exchange_code null — '
    'алиас для всех бирж. multiplier переезжает в contract_multiplier инструмента при резолве.';

comment on column asset_alias.multiplier is
    'Сколько единиц канонического актива в одной единице количества этого алиаса '
    '(1000PEPE -> PEPE, multiplier 1000). Перемножается с множителем инструмента от адаптера.';

-- Сиды. Канонические активы: BTC, ETH и всё, что сейчас отдаёт фейковая биржа
-- (её base_asset уже канонические — FK ниже требует, чтобы они существовали).
insert into asset (code) values
    ('BTC'), ('ETH'), ('SOL'), ('XRP'), ('DOGE'), ('ADA'), ('AVAX'), ('LINK'),
    ('DOT'), ('LTC'), ('BCH'), ('ATOM'), ('NEAR'), ('APT'), ('ARB'), ('OP'),
    ('INJ'), ('SUI'), ('TIA'), ('PEPE')
on conflict (code) do nothing;

-- Глобальные алиасы — то немногое, что известно заранее. Остальное само
-- зарегистрируется при discovery реальных бирж, руками сюда ничего не выдумываем.
insert into asset_alias (exchange_code, alias, asset_code, multiplier) values
    (null, 'XBT',      'BTC',  1),
    (null, '1000PEPE', 'PEPE', 1000),
    (null, 'kPEPE',    'PEPE', 1000)
on conflict (exchange_code, alias) do nothing;


-- exchange_instrument: и оригинал биржи, и канон.
alter table exchange_instrument
    add column base_asset_raw  text,
    add column quote_asset_raw text,
    add column listed_at       timestamptz;   -- дата листинга ПО ДАННЫМ БИРЖИ (Kraken: openingDate)

comment on column exchange_instrument.base_asset_raw is
    'Базовый актив КАК ЕГО ПИШЕТ БИРЖА (XBT, 1000PEPE). base_asset — канон после резолва.';
comment on column exchange_instrument.quote_asset_raw is
    'Котировка как её пишет биржа. quote_asset — канон (в V1 совпадает: USD-семейство).';
comment on column exchange_instrument.listed_at is
    'Когда контракт залистился НА БИРЖЕ (не когда увидели мы — это first_seen_at). '
    'В 0001 поле сознательно не вошло («нет потребителя»); потребитель появился: '
    'фильтр «контракту меньше N дней — не торгуем, истории мало». null, если биржа не отдаёт.';

-- Бэкфилл существующих строк: raw = текущему (каноническому) значению. Существующая
-- фейковая биржа отдавала уже канон, так что raw для неё равен канону.
update exchange_instrument
   set base_asset_raw  = base_asset,
       quote_asset_raw = quote_asset;

alter table exchange_instrument
    alter column base_asset_raw  set not null,
    alter column quote_asset_raw set not null;

-- FK base_asset -> asset(code). Страховка на случай данных, которых нет в сидах:
-- регистрируем каждый уже существующий канон, иначе добавление FK упало бы.
insert into asset (code, note)
    select distinct base_asset, 'auto-registered (0006 backfill)'
      from exchange_instrument
on conflict (code) do nothing;

alter table exchange_instrument
    add constraint exchange_instrument_base_asset_fkey
        foreign key (base_asset) references asset (code);

-- instrument_v — чтобы «все листинги BTC по биржам» были одним where, а не join-ом
-- в каждом запросе. Канон base_asset уже в строке; вью добавляет имя биржи и актива.
create view instrument_v as
    select i.*,
           e.name        as exchange_name,
           e.status      as exchange_status,
           a.name        as base_asset_name
      from exchange_instrument i
      join exchange e on e.code = i.exchange_code
      join asset    a on a.code = i.base_asset;

comment on view instrument_v is
    'Инструмент + имя биржи + канонический актив. «Все листинги BTC»: '
    'select * from instrument_v where base_asset = ''BTC''.';


-- ----------------------------------------------------------------------------
-- ЗАДАЧА B — данные, которых нет, но которые не добрать задним числом
-- ----------------------------------------------------------------------------

-- funding_rate_history — исторические ставки funding. НЕ ротируется: их нельзя
-- восстановить, а серия нужна для длинных прогонов. Коллектор 'funding' дописывает
-- недостающее раз в час (on conflict do nothing).
create table funding_rate_history (
    exchange_instrument_id integer          not null references exchange_instrument (id),
    funding_time           timestamptz      not null,   -- момент платежа (граница интервала)
    rate                   double precision not null,   -- та же семантика, что funding_rate в снапшоте
    primary key (exchange_instrument_id, funding_time)
);

comment on table funding_rate_history is
    'Историческая ставка funding по инструменту на границе интервала. Не ротируется. '
    'rate — доля notional за один funding_interval_hours, знак как в market_snapshot.funding_rate.';

-- Коллектор funding добавляется в перечень операций.
alter table collector_status drop constraint collector_status_collector_check;
alter table collector_status
    add constraint collector_status_collector_check
        check (collector in ('discovery', 'snapshot', 'depth', 'candles', 'rollup', 'funding'));


-- market_metric_hour — часовой срез микроструктуры. НЕ ротируется. Снапшоты живут
-- 90 дней, а OI/спред/глубина должны жить дольше — здесь по одной строке на
-- инструмент-час, из закрытого часа market_snapshot. Пишет rollup-джоб последним
-- шагом (отдельный джоб не заводим). Перезапись закрытого часа — upsert целиком,
-- как у свечей.
create table market_metric_hour (
    exchange_instrument_id integer          not null references exchange_instrument (id),
    hour_time              timestamptz      not null,   -- начало часа, UTC
    open_interest_last     double precision not null,   -- последнее наблюдение часа
    funding_rate_last      double precision not null,   -- последнее наблюдение часа
    spread_bps_avg         double precision,            -- avg((ask-bid)/mid*1e4); null, если стакан всегда кривой
    depth_bid_25bps_avg    double precision,            -- avg по ненулевым; null, если измерений не было
    depth_ask_25bps_avg    double precision,
    snapshot_count         smallint         not null,   -- сколько снапшотов вошло — мера доверия к строке
    updated_at             timestamptz      not null default now(),
    primary key (exchange_instrument_id, hour_time)
);

comment on table market_metric_hour is
    'Почасовой срез микроструктуры из market_snapshot: OI и funding (последнее наблюдение), '
    'средние спред и глубина 25 bps. Не ротируется — снапшоты умирают через 90 дней, это остаётся. '
    'Выбрано 25 bps: 10/50 bps живут свои 90 дней в снапшотах, навсегда дублируется только один срез.';

comment on column market_metric_hour.spread_bps_avg is
    'Среднее (ask-bid)/mid*1e4 по снапшотам часа, где стакан не пересечён и mid>0. '
    'null, если валидного измерения в часе не было.';
comment on column market_metric_hour.snapshot_count is
    'Число снапшотов, вошедших в час. Мало снапшотов — меньше доверия к средним.';
