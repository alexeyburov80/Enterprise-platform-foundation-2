# Data Platform MVP — локальный стенд с имитаторами 1С и Битрикс24

Минимальная рабочая реализация базового функционала Data Platform по утверждённой
спецификации **этапа A** (`Data_Platform_v1_Etap_A_texhnicheskaya_specifikaciya.docx`).
Всё работает **локально и офлайн**: docker compose поднимает **6 отдельных контейнеров** —
PostgreSQL, RabbitMQ, два сервиса платформы (dp-api и dp-sync) и два имитатора
внешних систем. Ключевая демонстрация — **переключение интеграции между режимами
`auto` / `real` / `stub` без изменения кода и рестарта** (Strangler Fig / Adoption v1),
в том числе **автоматическая подмена реальной 1С/Битрикс нашими сервисами-имитаторами**,
когда реальные системы недоступны.

## 1. Состав стенда и описание сервисов

```
                    ┌──────────────────────────┐
   клиенты ────────►│  dp-api :8080            │  Data Access API (/dp/v1, 9 операций)
                    │  (Dp.Api)                │  admin-контур, миграции, SD-реестр
                    └─────┬───────────┬────────┘
                          │ SQL       │ HTTP (X-Api-Key) — управление
                ┌─────────▼───┐   ┌───▼──────────────┐
                │ PostgreSQL  │   │ dp-sync :8090    │  интеграционный воркер:
                │ :5432       │   │ (Dp.Sync)        │  адаптеры 1С/Б24, auto|real|stub,
                │ store'ы +   │   └───┬──────┬───────┘  fallback, outbox → RabbitMQ
                │ dp.outbox   │       │      │ AMQP: dp.data.changed.v1
                └─────────────┘       │  ┌───▼─────────┐
                          │           │  │ RabbitMQ    │
                          │           │  │ :5672/:15672│
                          │           │  └─────────────┘
              HTTP (real/stub)        │
        ┌─────────────┬───────────────┤
        ▼             ▼               ▼
┌───────────────┐ ┌─────────────────┐ ┌───────────────────────────┐
│ imitator-1c   │ │ imitator-       │ │ «реальные» системы        │
│ :8081 (OData) │ │ bitrix :8082    │ │ real-1c :8083 / real-     │
│ НАШ           │ │ НАШ заместитель │ │ bitrix :8084              │
│ заместитель   │ │ Битрикс24       │ │ (docker-compose.real.yml  │
│ 1С            │ │                 │ │ или настоящие контуры)    │
└───────────────┘ └─────────────────┘ └───────────────────────────┘
```

### Что делает Dp.Platform

Бывший монолит `Dp.Platform` (v0) **разделён на три проекта** — библиотеку ядра и
два самостоятельно развёртываемых сервиса. «Dp.Platform» как единый контейнер
больше не существует; его функции распределены так:

| Проект | Контейнер | Назначение |
|---|---|---|
| **Dp.Core** | (библиотека) | Ядро платформы, общий код для обоих сервисов: SD DSL (реестр store'ов `SdRegistry`), генератор DDL + forward-only migrator (`DdlGenerator`/`Migrator`), движок записи (`StoreService`: missing/null/default по табл. 6, Extension DTO jsonb+namespace, idempotency, ETag/If-Match, soft-delete, outbox), Query DSL → SQL (`QueryEngine`, без SQL от клиента), outbox (`OutboxWriter`/`OutboxPublisher`), коннекторы и переключение адаптеров (`AdapterSwitcher`), RFC 9457 (`Problems`, коды DP-XXXX) |
| **Dp.Api** | **dp-api** :8080 | Публичный синхронный контур: Data Access API v1 — 9 операций под `/dp/v1` (Get/Query/Insert/Update PATCH\|PUT/Delete/Batch/ExecuteTransaction/GetSchema/GetCapabilities), Swagger UI `/swagger`; admin-контур `/admin`: health (агрегирует dp-sync), outbox-просмотр, журнал и применение миграций, reload SD; **про интеграционные системы не знает вообще** — управление адаптерами и запуск sync это HTTP-прокси к dp-sync (DP-4002, если воркер недоступен). Только он применяет миграции (журнал `dp.migration_journal`) |
| **Dp.Sync** | **dp-sync** :8090 | Интеграционный воркер — весь интеграционный контур: коннекторы 1С/Битрикс24 (`AdapterSwitcher`: режимы `auto\|real\|stub`, health-probe реальных систем, **fallback на наши имитаторы**), pull-синхронизация в store'ы (`IngestionService`, uuid5-ключи, ext-namespace источника), публикация событий `dp.data.changed.v1` в RabbitMQ (`OutboxPublisher`, at-least-once). Внутреннее API: `GET /health`, `GET /adapters`, `POST /adapters/{code}/mode`, `POST /run/{code}`, `POST /sd/reload`. Схему БД не мигрирует — ждёт dp-api |
| **Dp.Imitator1C** | **imitator-1c** :8081 | НАШ сервис-заместитель 1С: OData-подобный REST (`/odata/Catalog_Nomenclature`, `Document_CustomerOrder[_Lines]`), Basic-auth, `$top/$skip/$filter/$count`, ревизия цен `POST /admin/rotate`, `GET /health` (без auth — для probe) |
| **Dp.ImitatorBitrix** | **imitator-bitrix** :8082 | НАШ сервис-заместитель Битрикс24: webhook REST (`crm.deal.list/get/add`, `crm.product.list`, `batch`), ключ вебхука, `GET /health` |

**Почему так:** каждый компонент — отдельный контейнер с собственной зоной
ответственности; dp-api можно масштабировать/пересобирать, не трогая интеграции;
коннекторы изолированы от публичного API; имитаторы существуют только как внешние
сервисы — встроенных заглушек в платформе больше нет.

## 2. Быстрый старт (Docker)

Требуется Docker + Docker Compose. **Единственный раз** нужна сеть (образы
mcr.microsoft.com/postgres/rabbitmq + NuGet-пакеты при сборке); после сборки
стенд работает полностью офлайн.

```bash
docker compose up -d --build
# готовность: docker compose ps → все healthy; Swagger: http://localhost:8080/swagger
```

Без Docker (вручную): .NET 8 SDK и PostgreSQL 14+; настройки — `appsettings.json`
каждого сервиса или переменные окружения (`ConnectionStrings__Db`, `Sd__Path`,
`Worker__BaseUrl`, `Integration__Systems__1c__Mode`, ...), затем `dotnet run` в
`src/Dp.Api`, `src/Dp.Sync`, `src/Dp.Imitator1C`, `src/Dp.ImitatorBitrix`.

## 3. Режимы интеграции и переключения (главный сценарий)

Каждая внешняя система (`1c`, `bitrix`) работает в одном из трёх режимов:

| Режим | Поведение |
|---|---|
| `auto` (по умолчанию) | Перед каждой синхронизацией dp-sync пробует `GET {RealBaseUrl}/health` реальной системы. Доступна — тянет данные из неё (`auto→real`, `ext.<ns>.source=real`). **Недоступна или не настроена — transparentно замещает её нашим сервисом-имитатором** (`auto→fallback`, `ext.<ns>.source=stub`), причина фиксируется в `GET /admin/adapters` (`lastFallbackReason`) |
| `real` | Только реальный контур (`RealBaseUrl`). Недоступность = ошибка **DP-4001** (RFC 9457), подмены нет — явное обязательство |
| `stub` | Всегда наш имитатор (`StubBaseUrl`) — детерминированный офлайн-контур |

Переключение — горячее, без рестарта и без изменения кода:

```bash
bash deploy/demo/demo.sh          # сценарий целиком, с пояснениями
```

Или вручную (см. `deploy/demo/demo.http`):

```bash
DP=http://localhost:8080; H="X-Api-Key: dev-key"

# 1) sync в режиме auto: реальной 1С нет → данные из нашего имитатора (fallback)
curl -s -H "$H" -X POST $DP/admin/sync/1c

# 2) состояние адаптеров: probe, modeSource=auto→fallback, причина
curl -s -H "$H" $DP/admin/adapters

# 3) подключаем «реальную» 1С (demo-оверлей; в проде — адрес настоящего контура)
docker compose -f docker-compose.yml -f docker-compose.real.yml up -d
curl -s -H "$H" -X POST $DP/admin/sync/1c      # → auto→real, endpoint real-1c

# 4) система «пропала» → transparentный возврат на имитатор
docker stop dp-real-1c
curl -s -H "$H" -X POST $DP/admin/sync/1c      # → auto→fallback

# 5) явный real без подмены: если система недоступна — DP-4001
curl -s -H "$H" -H "Content-Type: application/json" \
     -X POST $DP/admin/adapters/1c/mode -d '{"mode":"real"}'
curl -s -H "$H" -X POST $DP/admin/sync/1c      # → 503 DP-4001

# 6) всегда имитатор / возврат в auto
curl -s -H "$H" -H "Content-Type: application/json" \
     -X POST $DP/admin/adapters/1c/mode -d '{"mode":"stub"}'
curl -s -H "$H" -H "Content-Type: application/json" \
     -X POST $DP/admin/adapters/1c/mode -d '{"mode":"auto"}'
```

Дельта данных: `POST http://localhost:8081/admin/rotate` (цены 1С ×1.07ⁿ) или
`POST http://localhost:8082/admin/rotate` (суммы Б24 ×1.05ⁿ) — затем повторный
sync показывает обновления в store'ах и события в outbox.

## 4. Порты и учётные данные

| Компонент | Адрес | Учётные данные |
|---|---|---|
| dp-api (Data Access API + Swagger) | http://localhost:8080/swagger | `X-Api-Key: dev-key` |
| dp-sync (внутреннее API воркера) | http://localhost:8090/health, /adapters | `X-Api-Key: dev-key` (кроме /health) |
| Имитатор 1С (OData) | http://localhost:8081/odata | Basic `svc_dp` / `dp-secret` |
| Имитатор Битрикс24 (webhook) | http://localhost:8082/rest/crm.deal.list.json | ключ `webhook-dev-key` (`?auth=` или путь `/rest/crm/<key>/...`) |
| «Реальная» 1С / Б24 (оверлей) | http://localhost:8083 / :8084 | те же, что у имитаторов |
| PostgreSQL | localhost:5432, БД `dp` | `dp` / `dp-dev-secret` |
| RabbitMQ UI | http://localhost:15672 | `guest` / `guest` |
| Exchange | `dp.data` (topic) | routing key `dp.data.changed.<module>.<store>.v1` |

## 5. Что реализовано из спецификации этапа A

| Решение | Реализация в MVP |
|---|---|
| A-3 SD DSL v1, 16 канонических типов | `SdRegistry` — загрузка/валидация YAML (`deploy/sd/*.yaml`), эталонный `sales.orders@1.0.0` из листинга 1 |
| A-4/A-5 семантика missing/null/default, Extension DTO (jsonb + namespace) | `PayloadParser`/`StoreService`: insert/patch/replace по табл. 6, мердж namespace без затирания чужих, null удаляет ключ, tolerant reader (`__stray`) |
| A-8…A-12 миграции: собственный migrator, forward-only, журнал | `DdlGenerator` (канон → DDL) + `Migrator` (в dp-api): пакет хешируется SHA-256, `dp.migration_journal` по листингу 3, идемпотентный apply, DP-2004 при пине не-live версии |
| A-15 Data Access API: 9 операций | `Get/Query/Insert/Update(PATCH\|PUT)/Delete/Batch/ExecuteTransaction/GetSchema/GetCapabilities` под `/dp/v1`; Idempotency-Key 24 ч (DP-5001), ETag/If-Match (DP-5002), soft-delete |
| A-16 события dp.data.changed.v1 | Outbox в той же транзакции (В6) + `OutboxPublisher` в dp-sync → RabbitMQ topic `dp.data`; брокер недоступен — события копятся, данные не теряются |
| Ошибки RFC 9457 + коды DP-XXXX | `Problems` — problem+json с `code` (DP-1001…DP-6201) |
| Query DSL v1 (табл. 14) | 14 операторов, глубина ≤3, limit ≤1000, in/notIn ≤1000 значений, строгая типизация значений по SD |
| Adoption/Strangler (гл. 6) | Коннекторы 1С/Битрикс в dp-sync: `auto` (real при доступности + fallback на наши имитаторы), `real`, `stub`; горячее переключение конфигом (`Integration__Systems__1c__Mode`) или админ-API; коннектор 1С никогда не в adoption |

## 6. Осознанные упрощения MVP (отклонения от канона)

- **Аутентификация**: static `X-Api-Key` вместо OIDC + mTLS + capability token (A-18) —
  подлежит замене на этапе B, точки расширения — middleware в `Program.cs` dp-api/dp-sync.
- **Версии store'ов**: одна live-версия; пин чужой версии → DP-2004. Окно
  совместимости N/N-1 включается при первом MAJOR-изменении.
- **joinSpec/агрегаты/count**: не поддержаны (OA-4/OA-5 открыты); `GetCapabilities`
  возвращает фактический набор.
- **fileRef/binary(n)**: колонки и валидация есть; File Storage (CAS, dp_files) — следующий этап.
- **lockRisk/expand-contract**: классы зафиксированы в заголовке пакета, но MVP
  применяет только первичные (create) пакеты — волны expand/contract появятся
  при первом MAJOR-изменении канона.
- **DP-6201 MIGRATION_PACKAGE_MISMATCH** и **DP-4002 SYNC_WORKER_UNAVAILABLE** —
  MVP-локальные расширения реестра кодов (кандидаты на включение в табл. 15 при ревью).
- **CdcGateway (pgoutput)** — вне MVP; коннекторы тянут данные pull-моделью (sync по кнопке).
- **Режим адаптера не персистится**: после рестарта dp-sync возвращается к значению
  из конфигурации (`Integration__Systems__*__Mode`); рантайм-переключения живут в
  памяти процесса.
- **Health-probe реальных систем**: простой GET `{RealBaseUrl}/health` (тайм-аут 2.5 с)
  перед каждой синхронизацией/в `GET /admin/adapters`; кеширование и jitter-политики —
  при переходе к прод-контур.

## 7. Структура каталога

```
docker-compose.yml            стенд: postgres, rabbitmq, dp-api, dp-sync, 2 имитатора
docker-compose.real.yml       оверлей: «реальные» real-1c / real-bitrix (:8083/:8084)
deploy/
  sd/*.yaml                   SD DSL v1: sales.orders, mdm.nomenclature, crm.deals + ExtensionSchema wms
  demo/demo.sh, demo.http     сценарии демонстрации переключений
src/
  Dp.Core/                    ядро (библиотека, namespace Dp.Core)
    Sd.cs                     Schema Registry: загрузка/валидация SD DSL
    Ddl.cs                    генератор DDL + forward-only migrator + журнал
    Validate.cs               канонические типы, missing/null/default (табл. 6)
    StoreService.cs           движок записи: idempotency, ETag, outbox, ext-мерж
    QueryEngine.cs            Query DSL → параметризованный SQL (без SQL от клиента)
    Outbox.cs                 dp.outbox + publisher в RabbitMQ (at-least-once)
    Adapters.cs               ISystemAdapter, коннекторы 1С/Б24, AdapterSwitcher (auto/real/stub + fallback)
    Ingestion.cs              pull-синхронизация в logical store'ы (uuid5-ключи)
    Problems.cs               RFC 9457 + реестр DP-кодов
  Dp.Api/                     сервис dp-api: 9 операций /dp/v1 + admin (+ прокси к dp-sync)
  Dp.Sync/                    сервис dp-sync: адаптеры, sync, outbox publisher, расписание (Sync:AutoIntervalSeconds)
  Dp.Imitator1C/              OData-подобный имитатор 1С (Basic, $top/$skip/$filter, rotate, /health)
  Dp.ImitatorBitrix/          Bitrix24 webhook-имитатор (deal.list/get/add, product.list, batch, /health)
```

## 8. Управление имитаторами (для сценариев отказоустойчивости)

| Вызов | Эффект |
|---|---|
| `POST /admin/rotate` (оба) | сдвиг ревизии: цены 1С ×1.07^n, суммы Б24 ×1.05^n — видимость дельты при следующем sync |
| `Data:DelayMs`, `Data:FailRate` (env) | имитация задержки/доли ошибок 503 — проверка retry и поведения коннектора |
| `GET /admin/state` | ревизия и счётчики набора данных |
| `GET /health` | probe без аутентификации — им пользуются dp-sync (auto/real) и healthcheck |

## 9. Если что-то не работает

- `dp-sync` в цикле пишет «брокер недоступен» — это норма без RabbitMQ: события
  копятся в `dp.outbox` (проверка: `GET /admin/health` → `outboxPending` или
  `GET /admin/outbox`), при появлении брокера уйдут автоматически.
- «Outbox publisher: жду миграций dp-api» — воркер стартовал раньше: он ждёт
  `dp.migration_journal`, которую применяет dp-api; действий не требуется.
- dp-sync: `GET /admin/adapters` вернул DP-4002 — воркер не поднят/не готов;
  проверьте `docker compose ps` и `http://localhost:8090/health`.
- `POST /admin/sync/1c` вернул DP-4001 — задан явный режим `real`, а система
  недоступна. Поднимите реальный контур или переключитесь на `auto`/`stub`.
- Миграция не применилась: `GET /admin/migrations` (журнал), `POST /admin/migrations/apply`
  (повтор идемпотентен), свежий SD — `POST /admin/sd/reload` (перезагружает dp-api и dp-sync).
- 401 на всех запросах — не забывайте заголовок `X-Api-Key: dev-key`.
