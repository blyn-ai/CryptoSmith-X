-- ============================================================================
-- 0019 — иерархия становится трёхуровневой: exchange → segment → dataset.
--
-- До этой миграции `exchange` хранил склеенное. Коды `kraken-futures`,
-- `binance-usdm`, `weex-futures` — это не биржи, это торговые площадки внутри
-- бирж. Пока площадка у биржи одна, разница незаметна. С приходом спота Kraken
-- пришлось бы завести дважды, и две строки молча делили бы один аккаунт, одни
-- ключи и — важнее всего — один лимит запросов на IP. Учёт этого не увидел бы:
-- в базе это две независимые биржи.
--
-- Поэтому вводится средний уровень:
--
--     exchange   kraken, binance, weex, hyperliquid   личность: ключи, лимиты
--        ↓
--     segment    kraken-futures, binance-usdm         поверхность: адаптер,
--        ↓                                            base_url, ws_url, символы
--     dataset    snapshot, depth, candles, funding     вид данных
--
-- Сегмент — не класс активов: спотовый BTC и перпетуал на BTC это один и тот же
-- актив, разные инструменты. Сегмент — техническая поверхность: свой базовый
-- URL и свой WebSocket (api.kraken.com против futures.kraken.com), своё
-- пространство символов (XBTUSD против PF_XBTUSD), свой набор инструментов,
-- свой адаптер в коде. Отсюда же следует доступность данных: у спота не бывает
-- фандинга и открытого интереса — не потому, что биржа их «не умеет», а потому,
-- что у спота их не существует.
--
-- Второе переименование: `collection` → `dataset`. Слово «collection» означало
-- одновременно вид данных (snapshot, depth) и процесс их сбора, и в паре
-- `exchange_collection` читалось как «сборы биржи», хотя это политика ячейки.
--
-- Данные не переносятся. Существующие коды бирж уже являются валидными кодами
-- сегментов, поэтому переименовывается колонка, а значения остаются как есть.
-- `market_snapshot`, `market_candle`, `funding_rate_history` ключуются на
-- `exchange_instrument_id` и этой миграцией не затрагиваются вовсе — ни одна
-- строка рыночных данных не читается и не пишется. На 26 млн строк снапшотов
-- миграция занимает доли секунды: это правка каталога, а не данных.
--
-- Делается сейчас, пока это пять сегментов и сорок пять клеток матрицы. После
-- спота таблицы те же, но переписывать пришлось бы против живого сбора.
-- ============================================================================

-- Вью пересоздаётся в конце: она держит колонку exchange_code по имени.
drop view if exists instrument_v;

-- Тела plpgsql-функций хранятся текстом и разрешаются в момент срабатывания,
-- поэтому переименование колонки их не чинит — их надо переписать.
drop trigger if exists collector_status_notify on collector_status;
drop trigger if exists exchange_notify on exchange;
drop trigger if exists exchange_collection_notify on exchange_collection;
drop function if exists notify_collector_status_change();
drop function if exists notify_exchange_change();
drop function if exists notify_exchange_collection_change();

-- ---------------------------------------------------------------------------
-- 1. Бывшая `exchange` — это на самом деле сегмент. Все её колонки (adapter,
--    base_url, ws_url, quote_assets, blacklist) описывают поверхность, а не
--    компанию, поэтому таблица переезжает целиком.
-- ---------------------------------------------------------------------------
alter table exchange rename to segment;
alter index exchange_pkey rename to segment_pkey;
alter table segment rename constraint exchange_status_check to segment_status_check;

alter table asset_alias                    rename column exchange_code to segment_code;
alter table capability_log                 rename column exchange_code to segment_code;
alter table collection_gap                 rename column exchange_code to segment_code;
alter table collector_run                  rename column exchange_code to segment_code;
alter table collector_status               rename column exchange_code to segment_code;
alter table exchange_collection            rename column exchange_code to segment_code;
alter table exchange_collection_capability rename column exchange_code to segment_code;
alter table exchange_instrument            rename column exchange_code to segment_code;

alter table asset_alias         rename constraint asset_alias_exchange_code_fkey         to asset_alias_segment_code_fkey;
alter table collection_gap      rename constraint collection_gap_exchange_code_fkey      to collector_gap_segment_code_fkey;
alter table collector_run       rename constraint collector_run_exchange_code_fkey       to collector_run_segment_code_fkey;
alter table collector_status    rename constraint collector_status_exchange_code_fkey    to collector_status_segment_code_fkey;
alter table exchange_collection rename constraint exchange_collection_exchange_code_fkey to segment_dataset_segment_code_fkey;
alter table exchange_instrument rename constraint exchange_instrument_exchange_code_fkey to exchange_instrument_segment_code_fkey;
alter index asset_alias_exchange_code_alias_key rename to asset_alias_segment_code_alias_key;
alter index exchange_instrument_exchange_code_exchange_symbol_key rename to exchange_instrument_segment_code_symbol_key;

-- ---------------------------------------------------------------------------
-- 2. Новая `exchange` — венью. Держит то, что общее у всех его площадок:
--    аккаунт, ключи и бюджет запросов на IP. Ради последнего уровень и заведён.
-- ---------------------------------------------------------------------------
create table exchange (
    code        text primary key,
    name        text not null,
    description text,
    website_url text,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),
    updated_by  text
);

insert into exchange (code, name, description) values
    ('binance',     'Binance',     'Крупнейшая по обороту площадка; USDⓈ-M и COIN-M деривативы плюс спот.'),
    ('kraken',      'Kraken',      'Регулируемая площадка США/ЕС; спот и деривативы живут на разных доменах и разных API.'),
    ('weex',        'WEEX',        'Деривативная площадка второго эшелона.'),
    ('hyperliquid', 'Hyperliquid', 'Ончейн-биржа перпетуалов на собственном L1; книга заявок в консенсусе, а не у оператора.'),
    ('fake',        'Fake',        'Внутрипроцессная биржа для разработки и тестов; наружу не ходит.');

alter table segment add column exchange_code text;
alter table segment add column kind          text;

update segment set exchange_code = m.exchange, kind = m.kind
  from (values
        ('binance-usdm',   'binance',     'perp'),
        ('kraken-futures', 'kraken',      'perp'),
        ('weex-futures',   'weex',        'perp'),
        ('hyperliquid',    'hyperliquid', 'perp'),
        ('fake',           'fake',        'perp')
       ) as m(code, exchange, kind)
 where segment.code = m.code;

-- Ни одна строка не должна остаться без венью: молча пропущенный сегмент
-- означал бы биржу, чей бюджет запросов никто не считает.
do $$
declare orphans text;
begin
    select string_agg(code, ', ') into orphans from segment where exchange_code is null;
    if orphans is not null then
        raise exception 'сегменты без биржи: %', orphans;
    end if;
end $$;

alter table segment alter column exchange_code set not null;
alter table segment alter column kind          set not null;
alter table segment add constraint segment_exchange_code_fkey foreign key (exchange_code) references exchange(code);
alter table segment add constraint segment_kind_check
    check (kind in ('spot', 'perp', 'futures', 'option', 'stock', 'synthetic'));
create index segment_by_exchange on segment (exchange_code, kind);

-- ---------------------------------------------------------------------------
-- 3. collection → dataset. Вид данных, а не процесс сбора.
-- ---------------------------------------------------------------------------
alter table collection rename to dataset;
alter index collection_pkey rename to dataset_pkey;
alter table dataset rename constraint collection_default_interval_s_check     to dataset_default_interval_s_check;
alter table dataset rename constraint collection_default_mode_check           to dataset_default_mode_check;
alter table dataset rename constraint collection_default_retention_days_check to dataset_default_retention_days_check;
alter table dataset rename constraint collection_kind_check                   to dataset_kind_check;

alter table collection_setting rename to dataset_setting;
alter table dataset_setting rename column collection_code to dataset_code;
alter index collection_setting_pkey rename to dataset_setting_pkey;
alter table dataset_setting rename constraint collection_setting_kind_check            to dataset_setting_kind_check;
alter table dataset_setting rename constraint collection_setting_collection_code_fkey  to dataset_setting_dataset_code_fkey;

-- Политика ячейки (сегмент × датасет): режим, такт, ретеншен, транспорт.
alter table exchange_collection rename to segment_dataset;
alter table segment_dataset rename column collection_code to dataset_code;
alter index exchange_collection_pkey rename to segment_dataset_pkey;
alter table segment_dataset rename constraint exchange_collection_interval_s_check     to segment_dataset_interval_s_check;
alter table segment_dataset rename constraint exchange_collection_mode_check           to segment_dataset_mode_check;
alter table segment_dataset rename constraint exchange_collection_retention_days_check to segment_dataset_retention_days_check;
alter table segment_dataset rename constraint exchange_collection_transport_check      to segment_dataset_transport_check;
alter table segment_dataset rename constraint exchange_collection_collection_code_fkey to segment_dataset_dataset_code_fkey;

alter table exchange_collection_capability rename to segment_dataset_capability;
alter table segment_dataset_capability rename column collection_code to dataset_code;
alter index exchange_collection_capability_pkey rename to segment_dataset_capability_pkey;
alter table segment_dataset_capability rename constraint exchange_collection_capability_source_check to segment_dataset_capability_source_check;
alter table segment_dataset_capability rename constraint exchange_collection_capabilit_exchange_code_collection_cod_fkey to segment_dataset_capability_cell_fkey;
alter table segment_dataset_capability rename constraint exchange_collection_capability_capability_key_fkey to segment_dataset_capability_key_fkey;

alter table capability_log rename column collection_code to dataset_code;

-- Провал сбора — это провал коллектора в клетке, отсюда имя в один ряд с
-- collector_run и collector_status.
alter table collection_gap rename to collector_gap;
alter index collection_gap_pkey rename to collector_gap_pkey;
alter index collection_gap_instrument rename to collector_gap_instrument;
alter index collection_gap_lookup rename to collector_gap_lookup;
alter index collection_gap_open rename to collector_gap_open;
alter table collector_gap rename constraint collection_gap_cause_check to collector_gap_cause_check;
alter table collector_gap rename constraint collection_gap_exchange_instrument_id_fkey to collector_gap_exchange_instrument_id_fkey;

alter table collector_status rename constraint collector_status_collector_fkey to collector_status_dataset_fkey;

-- ---------------------------------------------------------------------------
-- 4. Оповещения. Ключ в payload теперь segment: слушатель обновляет строку
--    площадки, а не биржи целиком.
-- ---------------------------------------------------------------------------
create function notify_collector_status_change() returns trigger as $$
begin
    perform pg_notify('csx_live', json_build_object('segment', new.segment_code, 'collector', new.collector)::text);
    return new;
end;
$$ language plpgsql;

create trigger collector_status_notify
    after insert or update on collector_status
    for each row execute function notify_collector_status_change();

create function notify_segment_change() returns trigger as $$
begin
    perform pg_notify('csx_live', json_build_object('segment', new.code, 'collector', null)::text);
    return new;
end;
$$ language plpgsql;

create trigger segment_notify
    after update on segment
    for each row execute function notify_segment_change();

create function notify_segment_dataset_change() returns trigger as $$
begin
    perform pg_notify('csx_live', json_build_object('segment', new.segment_code, 'collector', null)::text);
    return new;
end;
$$ language plpgsql;

create trigger segment_dataset_notify
    after update on segment_dataset
    for each row execute function notify_segment_dataset_change();

comment on function notify_collector_status_change() is
    'Fires once per completed collector pass (collector_status is upserted, not inserted per row) — '
    'never per market_snapshot row. See the 0015 migration header for why this is the right frequency.';

-- ---------------------------------------------------------------------------
-- 5. Вью инструментов: та же форма, поля переименованы вслед за колонками, плюс
--    биржа рядом с сегментом — она нужна везде, где показывают инструмент.
-- ---------------------------------------------------------------------------
create view instrument_v as
select i.id,
       i.segment_code,
       i.exchange_symbol,
       i.base_asset,
       i.quote_asset,
       i.contract_multiplier,
       i.price_step,
       i.qty_step,
       i.min_qty,
       i.min_notional,
       i.funding_interval_hours,
       i.status,
       i.status_changed_at,
       i.first_seen_at,
       i.last_seen_at,
       i.raw_json,
       i.updated_at,
       i.base_asset_raw,
       i.quote_asset_raw,
       i.listed_at,
       s.name          as segment_name,
       s.status        as segment_status,
       s.kind          as segment_kind,
       s.exchange_code as exchange_code,
       x.name          as exchange_name,
       a.name          as base_asset_name
  from exchange_instrument i
  join segment  s on s.code = i.segment_code
  join exchange x on x.code = s.exchange_code
  join asset    a on a.code = i.base_asset;

-- ---------------------------------------------------------------------------
-- 6. Что означает каждый уровень — в самой схеме, а не только в этом файле.
-- ---------------------------------------------------------------------------
comment on table exchange is
    'Биржа как организация: один оператор, один аккаунт, одни ключи и один лимит запросов на IP, '
    'общий для всех её площадок. Уровень существует именно ради этого общего бюджета — без него '
    'спот и перпы одной биржи выглядели бы независимыми и молча делили его.';

comment on table segment is
    'Торговая площадка внутри биржи: свой базовый URL и WebSocket, своё пространство символов, свой '
    'набор инструментов и свой адаптер в коде. Не класс активов — спотовый BTC и перпетуал на BTC это '
    'один актив в двух сегментах. Доступность данных определяется здесь: у спота не бывает фандинга.';

comment on table dataset is
    'Вид рыночных данных — snapshot, depth, candles, funding. У каждого своя схема, свой такт опроса '
    'и свой срок хранения.';

comment on table segment_dataset is
    'Политика одной клетки матрицы (сегмент × датасет): собираем или нет, с каким тактом, каким '
    'транспортом и сколько храним. Пустое значение означает «взять из умолчания датасета».';

comment on table collector_gap is
    'Интервал, в течение которого клетка не наблюдалась, с известной причиной. Отсутствие строки '
    'здесь напротив пропуска в данных означает причину, которой мы не знаем, — а не спокойный рынок.';

comment on column segment.kind is
    'Род площадки: spot, perp, futures, option, stock, synthetic. Определяет, какие датасеты для неё '
    'вообще осмысленны — фандинг и открытый интерес не существуют вне деривативов.';
