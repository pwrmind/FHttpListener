open System
open System.Net
open System.Text
open System.IO
open System.Collections.Generic
open System.IO.Compression
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading

// Тип для описания HTTP ошибок с JSON сериализацией
[<CLIMutable>]
type HttpError = {
    [<JsonPropertyName("message")>]
    Message: string
    
    [<JsonPropertyName("statusCode")>]
    StatusCode: int
    
    [<JsonPropertyName("details")>]
    Details: string option
    
    [<JsonPropertyName("path")>]
    Path: string option
}

// Результат обработки
type Result<'T> =
    | Success of 'T
    | Failure of HttpError

// Реализация ReaderT монады
type ReaderT<'env, 'a> = ReaderT of ('env -> Async<Result<'a>>)

module ReaderT =
    let run (env: 'env) (ReaderT f) = f env
    
    let return' (x: 'a) : ReaderT<'env, 'a> = 
        ReaderT (fun _ -> async { return Success x })
    
    let bind (f: 'a -> ReaderT<'env, 'b>) (m: ReaderT<'env, 'a>) : ReaderT<'env, 'b> =
        ReaderT (fun env -> 
            async {
                let! result = run env m
                match result with
                | Success x -> return! run env (f x)
                | Failure e -> return Failure e
            })
    
    // Комбинаторы для работы с эффектами
    let liftAsync (asyncComp: Async<'a>) : ReaderT<'env, 'a> =
        ReaderT (fun _ -> async {
            let! x = asyncComp
            return Success x
        })
    
    let liftResult (result: Result<'a>) : ReaderT<'env, 'a> =
        ReaderT (fun _ -> async { return result })
    
    let map (f: 'a -> 'b) (m: ReaderT<'env, 'a>) : ReaderT<'env, 'b> =
        bind (f >> return') m
    
    let ask : ReaderT<'env, 'env> = 
        ReaderT (fun env -> async { return Success env })
    
    // Computation Expression
    type ReaderBuilder() =
        member __.Return(x) = return' x
        member __.Bind(m, f) = bind f m
        member __.ReturnFrom(m) = m
        member __.Zero() = return' ()
        member __.Delay(f: unit -> ReaderT<'env, 'a>) = 
            ReaderT (fun env -> async { return! run env (f()) })
        member __.Combine(m1, m2) = bind (fun () -> m2) m1
        member __.TryWith(m, h) =
            ReaderT (fun env -> 
                async {
                    try return! run env m
                    with e -> return! run env (h e)
                })
        member __.TryFinally(m, compensation) =
            ReaderT (fun env -> 
                async {
                    try return! run env m
                    finally compensation()
                })
    
    let reader = ReaderBuilder()

// DI контейнер с управлением временем жизни
type ServiceLifetime =
    | Singleton
    | Transient
    | Scoped

type ServiceDescriptor = {
    ServiceType: Type
    ImplementationFactory: (unit -> obj)
    Lifetime: ServiceLifetime
}

type ServiceProvider(services: ServiceDescriptor list) =
    let singletons = Dictionary<Type, obj>()
    let scoped = Dictionary<Type, obj>()
    
    member this.GetService<'T>() : 'T =
        let t = typeof<'T>
        match services |> List.tryFind (fun s -> s.ServiceType = t) with
        | Some descriptor ->
            match descriptor.Lifetime with
            | Singleton ->
                lock singletons (fun () ->
                    match singletons.TryGetValue(t) with
                    | true, instance -> instance :?> 'T
                    | false, _ ->
                        let instance = descriptor.ImplementationFactory() :?> 'T
                        singletons.Add(t, instance)
                        instance
                )
            | Transient -> descriptor.ImplementationFactory() :?> 'T
            | Scoped ->
                match scoped.TryGetValue(t) with
                | true, instance -> instance :?> 'T
                | false, _ ->
                    let instance = descriptor.ImplementationFactory() :?> 'T
                    scoped.Add(t, instance)
                    instance
        | None -> failwithf "Service %s not registered" t.Name
    
    member this.CreateScope() = ServiceProvider(services)

// Потокобезопасный сервис-счетчик
type CounterMessage =
    | Increment
    | GetCount of AsyncReplyChannel<int>

type CounterService() =
    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop count = async {
            let! msg = inbox.Receive()
            match msg with
            | Increment -> 
                return! loop (count + 1)
            | GetCount replyChannel -> 
                replyChannel.Reply count
                return! loop count
        }
        loop 0)

    member this.Increment() = agent.Post Increment
    member this.GetCountAsync() = agent.PostAndAsyncReply GetCount

// Логгер с разными уровнями
type LogLevel = Debug | Info | Warning | Error

type Logger() =
    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
            let! (level, msg) = inbox.Receive()
            let prefix = match level with
                         | Debug -> "DBG"
                         | Info -> "INF"
                         | Warning -> "WRN"
                         | Error -> "ERR"
            printf $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{prefix}] {msg}"
            return! loop()
        }
        loop())

    member __.Debug(message) = agent.Post(Debug, message)
    member __.Info(message) = agent.Post(Info, message)
    member __.Warning(message) = agent.Post(Warning, message)
    member __.Error(message) = agent.Post(Error, message)

// Сервис кэширования
type CacheService() =
    let cache = Dictionary<string, obj * DateTime>()
    let lockObj = obj()
    let expiration = TimeSpan.FromMinutes(5.0)
    
    member this.Get<'T>(key: string) : 'T option =
        lock lockObj (fun () ->
            match cache.TryGetValue(key) with
            | true, (value, time) when DateTime.Now - time < expiration ->
                Some (value :?> 'T)
            | _ -> None
        )
    
    member this.Set<'T>(key: string, value: 'T) =
        lock lockObj (fun () ->
            cache.[key] <- (box value, DateTime.Now)
        )
    
    member this.Clear() =
        lock lockObj (fun () ->
            cache.Clear()
        )

// Сервис аутентификации
type AuthService(logger: Logger) =
    let validTokens = ["token1"; "token2"; "admin"]
    
    member this.Authenticate(token: string) : bool =
        if validTokens |> List.contains token then
            logger.Debug($"Authenticated token: {token}")
            true
        else
            logger.Warning($"Invalid token: {token}")
            false

// Контейнер сервисов
type Services = {
    Logger: Logger
    Counter: CounterService
    Cache: CacheService
    Auth: AuthService
    ServiceProvider: ServiceProvider
}

// Контекст запроса
type RequestContext = {
    Request: HttpListenerRequest
    Response: HttpListenerResponse
    Services: Services
}

// Middleware и обработчики
type Middleware = ReaderT<RequestContext, string> -> ReaderT<RequestContext, string>
type RouteHandler = ReaderT<RequestContext, string>
type Route = string * string * RouteHandler // Метод, Путь, Обработчик

// Стандартные ошибки
module Errors =
    let notFound (path: string) = { 
        Message = "Route not found"
        StatusCode = 404
        Details = Some $"Path: {path} not found"
        Path = Some path 
    }
    
    let methodNotAllowed = { 
        Message = "Method not allowed"
        StatusCode = 405
        Details = None
        Path = None 
    }
    
    let unauthorized path = { 
        Message = "Unauthorized"
        StatusCode = 401
        Details = Some $"Path: {path} requires authentication"
        Path = Some path 
    }
    
    let badRequest msg path = { 
        Message = "Bad request"
        StatusCode = 400
        Details = Some msg
        Path = Some path 
    }
    
    let internalError msg path = { 
        Message = "Internal server error"
        StatusCode = 500
        Details = Some msg
        Path = Some path 
    }

// JSON сериализация
let jsonSerializerOptions = 
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    
let toJson (data: 'a) =
    JsonSerializer.Serialize(data, jsonSerializerOptions)

// Запись HTTP ответа с поддержкой сжатия
let writeResponse (ctx: RequestContext) (result: Result<string>) =
    async {
        let response = ctx.Response
        let jsonResult = 
            match result with
            | Success content -> content
            | Failure error -> toJson error
        
        // Определение необходимости сжатия
        let acceptEncoding = ctx.Request.Headers.["Accept-Encoding"]
        let useGzip = acceptEncoding <> null && acceptEncoding.Contains("gzip")
        
        // Установка заголовков
        response.ContentType <- 
            match result with
            | Success _ -> "application/json"
            | Failure _ -> "application/json"
        
        response.StatusCode <-
            match result with
            | Success _ -> 200
            | Failure error -> error.StatusCode
        
        // Подготовка данных
        let buffer = Encoding.UTF8.GetBytes(jsonResult)
        
        if useGzip then
            response.Headers.Add("Content-Encoding", "gzip")
            use gzipStream = new GZipStream(response.OutputStream, CompressionMode.Compress)
            do! gzipStream.WriteAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
        else
            response.ContentLength64 <- int64 buffer.Length
            do! response.OutputStream.WriteAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
        
        response.OutputStream.Close()
    }

// Middleware
let loggingMiddleware : Middleware =
    fun next ->
        ReaderT.reader {
            let! ctx = ReaderT.ask
            let request = ctx.Request
            let path = request.Url.AbsolutePath
            
            ctx.Services.Logger.Info(
                $"Request started: {request.HttpMethod} {path}\n" +
                $"Headers: {request.Headers}"
            )
            
            try
                let! result = next
                ctx.Services.Logger.Info(
                    $"Request completed: {request.HttpMethod} {path}\n" +
                    $"Status: {ctx.Response.StatusCode}"
                )
                return result
            with ex ->
                ctx.Services.Logger.Error(
                    $"Request failed: {request.HttpMethod} {path}\n" +
                    $"Error: {ex.Message}\n" +
                    $"Stack: {ex.StackTrace}"
                )
                return! ReaderT.liftResult (Failure (Errors.internalError ex.Message path))
        }

let methodCheckMiddleware (allowedMethods: string list) : Middleware =
    fun next ->
        ReaderT.reader {
            let! ctx = ReaderT.ask
            let request = ctx.Request
            let path = request.Url.AbsolutePath
            
            if List.contains request.HttpMethod allowedMethods then
                return! next
            else
                return! ReaderT.liftResult (Failure Errors.methodNotAllowed)
        }

let authMiddleware : Middleware =
    fun next ->
        ReaderT.reader {
            let! ctx = ReaderT.ask
            let request = ctx.Request
            let path = request.Url.AbsolutePath
            
            // Пропускаем аутентификацию для публичных путей
            if path = "/public" then 
                return! next
            
            let token = request.Headers.["Authorization"]
            if String.IsNullOrEmpty(token) then
                return! ReaderT.liftResult (Failure (Errors.unauthorized path))
            elif ctx.Services.Auth.Authenticate(token) then
                return! next
            else
                return! ReaderT.liftResult (Failure (Errors.unauthorized path))
        }

let cachingMiddleware (duration: TimeSpan) : Middleware =
    fun next ->
        ReaderT.reader {
            let! ctx = ReaderT.ask
            let request = ctx.Request
            let cacheKey = $"{request.HttpMethod}:{request.Url}"
            
            match ctx.Services.Cache.Get<string>(cacheKey) with
            | Some cachedResponse -> 
                ctx.Services.Logger.Debug($"Cache hit for {cacheKey}")
                return cachedResponse
            | None ->
                let! result = next
                ctx.Services.Cache.Set(cacheKey, result)
                ctx.Services.Logger.Debug($"Cached response for {cacheKey}")
                return result
        }

// Парсинг параметров запроса
module RequestParser =
    let parseQuery (request: HttpListenerRequest) =
        request.QueryString
        |> Seq.cast<System.Collections.Generic.KeyValuePair<string, string>>
        |> Seq.map (fun kvp -> (kvp.Key, kvp.Value))
        |> Map.ofSeq
    
    let parseBody<'T> (request: HttpListenerRequest) = async {
        use reader = new StreamReader(request.InputStream, request.ContentEncoding)
        let! body = reader.ReadToEndAsync() |> Async.AwaitTask
        return JsonSerializer.Deserialize<'T>(body, jsonSerializerOptions)
    }

// Обработчики
let helloHandler : RouteHandler = 
    ReaderT.reader {
        let! ctx = ReaderT.ask
        let request = ctx.Request
        let path = request.Url.AbsolutePath
        
        ctx.Services.Counter.Increment()
        let! count = ctx.Services.Counter.GetCountAsync() |> ReaderT.liftAsync
        
        // Парсинг параметров
        let queryParams = RequestParser.parseQuery request
        let name = 
            match queryParams.TryFind "name" with
            | Some n -> n
            | None -> "World"
        
        return toJson {| 
            Message = $"Hello, {name}!" 
            RequestCount = count 
            Path = path 
        |}
    }

let goodbyeHandler : RouteHandler = 
    ReaderT.reader {
        let! ctx = ReaderT.ask
        return toJson {| Message = "Goodbye, World!" |}
    }

let publicHandler : RouteHandler = 
    ReaderT.reader {
        return toJson {| Message = "Public content" |}
    }

let errorHandler : RouteHandler = 
    ReaderT.reader {
        let! ctx = ReaderT.ask
        let path = ctx.Request.Url.AbsolutePath
        return! ReaderT.liftResult (Failure (Errors.badRequest "Simulated error" path))
    }

// Маршрутизатор
let routeRequest (routes: Route list) : RouteHandler =
    ReaderT.reader {
        let! ctx = ReaderT.ask
        let request = ctx.Request
        let method = request.HttpMethod
        let path = request.Url.AbsolutePath
        
        match routes |> List.tryFind (fun (m, p, _) -> m = method && p = path) with
        | Some (_, _, handler) -> return! handler
        | None -> return! ReaderT.liftResult (Failure (Errors.notFound path))
    }

// Композиция middleware
let composeMiddleware (middlewares: Middleware list) (handler: RouteHandler) =
    List.foldBack (fun mw acc -> mw acc) middlewares handler

// Инициализация DI
let createServiceProvider () =
    let services = [
        // Singleton services
        { 
            ServiceType = typeof<Logger>
            ImplementationFactory = fun () -> Logger() :> obj
            Lifetime = Singleton 
        }
        { 
            ServiceType = typeof<CounterService>
            ImplementationFactory = fun () -> CounterService() :> obj
            Lifetime = Singleton 
        }
        { 
            ServiceType = typeof<CacheService>
            ImplementationFactory = fun () -> CacheService() :> obj
            Lifetime = Singleton 
        }
        
        // Scoped services
        { 
            ServiceType = typeof<AuthService>
            ImplementationFactory = fun () -> 
                let logger = ServiceProvider.GetService<Logger>()
                AuthService(logger) :> obj
            Lifetime = Scoped 
        }
    ]
    
    ServiceProvider(services)

// Запуск сервера
let startServer (prefix: string) =
    let listener = new HttpListener()
    listener.Prefixes.Add(prefix)
    listener.Start()
    Console.WriteLine($"Listening on {prefix}")

    // Инициализация DI
    let rootServiceProvider = createServiceProvider ()
    
    // Определение маршрутов
    let routes = [
        ("GET", "/hello", helloHandler)
        ("GET", "/goodbye", goodbyeHandler)
        ("GET", "/error", errorHandler)
        ("GET", "/public", publicHandler)
    ]

    let rec handleRequest () =
        async {
            let! context = listener.GetContextAsync() |> Async.AwaitTask
            
            // Создаем область видимости для запроса
            let serviceProvider = rootServiceProvider.CreateScope()
            
            // Инициализируем сервисы
            let services = {
                Logger = serviceProvider.GetService<Logger>()
                Counter = serviceProvider.GetService<CounterService>()
                Cache = serviceProvider.GetService<CacheService>()
                Auth = serviceProvider.GetService<AuthService>()
                ServiceProvider = serviceProvider
            }
            
            // Создаем контекст запроса
            let requestContext = {
                Request = context.Request
                Response = context.Response
                Services = services
            }
            
            try
                // Сборка обработчика с middleware
                let baseHandler = routeRequest routes
                let handlerWithMiddleware = 
                    baseHandler
                    |> composeMiddleware [
                        loggingMiddleware
                        methodCheckMiddleware ["GET"; "POST"]
                        authMiddleware
                        cachingMiddleware (TimeSpan.FromSeconds(30.0))
                    ]
                
                // Запуск обработчика
                let! result = ReaderT.run requestContext handlerWithMiddleware
                
                // Отправка ответа
                do! writeResponse requestContext result
            with ex ->
                services.Logger.Error(
                    $"Unhandled exception: {ex.Message}\n" +
                    $"Stack: {ex.StackTrace}"
                )
                let error = Errors.internalError ex.Message context.Request.Url.AbsolutePath
                do! writeResponse requestContext (Failure error)
            
            return! handleRequest ()
        }

    Async.Start(handleRequest ())

[<EntryPoint>]
let main argv =
    startServer "http://localhost:8080/"
    Console.WriteLine("Press Enter to stop the server...")
    Console.ReadLine() |> ignore
    0