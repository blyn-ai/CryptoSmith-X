-- ============================================================================
-- CryptoSmith X — миграция 0066: studio читает segment_dataset_capability.
--
-- 0065 (Aster) научила страницу актива ставить значок SAMPLE у ликвидаций, если у сегмента в
-- segment_dataset_capability записано history_depth = sampled_1s_per_symbol
-- (V2Store.LiquidationCapabilityAsync). Права на эту таблицу у studio_reader не было, и после
-- выкатки КАЖДАЯ страница /studio/v2/<актив> отвечала 500: 42501 permission denied for table
-- segment_dataset_capability (2026-09-17). Тесты этого не видят — они ходят в базу владельцем.
--
-- Та же форма, что 0042, 0043 и 0048: одно право на чтение, ничего больше.
-- ============================================================================

grant select on segment_dataset_capability to studio_reader;

comment on table segment_dataset_capability is
    'Возможности сегмента по наборам данных (transports, history_depth и др.) с источником. '
    'С 0066 читается и studio: history_depth у ликвидаций решает, печатается ли у объёма '
    'значок SAMPLE (выборка, нижняя граница).';
