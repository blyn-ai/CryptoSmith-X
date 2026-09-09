-- ============================================================================
-- CryptoSmith X — миграция 0037: второй сокет Binance в схеме, и
-- transports_venue наконец заполнена.
--
-- Фаза 1 плана владельца («перевести на WS всё, что можно») — только транспорт,
-- без тюнинга REST-петель и без новых наборов. Полный список того, что
-- переведено, что нет и почему — в изменённом коде и в plans/ws-coverage.md.
--
-- ----------------------------------------------------------------------------
-- A. segment.market_ws_url — второй сокет Binance
-- ----------------------------------------------------------------------------
-- Проверено подключением 2026-09-09 (Fixtures/binance-market-ws): Binance
-- разносит потоки по РАЗНЫМ путям одного хоста — depth живёт на /public/stream
-- (уже наш segment.ws_url), тикер/mark-price/свечи — только на /market/stream.
-- Подписка на «не тот» путь подтверждается тем же {"result":null}, что и
-- успешная, и данных не присылает никогда (см. BinanceWsFeed.cs — тот же
-- капкан документирован там для depth). Это НЕ альтернативный URL одного и
-- того же сокета — это второе соединение, со своим жизненным циклом, и оно
-- получает своё поле, а не перегружает ws_url.
--
-- NULL у всех сегментов, кроме binance-usdm: это первый венью с двумя
-- сокетами и, вероятно, останется единственным — колонка названа под
-- конкретную потребность, а не обобщена в «дополнительные WS-адреса» под
-- нужду, которой пока ни у кого больше нет.
alter table segment
    add column market_ws_url text;

comment on column segment.market_ws_url is
    'Второй WS-адрес Binance USDⓈ-M (/market/stream: !ticker@arr, !markPrice@arr@1s, '
    '<symbol>@kline_1m) — отдельное соединение от ws_url (/public/stream: depth). '
    'NULL у всех прочих сегментов; см. BinanceMarketWsFeed.';

update segment
   set market_ws_url = 'wss://fstream.binance.com/market/stream',
       updated_at    = now(),
       updated_by    = 'migration 0037'
 where code = 'binance-usdm';

-- ----------------------------------------------------------------------------
-- B. transports_venue — заполнена впервые
-- ----------------------------------------------------------------------------
-- capability_key.transports_venue существует с 0034 («Transports the venue
-- offers for it») и не была заполнена ни для одной строки — 0034 сеяла
-- только сами строки (пустыми), не факты в них. Значения ниже — из
-- plans/ws-coverage.md §1, метод «подписка к живому сокету каждой площадки,
-- приём 6–14 с, засчитан только кадр нужного типа» (дата в файле). source
-- = 'probed': это не наша реализация (transports_us, отдельно, пишет сам
-- адаптер при сборке — ExchangeWorker.WriteDeclaredCapabilityAsync) и не
-- ручная правка — это внешний факт о площадке, установленный подключением.
--
-- Что НЕ заполнено, и почему явно, а не по умолчанию:
--   * rollup, spec_versions — не площадка отвечает на эти вопросы: rollup
--     считаем сами, ни одна строка venue-транспорта тут не имеет смысла
--     (собственный комментарий датасета: «no venue is involved»);
--     spec_versions — производная запись из discovery, а не поток площадки.
--   * open_interest у kraken-futures и hyperliquid — не отдельная
--     возможность площадки: обе отдают его ВНУТРИ тикера (REST и WS), что
--     и значит колонка snapshot ниже; отдельного transports_venue у
--     open_interest там нет, ровно как этот датасет сам описывает себя
--     («most venues already carry it inline in the ticker; not collected
--     separately»). У weex и binance такого совмещения нет — там это
--     отдельная, хоть и небатчевая, REST-ручка, и это записано.
--   * candles_mark, candles_index — ни одна из четырёх площадок не отдаёт
--     свечи mark/index ни по REST, ни по WS (у binance markPrice — это
--     поток цены, не свечной ряд; см. ws-coverage.md §3). Пусто у всех.
--   * trades, liquidations — venue-транспорт указан только там, где он
--     подтверждён подключением (ws-coverage.md), и только 'ws': REST-ручки
--     для них в нашем клиентском коде нет ни у одной площадки, и утверждать
--     «rest» без этого было бы гаданием, а не фактом.
--
-- 'depth' и 'book' делят одну строку значений: обе описывают одну и ту же
-- возможность площадки (сырые уровни книги), различие — в том, что считаем
-- МЫ из этих уровней (сведённые полосы против сырых), не в том, что отдаёт
-- площадка.
update segment_dataset_capability
   set value = v.transports_venue, source = 'probed', valid_since = now(),
       filled_at = now(), filled_by = 'migration 0037 (plans/ws-coverage.md, 2026-09-09)'
  from (values
    -- сегмент,          датасет,          то, что реально отдаёт площадка
    ('weex-futures',    'depth',           'rest,ws'),
    ('weex-futures',    'book',            'rest,ws'),
    ('weex-futures',    'candles',         'rest,ws'),
    ('weex-futures',    'snapshot',        'rest'),
    ('weex-futures',    'funding',         'rest'),
    ('weex-futures',    'trades',          'ws'),
    ('weex-futures',    'open_interest',   'rest'),
    ('weex-futures',    'discovery',       'rest'),

    ('binance-usdm',    'depth',           'rest,ws'),
    ('binance-usdm',    'book',            'rest,ws'),
    ('binance-usdm',    'candles',         'rest,ws'),
    ('binance-usdm',    'snapshot',        'rest,ws'),
    ('binance-usdm',    'funding',         'rest'),
    ('binance-usdm',    'trades',          'ws'),
    ('binance-usdm',    'liquidations',    'ws'),
    ('binance-usdm',    'open_interest',   'rest'),
    ('binance-usdm',    'discovery',       'rest'),

    ('kraken-futures',  'depth',           'rest,ws'),
    ('kraken-futures',  'book',            'rest,ws'),
    ('kraken-futures',  'candles',         'rest'),
    ('kraken-futures',  'snapshot',        'rest,ws'),
    ('kraken-futures',  'funding',         'rest'),
    ('kraken-futures',  'trades',          'ws'),
    ('kraken-futures',  'liquidations',    'ws'),
    ('kraken-futures',  'discovery',       'rest'),

    ('hyperliquid',     'depth',           'rest,ws'),
    ('hyperliquid',     'book',            'rest,ws'),
    ('hyperliquid',     'candles',         'rest,ws'),
    ('hyperliquid',     'snapshot',        'rest,ws'),
    ('hyperliquid',     'funding',         'rest'),
    ('hyperliquid',     'trades',          'ws'),
    ('hyperliquid',     'discovery',       'rest')
  ) as v(segment_code, dataset_code, transports_venue)
 where segment_dataset_capability.segment_code = v.segment_code
   and segment_dataset_capability.dataset_code = v.dataset_code
   and segment_dataset_capability.capability_key = 'transports_venue';
