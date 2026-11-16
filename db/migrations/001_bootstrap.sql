-- 001_bootstrap.sql
-- Журнал миграций создаётся самим мигратором (dp_meta.migration_journal).
-- Скрипт документирует старт конвейера; schema dp_meta гарантируется здесь же.
CREATE SCHEMA IF NOT EXISTS dp_meta;
