-- ============================================================================
-- CryptoSmith X — миграция 0015: сигнал для живых обновлений админки.
--
-- live.js раньше перезапрашивал всю страницу раз в 10 с вслепую — тянул
-- разметку целиком ради одной изменившейся цифры и дёргал интерфейс, даже
-- когда ничего не менялось. Хаб уже пишет в базу; пусть база и сообщает,
-- что изменилось — Postgres LISTEN/NOTIFY, один канал 'csx_live'.
--
-- Не по строке: collector_status — это UPSERT раз на завершённый проход
-- коллектора (не раз на снапшот-строку — их в проходе тысяча), так что
-- триггер на ней уже даёт ровно нужную частоту без явного дросселирования
-- здесь. exchange и exchange_collection добавлены отдельно: статус биржи
-- (Lifecycle) и policy фида (диалог Edit feed) — решения человека, а не
-- проходы коллектора, и должны отражаться на экране так же быстро.
--
-- Payload — {"exchange": "...", "collector": "..." | null}. collector null
-- значит «изменилась сама биржа или её матрица целиком», не конкретный
-- коллектор — WebApp просто освежает все живые панели этой биржи; для
-- «пользователей у нас двое» дробить точнее — лишняя сложность.
--
-- collector_run НЕ получает свой триггер: он пишется в той же паре вызовов,
-- что и collector_status (ExchangeWorker.WriteStatusAsync), так что триггер
-- на collector_status уже сигнализирует «у этого коллектора был проход» —
-- второй триггер на том же событии был бы дублем.
-- ============================================================================

create function notify_collector_status_change() returns trigger as $$
begin
    perform pg_notify('csx_live', json_build_object('exchange', new.exchange_code, 'collector', new.collector)::text);
    return new;
end;
$$ language plpgsql;

create trigger collector_status_notify
    after insert or update on collector_status
    for each row execute function notify_collector_status_change();

create function notify_exchange_change() returns trigger as $$
begin
    perform pg_notify('csx_live', json_build_object('exchange', new.code, 'collector', null)::text);
    return new;
end;
$$ language plpgsql;

create trigger exchange_notify
    after update on exchange
    for each row execute function notify_exchange_change();

create function notify_exchange_collection_change() returns trigger as $$
begin
    perform pg_notify('csx_live', json_build_object('exchange', new.exchange_code, 'collector', null)::text);
    return new;
end;
$$ language plpgsql;

create trigger exchange_collection_notify
    after update on exchange_collection
    for each row execute function notify_exchange_collection_change();

comment on function notify_collector_status_change() is
    'Fires once per completed collector pass (collector_status is upserted, not inserted per row) — '
    'never per market_snapshot row. See the 0015 migration header for why this is the right frequency.';
