open System
open System.Net.Http
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open System.Threading

// Функция для отправки HTTP-запроса
let sendRequest (client: HttpClient) (method: HttpMethod) (url: string) (headers: (string * string) list) (body: string option) : Task<HttpResponseMessage> =
    let request = new HttpRequestMessage(method, url)
    
    // Добавляем заголовки
    headers |> List.iter (fun (key, value) ->
        if key <> "Content-Type" then
            request.Headers.TryAddWithoutValidation(key, value) |> ignore)
    
    // Добавляем тело запроса, если оно есть
    match body with
    | Some content ->
        let stringContent = new StringContent(content, Encoding.UTF8)
        stringContent.Headers.ContentType <- System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json")
        request.Content <- stringContent
    | None -> ()

    client.SendAsync(request)

// Функция для выполнения нагрузочного тестирования с ограничением параллелизма
let loadTest (client: HttpClient) (url: string) (headers: (string * string) list) (body: string option) (numRequests: int) (maxParallel: int) : Task<(HttpResponseMessage * float)[]> =
    let semaphore = new SemaphoreSlim(maxParallel)
    let results = ResizeArray<(HttpResponseMessage * float)>()

    let processRequest = async {
        do! semaphore.WaitAsync() |> Async.AwaitTask
        try
            let stopwatch = Stopwatch.StartNew()
            let! response = sendRequest client HttpMethod.Get url headers body |> Async.AwaitTask
            stopwatch.Stop()
            return (response, stopwatch.Elapsed.TotalMilliseconds)
        finally
            semaphore.Release() |> ignore
    }
    
    async {
        // Создаем список асинхронных операций
        let asyncs = 
            [ for _ in 1 .. numRequests -> processRequest ]
        
        // Выполняем параллельно с ограничением по количеству
        let! completedResults = Async.Parallel(asyncs, maxDegreeOfParallelism = maxParallel)
        return completedResults
    } |> Async.StartAsTask

// Функция для вывода результатов
let printResults (results: (HttpResponseMessage * float)[]) =
    let mutable successCount = 0
    let mutable errorCount = 0
    let times = Array.zeroCreate results.Length

    for i in 0 .. results.Length - 1 do
        let response, time = results[i]
        times[i] <- time
        
        match response.IsSuccessStatusCode with
        | true -> 
            successCount <- successCount + 1
        | false ->
            errorCount <- errorCount + 1
            let reason = 
                if String.IsNullOrWhiteSpace(response.ReasonPhrase) 
                then "Unknown error" 
                else response.ReasonPhrase
            printfn "Error: Status %A, Reason: %s" response.StatusCode reason
        
        response.Dispose() // Освобождаем ресурсы сразу

    let averageTime = Array.average times
    let minTime = Array.min times
    let maxTime = Array.max times
    let errorRate = 
        if results.Length > 0 
        then float errorCount / float results.Length * 100.0 
        else 0.0
    
    printfn "\n=== Results ==="
    printfn "Total requests:    %d" results.Length
    printfn "Successful:        %d" successCount
    printfn "Errors:            %d" errorCount
    printfn "Error rate:        %.2f%%" errorRate
    printfn "Average time:      %.2f ms" averageTime
    printfn "Min time:          %.2f ms" minTime
    printfn "Max time:          %.2f ms" maxTime
    printfn "================="

// Основная функция
[<EntryPoint>]
let main argv =
    let url = "http://localhost:8080/hello"
    let headers = []
    let body = Some """"""
    let numRequests = 1000000
    let maxParallel = 16 // Ограничение параллелизма

    // Настраиваем HTTP-клиент
    let handler = new SocketsHttpHandler(
        PooledConnectionLifetime = TimeSpan.FromMinutes(5.0),
        MaxConnectionsPerServer = 100,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2.0))
    
    use client = new HttpClient(handler, Timeout = TimeSpan.FromSeconds(30.0))
    
    printfn "Starting load test with %d requests (max parallel: %d)..." numRequests maxParallel
    let sw = Stopwatch.StartNew()
    
    // Выполняем нагрузочное тестирование
    let task = loadTest client url headers body numRequests maxParallel
    task.Wait()

    sw.Stop()
    printfn "Load test completed in %.2f seconds" sw.Elapsed.TotalSeconds

    // Получаем результаты и выводим их
    let results = task.Result
    printResults results

    0 // Возвращаем код завершения