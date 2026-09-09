-- ============================================================================
-- CryptoSmith X — миграция 0040: пять комментариев 0030 поправлены по факту
-- того, что писатель, добавленный этой же фазой, реально нашёл на четырёх
-- площадках. Комментарий — не данные, править можно (прецедент 0035, last_price).
--
-- Фаза 4, пункт 2 плана владельца. 0030 писала комментарии ДО того, как
-- появился писатель этих восьми колонок — часть предположений о том, что
-- где доступно, не подтвердилась при реализации; три расходятся с живыми
-- фикстурами настолько, что молчать было бы врать читателю схемы.
--
--
-- ----------------------------------------------------------------------------
-- venue_ts — 0030 сказала «WEEX не шлёт», а WEEX шлёт
-- ----------------------------------------------------------------------------
-- Fixtures/weex/tickers.json: живой кадр несёт "timestamp":"1788274362812" рядом
-- с ценой. Поле было в ответе всегда — просто WeexTicker его не объявлял, и
-- 0030 списала это на «биржа не шлёт», не на «мы не читаем». Реализовано.
--
-- Единственная площадка без него — Hyperliquid: HlAssetCtx (REST metaAndAssetCtxs
-- и WS activeAssetCtx) не несёт временной метки вовсе, ни в каком виде.
--
--
-- ----------------------------------------------------------------------------
-- next_funding_at — есть у Binance (REST и WS), у Kraken только на WS
-- ----------------------------------------------------------------------------
-- 0030 называла источником Kraken (next_funding_rate_time) и не упоминала
-- Binance вовсе. По факту:
--   Binance   — REST premiumIndex.nextFundingTime и WS !markPrice@arr@1s "T",
--               оба несут его всегда.
--   Kraken    — WS-тикер несёт next_funding_rate_time (Fixtures/kraken-ws/
--               ticker.json — живьём); REST /tickers НЕ несёт: три живых
--               кадра в Fixtures/kraken/tickers.json дают полный список полей
--               ответа, next_funding_rate_time среди них нет. NULL на REST-
--               проходе Kraken — не «не измерено», а «этот транспорт не
--               предоставляет» (WS-проход отдаёт значение, когда WS здоров).
--   WEEX, HL  — нет ни на одном транспорте.
--
--
-- ----------------------------------------------------------------------------
-- volume_24h_base, funding_rate_predicted, last_trade_at — уточнены по площадкам
-- ----------------------------------------------------------------------------
-- volume_24h_base:
--   WEEX — base_volume (REST /market/tickers, живой кадр).
--   Binance — REST ticker/24hr "volume", WS !ticker@arr "v".
--   Kraken — REST/WS "vol24h"/"volume" (0030 сомневалась, есть ли у Kraken
--            это поле вовсе — есть, подтверждено Fixtures/kraken/tickers.json:
--            vol24h соседствует с volumeQuote в одном кадре).
--   Hyperliquid — нет: DayNtlVlm — оборот в quote (USD notional), базового
--   аналога не существует на этом масштабе.
--
-- funding_rate_predicted: Kraken (REST fundingRatePrediction, поделённое на
-- markPrice — то же преобразование, что уже есть у funding_rate; WS
-- relative_funding_rate_prediction — площадка отдаёт уже готовой долей, без
-- деления). WEEX/Binance/Hyperliquid — нет ни на одном транспорте.
--
-- last_trade_at: Kraken REST несёт его как lastTime — отдельное поле рядом с
-- last, подтверждено живым кадром. Kraken WS этого поля не несёт (только
-- last без времени) — NULL на WS-проходе, не «не измерено». WEEX и Binance —
-- нет ни на одном транспорте. Hyperliquid — структурно НИКОГДА (0030,
-- 0035 §A: last_price там — mid книги, не цена сделки).
-- ============================================================================

comment on column market_snapshot.venue_ts is
    'Время тикера ПО БИРЖЕ, как прислано в кадре. NULL только у Hyperliquid — его тикер (REST '
    'metaAndAssetCtxs, WS activeAssetCtx) не несёт временной метки ни в каком виде. WEEX, Binance '
    'и Kraken несут его на каждом транспорте, REST и WS. received_at ("наши часы получения") '
    'остаётся ключом партиции в любом случае.';
comment on column market_snapshot_latest.venue_ts is
    'Время тикера ПО БИРЖЕ, как прислано в кадре. NULL только у Hyperliquid — его тикер (REST '
    'metaAndAssetCtxs, WS activeAssetCtx) не несёт временной метки ни в каком виде. WEEX, Binance '
    'и Kraken несут его на каждом транспорте, REST и WS. received_at ("наши часы получения") '
    'остаётся ключом партиции в любом случае.';

comment on column market_snapshot.last_trade_at is
    'Момент СДЕЛКИ, которой соответствует last_price. Kraken REST несёт его отдельным полем '
    '(lastTime) — Kraken WS этого поля не несёт (NULL на WS-проходе). WEEX и Binance — не несут '
    'ни на одном транспорте. Для Hyperliquid ВСЕГДА NULL структурно: last_price там — mid книги, '
    'не цена сделки (0035 §A), и подставлять сюда время значило бы утверждать про число то, что '
    'неправда.';
comment on column market_snapshot_latest.last_trade_at is
    'Момент СДЕЛКИ, которой соответствует last_price. Kraken REST несёт его отдельным полем '
    '(lastTime) — Kraken WS этого поля не несёт (NULL на WS-проходе). WEEX и Binance — не несут '
    'ни на одном транспорте. Для Hyperliquid ВСЕГДА NULL структурно: last_price там — mid книги, '
    'не цена сделки (0035 §A), и подставлять сюда время значило бы утверждать про число то, что '
    'неправда.';

comment on column market_snapshot.funding_rate_predicted is
    'Прогноз СЛЕДУЮЩЕГО периода funding, как отдаёт биржа. Только Kraken (REST '
    'fundingRatePrediction, поделённое на markPrice тем же приёмом, что и funding_rate; WS '
    'relative_funding_rate_prediction — уже готовая доля). WEEX, Binance, Hyperliquid — нет ни на '
    'одном транспорте. Не путать с funding_rate — тот про уже наступивший период.';
comment on column market_snapshot_latest.funding_rate_predicted is
    'Прогноз СЛЕДУЮЩЕГО периода funding, как отдаёт биржа. Только Kraken (REST '
    'fundingRatePrediction, поделённое на markPrice тем же приёмом, что и funding_rate; WS '
    'relative_funding_rate_prediction — уже готовая доля). WEEX, Binance, Hyperliquid — нет ни на '
    'одном транспорте. Не путать с funding_rate — тот про уже наступивший период.';

comment on column market_snapshot.next_funding_at is
    'Момент следующего платежа funding КАК ПРИСЛАЛА БИРЖА — наблюдение, не наш расчёт из '
    'funding_interval_hours. Binance несёт его на обоих транспортах (REST premiumIndex.'
    'nextFundingTime, WS !markPrice@arr@1s "T"). Kraken — только на WS (next_funding_rate_time); '
    'REST /tickers этого поля не отдаёт вовсе, NULL там — свойство транспорта, не пробел в '
    'измерении. WEEX и Hyperliquid — нет ни на одном транспорте.';
comment on column market_snapshot_latest.next_funding_at is
    'Момент следующего платежа funding КАК ПРИСЛАЛА БИРЖА — наблюдение, не наш расчёт из '
    'funding_interval_hours. Binance несёт его на обоих транспортах (REST premiumIndex.'
    'nextFundingTime, WS !markPrice@arr@1s "T"). Kraken — только на WS (next_funding_rate_time); '
    'REST /tickers этого поля не отдаёт вовсе, NULL там — свойство транспорта, не пробел в '
    'измерении. WEEX и Hyperliquid — нет ни на одном транспорте.';

comment on column market_snapshot.volume_24h_base is
    'Скользящие 24 ч оборота в BASE (единицах инструмента, как qty_step). WEEX (base_volume), '
    'Binance (REST volume, WS "v") и Kraken (vol24h, оба транспорта) несут его; Hyperliquid — '
    'нет: DayNtlVlm — оборот в quote (USD notional), базового аналога не существует на этом '
    'масштабе. turnover_24h остаётся оборотом в QUOTE — оба поля вместе не выводят одно из '
    'другого без цены на каждый момент внутри окна, а не только на его конец.';
comment on column market_snapshot_latest.volume_24h_base is
    'Скользящие 24 ч оборота в BASE (единицах инструмента, как qty_step). WEEX (base_volume), '
    'Binance (REST volume, WS "v") и Kraken (vol24h, оба транспорта) несут его; Hyperliquid — '
    'нет: DayNtlVlm — оборот в quote (USD notional), базового аналога не существует на этом '
    'масштабе. turnover_24h остаётся оборотом в QUOTE — оба поля вместе не выводят одно из '
    'другого без цены на каждый момент внутри окна, а не только на его конец.';
