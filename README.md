# Dp.Platform — MVP

Модульная B2B-платформа данных (Data Platform v1, Этап A): единое место хранения
и версионирования бизнес-данных (логических хранилищ), файловое хранилище,
события изменений (CDC) и интеграционные адаптеры к внешним системам (1С,
Битрикс24) с переключением real↔stub **без изменения кода**.

Реализация MVP: .NET 8 / C#, PostgreSQL 16, RabbitMQ 3.13, Docker Compose.
Полностью офлайн: NuGet-зависимости вендорены в `lib/` (Npgsql 8.0.7,
RabbitMQ.Client 6.8.1 + их зависимости), сборка контейнеров не требует доступа
в интернет (только базовые образы .NET при первой сборке).

## Что делает Dp.Platform (кратко)

| Блок | Что делает |
|---|---|
| **Реестр схем** | Принимает определения logical store на SD DSL v1 (`sales.orders@1.0.0`), валидирует, проверяет совместимость версий (матрица изменений), хранит историю версий |
| **Data Access API** | 9 операций над записями logical store (insert, bulk-insert, get, query, patch, replace, delete, count, patch-all) + Query DSL (filter/sort/pagination), Extension DTO (`ns.field` → jsonb) |
| **Файловое хранилище** | Content-addressed хранилище: id = SHA-256 контента, дедупликация, tombstone-удаление |
| **CDC** | Транзакционный outbox (триггер в транзакции изменения — без dual-write) → публикация `dp.data.changed.v1` в RabbitMQ (topic exchange `dp.events`) |
| **Мигратор** | Forward-only SQL-миграции, журнал `dp_meta.migration_journal`, защита истории (checksum) |
| **Адаптеры интеграций** | 1 контейнер = 1 внешняя система. Режим `auto`/`real`/`stub`; в `auto` при недоступности реальной системы трафик автоматически уходит на наш имитатор (замещение) |
| **Имитаторы** | Полные копии API 1С (OData `Document_SalesOrder`) и Битрикс24 (REST-вебхуки `crm.deal.*`) в отдельных контейнерах, с тумблером отказа `/__fail` |
| **Шлюз** | Единая точка входа: маршрутизация по префиксам, bearer-токен (offline-заглушка OIDC), агрегированный `/health/services`, карта сервисов `/info` |

## Быстрый старт

```bash
cp .env.example .env          # при необходимости поправьте режимы/адреса
docker compose up -d --build  # поднимает 11 контейнеров + migrator
docker compose logs -f migrator   # дождитесь "done: N applied"
bash scripts/smoke-test.sh    # 25 проверок сквозного сценария
```

- Единая точка входа: `http://localhost:8080` (шлюз), токен из `.env`
  (`DP_API_TOKEN`, по умолчанию `dp-dev-token`): `Authorization: Bearer dp-dev-token`
- Management UI RabbitMQ: `http://localhost:15672` (dp/dp)
- PostgreSQL: `localhost:5433` (dp/dp, база `dp`)

## Состав контейнеров

| Контейнер | Порт (host) | Проект | Назначение |
|---|---|---|---|
| `gateway` | 8080 | Dp.Platform.Gateway | Входная точка, auth, health-агрегация |
| `registry` | 8081 | Dp.Platform.Registry | SD DSL v1, версии, совместимость |
| `data` | 8082 | Dp.Platform.Data | Data Access API + Query DSL |
| `files` | 8083 | Dp.Platform.Files | Content-addressed файлы |
| `cdc` | 8084 | Dp.Platform.Cdc | Outbox → RabbitMQ |
| `adapter-1c` | 8085 | Dp.Platform.Adapter.OneC | Адаптер 1С (auto/real/stub) |
| `adapter-bitrix` | 8086 | Dp.Platform.Adapter.Bitrix | Адаптер Битрикс24 (auto/real/stub) |
| `stub-1c` | 8091 | Dp.Platform.Stub.OneC | Имитатор API 1С |
| `stub-bitrix` | 8092 | Dp.Platform.Stub.Bitrix | Имитатор API Битрикс24 |
| `migrator` | — (одноразовый) | Dp.Platform.Migrator | Миграции + журнал |
| `postgres` | 5433 | — | Схемы dp_registry/dp_data/dp_files/dp_cdc/dp_meta |
| `rabbitmq` | 5672 / 15672 | — | Брокер событий dp.events |

Каждый сервис — отдельный контейнер, отдельная схема PostgreSQL и свой
`/health`. Прямые обращения между схемами запрещены: сервисы общаются
только по HTTP (реестр ↔ data ↔ cdc) и через RabbitMQ.

## Переключение real↔stub и замещение

Управляется **только конфигурацией** (см. [docs/SWITCHING.md](docs/SWITCHING.md)):

```bash
# .env
ADAPTER_1C_MODE=auto          # auto | real | stub
ADAPTER_1C_PRIMARY_URL=http://1c-real:8080   # реальная 1С (если есть)
```

- `auto` (по умолчанию) — запрос идёт в реальную 1С; если она недоступна
  (health-probe каждые 5 с), трафик автоматически уходит на `stub-1c`.
  Требование «если нет 1С или Битрикс — замещаем своими сервисами»
  выполняется без правки кода.
- `stub` — всегда имитатор (разработка без внешних систем).
- `real` — только реальная система (быстрый отказ 502 DP-1050).

## Документация

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — архитектура, потоки данных, БД, события
- [docs/SWITCHING.md](docs/SWITCHING.md) — переключение адаптеров и замещение
- [docs/CODESTYLE.md](docs/CODESTYLE.md) — правила SOLID/DRY/KISS этого репозитория

## Соответствие Этапу A и MVP-упрощения

Реализовано по спецификации «Data Platform v1, Этап А»: SD DSL v1 (канонические
типы, Extension DTO, логическое хранилище = единица версии), матрица
совместимости + tolerant reader (перегорание неизвестных полей), forward-only
миграции с журналом, content-addressed файловое хранилище (SHA-256, лимит 2 ГБ),
RFC 9457 + коды DP-XXXX, контракт `dp.data.changed.v1`.

MVP-упрощения (осознанные, зафиксированы для Этапа B):

1. **CDC**: вместо CdcGateway на pgoutput — транзакционный outbox (тот же
   контракт событий, без dual-write; декодер pgoutput — Этап B).
2. **Версия события** `dp.data.changed.v1` — актуальная версия store на момент
   публикации (точная версия на момент изменения — Этап B).
3. **DDL logical store** — провизионер Data-сервиса (idempotent `CREATE TABLE
   IF NOT EXISTS`); в prod DDL переносится в мигратор.
4. **Auth** — статический bearer-токен вместо OIDC/Keycloak (Этап B).
5. **Query DSL** — подмножество операторов (eq/ne/gt/ge/lt/le/like/in/nin/isNull),
   сортировка только по обычным полям, лимит ≤ 500.
6. **Required-поля DSL** — принимаются реестром, enforcement — Этап B
   (сейчас обязательным является только ключ).
