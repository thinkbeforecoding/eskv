module kv.Client

open FSharp.Control
open System.Net.Http
open System.Net

let tryLoadAsync name =
    task {
        use client = new HttpClient()
        let response  = client.GetAsync($"http://localhost:5000/kv/{name}").Result
        if response.StatusCode = HttpStatusCode.NotFound then
            return null, None
        else
            let etag = 
                match response.Headers.ETag with
                | null -> null
                | t -> t.Tag
            let! data = response.Content.ReadAsStringAsync()


            return etag, Some data
    }
let tryLoad name = (tryLoadAsync name).Result

let trySaveAsync name (etag: string) data =
    task {
    use client = new HttpClient()
    if not (isNull etag) then
        client.DefaultRequestHeaders.IfMatch.Add(Headers.EntityTagHeaderValue.Parse(etag))
    else
        client.DefaultRequestHeaders.IfNoneMatch.Add(Headers.EntityTagHeaderValue.Any)

    let! response = client.PutAsync($"http://localhost:5000/kv/{name}", new StringContent(data))
    if response.StatusCode = HttpStatusCode.Conflict then
        return None
    else
        return Some response.Headers.ETag.Tag
    }

let trySave name etag data = (trySaveAsync name etag data).Result

let saveAsync name data =
    task {
    use client = new HttpClient()

    let! response = client.PutAsync($"http://localhost:5000/kv/{name}", new StringContent(data))
    ()
    }

let save name data = (saveAsync name data).Result


type EventData = {
    EventType: string
    Data: string
}

let appendAsync stream events =
    task {
    use client = new HttpClient()

    let content = new MultipartContent()
    for e in events do
        let part = new StringContent(e.Data)
        part.Headers.Add("X-Event-Type", e.EventType)

        content.Add(part)

    let! resp = client.PostAsync($"http://localhost:5000/es/{stream}",content)
    ()
    }






(appendAsync "test" [ { EventType = "SwitchedOn"; Data = "5"}; { EventType = "SwitchedOff"; Data = "4"}]).Result

