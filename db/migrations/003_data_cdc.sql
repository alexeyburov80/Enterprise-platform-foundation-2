-- 003_data_cdc.sql
-- Физическое хранилище logical store + транзакционный outbox (замена CdcGateway
-- на pgoutput в MVP: тот же контракт dp.data.changed.v1, без dual-write).
CREATE SCHEMA IF NOT EXISTS dp_data;
CREATE SCHEMA IF NOT EXISTS dp_cdc;

CREATE TABLE IF NOT EXISTS dp_cdc.outbox (
  id           bigserial   PRIMARY KEY,
  store        text        NOT NULL,
  op           text        NOT NULL,
  key          text,
  payload      jsonb,
  created_at   timestamptz NOT NULL DEFAULT now(),
  processed_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_outbox_pending
  ON dp_cdc.outbox (id) WHERE processed_at IS NULL;

-- Общий триггер: каждая таблица logical store подключается аргументами
-- (имя store, имя ключевого поля). Функция создаётся один раз.
CREATE OR REPLACE FUNCTION dp_cdc.capture_change() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  payload jsonb;
  key_val text;
  op_name text;
BEGIN
  op_name := CASE TG_OP WHEN 'INSERT' THEN 'inserted'
                        WHEN 'UPDATE' THEN 'updated'
                        ELSE 'deleted' END;
  IF TG_OP = 'DELETE' THEN
    payload := to_jsonb(OLD);
    key_val := payload ->> TG_ARGV[1];
    INSERT INTO dp_cdc.outbox (store, op, key, payload)
    VALUES (TG_ARGV[0], op_name, key_val, NULL);
    RETURN OLD;
  ELSE
    payload := to_jsonb(NEW);
    key_val := payload ->> TG_ARGV[1];
    INSERT INTO dp_cdc.outbox (store, op, key, payload)
    VALUES (TG_ARGV[0], op_name, key_val, payload);
    RETURN NEW;
  END IF;
END $$;
