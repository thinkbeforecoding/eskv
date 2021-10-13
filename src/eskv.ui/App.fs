module kv.ui

open Fable.Core
open Elmish.React

open Elmish
open Feliz
open Fetch
open Feliz.Bulma
open Browser
open Shared
open Style

type Editing = 
    { Container: Container
      Key: Key
      Value: string
      ETag: ETag
      Saving: bool}

type EditState =
    | NoEdit
    | Editing of Editing

type EventView =
    | All
    | Stream of string

type Command =
    | ChangeNewKey of string
    | ChangeNewValue of string
    | CreateNew
    | OpenNew
    | CloseNew
    | DeleteKey of Container*Key*ETag
    | DeleteContainer of Container
    | Deleted
    | Saved
    | Edit of Editing
    | EditChanged of string
    | CancelEdit
    | SaveEdit
    | EditSaved
    | Server of Shared.ServerCmd
    | ChangeEventView of EventView


type Model =
    { NewKey: string
      NewValue: string
      ShowCreateNew: bool
      Saving: bool
      Keys: Map<string, Map<string, string*ETag>>
      Editing: EditState
      Events: (string * int * string*string) list 
      EventView: EventView
      }

let init() =
    { NewKey = ""
      NewValue = ""
      ShowCreateNew = false
      Saving = false
      Keys = Map.empty
      Editing = NoEdit
      Events = []
      EventView = All}, Cmd.none

let update (command: Command) (model: Model)  =
    match command with
    |ChangeNewKey txt ->
        { model with NewKey = txt }, Cmd.none
    |ChangeNewValue txt ->
        { model with NewValue = txt }, Cmd.none
    |CreateNew ->
        { model with Saving = true}, 
            Cmd.OfAsync.result (async {
                let container, key =
                    match model.NewKey.Split('/', 2) |> Array.toList with
                    | container:: key :: _ -> container, key
                    | [ key ] -> "default", key
                    | [] -> "default", "key"


                
                let! response =
                    fetch $"/kv/{container}/{key}" 
                        [
                           requestHeaders [IfNoneMatch "*"]
                           RequestProperties.Method HttpMethod.PUT
                           RequestProperties.Body (U3.Case3 model.NewValue)
                        ] |> Async.AwaitPromise
                return Saved
                     })
    | OpenNew ->
        { model with ShowCreateNew = true }, Cmd.none
    | CloseNew ->
        { model with ShowCreateNew = false }, Cmd.none
    | Saved ->
        { model with NewKey = ""
                     NewValue = ""
                     ShowCreateNew = false
                     Saving = false}, Cmd.none
                     
    | DeleteKey(container,key,etag) ->
        { model with Saving = true}, 
            Cmd.OfAsync.result (async {
               let! response =
                   fetch $"/kv/{container}/{key}" 
                    [
                       requestHeaders [IfMatch etag]
                       RequestProperties.Method HttpMethod.DELETE
                   ]
                   |> Async.AwaitPromise
               return Saved
                     })
    | DeleteContainer(container) ->
        { model with Saving = true}, 
            Cmd.OfAsync.result (async {
               let! response =
                   fetch $"/kv/{container}" 
                    [
                       RequestProperties.Method HttpMethod.DELETE
                   ]
                   |> Async.AwaitPromise
               return Saved
                     })
    | Deleted ->
        { model with Saving = false }, Cmd.none
    | Edit edit ->
        { model with Editing = Editing edit }, Cmd.none
    | EditChanged text ->
        match model.Editing with 
        | Editing edit ->
            { model with Editing = Editing { edit with Value = text}}, Cmd.none
        | _ -> model, Cmd.none

    | CancelEdit ->
        { model with Editing = NoEdit}, Cmd.none
    | SaveEdit ->
        match model.Editing with
        | Editing edit ->
            { model with Editing = Editing { edit with Editing.Saving = true}}, Cmd.OfAsync.result (async {
                let! response = 
                    fetch $"/kv/{edit.Key}"
                        [ requestHeaders [ IfMatch edit.ETag]
                          RequestProperties.Method HttpMethod.PUT
                          RequestProperties.Body (U3.Case3 edit.Value)]
                    |> Async.AwaitPromise
                return EditSaved            })
        | _ -> model, Cmd.none
    | EditSaved ->
        { model with Editing = NoEdit }, Cmd.none
    | ChangeEventView view ->
        { model with EventView = view }, Cmd.none
    | Server (Shared.KeyChanged (containerKey, key,value)) ->
        { model with 
            Keys =  
                let container =
                    Map.tryFind containerKey model.Keys
                    |> Option.defaultValue Map.empty
               
                Map.add containerKey (Map.add key value container) model.Keys

        }, Cmd.none
    | Server (Shared.KeyDeleted (containerKey, key)) ->
        { model with 
            Keys =
                model.Keys
                |> Map.change containerKey (function
                    | None -> None
                    | Some container -> 
                        Some (Map.remove key container))  
        }, Cmd.none
    | Server (Shared.ContainerDeleted containerKey) ->
        { model with 
            Keys =
                model.Keys
                |> Map.remove containerKey
        }, Cmd.none
    | Server (Shared.KeysLoaded keys) ->
       { model with
            Keys = 
                keys
                |> List.groupBy (fun (containerKey,_,_) -> containerKey)
                |> List.map(fun (containerKey, entries) -> 
                    containerKey, entries |> List.map(fun (_,k,v) -> k,v) |> Map.ofList
                )
                |> Map.ofList }, Cmd.none
    | Server (Shared.StreamUpdated stream ) ->
        { model with
            Events =
                model.Events @
                 [ for e in stream -> e.StreamId, e.EventNumber, e.EventType, e.EventData ]
        }, Cmd.none
    | Server (Shared.StreamLoaded events ) ->
        { model with
            Events = events
                      |> Seq.map (fun s -> s.StreamId, s.EventNumber, s.EventType, s.EventData)
                      |> Seq.toList
        }, Cmd.none



        
let view model dispatch =
    Html.div [
        //prop.style [style.overflow.hidden]
        prop.children [

            Bulma.navbar [
                Bulma.color.isPrimary
                prop.children [
                    Bulma.navbarBrand.div [
                        prop.style [ 
                            style.width (length.percent 100)
                            style.display.block
                            style.verticalAlign.top
                        ]

                        prop.children [
                            Bulma.navbarItem.div [
                                prop.style [
                                    style.alignItems.stretch
                                ]
                                prop.children [
                                    Html.div [
                                        prop.text "eskv"

                                        prop.style [ style.fontSize (length.em 3)
                                                     style.fontWeight.bold
                                                     style.fontStyle.italic
                                                     ]
                                    ]
                                    Html.div [
                                        prop.children [
                                            Html.div "in memory event stream / key value store "
                                            Html.div " - for learning purpose."
                                        ]
                                        prop.style [
                                            style.fontStyle.italic
                                            style.display.flex
                                            style.flexDirection.row
                                            style.flexWrap.wrap
                                            style.overflow.hidden
                                            //style.position.relative
                                            style.marginLeft (length.pt -59)
                                            style.marginTop (length.pt 39)
                                            style.lineHeight (length.em 1)
                                            
                                            style.width (length.calc "100%")

                                        ]
                                    ]
                                ]
                            ]
                        ]
                    ]
                ]
            ]

            Bulma.panel [
                Bulma.panelHeading [ 
                    Html.div [
                        prop.style [ 
                            style.display.flex
                            style.flexDirection.row
                            
                            ]
                        
                        prop.children [
                            Html.div [
                                prop.style [
                                    style.flexGrow 1
                                    style.textAlign.center
                                ]
                                prop.text "Entries"
                            ]
                            Html.a [
                                prop.className "icon-text"
                                Bulma.size.isSize6
                                prop.style [ 
                                    style.flexGrow 0
                                ]

                                prop.children [
                                    Bulma.icon [
                                        
                                        Html.i [ 
                                            if model.ShowCreateNew then
                                                prop.className "fa fa-minus"
                                            else
                                                prop.className "fa fa-plus"
                                        ]
                                    ]
                                    Html.text "New"
                                ]

                                prop.onClick (fun _ ->
                                    if model.ShowCreateNew then
                                        dispatch CloseNew
                                    else
                                        dispatch OpenNew)  
                            ]
                        ]

                    ]
                ]

                table.fixed' [
                    if model.ShowCreateNew || not (Map.isEmpty model.Keys) then
                        Html.tr [
                            Html.th [
                                prop.text "Key"
                                prop.style [ style.width (length.calc("30% - 5em")) ]
                            ]
                            Html.th [
                                prop.text "Value"
                                prop.style [ style.width (length.calc("70% - 5em")) ]
                            ]
                            Html.th [
                                prop.style [ style.width (length.em 5) ]
                            ]
                        ]

                    if model.ShowCreateNew then
                        Html.tr [
                            Html.td [ 
                                Bulma.input.text [
                                    prop.disabled model.Saving
                                    prop.value model.NewKey
                                    prop.onChange (dispatch << ChangeNewKey)
                                    prop.onKeyDown(fun e -> 
                                        match e.key with
                                        | "Enter" ->
                                            dispatch CreateNew
                                        | "Escape" ->
                                            dispatch CloseNew
                                        | _ -> ()
                                        )
                                    prop.placeholder "container/key"
                                ]
                            ]
                            Html.td [ 
                                Bulma.input.text [
                                    prop.disabled model.Saving
                                    prop.value model.NewValue
                                    prop.onChange (dispatch << ChangeNewValue)
                                    prop.onKeyDown(fun e -> 
                                        match e.key with
                                        | "Enter" ->
                                            dispatch CreateNew
                                        | "Escape" ->
                                            dispatch CloseNew
                                        | _ -> ()
                                         )
                                ]
                            ]

                            Html.td [
                                Bulma.column.isNarrow
                                prop.children [
                                        if model.NewKey = "" then
                                            Html.a [
                                                prop.children [
                                                    Bulma.icon [
                                                        prop.children [
                                                            Html.i [ prop.className "fas fa-exclamation-triangle" ]
                                                        ]

                                                        Bulma.color.hasTextDanger
                                                    ]
                                                    
                                                ]
                                                Bulma.tooltip.text "You should provide a key"
                                                Bulma.tooltip.hasTooltipDanger
                                                prop.className "has-tooltip-danger"
                                                prop.disabled true
                                            ]

                                        else
                                            Html.a [

                                                prop.disabled model.Saving

                                                prop.children [
                                                    Bulma.icon [
                                                        Html.i [ prop.className "fas fa-plus" ]
                                                    ]
                                                ]
                                                prop.onClick (fun _ -> dispatch CreateNew )
                                            ]

                                        Html.a [
                                            prop.disabled model.Saving
                                            prop.children [
                                                Bulma.icon [
                                                    Html.i [ prop.className "fas fa-times" ]
                                                ]
                                            ]
                                            prop.onClick (fun _ -> dispatch CloseNew )
                                        ]

                                ]


                            ]
                        ]

                    if not model.ShowCreateNew && Map.isEmpty model.Keys then
                            Html.tr [
                                Html.td [
                                    prop.colSpan 3
                                    prop.children [
                                        Bulma.block [
                                            Bulma.color.hasTextGreyLight
                                            Bulma.text.hasTextCentered
                                            Bulma.text.isItalic
                                            prop.className "is-justify-content-center"
                                            prop.text "no entry yet..."
                                        ]
                                    ]
                                ]
                            ]
                    else
                        for container, keys in Map.toSeq model.Keys do
                            Html.tr [
                                prop.style [
                                    //style.borderBottom(length.px 1, borderStyle.solid, "#dddddd")
                                    style.borderTop(length.px 1, borderStyle.solid, "#dddddd")
                                    style.backgroundColor "#f3f3f3"
                                ]
                                prop.children [
                                    Html.td [
                                        prop.style [ 
                                            style.paddingLeft (length.em 0.75)
                                            style.paddingTop (length.em 0.25)
                                            style.paddingBottom (length.em 0.25)
                                            style.paddingRight (length.em 0.75)
                                        ] 
                                        Bulma.text.hasTextWeightSemibold
                                        
                                        prop.colSpan 2
                                        prop.text (container+"/")

                                    ]
                                    Html.td [
                                        Bulma.column.isNarrow
                                        prop.children [
                                            Html.a [
                                                prop.children [
                                                    Bulma.icon [
                                                        Html.i [ prop.className "fas fa-trash" ]
                                                    ]
                                                ]
                                                prop.onClick (fun _ -> dispatch (DeleteContainer (container)) )
                                            ]
                                        
                                        ]
                                   ]
                                ]
                            ]
                            for key,(v,etag) in Map.toSeq keys do
                                Html.tr [
                                    table.tdEllipsis [
                                                prop.className "key"
                                                prop.text key
                                    ]

                                    table.tdEllipsis [
                                        match model.Editing with
                                        | Editing {Key = k; Value= v; Saving = saving} when k = key ->
                                            prop.children [
                                                Bulma.input.text [
                                                    prop.disabled saving
                                                    prop.value v
                                                    prop.onChange (dispatch << EditChanged)
                                                    prop.onKeyDown(fun e -> 
                                                        match e.key with
                                                        | "Enter" ->
                                                            dispatch SaveEdit
                                                        | "Escape" ->
                                                            dispatch CancelEdit
                                                        | _ -> ()
                                                          )
                                                ]
                                            ]

                                        | _ ->
                                            prop.onClick (fun _ -> dispatch (Edit { Container = container; Key = key; Value = v; ETag = etag; Saving = false}))
                                            prop.text v
                                            prop.className "editable"
                                            
                                        
                                    ]
                                    Html.td [
                                        Bulma.column.isNarrow
                                        prop.children [
                                            match model.Editing with
                                            | Editing { Key = k; Saving = saving; ETag = oldEtag} when k = key ->
                                                if oldEtag <> etag then
                                                    Html.a [
                                                        prop.children [
                                                            Bulma.icon [
                                                                prop.children [
                                                                    Html.i [ prop.className "fas fa-exclamation-triangle" ]
                                                                ]

                                                                Bulma.color.hasTextDanger
                                                            ]
                                                            
                                                        ]
                                                        Bulma.tooltip.text "Value has changed"
                                                        Bulma.tooltip.hasTooltipDanger
                                                        prop.className "has-tooltip-danger"
                                                        prop.disabled true
                                                    ]

                                                else
                                                    Html.a [

                                                        prop.disabled (saving)

                                                        prop.children [
                                                            Bulma.icon [
                                                                Html.i [ prop.className "fas fa-check" ]
                                                            ]
                                                        ]
                                                        prop.onClick (fun _ -> dispatch SaveEdit )
                                                    ]

                                                Html.a [
                                                    prop.disabled saving
                                                    prop.children [
                                                        Bulma.icon [
                                                            Html.i [ prop.className "fas fa-times" ]
                                                        ]
                                                    ]
                                                    prop.onClick (fun _ -> dispatch CancelEdit )
                                                ]
                                            | _ ->
                                                Html.a [
                                                    prop.children [
                                                        Bulma.icon [
                                                            Html.i [ prop.className "fas fa-trash" ]
                                                        ]
                                                    ]
                                                    prop.onClick (fun _ -> dispatch (DeleteKey (container,key,etag)) )
                                                ]
                                                Html.a [
                                                    prop.children [
                                                        Bulma.icon [
                                                            
                                                        ]
                                                    ]
                                                ]


                                        ]
                            ]
                        ]
                ]
            ]
            Bulma.panel [
                prop.children [
                    Bulma.panelHeading [
                        Bulma.text.hasTextCentered
                        match model.EventView with
                        | All -> prop.text "Streams - All"
                        | Stream stream -> 
                            prop.style [
                                style.display.flex
                                style.flexDirection.row
                            ]
                            prop.children [
                                Html.span [
                                    prop.style [
                                        style.flexGrow 1
                                        style.textAlign.center
                                        style.overflow.hidden
                                        style.textOverflow.ellipsis
                                        style.whitespace.nowrap
                                    ]
                                    prop.text $"Stream - {stream}"
                                ]
                                Bulma.delete [ 
                                    prop.style [
                                        style.flexGrow 0
                                    ]
                                    prop.onClick (fun _ -> dispatch (ChangeEventView All))
                                ]
                            ]

                        
                    ]

                    if List.isEmpty model.Events then
                        

                        Bulma.panelBlock.div [
                            
                            Bulma.color.hasTextGreyLight
                            Bulma.text.hasTextCentered
                            Bulma.text.isItalic
                            prop.className "is-justify-content-center"
                            prop.text "no event yet..."
                        ]
                    else
                    
                        table.fixed' [
                            match model.EventView with
                            | All ->
                                Html.tr [
                                    Html.th [
                                        prop.style [ style.width (length.calc "30% - 5em") ]
                                        prop.text "Stream" 
                                    ]
                                    Html.th [
                                        prop.style [ style.width (length.em 5) ]
                                        prop.text "Event#"
                                    ]
                                    Html.th [
                                        prop.style [ style.width (length.calc "40% - 5em") ]
                                        prop.text "Type"
                                    ]
                                    Html.th [ 
                                        prop.style [ style.width (length.calc "30% - 5em") ]
                                        prop.text "Data" 
                                    ]
                                ]
                                for streamid, eventNumber, eventType, eventData in List.rev model.Events do
                                    Html.tr [
                                        table.tdEllipsis.a [
                                            prop.className "is-cell"
                                            prop.text streamid
                                            prop.onClick (fun _ -> dispatch (ChangeEventView( Stream streamid)))
                                        ]
                                        table.tdEllipsis [ 
                                            prop.text eventNumber
                                            prop.style [ 
                                                style.textAlign.right
                                                style.paddingRight (length.em 1)
                                            ]
                                        ]
                                        table.tdEllipsis [
                                            prop.className "is-cell"
                                            prop.text eventType
                                        ]
                                        table.tdEllipsis [
                                            prop.className "is-cell"
                                            prop.text eventData
                                        ]
                                    ]
                            | Stream stream ->
                                Html.tr [
                                    Html.th [ 
                                        prop.style [ style.width (length.em 5) ]
                                        prop.text "Event#"
                                    ]
                                    Html.th [
                                        prop.style [ style.width (length.calc "65% - 5em") ]
                                        prop.text "Type"
                                    ]
                                    Html.th [
                                        prop.style [ style.width (length.calc "35% - 5em") ]
                                        prop.text "Data"
                                    ]
                                ]
                                for _, eventNumber, eventType, eventData in model.Events |> List.filter (fun (id,_,_,_) -> id = stream) |> List.rev do
                                    Html.tr [
                                        Html.td [ 
                                            prop.text eventNumber
                                            prop.style [ 
                                                style.textAlign.right
                                                style.paddingRight (length.em 1)
                                            ]
                                        ]
                                        table.tdEllipsis [
                                            prop.className "is-cell"
                                            prop.text eventType
                                        ]
                                        table.tdEllipsis [
                                            prop.className "is-cell"
                                            prop.text eventData
                                        ]
                                    ]


                    ]

                ]



            ]
            Bulma.footer [
                Bulma.content [
                    Bulma.text.hasTextCentered
                    prop.children [
                        Html.a [
                            prop.style [
                                style.fontStyle.italic
                            ]
                            Bulma.color.hasTextGrey
                           
                            prop.text "// thinkbeforecoding"
                            prop.href "https://thinkbeforecoding.com"
                        ]
                    ]
                ]
            ]
        ]
    ]

#if DEBUG
open Elmish.HMR
#endif

let ws model =
    let sub dispatch =
        let loc = window.location
        let ws = WebSocket.Create($"ws://{loc.hostname}:{loc.port}/ws")
        ws.onmessage <- ( fun e ->
        
         match Thoth.Json.Decode.Auto.fromString<ServerCmd>(string e.data)  with
         | Ok msg -> dispatch (Server msg)
         | Error e -> console.error(e)
        )

    Cmd.ofSub sub


Program.mkProgram init update view
|> Program.withReactBatched "elmish-app"
|> Program.withSubscription ws
|> Program.run

