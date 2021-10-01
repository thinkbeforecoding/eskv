module AspNetExtensions
open System
open System.Threading
open System.Threading.Tasks
open System.Net.WebSockets
open System.Buffers
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Hosting
open System.Net.Http
open Microsoft.Extensions.Primitives

type HttpContext with
    member this.GetService<'t>() = 
        this.RequestServices.GetService(typeof<'t>) :?> 't


    member ctx.ProxyWebSocketAsync(upstreamUri: Uri, cancellationToken : CancellationToken) =
        task {
            let! ws = ctx.WebSockets.AcceptWebSocketAsync()
            use upstream = new ClientWebSocket()
            do! upstream.ConnectAsync(upstreamUri, cancellationToken)
            
            let cp1 = task {
                use mem = MemoryPool.Shared.Rent(4096)
                while true do
                    let! result = upstream.ReceiveAsync(mem.Memory,cancellationToken)
                    do! ws.SendAsync(mem.Memory.Slice(0,result.Count), result.MessageType, result.EndOfMessage, cancellationToken)
                    
            }

            let cp2 = task {
                use mem = MemoryPool.Shared.Rent(4096)
                while true do
                    let! result = ws.ReceiveAsync(mem.Memory,cancellationToken)
                    do! upstream.SendAsync(mem.Memory.Slice(0,result.Count), result.MessageType, result.EndOfMessage, cancellationToken)
                    
            }

            return! Task.WhenAll [|cp1 :> Task;cp2|]
        } :> Task
        


type HttpResponse with
    member this.SendWebFileAsync(subpath) =
        task {
            let env = this.HttpContext.GetService<IWebHostEnvironment>()
            let file = env.WebRootFileProvider.GetFileInfo(subpath)
            if isNull file then
                this.StatusCode <- 404
            else
                return! this.SendFileAsync(file)
        } :> Task



    member this.ProxyGetAsync(upstream: Uri) =
        task {
            use client = new HttpClient()
            use! response = client.GetAsync(upstream)
            
            for h in response.Headers do
                this.Headers.Add(h.Key,StringValues(Seq.toArray h.Value))

            return! response.Content.CopyToAsync(this.Body)
        } :> Task
