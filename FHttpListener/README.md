# F# Starter: ADR × Vertical Slicing

Однофайловый каркас веб-приложения на F#. Две NuGet-зависимости. Всё остальное — BCL.

**Идея.** Весь конвейер ADR выражается одним type alias:

```fsharp
Handler = Request -> Context -> Async<Result<Response, DomainError>>
```

Middleware — декоратор `Handler -> Handler`. Срез — данные `Map<string, Handler>`. Kernel — `match` по route. Никакой рефлексии, никаких атрибутов, никаких DI-контейнеров.

---

## Что это и зачем

Это **эталон формы**, а не production-фреймворк. Он решает одну задачу: показать, как выглядит ADR + Vertical Slicing, если писать на современном F#.

Две роли одновременно:

1. **Работающий сервер.** `dotnet run` — и у тебя HTTP на :8080 с SQLite и шаблонами.
2. **Few-shot для LLM.** Один файл + шаблоны. Модель видит весь паттерн целиком и воспроизводит его для новых фич.

Не разбивай на файлы. Не вводи новых абстракций. Не плоди слоёв. Если нужна новая фича — скопируй `itemsSlice` и замени SQL.

---

## Структура

```
FHttpListener/
├── FHttpListener.fsproj
├── Program.fs              ← весь F# код
└── views/
    ├── _layout.liquid      ← базовый layout
    ├── items.liquid        ← шаблон списка
    ├── item_detail.liquid  ← шаблон детали
    └── error.liquid        ← шаблон ошибки
```

`Program.fs` — ~700 строк, из них:
- ~150 строк — инфраструктура (Kernel, HTTP bridge, Db, Templates)
- ~250 строк — ядро ADR (типы, `action`, middleware, responders)
- ~150 строк — единственный эталонный срез (`items`)
- ~150 строк — тесты

---

## Установка и запуск

```bash
dotnet add package Microsoft.Data.Sqlite
dotnet add package Fluid.Core
dotnet run
```

Открыть:
- `http://localhost:8080/?action=items` — HTML
- `http://localhost:8080/?action=items&format=json` — JSON
- `http://localhost:8080/?action=items&op=detail&id=i_xxxxxxxx` — деталь

Тесты:

```bash
RUN_CI=1 dotnet run
# или
dotnet run -- --run-ci
```

---

## Как добавить фичу

**Пять шагов. Больше ничего.**

### 1. Скопируй срез

В `Program.fs` есть `itemsSlice` — эталон. Скопируй его целиком: Domain-функции, responder, Slice.

### 2. Замени предметную логику

```fsharp
// Было:
let itemsList : Domain = fun _req ctx ->
    async {
        use cmd = ctx.Db.CreateCommand()
        cmd.CommandText <- "SELECT id, title, created_at FROM items ORDER BY created_at DESC"
        // ...
    }

// Стало (для orders):
let ordersList : Domain = fun _req ctx ->
    async {
        use cmd = ctx.Db.CreateCommand()
        cmd.CommandText <- "SELECT id, amount, created_at FROM orders ORDER BY created_at DESC"
        // ...
    }
```

### 3. Добавь шаблон

`views/orders.liquid` — обычный Liquid, доступ к полям через `{{ order.amount }}`.

### 4. Зарегистрируй responder

```fsharp
let htmlOrders = htmlResponder "orders" "Orders"
let responderOrders = negotiatingResponder htmlOrders jsonResponder
```

### 5. Добавь срез в список

```fsharp
let slices : Slice list = [ itemsSlice; ordersSlice ]
```

Всё. Новая фича работает.

---

## Инварианты

Это не рекомендации. Нарушение = баг в архитектуре.

### Domain

- **Только логика + SQL.** Не знает про HTML, JSON, шаблоны.
- **Возвращает `Async<Result<Outcome, DomainError>>`.** Никогда не бросает исключений для бизнес-ошибок.
- **Один срез = один запрос.** Или два, если нужно. Но не N+1.
- **Каждый запрос параметризован.** `@id`, `@title`, никогда — конкатенация строк.
- **Поля перечислены явно.** `SELECT id, title` — не `SELECT *`.
- **Сортировка и фильтрация — в SQL.** Не в F#.

### Responder

- **Только форматирование.** Не знает про БД.
- **HTML vs JSON — через `negotiatingResponder`.** Domain один, формат выбирается по `?format=json`.

### Middleware

- **`Handler -> Handler`.** Оборачивает, не фильтрует.
- **Порядок важен.** `action ... |> withCsrf` — CSRF сработает до Domain.
- **Может прервать цепочку** — вернуть `Error`, не вызывая `next`.

### Slice

- **Это данные, не поведение.** `record Slice { Name; Routes: Map<string, Handler> }`.
- **Один срез = одна сущность.** Items со всеми операциями — один срез. Items и Orders — два среза.

### Context

- **Единственная точка DI.** Всё, что нужно Domain, приходит через Context.
- **Никаких глобалов в Domain.** Даже логгер — через Context.

---

## Как это устроено

### ADR в одной функции

```fsharp
let action (domain: Domain) (responder: Responder) : Handler =
    fun req ctx -> async {
        let! result = domain req ctx
        return Ok (responder result req)
    }
```

`action` склеивает Domain и Responder. Middleware оборачивает результат:

```fsharp
action itemsCreate responder |> withCsrf
```

### Kernel — 15 строк

```fsharp
let dispatch slices req ctx = async {
    let! result =
        match slices |> List.tryFind (fun s -> s.Name = req.Route) with
        | None -> async { return Error NotFound }
        | Some slice ->
            match slice.Routes.TryFind (routeKey req) with
            | None         -> async { return Error MethodNotAllowed }
            | Some handler -> handler req ctx
    // ... renderError или resp
}
```

Никакой рефлексии. Route key — строка: `"GET"`, `"POST:create"`, `"GET:detail"`. Добавить новый роут — добавить строку в `Map`.

### Ответ — value object

```fsharp
type Response = { Status: int; Headers: Map<string, string>; Body: string }
```

Никаких `header()` внутри Domain. Никаких `HttpResponseMessage`. Чистые данные, которые потом превращаются в HTTP в `emit`.

### Redirect — часть контракта

```fsharp
type Outcome =
    | Show     of obj
    | Redirect of string
```

Domain говорит «редирект на URL», Responder решает, как это сделать. В HTML — 302 с `Location`. В JSON — тоже 302. В тестах — легко проверить.

### Шаблоны — Fluid.Core

Весь HTML в `.liquid`-файлах. F#-код не содержит HTML.

```liquid
{% for item in items %}
    <div class="item">
        <a href="?action=items&op=detail&id={{ item.id }}">{{ item.title }}</a>
    </div>
{% endfor %}
```

`MemberAccessStrategy.Register<Item>()` — белый список полей. Только зарегистрированные поля доступны в шаблоне. Это защита от XSS и от случайного доступа к внутренним полям.

---

## Тесты

Тесты — функции, не методы. Свежее in-memory соединение на каждый тест. Никаких моков — просто другой Context.

```fsharp
"items/detail_shows_item", fun () -> async {
    let ctx = freshContext ()
    let! _ = dispatch [itemsSlice] (testReq "POST" "items"
                (Map [ "op", "create"; "title", "Detail"; "csrf_token", "test-token" ])) ctx
    use get = ctx.Db.CreateCommand()
    get.CommandText <- "SELECT id FROM items LIMIT 1"
    let id = get.ExecuteScalar() :?> string
    let req = { testReq "GET" "items" Map.empty with
                    Params = Map [ "op", "detail"; "id", id ] }
    let! r = dispatch [itemsSlice] req ctx
    if r.Status <> 200 then fail $"expected 200, got {r.Status}"
    if not (r.Body.Contains "Detail") then fail "detail not shown"
}
```

Что покрыто в стартере:

- `items/empty_list` — пустое состояние
- `items/create_validation` — валидация длины
- `items/create_then_list` — создание + видимость
- `items/redirect_after_create` — 302 после POST
- `items/csrf_rejects_bad_token` — middleware блокирует
- `items/json_negotiation` — ?format=json работает
- `items/delete` — удаление
- `items/detail_shows_item` — деталь рендерится
- `items/detail_not_found` — 404
- `kernel/not_found` — неизвестный route
- `kernel/method_not_allowed` — неверный метод

---

## Что внутри

| Слой | Что | Почему |
|---|---|---|
| **HTTP** | `HttpListener` | Из BCL. Не требует Kestrel. |
| **БД** | `Microsoft.Data.Sqlite` | Одна NuGet. Показывает правильный SQL-паттерн. |
| **Шаблоны** | `Fluid.Core` | Liquid-синтаксис, XSS-защита, white-list полей. |
| **JSON** | `System.Text.Json` | Встроен. Сериализует F# records и lists нативно. |
| **Async** | `Async` | Идиоматичнее `Task` для F#. |
| **Логгер** | `MailboxProcessor` | Актор без блокировок. |

---

## Известные ограничения

Это эталон, не продакшн. Что сознательно упрощено:

- **`HttpListener`** — не самый быстрый транспорт. Kestrel в разы быстрее. Меняется одной строкой в `handleRequest`.
- **SQLite без пула соединений.** Одно соединение на весь процесс. Для SQLite — правильно. Для Postgres — нужен `NpgsqlDataSource`.
- **Сессий нет.** `CsrfToken = "dev-token"`. В продакшне — подписанные cookie или серверные сессии.
- **Нет ротации логов.** MailboxProcessor пишет в stdout.
- **CSRF-токен зашит в шаблон.** В продакшне — через Context.
- **Нет graceful shutdown.** `Async.RunSynchronously` в цикле.

Всё это — точки расширения. Каркас специально оставляет их за бортом, чтобы каждая фича была видна целиком.

---

## Философия

**Один файл.** Не потому что экономия места. Потому что весь паттерн должен быть виден сразу — и человеку, и LLM.

**Один эталонный срез.** Не матрица примеров. Одна форма, повтори её. `items` достаточно для CRUD: list, detail, create, delete. Edit добавится тем же способом.

**ФП вместо ООП.** Не потому что модно. Потому что ADR — это буквально композиция функций. Middleware — декоратор. Slice — данные. Kernel — `match`. ООП здесь только добавляет классы-обёртки.

**Ноль магии.** Никаких атрибутов, рефлексии, DI-контейнеров. Каждый route — строка в `Map`. Каждый handler — функция. Что видишь — то и работает.

**Один срез = один запрос.** Узкое место — БД, не приложение. Приложение должно не мешать: минимум round-trip'ов, явные поля, параметризация, индексы под запросы.

---

## Что дальше

- **Добавить `edit`** — копия `detail` + `POST:edit` с UPDATE.
- **Добавить вторую сущность** — копия всего блока `items`.
- **Показать связь между сущностями** — `JOIN` в одном запросе, не N+1.
- **Добавить auth** — middleware `withAuth` уже есть, нужно только `AuthState` наполнять из cookie.
- **Заменить `HttpListener` на Kestrel** — если понадобится throughput.

Каркас трогать не нужно. Только срезы.