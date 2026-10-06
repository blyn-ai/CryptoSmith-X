-- ============================================================================
-- CryptoSmith X — миграция 0068: партиции по дням и ретеншен, который
-- действительно удаляет.
--
-- ПОЧЕМУ. 24 сентября на ТЕСТЕ кончилось место, docker не смог записать даже
-- служебный файл при рестарте, хаб вышел и больше не поднялся — двенадцать
-- дней простоя, которые никто не заметил. 3 октября то же самое случилось на
-- ПРОДЕ: диск под /csx-data забился, Postgres упал в цикл «чекпоинт → PANIC →
-- recovery», и сбор встал на трое суток. Причина у обоих одна: база росла
-- строго вверх, а удалять было нечем.
--
-- Нечем — буквально. RetentionJob сканировал партиции и писал в лог
-- «is being KEPT», удаляя ноль строк: удаление было намеренно отключено,
-- потому что снимок рынка нельзя купить обратно. Это решение остаётся в силе
-- для прода и здесь НЕ отменяется — настройка ниже по умолчанию выключена,
-- и миграция ничего не удаляет.
--
-- ---------------------------------------------------------------------------
-- 1. Дневные партиции — иначе «48 часов» невыразимы
-- ---------------------------------------------------------------------------
-- Все пять тяжёлых таблиц нарезаны по месяцам (0001, 0032). Минимальный кусок,
-- который можно отрезать мгновенно, — целый месяц, поэтому окно хранения
-- короче месяца месячными партициями не описать: DELETE место операционной
-- системе не возвращает, а VACUUM FULL требует второй копии таблицы, которой
-- на забитом диске заведомо нет.
--
-- Отсюда вторая гранулярность. Выбор — глобальная настройка
-- partition_granularity: 'month' (умолчание, как было) или 'day'. Она читается
-- не кодом, а самой SQL-функцией ensure_partition, потому что партицию создают
-- восемь разных мест в хабе, и единственный способ гарантировать, что они не
-- разойдутся во мнениях, — не давать им выбора вовсе. Разошедшись, они бы не
-- просто создали лишнее: месячная партиция поверх дневных — это пересечение
-- диапазонов, то есть отказ вставки, то есть остановка сбора.
--
-- ---------------------------------------------------------------------------
-- 2. Переход между гранулярностями не ломает запись
-- ---------------------------------------------------------------------------
-- На контуре, который уже писал месяцами, дневная партиция для сегодняшнего дня
-- пересечётся с существующей месячной. Postgres на это отвечает ошибкой, и она
-- здесь гасится: пересечение означает, что диапазон УЖЕ покрыт, то есть работа
-- функции и так выполнена. Поэтому переключение granularity безопасно в любой
-- момент: старые месячные партиции доживают свой месяц и обслуживают запись,
-- новые дни нарезаются рядом, как только месяц кончится.
--
-- ---------------------------------------------------------------------------
-- 3. retention_delete_after_hours — выключатель, а не срок
-- ---------------------------------------------------------------------------
-- 0 = не удалять никогда, и это умолчание для всех контуров, включая прод.
-- Любое положительное число = дропать партиции, чей диапазон ЦЕЛИКОМ старше
-- этого числа часов. Партиция, в которую ещё может прийти строка, не трогается
-- никогда: условие проверяется по ВЕРХНЕЙ границе диапазона, а не по нижней.
--
-- Тест включает это сам (ops/test-retention-48h.sql): 48 часов и 'day'.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- Дневная партиция. Зеркало create_month_partition из 0001, включая его
-- идемпотентность: имя детерминировано, create table if not exists.
-- ---------------------------------------------------------------------------
create or replace function create_day_partition(parent regclass, day date)
returns void
language plpgsql
as $$
declare
    start_date date := day;
    end_date   date := (day + interval '1 day')::date;
    part_name  text := format('%s_%s', parent::text, to_char(start_date, 'YYYY_MM_DD'));
begin
    execute format(
        'create table if not exists %I partition of %s for values from (%L) to (%L)',
        part_name, parent, start_date, end_date
    );
exception
    -- «would overlap partition» — диапазон уже покрыт (обычно месячной
    -- партицией на контуре, который только что переключили на дни). Цель
    -- вызова достигнута, писать есть куда.
    when invalid_object_definition then
        null;
end
$$;

comment on function create_day_partition(regclass, date) is
    'Идемпотентно, как и месячный близнец. Пересечение с существующей партицией не ошибка: '
    'диапазон уже покрыт, значит запись пройдёт.';

-- Тот же глушитель пересечений месячной функции: при обратном переключении
-- (day -> month) месяц ляжет поверх уже существующих дней.
create or replace function create_month_partition(parent regclass, month date)
returns void
language plpgsql
as $$
declare
    start_date date := date_trunc('month', month)::date;
    end_date   date := (start_date + interval '1 month')::date;
    part_name  text := format('%s_%s', parent::text, to_char(start_date, 'YYYY_MM'));
begin
    execute format(
        'create table if not exists %I partition of %s for values from (%L) to (%L)',
        part_name, parent, start_date, end_date
    );
exception
    when invalid_object_definition then
        null;
end
$$;

-- ---------------------------------------------------------------------------
-- Одна точка входа для хаба. Гранулярность решается здесь, а не в вызывающем
-- коде — см. раздел 1 шапки.
-- ---------------------------------------------------------------------------
create or replace function ensure_partition(parent regclass, at timestamptz)
returns void
language plpgsql
as $$
declare
    granularity text := coalesce((select value from setting where key = 'partition_granularity'), 'month');
begin
    if granularity = 'day' then
        perform create_day_partition(parent, (at at time zone 'UTC')::date);
    else
        perform create_month_partition(parent, (at at time zone 'UTC')::date);
    end if;
end
$$;

comment on function ensure_partition(regclass, timestamptz) is
    'Партиция, покрывающая этот момент, по текущей partition_granularity. Единственный способ '
    'создания партиций из хаба: восемь мест вызывают её и поэтому не могут разойтись в гранулярности.';

-- Партиция «сейчас» и следующая за ней — ровно то, что нужно службе, которая
-- пишет только настоящее, и то, что переживает смену суток и месяца.
create or replace function ensure_next_partitions(parent regclass, at timestamptz)
returns void
language plpgsql
as $$
declare
    granularity text := coalesce((select value from setting where key = 'partition_granularity'), 'month');
begin
    perform ensure_partition(parent, at);
    if granularity = 'day' then
        perform ensure_partition(parent, at + interval '1 day');
    else
        perform ensure_partition(parent, at + interval '1 month');
    end if;
end
$$;

-- ---------------------------------------------------------------------------
-- Настройки. Обе — выключенное состояние, то есть сегодняшнее поведение.
-- ---------------------------------------------------------------------------
insert into setting (key, value, kind, description) values
    ('partition_granularity', 'month', 'text',
     'Нарезка партиций тяжёлых таблиц: month или day. day нужен там, где окно хранения короче месяца.'),
    ('retention_delete_after_hours', '0', 'int',
     'Через сколько часов дропать партицию, диапазон которой целиком в прошлом. 0 = не удалять никогда (умолчание и правило прода).')
on conflict (key) do nothing;

do $$
begin
    if (select count(*) from setting where key in ('partition_granularity', 'retention_delete_after_hours')) <> 2 then
        raise exception 'настройки ретеншена не записались';
    end if;

    if (select value from setting where key = 'retention_delete_after_hours') <> '0' then
        raise exception 'retention_delete_after_hours должен быть 0 после миграции: включение — решение оператора, не миграции';
    end if;
end $$;
