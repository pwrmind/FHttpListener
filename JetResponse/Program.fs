open System.Net
open System.Text
open System

// Тип для описания ошибок с HTTP-статусом
type HttpError = { Message: string; StatusCode: int }

// Результат с явным разделением успеха/ошибки
type Result<'T> =
    | Success of 'T
    | Failure of HttpError

// Типы для обработчиков и middleware
type HttpRequestHandler = HttpListenerRequest -> Async<Result<string>>
type Middleware = HttpRequestHandler -> HttpRequestHandler

// Тип для маршрута (Метод, Путь, Обработчик)
type Route = string * string * HttpRequestHandler

// Логгер с обработкой сообщений в отдельном потоке
type Logger() =
    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
            let! (msg: string) = inbox.Receive()
            do! Async.SwitchToNewThread()
            printfn "[%s] %s" (DateTime.Now.ToString("o")) msg
            return! loop()
        }
        loop())

    member __.Log(message) = agent.Post(message)

// Глобальный логгер (в реальном приложении лучше инжектировать)
let logger = Logger()

// Стандартные ошибки
module Errors =
    let notFound = { Message = "Route not found"; StatusCode = 404 }
    let methodNotAllowed = { Message = "Method not allowed"; StatusCode = 405 }
    let badRequest msg = { Message = msg; StatusCode = 400 }

// Асинхронная запись ответа
let writeResponse (response: HttpListenerResponse) = function
    | Success (content: string) ->
        async {
            response.StatusCode <- 200
            let buffer = Encoding.UTF8.GetBytes(content)
            response.ContentLength64 <- int64 buffer.Length
            do! response.OutputStream.WriteAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
            do! response.OutputStream.FlushAsync() |> Async.AwaitTask
        }
    | Failure (error: HttpError) ->
        async {
            response.StatusCode <- error.StatusCode
            let buffer = Encoding.UTF8.GetBytes(error.Message)
            response.ContentLength64 <- int64 buffer.Length
            do! response.OutputStream.WriteAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
            do! response.OutputStream.FlushAsync() |> Async.AwaitTask
        }


// Middleware для логирования (теперь обрабатывает и результат)
let loggingMiddleware: Middleware =
    fun next ->
        fun request ->
            async {
                logger.Log(sprintf "Request started: %s %s" request.HttpMethod request.Url.AbsolutePath)
                
                let! result = next request
                
                match result with
                | Success _ -> logger.Log "Request completed successfully"
                | Failure e -> logger.Log(sprintf "Request failed: %s (Status: %d)" e.Message e.StatusCode)
                
                return result
            }

// Middleware для проверки метода
let methodCheckMiddleware: Middleware =
    fun next ->
        fun request ->
            async {
                if request.HttpMethod = "GET" || request.HttpMethod = "POST" then
                    return! next request
                else
                    return Failure Errors.methodNotAllowed
            }

// Middleware для обработки 404 ошибок
let notFoundMiddleware: Middleware =
    fun next ->
        fun request ->
            async {
                match! next request with
                | Failure e when e.StatusCode = 404 -> 
                    return Failure { e with Message = $"Path not found: {request.Url.AbsolutePath}" }
                | result -> return result
            }

// Обработчики маршрутов
let helloHandler: HttpRequestHandler =
    fun request ->
        async {
            return Success "Hello, World!"
        }

let goodbyeHandler: HttpRequestHandler =
    fun request ->
        async {
            return Success "Goodbye, World!"
        }

// Маршрутизатор
let routeRequest (routes: Route list) (request: HttpListenerRequest): Async<Result<string>> =
    let method = request.HttpMethod
    let path = request.Url.AbsolutePath
    match routes |> List.tryFind (fun (m, p, _) -> m = method && p = path) with
    | Some (_, _, handler) -> handler request
    | None -> async { return Failure Errors.notFound }

// Композиция middleware
let composeMiddleware (middlewares: Middleware list) (handler: HttpRequestHandler): HttpRequestHandler =
    List.foldBack (fun middleware acc -> middleware acc) middlewares handler

// Запуск сервера
let startServer (prefix: string) =
    let listener = new HttpListener()
    listener.Prefixes.Add(prefix)
    listener.Start()
    printfn "Listening on %s" prefix

    // Функции-помощники для маршрутов
    let get path handler = ("GET", path, handler)
    let post path handler = ("POST", path, handler)

    // Функция для добавления middleware
    let withMiddleware middleware handler = middleware handler

    // Определение маршрутов
    let routes = [
        get "/hello" (fun _ -> async { return Success "Hello, World!" })
        post "/goodbye" goodbyeHandler
    ]

    // Построение обработчика с middleware
    let composedHandler =
        routeRequest routes
        |> withMiddleware loggingMiddleware
        |> withMiddleware methodCheckMiddleware
        |> withMiddleware notFoundMiddleware

    let rec handleRequest() =
        async {
            let! context = listener.GetContextAsync() |> Async.AwaitTask
            let response = context.Response
            let! result = composedHandler context.Request
            do! writeResponse response result
            response.Close()
            return! handleRequest()
        }

    Async.Start(handleRequest())

[<EntryPoint>]
let main argv =
    startServer "http://localhost:8080/"
    Console.ReadLine() |> ignore
    0