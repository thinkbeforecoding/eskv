module Shared

type Key = string
type ETag = string

type StreamData = 
    { Id: string
      Events: (string*string) list
    }

type ServerCmd =
    | KeyChanged of Key*(string*ETag)
    | KeyDeleted of Key
    | StreamUpdated of StreamData
    | StreamLoaded of StreamData list
    | KeysLoaded of (Key*(string*ETag)) list
