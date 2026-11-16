# Переключение real↔stub и замещение 1С/Битрикс

Цель: опробовать переключение между реальной системой и имитатором **без
изменения кода** — только конфигурацией, и автоматически замещать отсутствующие
системы собственными сервисами.

## Режимы адаптера

Параметры задаются в `.env` / `docker-compose.yml` (секция `environment` адаптера):

| Переменная | Значение |
|---|---|
| `Upstream__Mode` | `auto` (по умолчанию) / `real` / `stub` |
| `Upstream__PrimaryUrl` | базовый URL реальной системы |
| `Upstream__FallbackUrl` | базовый URL нашего имитатора |
| `Upstream__ProbePath` | путь health-проверки primary (`/health`) |
| `Upstream__ProbeIntervalSeconds` | период probe (5 с) |

### Поведение режимов

| Режим | Куда идёт трафик | Случай применения |
|---|---|---|
| `real` | всегда primary; при недоступности — 502 `DP-1050` | жёсткое требование реальной системы |
| `stub` | всегда fallback (имитатор) | разработка без внешних систем |
| `auto` | primary, пока жив; при падении — fallback; при восстановлении — обратно | **замещение по умолчанию** |

## Как это работает внутри

1. `UpstreamSwitcher` (BackgroundService) пингует
   `PrimaryUrl + ProbePath` каждые N секунд → флаг `PrimaryHealthy`.
2. Запросы адаптера всегда строятся против primary;
   `UpstreamSwitchHandler` (DelegatingHandler) переписывает адрес на активный
   upstream (`Active = mode==real ? primary : mode==stub ? fallback : primaryHealthy ? primary : fallback`).
3. В `auto` первый сетевой сбой соединения или 502/503/504 от primary
   немедленно ретраится на fallback — пользователь переключения не замечает.
4. Прозрачность диагностики: `GET /api/v1/int/{system}/route` возвращает
   `{system, mode, active: "real"|"stub", primaryHealthy}`.

## Сценарии

### 1. Клиент вообще не имеет 1С (из коробки)
```bash
docker compose up -d
curl -H "Authorization: Bearer dp-dev-token" http://localhost:8080/api/v1/int/1c/route
# → {"system":"1c","mode":"auto","active":"stub","primaryHealthy":false}
```
Адаптер замещает отсутствующую 1С имитатором автоматически.

### 2. Клиент подключает реальную 1С
```bash
# .env
ADAPTER_1C_PRIMARY_URL=http://1c-host:8080/erp/odata
ADAPTER_1C_MODE=real      # или auto, чтобы подстраховаться имитатором
docker compose up -d adapter-1c
```
> Примечание: для реальной 1С настройте `Upstream__ProbePath` на реально
> доступный лёгкий эндпоинт (например, корень OData), иначе probe будет считать
> систему недоступной.

### 3. Опробование переключения (демо отказа имитатора)
```bash
# имитатор «умирает»:
curl "http://localhost:8091/__fail?mode=down"
# документы через адаптер → 502 DP-1050 (обе системы недоступны)
curl -H "Authorization: Bearer dp-dev-token" http://localhost:8080/api/v1/int/1c/documents

# имитатор «оживает» (или через N секунд: /__fail?mode=down&for=10):
curl "http://localhost:8091/__fail?mode=ok"
# → замещение снова работает
```

### 4. Битрикс — аналогично
`ADAPTER_BITRIX_MODE`, `ADAPTER_BITRIX_PRIMARY_URL`, ключ вебхука имитатора —
`STUB_BITRIX_KEY` (пути вида `/rest/{key}/crm.deal.list.json`).

## API адаптера (унифицированный, для любой системы)

| Метод и путь | Назначение |
|---|---|
| `GET /api/v1/int/{system}/route` | какой upstream активен сейчас |
| `GET /api/v1/int/{system}/health` | живость активного upstream |
| `GET /api/v1/int/{system}/documents?type=&since=` | выборка документов (перевод в универсальный контракт) |
| `POST /api/v1/int/{system}/documents` | отправка документа во внешнюю систему |

## Имитаторы

- **stub-1c** (порт 8091): OData-подобные эндпоинты
  `/odata/Standard.odata/Document_SalesOrder`, `$metadata`, `$filter` (eq/ne/gt/ge/lt/le + and/or),
  CRUD, 10 предзаполненных заказов.
- **stub-bitrix** (порт 8092): REST-вебхуки `/rest/{key}/crm.deal.{list,get,add,update,delete}.json`,
  10 предзаполненных сделок, проверка ключа вебхука.

Оба имитатора: `/health`, тумблер `/__fail`, состояние in-memory (перезапуск
восстанавливает предзаполненные данные — для MVP достаточно).
