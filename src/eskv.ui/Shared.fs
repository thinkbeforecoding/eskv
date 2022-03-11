module Shared

type Container = string
type Key = string
type ETag = string


type AllStreamData =
    { StreamId: string
      EventNumber: int
      EventType: string
      EventData: string
    }

type ServerCmd =
    | KeyChanged of Container * Key*(string*ETag)
    | KeyDeleted of Container * Key
    | ContainerDeleted of Container
    | StreamUpdated of AllStreamData[]
    | StreamLoaded of AllStreamData[]
    | KeysLoaded of (Container * (Key*(string*ETag)) list) list
