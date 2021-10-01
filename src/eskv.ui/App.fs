module kv.ui

open Fable.Core
open Elmish.React

open Elmish
open Fable.React
open Feliz
open Browser.Types
open Fetch
open Feliz.Bulma
open Browser
open Shared


type Editing = 
    { Key: Key
      Value: string
      ETag: ETag
      Saving: bool}
type EditState =
    | NoEdit
    | Editing of Editing

type Command =
    | ChangeNewKey of string
    | ChangeNewValue of string
    | CreateNew
    | DeleteKey of string*ETag
    | Deleted
    | Saved
    | Edit of Editing
    | EditChanged of string
    | CancelEdit
    | SaveEdit
    | EditSaved
    | Server of Shared.ServerCmd


type Model =
    { NewKey: string
      NewValue: string
      Saving: bool
      Keys: Map<string, string*ETag>
      Editing: EditState
      Streams: Map<string, (string*string) list> 
      }
let init() =
    { NewKey = ""
      NewValue = ""
      Saving = false
      Keys = Map.empty
      Editing = NoEdit
      Streams = Map.empty }, Cmd.none

let update (command: Command) (model: Model)  =
    match command with
    |ChangeNewKey txt ->
        { model with NewKey = txt }, Cmd.none
    |ChangeNewValue txt ->
        { model with NewValue = txt }, Cmd.none
    |CreateNew ->
        { model with Saving = true}, 
            Cmd.OfAsync.result (async {
               let! response =
                   fetch $"/kv/{model.NewKey}" 
                    [
                       requestHeaders [IfNoneMatch "*"]
                       RequestProperties.Method HttpMethod.PUT
                       RequestProperties.Body (U3.Case3 model.NewValue)
                   ] |> Async.AwaitPromise
               return Saved
                     })
    | Saved ->
        { model with NewKey = ""
                     NewValue = ""
                     Saving = false}, Cmd.none
                     
    | DeleteKey(key,etag) ->
        { model with Saving = true}, 
            Cmd.OfAsync.result (async {
               let! response =
                   fetch $"/kv/{key}" 
                    [
                       requestHeaders [IfMatch etag]
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
        { model with Editing = NoEdit}, Cmd.none
    | Server (Shared.KeyChanged (key,value)) ->
        { model with 
            Keys =  Map.add key value model.Keys
        }, Cmd.none
    | Server (Shared.KeyDeleted key) ->
        { model with 
            Keys =  Map.remove key model.Keys
        }, Cmd.none
    | Server (Shared.KeysLoaded keys) ->
       { model with
            Keys = Map.ofList keys}, Cmd.none
    | Server (Shared.StreamUpdated stream ) ->
        { model with
            Streams = 
                match Map.tryFind stream.Id model.Streams with
                | None -> Map.add stream.Id stream.Events model.Streams
                | Some evts -> Map.add stream.Id (evts @ stream.Events) model.Streams
        }, Cmd.none
    | Server (Shared.StreamLoaded streams ) ->
        { model with
            Streams = streams
                      |> List.map (fun s -> s.Id, s.Events)
                      |> Map.ofList
        }, Cmd.none



        
let view model dispatch =
    Html.div [

        Bulma.navbar [
            Bulma.color.isPrimary
            prop.children [
            Bulma.navbarBrand.div [
                prop.style [ 
                    style.verticalAlign.baseline
                ]

                prop.children [
                    Bulma.navbarItem.div [
                        Html.div [
                            prop.text "eskv"

                            prop.style [ style.fontSize (length.em 3)
                                         style.fontWeight.bold
                                         style.fontStyle.italic
                                         ]
                        ]
                        Html.div [
                            prop.text "in memory key value store"
                            prop.style [ style.marginTop (length.em 3)
                                         style.marginLeft (length.em -2.7)
                                         style.fontStyle.italic
                                         ]

                        ]
                    ]

                ]
                

            
            ]
        ]
        ]

        Bulma.panel [
            Bulma.panelHeading [
                prop.text "New entry"
            ]

            Bulma.panelBlock.div [
               
                Bulma.control.div [
                    Bulma.field.div [
                        Bulma.label [
                            Html.text "Key"
                        ]
                        Bulma.input.text [ 
                            prop.value model.NewKey
                            prop.disabled model.Saving
                            prop.onChange (fun (e: Event) -> dispatch (ChangeNewKey e.Value))
                        ]
                    ]
                    Bulma.field.div [
                        Bulma.label [
                            Html.text "Value"
                        ]
                        Bulma.input.text [
                            prop.value model.NewValue
                            prop.disabled model.Saving
                            prop.onChange (fun (e: Event) -> dispatch (ChangeNewValue e.Value))
                        ]
                    ]
                    Bulma.button.button [
                        prop.text "New"
                        Bulma.color.isPrimary
                        prop.disabled model.Saving
                        prop.onClick (fun _ -> 
                            dispatch CreateNew
                        )
                        
                    ]
                ]
            ]

        ]

        Html.div [
        ]

        Bulma.panel [
            Bulma.panelHeading [
                Bulma.level [
                    Bulma.levelLeft [
                        Bulma.levelItem [
                            prop.text "Entries"
                        ]   
                    ]
                ]
            ]

            if Map.isEmpty model.Keys then
                Bulma.panelBlock.div [
                    
                    Bulma.color.hasTextGreyLight
                    Bulma.text.hasTextCentered
                    Bulma.text.isItalic
                    prop.className "is-justify-content-center"
                    prop.text "no entry yet..."
                ]

            else
                for key,(v,etag) in Map.toList model.Keys do
                    Bulma.panelBlock.div [
                        Bulma.columns [
                            prop.style [ style.width (length.percent 100)
                                         style.verticalAlign.middle]
                            prop.children [
                                Bulma.column [
                                    Html.div [
                                        prop.className "key"
                                        prop.text key
                                    ]
                                ]

                                Bulma.column [
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
                                        prop.children [
                                            Html.div [
                                                prop.onClick (fun _ -> dispatch (Edit { Key = key; Value = v; ETag = etag; Saving = false}))
                                                prop.text v
                                                prop.className "editable"
                                            ]
                                        ]
                                    
                                ]
                                Bulma.column [
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
                                                prop.onClick (fun _ -> dispatch (DeleteKey (key,etag)) )
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
        ]
        Bulma.panel [
                  Bulma.panelHeading [
                      Bulma.level [
                          Bulma.levelLeft [
                              Bulma.levelItem [
                                  prop.text "Streams"
                              ]   
                          ]
                      ]
                  ]
                  for streamid, stream in Map.toList model.Streams do
                    Bulma.panelBlock.div [
                        prop.style [ style.flexDirection.column ]
                        prop.children [
                            for t,d in stream do
                                Bulma.columns [
                                    prop.style [ style.width (length.percent 100)
                                                 style.verticalAlign.middle]
                                    prop.children [
                                        Bulma.column [
                                            prop.text streamid
                                        ]
                                        Bulma.column [
                                            prop.text t
                                        ]
                                        Bulma.column [
                                            prop.text d
                                        ]
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

