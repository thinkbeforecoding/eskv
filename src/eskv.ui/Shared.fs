module Shared

type Key = string
type ETag = string


type AllStreamData =
    { StreamId: string
      EventNumber: int
      EventType: string
      EventData: string
    }

type ServerCmd =
    | KeyChanged of Key*(string*ETag)
    | KeyDeleted of Key
    | StreamUpdated of AllStreamData[]
    | StreamLoaded of AllStreamData[]
    | KeysLoaded of (Key*(string*ETag)) list
