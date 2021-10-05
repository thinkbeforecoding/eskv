namespace eskv

open FSharp.Control
open System
open System.Net.Http
open System.Net
open System.Threading.Tasks


[<Struct>]
type EventData = {
    EventType: string
    Data: string
}

module private Http =

    let (|Success|Failure|) (statusCode: HttpStatusCode) =
        if int statusCode >= 200 && int statusCode <= 299 then
            Success
        else
            Failure

type Client(uri: Uri) =
    let kv = Uri(uri, "kv/")
    let es = Uri(uri, "es/")

    let raiseHttpException(response: HttpResponseMessage) =
        raise (HttpRequestException(response.ReasonPhrase, null, Nullable response.StatusCode))

    new() = Client(Uri "http://localhost:5000")

    
    member _.TryLoadAsync(key: string) =
        task {
            use client = new HttpClient()
            let! response  = client.GetAsync(Uri(kv, key))
            match response.StatusCode with
            | HttpStatusCode.NotFound ->
                return null, None
            | Http.Success ->
                let etag = 
                    match response.Headers.ETag with
                    | null -> null
                    | t -> t.Tag
                let! data = response.Content.ReadAsStringAsync()


                return etag, Some data
            | _ ->
                return raiseHttpException(response)

        } 

       

    member this.TryLoad(key) = this.TryLoadAsync(key).Result

    member _.TrySaveAsync(key: string, value: string, etag: string) =
        task {
            use client = new HttpClient()
            if not (isNull etag) then
                client.DefaultRequestHeaders.IfMatch.Add(Headers.EntityTagHeaderValue.Parse(etag))
            else
                client.DefaultRequestHeaders.IfNoneMatch.Add(Headers.EntityTagHeaderValue.Any)

            let! response = client.PutAsync(Uri(kv,key), new StringContent(value))
            
            match response.StatusCode with
            | HttpStatusCode.Conflict ->
                return None
            | Http.Success ->
                return Some response.Headers.ETag.Tag
            | _ ->
                 return raiseHttpException(response)
                
        }

    member this.TrySave(key, value, etag) = this.TrySaveAsync(key, value, etag).Result

    member _.SaveAsync(key: string, value: string) =
        task {
            use client = new HttpClient()

            let! response = client.PutAsync(Uri(kv,key), new StringContent(value))
            if not response.IsSuccessStatusCode then
                return raiseHttpException(response)
        } :> Task

    member this.SaveS(key, value) = this.SaveAsync(key, value).Wait()



    member _.AppendAsync(stream: string, events) =
        task {
            use client = new HttpClient()

            let content = new MultipartContent()
            for e in events do
                let part = new StringContent(e.Data)
                part.Headers.Add("ESKV-Event-Type", e.EventType)
                part.Headers.ContentType.MediaType <- "text/plain"

                content.Add(part)

            let! response = client.PostAsync(Uri(es,stream), content)
            if not response.IsSuccessStatusCode then
                return raiseHttpException(response)
            
        } :> Task


    member this.Append(stream: string, events) = this.AppendAsync(stream, events).Wait()







