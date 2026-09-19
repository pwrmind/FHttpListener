// ─────────────────────────────────────────────────────────────────────
// ADR × VERTICAL SLICING — F# STARTER (single file, zero NuGet)
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
// ── ИНВАРИАНТЫ ────────────────────────────────────────────────────
//   Domain    : Request -> Context -> Async<Result<Outcome, DomainError>>
//               только логика + БД, не знает про HTML/JSON.
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
//   dotnet fsi starter.fsx                 → сервер на :8080
//   RUN_CI=1 dotnet fsi starter.fsx        → тесты
//   dotnet fsi starter.fsx -- --run-ci     → тесты (альтернатива)
// ─────────────────────────────────────────────────────────────────────

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Collections.Concurrent

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

// Доменные данные — конкретны для фичи, но живут в общем Store.
type Item = { Id: string; Title: string; CreatedAt: DateTime }

type Store = { Items: ConcurrentDictionary<string, Item> }

type Context = {
    Store:  Store
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
    | Show     of obj          // данные для view (типизированы по конвенции)
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

// ─── Result CE для Async<Result<_,_>> — заменяет цепочки match! ───
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
// 4. VIEWS — обычные функции, никаких .phtml
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

    // Типизированная view: получает obj и приводит к конкретному record'у.
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
// 5. RESPONSES + RESPONDERS — curried, переиспользуемые
// ═════════════════════════════════════════════════════════════════════

let htmlHeaders = Map [ "Content-Type", "text/html; charset=utf-8" ]
let jsonHeaders = Map [ "Content-Type", "application/json; charset=utf-8" ]

let htmlBody (status: int) (body: string) : Response =
    { Status = status; Headers = htmlHeaders; Body = body }

let jsonBody (status: int) (body: string) : Response =
    { Status = status; Headers = jsonHeaders; Body = body }

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

let private jsonOptions =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let private serialize (value: obj) : string =
    JsonSerializer.Serialize(value, value.GetType(), jsonOptions)

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
// 6. MIDDLEWARE — Handler -> Handler
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
// 7. FEATURE — единственный эталонный срез
// ═════════════════════════════════════════════════════════════════════

// ─── Domain: использует CE result { } для цепочек ───

let itemsList : Domain =
    fun _req ctx ->
        result {
            let items =
                ctx.Store.Items.Values
                |> Seq.sortByDescending (fun i -> i.CreatedAt)
                |> Seq.toList
            return Show (box { Views.ItemsView.Items = items })
        }

let itemsCreate : Domain =
    fun req ctx ->
        result {
            let title = (req.Body.TryFind "title" |> Option.defaultValue "").Trim()
            if title.Length < 2 then
                return! async { return Error (Validation "Название минимум 2 символа.") }
            else
                let id = "i_" + Guid.NewGuid().ToString("N").Substring(0, 8)
                ctx.Store.Items.[id] <- { Id = id; Title = title; CreatedAt = DateTime.UtcNow }
                ctx.Log $"created item {id}"
                return Redirect "?action=items"
        }

let itemsDelete : Domain =
    fun req ctx ->
        result {
            match req.Body.TryFind "id" with
            | None | Some "" ->
                return! async { return Error (Validation "Не указан id.") }
            | Some id ->
                ctx.Store.Items.TryRemove id |> ignore
                ctx.Log $"deleted item {id}"
                return Redirect "?action=items"
        }

// ─── Slice: данные ───

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
// 8. KERNEL — 15 строк, без рефлексии
// ═════════════════════════════════════════════════════════════════════

let renderError (wantsJson: bool) (e: DomainError) : Response =
    if wantsJson then jsonBody (httpStatus e) ("{\"error\":\"" + errorMessage e + "\"}")
    else htmlBody (httpStatus e) (Views.layout (string (httpStatus e))
                                       (Views.error (string (httpStatus e)) (errorMessage e)))

let dispatch (slices: Slice list) (req: Request) (ctx: Context) : Async<Response> =
    async {
        let wantsJson = req.Params.TryFind "format" = Some "json"
        try
            ctx.Log $"→ {req.Method} {req.Route}"
            let! result =
                match slices |> List.tryFind (fun s -> s.Name = req.Route) with
                | None          -> async { return Error NotFound }
                | Some slice ->
                    match slice.Routes.TryFind (routeKey req) with
                    | None           -> async { return Error MethodNotAllowed }
                    | Some handler   -> handler req ctx
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
// 9. HTTP BRIDGE — HttpListener ⇄ Request/Response
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
                            ctx.Request.Headers.["X-Csrf-Token"] |> Option.ofObj |> Option.defaultValue "" ]
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
// 10. ХРАНИЛИЩЕ + ЛОГГЕР (MailboxProcessor — как в твоём коде)
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
let globalStore  = { Items = ConcurrentDictionary<string, Item>() }

let contextOf (req: Request) : Context =
    { Store  = globalStore
      Config = Map.empty
      Auth   = authOf req
      Log    = globalLogger.Log }

// ═════════════════════════════════════════════════════════════════════
// 11. ТЕСТЫ — функции, не методы
// ═════════════════════════════════════════════════════════════════════

let private fail msg = raise (Exception msg)

let testContext () : Context =
    { Store  = { Items = ConcurrentDictionary() }
      Config = Map.empty
      Auth   = { UserId = Some "u1"; CsrfToken = "test-token" }
      Log    = ignore }

let testReq method' route body : Request =
    { Method = method'; Route = route; Params = Map.empty
      Body = body; Headers = Map.empty }

let tests : (string * (unit -> Async<unit>)) list = [

    "items/empty_list", fun () -> async {
        let ctx = testContext ()
        let! r = dispatch [itemsSlice] (testReq "GET" "items" Map.empty) ctx
        if r.Status <> 200 then fail $"expected 200, got {r.Status}"
        if not (r.Body.Contains "Пока ничего нет") then fail "empty state not shown"
    }

    "items/create_validation", fun () -> async {
        let ctx = testContext ()
        let! r = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "x"; "csrf_token", "test-token" ])) ctx
        if r.Status <> 422 then fail $"expected 422, got {r.Status}"
        if ctx.Store.Items.Count <> 0 then fail "should not insert on validation error"
    }

    "items/create_then_list", fun () -> async {
        let ctx = testContext ()
        let! _ = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "Hello"; "csrf_token", "test-token" ])) ctx
        if ctx.Store.Items.Count <> 1 then fail "row not inserted"
        let! r = dispatch [itemsSlice] (testReq "GET" "items" Map.empty) ctx
        if not (r.Body.Contains "Hello") then fail "created item not visible"
    }

    "items/redirect_after_create", fun () -> async {
        let ctx = testContext ()
        let! r = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "A"; "csrf_token", "test-token" ])) ctx
        if r.Status <> 302 then fail $"expected 302, got {r.Status}"
        if r.Headers.TryFind "Location" <> Some "?action=items" then fail "wrong Location"
    }

    "items/csrf_rejects_bad_token", fun () -> async {
        let ctx = testContext ()
        let! r = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "X"; "csrf_token", "WRONG" ])) ctx
        if r.Status <> 403 then fail $"expected 403, got {r.Status}"
        if ctx.Store.Items.Count <> 0 then fail "should not insert on CSRF failure"
    }

    "items/json_negotiation", fun () -> async {
        let ctx = testContext ()
        let! _ = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "Json"; "csrf_token", "test-token" ])) ctx
        let req = { testReq "GET" "items" Map.empty with Params = Map [ "format", "json" ] }
        let! r = dispatch [itemsSlice] req ctx
        if not (r.Headers.["Content-Type"].Contains "json") then fail "expected JSON"
        if not (r.Body.Contains "\"Json\"") then fail "JSON payload missing"
    }

    "items/delete", fun () -> async {
        let ctx = testContext ()
        let! _ = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "create"; "title", "Del"; "csrf_token", "test-token" ])) ctx
        let id = ctx.Store.Items |> Seq.head |> fun kv -> kv.Key
        let! r = dispatch [itemsSlice] (testReq "POST" "items"
                    (Map [ "op", "delete"; "id", id; "csrf_token", "test-token" ])) ctx
        if r.Status <> 302 then fail $"expected 302, got {r.Status}"
        if ctx.Store.Items.Count <> 0 then fail "row not deleted"
    }

    "kernel/not_found", fun () -> async {
        let ctx = testContext ()
        let! r = dispatch [itemsSlice] (testReq "GET" "unknown" Map.empty) ctx
        if r.Status <> 404 then fail $"expected 404, got {r.Status}"
    }

    "kernel/method_not_allowed", fun () -> async {
        let ctx = testContext ()
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
// 12. RUNTIME
// ═════════════════════════════════════════════════════════════════════

let argv  = Environment.GetCommandLineArgs() |> Array.skip 1
let runCi = argv |> Array.contains "--run-ci"
            || Environment.GetEnvironmentVariable "RUN_CI" = "1"

if runCi then
    let failed = runTests () |> Async.RunSynchronously
    exit failed

let slices : Slice list = [ itemsSlice ]   // ← новые фичи сюда

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
