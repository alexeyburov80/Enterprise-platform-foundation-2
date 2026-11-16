-- 002_registry.sql
-- Реестр схем SD DSL v1: logical store = единица версии (Этап A).
CREATE SCHEMA IF NOT EXISTS dp_registry;

CREATE TABLE IF NOT EXISTS dp_registry.stores (
  name text PRIMARY KEY
);

CREATE TABLE IF NOT EXISTS dp_registry.store_versions (
  store         text        NOT NULL,
  version       text        NOT NULL,
  definition    jsonb       NOT NULL,
  status        text        NOT NULL DEFAULT 'active',
  registered_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (store, version)
);

CREATE INDEX IF NOT EXISTS ix_store_versions_latest
  ON dp_registry.store_versions (store, registered_at DESC);
