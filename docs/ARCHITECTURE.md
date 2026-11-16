# Архитектура Dp.Platform (MVP)

## Контур

```
                          ┌──────────────────────────────────────────────────┐
   Клиенты / UI ────────▶ │ gateway :8080                                    │
   (Bearer token)         │ маршрутизация по префиксам + агрегированный /health │
                          └───┬─────────┬─────────┬─────────┬────────────────┘
                              │         │         │         │
        /api/v1/stores        │         │         │         │  /api/v1/int/{system}
                              ▼         ▼         ▼         ▼
                        ┌──────────┐ ┌────────┐ ┌────────┐ ┌──────────────┐
                        │ registry │ │  data  │ │ files  │ │ adapter-1c   │──┐
                        │ :8081    │ │ :8082  │ │ :8083  │ └──────────────┘  │ auto
                        └────┬─────┘ └───┬────┘ └───┬────┘ ┌──────────────┐  │ замещение
                             │           │          │      │ adapter-bitrix│──┘
                             │           │          │      └──────────────┘
                             ▼           ▼          ▼          │        │
                        dp_registry  dp_data  dp_files        ▼        ▼
                             ▲           │                     stub-1c  stub-bitrix
                             │           │ триггер outbox      :8091    :8092
                        (HTTP API)       ▼                     (имитаторы API 1С
                        ┌──────────┐ dp_cdc.outbox              и Битрикс24)
                        │   cdc    │ ── dp.data.changed.v1 ──▶ RabbitMQ dp.events
                        │ :8084    └───────────────────────── (topic exchange)
                        └──────────┘
   migrator (одноразово) ──▶ dp_meta.migration_journal + схемы
```

## Принципы (выполнение требований Этапа A)

1. **Модульность**: сервис владеет только своей схемой PostgreSQL
   (dp_registry, dp_data, dp_files, dp_cdc, dp_meta). Кросс-доступ к чужим БД
   запрещён архитектурно: все межмодульные вызовы — HTTP/RabbitMQ.
2. **Единица версии = logical store**: `sales.orders@1.0.0`. Физическая таблица
   одна на store (`dp_data.sales_orders`), развитие схемы — только новыми
   версиями (expand/contract в миграциях Этапа B).
3. **Без dual-write**: outbox-строка пишется тем же COMMIT, что и изменение
   (триггер `dp_cdc.capture_change`), публикация в RabbitMQ — отдельным
   воркером cdc (at-least-once, семантика идемпотентного потребителя по eventId).
4. **Tolerant reader**: query/insert игнорируют неизвестные поля только
   на границе DTO; запись валидируется по схеме реестра (DP-1000 с перечнем).
5. **Ошибки**: RFC 9457 Problem Details + `code: DP-XXXX`
   (реестр кодов — `Dp.Platform.Framework/Results/DpCodes.cs`).
6. **Один Dockerfile на все сервисы** (`ARG PROJECT_PATH`) — DRY на уровне
   сборки; различия сервисов только в compose-конфигурации.

## Компоненты ядра `Dp.Platform.Framework`

| Модуль | Назначение |
|---|---|
| `Results/` | `Result`/`Result<T>`, `Error`, коды DP-XXXX, мост в RFC 9457 |
| `Hosting/` | `AddDpService`/`UseDpService`: исключения → Problem Details, `/health`, health-check PostgreSQL |
| `Messaging/` | `IEventPublisher` (DIP) + RabbitMQ-публикатор (topic, persistent), конверт `EventEnvelope` (snake_case) |
| `Storage/` | `IBlobStore` + `ContentAddressedBlobStore` (SHA-256, `blobs/ab/cd/<hash>`, атомарная запись, дедупликация) |
| `Http/` | `UpstreamOptions` (auto/real/stub), `UpstreamSwitcher` (probe), `UpstreamSwitchHandler` (перенаправление + retry на замещение) |
| `Adapters/` | Универсальный хост адаптера: `IAdapterProfile` + `IDocumentTranslator` — новая система = профиль, а не новый код хоста |
| `Stubs/` | Общие части имитаторов: in-memory store, JSON-merge (PATCH), фильтр-кит, тумблер отказа `/__fail` |

## Потоки

### Регистрация store
`gateway → registry`: валидация DSL (имена, 16 канонических типов, ключ),
semver-сравнение с последней версией, матрица совместимости
(`CompatibilityRules.Diff`): ломающие изменения требуют MAJOR-bump (DP-1004),
аддитивные — MINOR/PATCH. Определение сохраняется в `dp_registry.store_versions` (jsonb).

### Запись данных
`gateway → data`: резолвинг схемы через API реестра (TTL-кеш 30 с),
провизионинг таблицы (`CREATE TABLE IF NOT EXISTS` + PRIMARY KEY + триггер),
валидация имён полей/расширений, параметризованный SQL (идентификаторы только
из схемы). Триггер в той же транзакции пишет `dp_cdc.outbox` (store, op, key, payload).

### События изменений
`cdc`: батчами (FOR UPDATE SKIP LOCKED, публикация внутри транзакции) →
RabbitMQ `dp.events`, routing key `dp.data.changed.v1`, тело — `EventEnvelope`
(event_id, event_type, occurred_at, payload = DataChangedEventV1: store,
version, op ∈ inserted/updated/deleted, key, payload).

### Файлы
`gateway → files`: multipart → SHA-256 за один проход → `blobs/ab/cd/<hash>`
(атомарно: tmp+move; дубликат = дедупликация) → метаданные в `dp_files.files`.
Удаление — tombstone (`deleted_at`); повторная загрузка того же контента
воскрешает запись.

### Интеграции (замещение)
`gateway → adapter-{system}`: адаптер держит фоновый probe primary
(реальная система). Запрос всегда строится против primary, `UpstreamSwitchHandler`
переписывает адрес на `Active` (real|stub). В `auto` первый сетевой сбой или
502/503/504 немедленно ретраится на имитатор. Перевод диалекта —
`IDocumentTranslator` (1С: OData `Document_SalesOrder`, Битрикс: `crm.deal.*`).

## Схемы PostgreSQL

| Схема | Таблицы | Владелец-сервис |
|---|---|---|
| dp_meta | migration_journal | migrator |
| dp_registry | stores, store_versions | registry |
| dp_data | физические таблицы store | data (ddl), cdc (outbox-триггер) |
| dp_cdc | outbox, capture_change() | cdc (запись outbox — триггер от имени data) |
| dp_files | files | files |

## Расширение платформы (точки OCP)

- **Новая интеграция**: проект с `IAdapterProfile` (~70 строк) + сервис в compose.
- **Новая операция данных**: класс `IDataOperation` + 1 строка `AddKeyedSingleton`.
- **Новый канонический тип**: строка в `FieldKindMapper` (имя + PG-тип).
- **Новый код ошибки**: константа в `DpCodes` + строка в `HttpStatusOf`.
- **Новый маршрутизируемый сервис**: строка `Routes` в конфигурации шлюза.
- **Новое правило совместимости**: элемент словаря `CompatibilityRules.Breaking`.
