open System
open System.Net
open System.Text

// Тип для описания HTTP ошибок
type HttpError = {
    Message: string
    StatusCode: int
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
    
let reader = ReaderT.ReaderBuilder()

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

// Потокобезопасный логгер
type Logger() =
    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
            let! msg = inbox.Receive()
            Console.WriteLine($"[{DateTime.Now:O}] {msg}")
            return! loop()
        }
        loop())

    member __.Log(message) = agent.Post(message)

// Контейнер сервисов
type Services = {
    Logger: Logger
    Counter: CounterService
}

// Middleware и обработчики
type Middleware = ReaderT<Services, string> -> ReaderT<Services, string>
type Route = string * string * ReaderT<Services, string> // Метод, Путь, Обработчик

// Псевдоним для нашего обработчика
type WebHandler<'a> = ReaderT<Services, 'a>

// Стандартные ошибки
module Errors =
    let notFound = { Message = "Route not found"; StatusCode = 404 }
    let methodNotAllowed = { Message = "Method not allowed"; StatusCode = 405 }
    let internalError = { Message = "Internal server error"; StatusCode = 500 }

// Запись HTTP ответа
let writeResponse (response: HttpListenerResponse) = function
    | Success (content: string) ->
        async {
            response.StatusCode <- 200
            let buffer = Encoding.UTF8.GetBytes(content)
            response.ContentLength64 <- int64 buffer.Length
            do! response.OutputStream.WriteAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
            response.OutputStream.Close()
        }
    | Failure (error: HttpError) ->
        async {
            response.StatusCode <- error.StatusCode
            let buffer = Encoding.UTF8.GetBytes(error.Message)
            response.ContentLength64 <- int64 buffer.Length
            do! response.OutputStream.WriteAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
            response.OutputStream.Close()
        }

// Middleware
let loggingMiddleware : Middleware =
    fun next ->
        reader {
            let! services = ReaderT.ask
            let! request = reader {
                // В реальной реализации request должен быть частью окружения
                // Для простоты демонстрации используем заглушку
                return { 
                    new obj() with 
                        member __.ToString() = "HttpListenerRequest" 
                }
            }
            
            services.Logger.Log($"Request started: {request}")
            
            try
                let! result = next
                services.Logger.Log("Request completed successfully")
                return result
            with ex ->
                services.Logger.Log($"Request failed: {ex.Message}")
                return! ReaderT.liftResult (Failure Errors.internalError)
        }

let methodCheckMiddleware (allowedMethods: string list) : Middleware =
    fun next ->
        reader {
            let! request = reader {
                // Заглушка для демонстрации
                return { 
                    new obj() with 
                        member __.ToString() = "HttpListenerRequest" 
                }
            }
            
            // В реальной реализации проверяем метод запроса
            let method = "GET" // Заглушка
            if List.contains method allowedMethods then
                return! next
            else
                return! ReaderT.liftResult (Failure Errors.methodNotAllowed)
        }

// Обработчики
let helloHandler : WebHandler<string> = 
    reader {
        let! services = ReaderT.ask
        services.Counter.Increment()
        let! count = services.Counter.GetCountAsync() |> ReaderT.liftAsync
        return $"Hello, World! Request count: {count}"
    }

let goodbyeHandler : WebHandler<string> = 
    reader {
        let! services = ReaderT.ask
        return "Goodbye, World!"
    }

let errorHandler : WebHandler<string> = 
    reader {
        return! ReaderT.liftResult (Failure {
            Message = "Simulated error"
            StatusCode = 500
        })
    }

// Маршрутизатор
let routeRequest (routes: Route list) : WebHandler<string> =
    reader {
        // В реальной реализации используем реальный запрос
        let method = "GET"
        let path = "/hello"
        
        match routes |> List.tryFind (fun (m, p, _) -> m = method && p = path) with
        | Some (_, _, handler) -> return! handler
        | None -> return! ReaderT.liftResult (Failure Errors.notFound)
    }

// Композиция middleware
let composeMiddleware (middlewares: Middleware list) (handler: WebHandler<string>) =
    List.foldBack (fun mw acc -> mw acc) middlewares handler

// Запуск сервера
let startServer (prefix: string) =
    let listener = new HttpListener()
    listener.Prefixes.Add(prefix)
    listener.Start()
    Console.WriteLine($"Listening on {prefix}")

    // Инициализация сервисов
    let services = {
        Logger = Logger()
        Counter = CounterService()
    }

    // Определение маршрутов
    let routes = [
        ("GET", "/hello", helloHandler)
        ("GET", "/goodbye", goodbyeHandler)
        ("GET", "/error", errorHandler)
    ]

    // Сборка обработчика с middleware
    let baseHandler = routeRequest routes
    let handlerWithMiddleware = 
        baseHandler
        |> composeMiddleware [
            loggingMiddleware
            methodCheckMiddleware ["GET"; "POST"]
        ]

    let rec handleRequest () =
        async {
            let! context = listener.GetContextAsync() |> Async.AwaitTask
            let response = context.Response
            
            try
                // Запуск обработчика
                let! result = ReaderT.run services handlerWithMiddleware
                do! writeResponse response result
            with ex ->
                do! writeResponse response (Failure {
                    Message = $"Critical error: {ex.Message}"
                    StatusCode = 500
                })
            
            return! handleRequest ()
        }

    Async.Start(handleRequest ())

[<EntryPoint>]
let main argv =
    startServer "http://localhost:8080/"
    Console.WriteLine("Press Enter to stop the server...")
    Console.ReadLine() |> ignore
    0