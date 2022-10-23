module Style


open Fable.React
open Feliz

type table =

    static member fixed' xs =
        Html.table
            [ yield prop.style [ style.tableLayout.fixed'; style.width (length.percent 100) ]
              yield! xs ]

    static member fixed' xs =
        table.fixed' [ prop.children (xs: ReactElement seq) ]

    static member tdEllipsis xs =
        Html.td
            [ prop.style [ style.maxWidth 0 ]
              prop.children
                  [ Html.div
                        [ yield
                              prop.style [ style.overflow.hidden; style.textOverflow.ellipsis; style.whitespace.nowrap ]
                          yield! xs

                          ] ] ]

    static member tdEllipsis xs =
        table.tdEllipsis [ prop.children (xs: ReactElement seq) ]

    static member tdEllipsis(txt: string) = table.tdEllipsis [ prop.text txt ]

module table =
    type tdEllipsis =

        static member a xs =
            Html.td
                [ prop.style [ style.maxWidth 0 ]
                  prop.children
                      [ Html.a
                            [ yield
                                  prop.style
                                      [ style.display.block
                                        style.overflow.hidden
                                        style.textOverflow.ellipsis
                                        style.whitespace.nowrap ]
                              yield! xs

                              ] ] ]
