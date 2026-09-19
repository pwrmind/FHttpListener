// ─────────────────────────────────────────────────────────────────────
// ADR × VERTICAL SLICING — F# STARTER (single file, one NuGet)
// ─────────────────────────────────────────────────────────────────────
//
// Идея. Весь конвейер ADR выражается одним type alias:
//
//     Handler = Request -> Context -> Async<Result<Response, DomainError>>
//
// Middleware — декоратор: Handler -> Handler.
// Срез — данные: record Slice { Name; Routes: Map<string, Handler> }.
// Kernel — match по route, без рефлексии, без атрибутов.
//
// ── ЗАВИСИМОСТИ ───────────────────────────────────────────────────
//   dotnet add package Microsoft.Data.Sqlite
//   Всё остальное — BCL. System.Text.Json встроен.
//
// ── ИНВАРИАНТЫ ────────────────────────────────────────────────────
//   Domain    : Request -> Context -> Async<Result<Outcome, DomainError>>
//               только логика + SQL, не знает про HTML/JSON.
//               ОДИН срез = ОДИН запрос (или два, но не N+1).
//               Каждый запрос — параметризован, поля перечислены явно.
//   Responder : Result<Outcome, DomainError> -> Request -> Response
//               только форматирование, не знает про БД.
//   action    : Domain ∘ Responder → Handler.
//   Middleware: Handler -> Handler (оборачивает, не фильтрует).
//   Context   : единственный DI. Никаких глобалов в domain.
//
// ── КАК ДОБАВИТЬ ФИЧУ ─────────────────────────────────────────────
//   1. Скопируй блок `items` (Domain + View + Slice).
//   2. Поменяй SQL/логику и имя в `Name = "items"`.
//   3. Добавь срез в список `slices` внизу.
//
// ── ЗАПУСК ────────────────────────────────────────────────────────
//   dotnet run                     → сервер на :8080
//   RUN_CI=1 dotnet run            → тесты
//   dotnet run -- --run-ci         → тесты (альтернатива)
// ─────────────────────────────────────────────────────────────────────

open System
open System.Globalization
open System.IO
open System.Net
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite

// ═════════════════════════════════════════════════════════════════════
// 1. ДАННЫЕ — records без поведения
// ═════════════════════════════════════════════════════════════════════

type Request = {
    Method:  string
    Route:   string
    Params:  Map<string, string>
    Body:    Map<string, string>
    Headers: Map<string, string>
}

type Response = {
    Status:  int
    Headers: Map<string, string>
    Body:    string
}

type DomainError =
    | Unauthorized
    | Forbidden
    | NotFound
    | Validation  of string
    | Csrf
    | MethodNotAllowed
    | Server      of string

type AuthState = {
    UserId:    string option
    CsrfToken: string
}

// Доменный тип — конкретен для среза, но объявлен здесь как данные.
type Item = { Id: string; Title: string; CreatedAt: DateTime }

// Context — единственная точка DI. Db — открытое соединение,
// живёт всё время работы приложения (для SQLite это правильно:
// файл-хэндл один, SQLite сам сериализует записи).
type Context = {
    Db:     SqliteConnection
    Config: Map<string, string>
    Auth:   AuthState
    Log:    string -> unit
}

// ═════════════════════════════════════════════════════════════════════
// 2. ОШИБКИ → HTTP (exhaustive match — компилятор напомнит про ветку)
// ═════════════════════════════════════════════════════════════════════

let httpStatus = function
    | Unauthorized     -> 401
    | Forbidden        -> 403
    | NotFound         -> 404
    | Validation _     -> 422
    | Csrf             -> 403
    | MethodNotAllowed -> 405
    | Server _         -> 500

let errorMessage = function
    | Unauthorized     -> "Требуется вход."
    | Forbidden        -> "Доступ запрещён."
    | NotFound         -> "Не найдено."
    | Validation msg   -> msg
    | Csrf             -> "Неверный CSRF-токен."
    | MethodNotAllowed -> "Метод не разрешён."
    | Server msg       -> msg

// ═════════════════════════════════════════════════════════════════════
// 3. ЯДРО ADR — 4 типа + 1 функция
// ═════════════════════════════════════════════════════════════════════

type Outcome =
    | Show     of obj
    | Redirect of string

type Handler    = Request -> Context -> Async<Result<Response, DomainError>>
type Domain     = Request -> Context -> Async<Result<Outcome, DomainError>>
type Responder  = Result<Outcome, DomainError> -> Request -> Response
type Middleware = Handler -> Handler

let action (domain: Domain) (responder: Responder) : Handler =
    fun req ctx -> async {
        let! result = domain req ctx
        return Ok (responder result req)
    }

// CE result { } для Async<Result<_,_>> — для случаев с цепочками.
type AsyncResultBuilder() =
    member _.Bind(m: Async<Result<'a,'e>>, f: 'a -> Async<Result<'b,'e>>) =
        async {
            let! r = m
            match r with
            | Ok a    -> return! f a
            | Error e -> return Error e
        }
    member _.Return(x: 'a) = async { return Ok x }
    member _.ReturnFrom(m: Async<Result<'a,'e>>) = m

let result = AsyncResultBuilder()

// ═════════════════════════════════════════════════════════════════════
// 4. DB — тонкая обёртка над Microsoft.Data.Sqlite
// ═════════════════════════════════════════════════════════════════════

module Db =
    /// Открыть соединение и применить миграции.
    let openAndMigrate (path: string) : SqliteConnection =
        let conn = new SqliteConnection($"Data Source={path}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS items (
                id         TEXT PRIMARY KEY,
                title      TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_items_created_at
                ON items(created_at DESC);
        """
        cmd.ExecuteNonQuery() |> ignore
        conn

    /// In-memory соединение с применёнными миграциями — для тестов.
    let inMemory () : SqliteConnection =
        let conn = new SqliteConnection("Data Source=:memory:")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            CREATE TABLE items (
                id         TEXT PRIMARY KEY,
                title      TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX idx_items_created_at ON items(created_at DESC);
        """
        cmd.ExecuteNonQuery() |> ignore
        conn

// ═════════════════════════════════════════════════════════════════════
// 5. VIEWS — обычные функции, никаких .phtml
// ═════════════════════════════════════════════════════════════════════

module Views =
    let private esc (s: string) = WebUtility.HtmlEncode s

    let layout (title: string) (content: string) =
        $"""<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8"><title>{esc title}</title>
<style>
    body {{ font:16px/1.5 system-ui,sans-serif; max-width:640px; margin:40px auto; padding:0 20px; }}
    nav  {{ margin-bottom:24px; padding-bottom:12px; border-bottom:1px solid #eee; }}
    nav a {{ margin-right:12px; color:#06c; text-decoration:none; }}
    .item {{ border:1px solid #eee; border-radius:6px; padding:10px 14px; margin:6px 0;
             display:flex; justify-content:space-between; align-items:center; }}
    .item form {{ display:inline; }}
    .error {{ background:#fee; color:#900; padding:12px; border-radius:6px; margin:12px 0; }}
    input[type=text] {{ padding:8px 10px; font-size:16px; border:1px solid #ccc; border-radius:4px; }}
    button {{ padding:8px 14px; border:1px solid #ccc; border-radius:4px; background:#f6f6f6; cursor:pointer; }}
</style>
</head>
<body>
<nav>
    <strong>Starter</strong>
    <a href="?action=items">HTML</a>
    <a href="?action=items&amp;format=json">JSON</a>
</nav>
<main>{content}</main>
</body>
</html>"""

    let error (title: string) (message: string) =
        $"<h1>{esc title}</h1><p>{esc message}</p><p><a href=\"?action=items\">← назад</a></p>"

    type ItemsView = { Items: Item list }

    let items (data: obj) : string =
        let v = data :?> ItemsView
        let rows =
            if List.isEmpty v.Items then "<p style=\"color:#888\">Пока ничего нет.</p>"
            else
                v.Items
                |> List.map (fun i ->
                    $"""<div class="item"><span>{esc i.Title}</span>
<form method="post" action="?action=items">
<input type="hidden" name="csrf_token" value="dev-token">
<input type="hidden" name="op" value="delete">
<input type="hidden" name="id" value="{esc i.Id}">
<button>×</button>
</form></div>""")
                |> String.concat ""
        $"""<h1>Items</h1>
<form method="post" action="?action=items" style="margin-bottom:20px">
<input type="hidden" name="csrf_token" value="dev-token">
<input type="hidden" name="op" value="create">
<input type="text" name="title" required minlength="2" placeholder="Название" autofocus>
<button>Добавить</button>
</form>
{rows}"""

// ═════════════════════════════════════════════════════════════════════
// 6. RESPONSES + RESPONDERS
// ═════════════════════════════════════════════════════════════════════

let htmlHeaders = Map [ "Content-Type", "text/html; charset=utf-8" ]
let jsonHeaders = Map [ "Content-Type", "application/json; charset=utf-8" ]

let htmlBody (status: int) (body: string) : Response =
    { Status = status; Headers = htmlHeaders; Body = body }

let jsonBody (status: int) (body: string) : Response =
    { Status = status; Headers = jsonHeaders; Body = body }

let private jsonOptions =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let private serialize (value: obj) : string =
    JsonSerializer.Serialize(value, value.GetType(), jsonOptions)

let htmlResponder (view: obj -> string) (title: string) : Responder =
    fun result _req ->
        match result with
        | Ok (Show data) ->
            htmlBody 200 (Views.layout title (view data))
        | Ok (Redirect url) ->
            { Status = 302; Headers = Map [ "Location", url ]; Body = "" }
        | Error e ->
            let s = httpStatus e
            htmlBody s (Views.layout (string s) (Views.error (string s) (errorMessage e)))

let jsonResponder : Responder =
    fun result _req ->
        match result with
        | Ok (Show data) ->
            jsonBody 200 (serialize data)
        | Ok (Redirect url) ->
            { Status = 302; Headers = Map [ "Location", url ]; Body = "" }
        | Error e ->
            let msg  = JsonSerializer.Serialize(errorMessage e)
            let body = "{\"error\":" + msg + "}"
            jsonBody (httpStatus e) body

let negotiatingResponder (html: Responder) (json: Responder) : Responder =
    fun result req ->
        if req.Params.TryFind "format" = Some "json" then json result req
        else html result req

// ═════════════════════════════════════════════════════════════════════
// 7. MIDDLEWARE — Handler -> Handler
// ═════════════════════════════════════════════════════════════════════

let withCsrf : Middleware =
    fun next req ctx -> async {
        if req.Method <> "POST" then return! next req ctx
        else
            let token =
                req.Body.TryFind "csrf_token"
                |> Option.orElseWith (fun () -> req.Headers.TryFind "x-csrf-token")
            match token with
            | Some t when t = ctx.Auth.CsrfToken -> return! next req ctx
            | _ -> return Error Csrf
    }

let withAuth : Middleware =
    fun next req ctx -> async {
        match ctx.Auth.UserId with
        | Some _ -> return! next req ctx
        | None   -> return Error Unauthorized
    }

// ═════════════════════════════════════════════════════════════════════
// 8. FEATURE — единственный эталонный срез (SQL-паттерн)
// ═════════════════════════════════════════════════════════════════════

let itemsList : Domain =
    fun _req ctx ->
        async {
            // Один запрос. Явные поля. Сортировка в БД, не в памяти.
            use cmd = ctx.Db.CreateCommand()
            cmd.CommandText <-
                "SELECT id, title, created_at FROM items ORDER BY created_at DESC"
            use reader = cmd.ExecuteReader()
            let items =
                [ while reader.Read() do
                    yield {
                        Id        = reader.GetString 0
                        Title     = reader.GetString 1
                        CreatedAt = DateTime.Parse(
                                        reader.GetString 2,
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.RoundtripKind)
                    } ]
            return Ok (Show (box { Views.ItemsView.Items = items }))
        }

let itemsCreate : Domain =
    fun req ctx ->
        async {
            let title = (req.Body.TryFind "title" |> Option.defaultValue "").Trim()
            if title.Length < 2 then
                return Error (Validation "Название минимум 2 символа.")
            else
                let id = "i_" + Guid.NewGuid().ToString("N").Substring(0, 8)
                // Параметризованный INSERT. Никакой конкатенации строк.
                use cmd = ctx.Db.CreateCommand()
                cmd.CommandText <-
                    "INSERT INTO items (id, title, created_at) VALUES (@id, @title, @now)"
                cmd.Parameters.AddWithValue("@id",    id) |> ignore
                cmd.Parameters.AddWithValue("@title", title) |> ignore
                cmd.Parameters.AddWithValue("@now",   DateTime.UtcNow.ToString("o")) |> ignore
                cmd.ExecuteNonQuery() |> ignore
                ctx.Log $"created item {id}"
                return Ok (Redirect "?action=items")
        }

let itemsDelete : Domain =
    fun req ctx ->
        async {
            match req.Body.TryFind "id" with
            | None | Some "" ->
                return Error (Validation "Не указан id.")
            | Some id ->
                use cmd = ctx.Db.CreateCommand()
                cmd.CommandText <- "DELETE FROM items WHERE id = @id"
                cmd.Parameters.AddWithValue("@id", id) |> ignore
                cmd.ExecuteNonQuery() |> ignore
                ctx.Log $"deleted item {id}"
                return Ok (Redirect "?action=items")
        }

type Slice = { Name: string; Routes: Map<string, Handler> }

let routeKey (r: Request) : string =
    if r.Method = "POST" then
        "POST:" + (r.Body.TryFind "op" |> Option.defaultValue "")
    else r.Method

let itemsSlice : Slice =
    let responder = negotiatingResponder (htmlResponder Views.items "Items") jsonResponder
    { Name = "items"
      Routes = Map [
          "GET",         action itemsList   responder
          "POST:create", action itemsCreate responder |> withCsrf
          "POST:delete", action itemsDelete responder |> withCsrf
      ] }

// ═════════════════════════════════════════════════════════════════════
// 9. KERNEL — 15 строк, без рефлексии
// ═════════════════════════════════════════════════════════════════════

let renderError (wantsJson: bool) (e: DomainError) : Response =
    if wantsJson then
        let msg = JsonSerializer.Serialize(errorMessage e)
        jsonBody (httpStatus e) ("{\"error\":" + msg + "}")
    else
        htmlBody (httpStatus e) (Views.layout (string (httpStatus e))
                                       (Views.error (string (httpStatus e)) (errorMessage e)))

let dispatch (slices: Slice list) (req: Request) (ctx: Context) : Async<Response> =
    async {
        let wantsJson = req.Params.TryFind "format" = Some "json"
        try
            ctx.Log $"→ {req.Method} {req.Route}"
            let! result =
                match slices |> List.tryFind (fun s -> s.Name = req.Route) with
                | None -> async { return Error NotFound }
                | Some slice ->
                    match slice.Routes.TryFind (routeKey req) with
                    | None         -> async { return Error MethodNotAllowed }
                    | Some handler -> handler req ctx
            match result with
            | Ok resp ->
                ctx.Log $"← {resp.Status}"
                return resp
            | Error e ->
                ctx.Log $"← ERR {e}"
                return renderError wantsJson e
        with ex ->
            ctx.Log $"!! {ex.Message}"
            return renderError wantsJson (Server ex.Message)
    }

// ═════════════════════════════════════════════════════════════════════
// 10. HTTP BRIDGE — HttpListener ⇄ Request/Response
// ═════════════════════════════════════════════════════════════════════

let parseForm (s: string) : Map<string, string> =
    if String.IsNullOrEmpty s then Map.empty
    else
        s.Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun kv ->
            match kv.Split('=', 2) with
            | [| k; v |] -> Some (WebUtility.UrlDecode k, WebUtility.UrlDecode v)
            | [| k |]    -> Some (WebUtility.UrlDecode k, "")
            | _          -> None)
        |> Map.ofArray

let requestOf (ctx: HttpListenerContext) : Async<Request> =
    async {
        let q = ctx.Request.Url.Query
        let queryParams = if q.StartsWith "?" then parseForm (q.Substring 1) else Map.empty
        let! body =
            if ctx.Request.HasEntityBody then
                async {
                    use r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)
                    let! s = r.ReadToEndAsync() |> Async.AwaitTask
                    return parseForm s
                }
            else async { return Map.empty }
        return {
            Method  = ctx.Request.HttpMethod
            Route   = queryParams.TryFind "action" |> Option.defaultValue "items"
            Params  = queryParams
            Body    = body
            Headers = Map [ "x-csrf-token",
                            ctx.Request.Headers.["X-Csrf-Token"]
                            |> Option.ofObj |> Option.defaultValue "" ]
        }
    }

let authOf (req: Request) : AuthState =
    { UserId    = req.Headers.TryFind "x-user-id" |> Option.filter (fun s -> s <> "")
      CsrfToken = "dev-token" }

let emit (resp: Response) (ctx: HttpListenerContext) : Async<unit> =
    async {
        ctx.Response.StatusCode <- resp.Status
        for KeyValue (k, v) in resp.Headers do
            ctx.Response.Headers.[k] <- v
        let bytes = Encoding.UTF8.GetBytes resp.Body
        ctx.Response.ContentLength64 <- int64 bytes.Length
        do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
        do! ctx.Response.OutputStream.FlushAsync() |> Async.AwaitTask
        ctx.Response.OutputStream.Close()
    }

// ═════════════════════════════════════════════════════════════════════
// 11. ХРАНИЛИЩЕ + ЛОГГЕР (MailboxProcessor)
// ═════════════════════════════════════════════════════════════════════

type Logger() =
    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
            let! msg = inbox.Receive()
            printfn "[%s] %s" (DateTime.Now.ToString "HH:mm:ss") msg
            return! loop ()
        }
        loop ())
    member _.Log(msg) = agent.Post msg

let globalLogger = Logger()

// Одно соединение на приложение. SQLite сам сериализует записи.
// Для Postgres здесь был бы пул — Context хранил бы DataSource.
let globalDb = Db.openAndMigrate "app.db"

let contextOf (req: Request) : Context =
    { Db     = globalDb
      Config = Map.empty
      Auth   = authOf req
      Log    = globalLogger.Log }

// ═════════════════════════════════════════════════════════════════════
// 12. ТЕСТЫ — свежее in-memory соединение на каждый тест
// ═════════════════════════════════════════════════════════════════════

let private fail msg = raise (Exception msg)

let freshContext () : Context =
    { Db     = Db.inMemory ()
      Config = Map.empty
      Auth   = { UserId = Some "u1"; CsrfToken = "test-token" }
      Log    = ignore }

let testReq method' route body : Request =
    { Method = method'; Route = route; Params = Map.empty
      Body = body; Headers = Map.empty }

let tests : (string * (unit -> Async<unit>)) list = [

    "items/empty_list", fun () -> async {
        let ctx = freshContext ()
        let! r = dispatch [itemsSlice] (testReq "GET" "items" Map.empty) ctx
        if r.Status <> 200 then fail $"expected 200, got {r.Status}"
        if not (r.Body.Contains "Пока ничего нет") then fail "empty state not shown"
    }

    "items/create_validation", fun () -> async {
        let ctx = freshContext ()
        let! r = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "x"; "csrf_token", "test-token" ])) ctx
        if r.Status <> 422 then fail $"expected 422, got {r.Status}"
        use cmd = ctx.Db.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM items"
        let n = Convert.ToInt32(cmd.ExecuteScalar())
        if n <> 0 then fail "should not insert on validation error"
    }

    "items/create_then_list", fun () -> async {
        let ctx = freshContext ()
        let! _ = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "Hello"; "csrf_token", "test-token" ])) ctx
        let! r = dispatch [itemsSlice] (testReq "GET" "items" Map.empty) ctx
        if not (r.Body.Contains "Hello") then fail "created item not visible"
    }

    "items/redirect_after_create", fun () -> async {
        let ctx = freshContext ()
        let! r = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "A"; "csrf_token", "test-token" ])) ctx
        if r.Status <> 302 then fail $"expected 302, got {r.Status}"
        if r.Headers.TryFind "Location" <> Some "?action=items" then fail "wrong Location"
    }

    "items/csrf_rejects_bad_token", fun () -> async {
        let ctx = freshContext ()
        let! r = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "X"; "csrf_token", "WRONG" ])) ctx
        if r.Status <> 403 then fail $"expected 403, got {r.Status}"
        use cmd = ctx.Db.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM items"
        let n = Convert.ToInt32(cmd.ExecuteScalar())
        if n <> 0 then fail "should not insert on CSRF failure"
    }

    "items/json_negotiation", fun () -> async {
        let ctx = freshContext ()
        let! _ = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "Json"; "csrf_token", "test-token" ])) ctx
        let req = { testReq "GET" "items" Map.empty with
                        Params = Map [ "format", "json" ] }
        let! r = dispatch [itemsSlice] req ctx
        if not (r.Headers.["Content-Type"].Contains "json") then fail "expected JSON"
        if r.Body.Contains "null" then fail $"JSON has nulls: {r.Body}"
        if not (r.Body.Contains "\"title\":\"Json\"") then fail $"title missing: {r.Body}"
    }

    "items/delete", fun () -> async {
        let ctx = freshContext ()
        let! _ = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "Del"; "csrf_token", "test-token" ])) ctx
        use get = ctx.Db.CreateCommand()
        get.CommandText <- "SELECT id FROM items LIMIT 1"
        let id = get.ExecuteScalar() :?> string
        let! r = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "delete"; "id", id; "csrf_token", "test-token" ])) ctx
        if r.Status <> 302 then fail $"expected 302, got {r.Status}"
        use cnt = ctx.Db.CreateCommand()
        cnt.CommandText <- "SELECT COUNT(*) FROM items"
        if Convert.ToInt32(cnt.ExecuteScalar()) <> 0 then fail "row not deleted"
    }

    "kernel/not_found", fun () -> async {
        let ctx = freshContext ()
        let! r = dispatch [itemsSlice] (testReq "GET" "unknown" Map.empty) ctx
        if r.Status <> 404 then fail $"expected 404, got {r.Status}"
    }

    "kernel/method_not_allowed", fun () -> async {
        let ctx = freshContext ()
        let! r = dispatch [itemsSlice] (testReq "DELETE" "items" Map.empty) ctx
        if r.Status <> 405 then fail $"expected 405, got {r.Status}"
    }
]

let runTests () : Async<int> =
    async {
        printfn "=== CI ==="
        let mutable failed = 0
        for (name, test) in tests do
            try
                do! test ()
                printfn "[PASS] %s" name
            with ex ->
                failed <- failed + 1
                printfn "[FAIL] %s: %s" name ex.Message
        if failed = 0 then printfn "=== ВСЕ ТЕСТЫ ПРОЙДЕНЫ (%d) ===" tests.Length
        else printfn "=== ПРОВАЛЕНО: %d из %d ===" failed tests.Length
        return failed
    }

// ═════════════════════════════════════════════════════════════════════
// 13. RUNTIME
// ═════════════════════════════════════════════════════════════════════

let argv  = Environment.GetCommandLineArgs() |> Array.skip 1
let runCi = argv |> Array.contains "--run-ci"
            || Environment.GetEnvironmentVariable "RUN_CI" = "1"

if runCi then
    let failed = runTests () |> Async.RunSynchronously
    exit failed

let slices : Slice list = [ itemsSlice ]

let handleRequest (listenerCtx: HttpListenerContext) : Async<unit> =
    async {
        let! req = requestOf listenerCtx
        let ctx  = contextOf req
        let! resp = dispatch slices req ctx
        do! emit resp listenerCtx
    }

let listener = new HttpListener()
listener.Prefixes.Add "http://localhost:8080/"
listener.Start ()
printfn "Listening on http://localhost:8080/"
printfn "  HTML: http://localhost:8080/?action=items"
printfn "  JSON: http://localhost:8080/?action=items&format=json"

let rec loop () = async {
    let! ctx = listener.GetContextAsync() |> Async.AwaitTask
    do! handleRequest ctx
    return! loop ()
}

loop () |> Async.RunSynchronously