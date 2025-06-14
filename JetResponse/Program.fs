open System.Net
open System.Text
open System

// Определяем типы для успешного и неуспешного результата
type Result<'T> =
    | Success of 'T
    | Failure of string

// Определяем тип для обработки HTTP-запросов
type HttpRequestHandler = HttpListenerRequest -> Async<Result<string>>

// Определяем тип для middleware
type Middleware = HttpRequestHandler -> HttpRequestHandler

// Определяем тип для маршрута
type Route = string * string * HttpRequestHandler // (HTTP метод, путь, обработчик)

// Асинхронный логгер с буферизацией
type Logger() =
    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
            let! (msg: string) = inbox.Receive()
            do! Async.SwitchToNewThread()  // Выполняем I/O в отдельном потоке
            printfn "[%s] %s" (DateTime.Now.ToString("o")) msg
            return! loop()
        }
        loop())

    member __.Log(message) = agent.Post(message)

// Глобальный экземпляр логгера
let logger = Logger()

// Функция для обработки успешного результата
let handleSuccess (response: HttpListenerResponse) (result: Result<string>) =
    match result with
    | Success content ->
        response.StatusCode <- 200
        let buffer = Encoding.UTF8.GetBytes(content)
        response.ContentLength64 <- int64 buffer.Length
        response.OutputStream.Write(buffer, 0, buffer.Length)
    | Failure errorMessage ->
        response.StatusCode <- 400
        let buffer = Encoding.UTF8.GetBytes(errorMessage)
        response.ContentLength64 <- int64 buffer.Length
        response.OutputStream.Write(buffer, 0, buffer.Length)

// Пример middleware для логирования
let loggingMiddleware: Middleware =
    fun next ->
        fun request ->
            async {
                //logger.Log(sprintf "Received request: %s %s" request.HttpMethod (request.Url.ToString()))
                let! result = next request
                return result
            }

// Пример middleware для проверки метода
let methodCheckMiddleware: Middleware =
    fun next ->
        fun request ->
            async {
                if request.HttpMethod = "GET" || request.HttpMethod = "POST" then
                    return! next request
                else
                    return Failure "Unsupported HTTP method"
            }

// Пример функции обработки запроса для маршрута "/hello"
let helloHandler: HttpRequestHandler =
    fun request ->
        async {
            return Success "Hello, World!"
        }

// Пример функции обработки запроса для маршрута "/goodbye"
let goodbyeHandler: HttpRequestHandler =
    fun request ->
        async {
            return Success "Goodbye, World!"
        }

// Функция для маршрутизации
let routeRequest (routes: Route list) (request: HttpListenerRequest): Async<Result<string>> =
    let method = request.HttpMethod
    let path = request.Url.AbsolutePath
    let matchingRoute = routes |> List.tryFind (fun (m, p, _) -> m = method && p = path)
    match matchingRoute with
    | Some (_, _, handler) -> handler request
    | None -> async { return Failure "Route not found." }

// Функция для компоновки нескольких middleware
let composeMiddleware (middlewares: Middleware list) (handler: HttpRequestHandler): HttpRequestHandler =
    List.foldBack (fun middleware acc -> middleware acc) middlewares handler

// Основная функция для запуска сервера
let startServer (prefix: string) =
    let listener = new HttpListener()
    listener.Prefixes.Add(prefix)
    listener.Start()
    printfn "Listening on %s" prefix

    // Определяем маршруты
    let routes: Route list = [
        ("GET", "/hello", helloHandler)
        ("GET", "/goodbye", goodbyeHandler)
    ]

    // Компонуем обработчики с middleware
    let composedHandler =
        composeMiddleware [loggingMiddleware; methodCheckMiddleware] (routeRequest routes)

    let rec handleRequest() =
        async {
            let! context = listener.GetContextAsync() |> Async.AwaitTask
            let response = context.Response
            let! result = composedHandler context.Request
            handleSuccess response result
            response.Close()
            return! handleRequest()
        }

    Async.Start(handleRequest())

// Запуск сервера
[<EntryPoint>]
let main argv =
    startServer "http://localhost:8080/"
    Console.ReadLine() |> ignore
    0