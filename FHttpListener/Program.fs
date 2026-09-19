// ─────────────────────────────────────────────────────────────────────
// ADR × VERTICAL SLICING — F# STARTER (single file, two NuGet)
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
//   Microsoft.Data.Sqlite — БД.
//   Fluid.Core            — Liquid-шаблоны в views/*.liquid.
//   Всё остальное — BCL. System.Text.Json встроен.
//
// ── ИНВАРИАНТЫ ────────────────────────────────────────────────────
//   Domain    : Request -> Context -> Async<Result<Outcome, DomainError>>
//               только логика + SQL. Не знает про HTML/JSON.
//               ОДИН срез = ОДИН запрос (или два, но не N+1).
//               Каждый запрос — параметризован, поля перечислены явно.
//   Responder : Result<Outcome, DomainError> -> Request -> Response
//               только форматирование. Не знает про БД.
//   action    : Domain ∘ Responder → Handler.
//   Middleware: Handler -> Handler (оборачивает, не фильтрует).
//   Context   : единственный DI. Никаких глобалов в domain.
//   Templates : весь HTML — в views/*.liquid. F#-код HTML не содержит.
//
// ── КАК ДОБАВИТЬ ФИЧУ ─────────────────────────────────────────────
//   1. Скопируй блок `items` (Domain + Slice).
//   2. Добавь views/<name>.liquid.
//   3. Поменяй SQL и имя в `Name = "items"`.
//   4. Добавь срез в список `slices` внизу.
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
open System.Collections.Concurrent
open Microsoft.Data.Sqlite
open Fluid

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

type Item = { Id: string; Title: string; CreatedAt: DateTime }

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
// 5. TEMPLATES — Fluid.Core
// ═════════════════════════════════════════════════════════════════════

module Templates =
    /// Singleton parser. Thread-safe.
    let private parser = new FluidParser()

    /// Кэш распарсенных шаблонов. IFluidTemplate — thread-safe.
    let private cache = ConcurrentDictionary<string, IFluidTemplate>()

    /// Директория с шаблонами. Рядом с исполняемым файлом.
    let private viewsDir = Path.Combine(AppContext.BaseDirectory, "views")

    /// Общие опции: white-list полей, регистрация типа Item.
    let private makeOptions () =
        let options = TemplateOptions()
        // Белый список: только поля Item доступны в шаблоне.
        // Fluid делает lookup регистронезависимым — `item.id` найдёт `Id`.
        options.MemberAccessStrategy.Register<Item>() |> ignore
        options

    /// Загрузить и распарсить шаблон (с кэшированием).
    let private load (name: string) : IFluidTemplate =
        cache.GetOrAdd(name, fun n ->
            let path = Path.Combine(viewsDir, n + ".liquid")
            if not (File.Exists path) then
                failwith $"Template not found: {path}"
            let source = File.ReadAllText(path, Encoding.UTF8)

            // Явно указываем компилятору использовать 3-параметрную перегрузку
            let mutable template = Unchecked.defaultof<IFluidTemplate>
            let mutable error = ""
            let ok = parser.TryParse(source, &template, &error)
            if ok then template
            else failwith $"Template parse error in {n}.liquid: {error}")

    /// Отрендерить шаблон с моделью (Map<string, obj>).
    let render (name: string) (model: Map<string, obj>) : string =
        let template = load name
        let ctx = TemplateContext(makeOptions ())
        for KeyValue (k, v) in model do
            ctx.SetValue(k, v) |> ignore
        template.Render(ctx)

    /// Отрендерить layout с уже готовым контентом.
    let renderLayout (title: string) (content: string) (model: Map<string, obj>) : string =
        let full =
            model
            |> Map.add "title"   (box title)
            |> Map.add "content" (box content)
        render "_layout" full

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

/// HTML-responder: рендерит <view>.liquid и оборачивает в _layout.liquid.
let htmlResponder (viewName: string) (pageTitle: string) : Responder =
    fun result _req ->
        match result with
        | Ok (Show data) ->
            let model = data :?> Map<string, obj>
            let content = Templates.render viewName model
            htmlBody 200 (Templates.renderLayout pageTitle content model)

        | Ok (Redirect url) ->
            { Status = 302; Headers = Map [ "Location", url ]; Body = "" }

        | Error e ->
            let s = httpStatus e
            let model = Map [ "status"  , box s
                              "message" , box (errorMessage e) ]
            let content = Templates.render "error" model
            htmlBody s (Templates.renderLayout (string s) content model)

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
// 8. FEATURE — единственный эталонный срез (SQL + Fluid)
// ═════════════════════════════════════════════════════════════════════

let itemsList : Domain =
    fun _req ctx ->
        async {
            // Один запрос. Явные поля. Сортировка в БД.
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
            // Модель для Fluid: ключи становятся переменными в шаблоне.
            let model = Map [ "items", box items ]
            return Ok (Show (box model))
        }

let itemsCreate : Domain =
    fun req ctx ->
        async {
            let title = (req.Body.TryFind "title" |> Option.defaultValue "").Trim()
            if title.Length < 2 then
                return Error (Validation "Название минимум 2 символа.")
            else
                let id = "i_" + Guid.NewGuid().ToString("N").Substring(0, 8)
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

let itemsDetail : Domain =
    fun req ctx ->
        async {
            match req.Params.TryFind "id" with
            | None | Some "" ->
                return Error (Validation "Не указан id.")
            | Some id ->
                // Один запрос. Явные поля. Поиск по первичному ключу.
                use cmd = ctx.Db.CreateCommand()
                cmd.CommandText <-
                    "SELECT id, title, created_at FROM items WHERE id = @id"
                cmd.Parameters.AddWithValue("@id", id) |> ignore
                use reader = cmd.ExecuteReader()
                if reader.Read() then
                    let item = {
                        Id        = reader.GetString 0
                        Title     = reader.GetString 1
                        CreatedAt = DateTime.Parse(
                                        reader.GetString 2,
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.RoundtripKind)
                    }
                    return Ok (Show (box (Map [ "item", box item ])))
                else
                    return Error NotFound
        }

type Slice = { Name: string; Routes: Map<string, Handler> }

let routeKey (r: Request) : string =
    match r.Method, r.Params.TryFind "op" with
    | "POST", Some o -> "POST:" + o
    | "POST", None   -> "POST:"
    | "GET",  Some o -> "GET:" + o
    | "GET",  None   -> "GET"
    | m, _           -> m

let itemsSlice : Slice =
    let html = htmlResponder "items" "Items"
    let htmlDetail = htmlResponder "item_detail" "Item"    // ← новый responder
    let responder = negotiatingResponder html jsonResponder
    let responderDetail = negotiatingResponder htmlDetail jsonResponder
    { Name = "items"
      Routes = Map [
          "GET",         action itemsList   responder
          "GET:detail",  action itemsDetail responderDetail   // ← новая строка
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
        let s = httpStatus e
        let model = Map [ "status", box s; "message", box (errorMessage e) ]
        let content = Templates.render "error" model
        htmlBody s (Templates.renderLayout (string s) content model)

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
// 11. ХРАНИЛИЩЕ + ЛОГГЕР
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
        if Convert.ToInt32(cmd.ExecuteScalar()) <> 0 then
            fail "should not insert on validation error"
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
        if Convert.ToInt32(cmd.ExecuteScalar()) <> 0 then
            fail "should not insert on CSRF failure"
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

    "items/detail_not_found", fun () -> async {
        let ctx = freshContext ()
        let req = { testReq "GET" "items" Map.empty with
                        Params = Map [ "op", "detail"; "id", "nonexistent" ] }
        let! r = dispatch [itemsSlice] req ctx
        if r.Status <> 404 then fail $"expected 404, got {r.Status}"
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