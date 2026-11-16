-- 004_files.sql
-- Метаданные content-addressed файлового хранилища (id = SHA-256 контента).
CREATE SCHEMA IF NOT EXISTS dp_files;

CREATE TABLE IF NOT EXISTS dp_files.files (
  id            text        PRIMARY KEY,
  size          bigint      NOT NULL,
  content_type  text        NOT NULL DEFAULT 'application/octet-stream',
  original_name text        NOT NULL DEFAULT '',
  created_at    timestamptz NOT NULL DEFAULT now(),
  deleted_at    timestamptz
);
