# Правила кода Dp.Platform (SOLID / DRY / KISS)

Требование ревью: «файлы очень большие, нужна лаконичность; нужны универсальные
решения, которые легко расширять». Этот репозиторий выполняет правила ниже;
при доработках они обязательны.

## Жёсткие бюджеты

1. **Файл ≤ 120 строк**, один тип/ответственность на файл (SRP). Если файл
   растёт — выносится класс/расширение, а не «ещё один метод в конец».
2. **Program.cs сервиса ≤ 25 строк**: только композиция (AddDpService + DI +
   Map-эндпоинты). Вся логика — в именованных классах.
3. Никаких `Region`, никаких файлов-«свалок» (Utils/Common без темы).

## SOLID — как он реализован здесь (образцы для подражания)

### SRP — один класс = одна причина меняться
- `SchemaService` (правила DSL/версий) ≠ `PgStoreRepository` (SQL) ≠ `SchemaEndpoints` (HTTP).
- `MigrationRunner` (порядок/журнал) ≠ `SqlSplitter` (лексика SQL).

### OCP — расширение без правки существующего кода
- Операции данных: класс `IDataOperation` + 1 строка `AddKeyedSingleton`
  (`src/Dp.Platform.Data/Program.cs`). Эндпоинты не меняются.
- Канонические типы: словарь `FieldKindMapper` — тип = пара строк.
- Матрица совместимости: словарь `CompatibilityRules.Breaking` — правило = строка.
- Операторы Query DSL: ветка switch в `SqlFilterTranslator`.
- Новая интеграция: новый `IAdapterProfile` + запись в compose, хост не меняется.

### LSP — реализации взаимозаменяемы
- `IEventPublisher`: RabbitMQ-реализация заменяется on-prem/дугой без изменения
  потребителей; `IBlobStore`, `IStoreSchemaProvider` — то же.

### ISP — узкие интерфейсы
- `IDataOperation.ExecuteAsync(ctx)`; `IOutboxProcessor.ProcessPendingAsync`;
  `IAdapterProfile` из 3 членов.

### DIP — зависимость от абстракций
- Сервисы зависят от `IStoreSchemaProvider`, `IEventPublisher`, `IBlobStore`,
  `IUpstreamState` (не от HTTP/RabbitMQ/файловой системы).

## DRY — единственная точка знания

- Ошибки: код + HTTP-статус только в `DpCodes`/`ResultHttp`; в сервисах
  нельзя возвращать «сырые» 400/500 — только `Error(...).ToProblem()`.
- Сборка сервиса: `AddDpService`/`UseDpService` (Problem Details, /health).
- Типы полей: `FieldKindMapper` (DSL ↔ PG), `Values` (JSON ↔ .NET ↔ NpgsqlDbType).
- SQL-параметры: только через `ParameterSink` (защита от инъекций + нумерация).
- Строки подключения/URL: `appsettings.json` + переменные окружения, в коде нет адресов.
- Dockerfile: один универсальный с `ARG PROJECT_PATH`.

## KISS

- Никаких MediatR/AutoMapper/Dapper — минимализм: minimal API, Npgsql, RabbitMQ.Client.
- Кеш — `ConcurrentDictionary` + TTL, не кеш-фреймворк.
- CDC — транзакционный outbox, не pgoutput-декодер (решение зафиксировано,
  усложнение — в Этапе B, когда потребуется).
- Прокси шлюза — прямой pass-through, не YARP.

## Практики

1. Идентификаторы, попадающие в SQL, всегда проходят валидацию
   (`StoreName`/`FieldName`); значения — только параметры (`@pN`).
2. I/O асинхронно до конца (async/await, CancellationToken по цепочке).
3. Логи — через `ILogger<T>`, без `Console.WriteLine` (кроме мигратора).
4. Иммутабельность: records для контрактов (`required` + `init`).
5. Именование: русские комментарии описывают «почему», английские идентификаторы — «что».
6. Перед коммитом: `dotnet build` без ошибок, `scripts/smoke-test.sh` — зелёный.
