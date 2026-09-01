-- ============================================================================
-- 0009 — collector_run: история прогонов коллекторов.
--
-- Зачем: collector_status хранит ОДНУ строку на коллектор — текущее состояние,
-- каждый прогон её перезаписывает. Посмотреть «что происходило» задним числом
-- было не из чего: ни списка сборов, ни тренда латентности (панель в UI честно
-- висела NOT WIRED с запросом ровно этой таблицы). Теперь петля пишет строку
-- на каждый прогон; ретеншен 7 дней держит таблицу маленькой (~100k строк на
-- биржу при полном наборе петель).
--
-- Что НЕ вошло и почему:
--   * привязка записанных данных к прогону (run_id на строках данных) — данные
--     upsert-ятся и переживают много прогонов; «что пришло» отвечается выборкой
--     по окну времени прогона (updated_at/received_at), это честнее и бесплатно;
--   * items у funding-строк не проверить тем же способом — у funding_rate_history
--     нет отметки вставки (funding_time — время платежа); там источник истины
--     только счётчик items самого прогона.
-- ============================================================================

create table collector_run (
    id            bigint      generated always as identity primary key,
    exchange_code text        not null references exchange (code),
    collector     text        not null,
    started_at    timestamptz not null,
    duration_ms   integer     not null check (duration_ms >= 0),
    ok            boolean     not null,
    error         text,
    items         integer                check (items >= 0)
);

comment on table collector_run is
    'Одна строка на прогон петли коллектора. Пишет CollectorLoop через WriteStatusAsync; '
    'ретеншен 7 дней делает RetentionJob. Источник для списка сборов и тренда латентности в UI.';

comment on column collector_run.items is
    'Сколько элементов вернул прогон (инструментов у discovery, строк у snapshot/candles). '
    'null у провала. Для «что пришло» данные ищутся по окну времени, не по этому числу.';

create index collector_run_lookup
    on collector_run (exchange_code, collector, started_at desc);
