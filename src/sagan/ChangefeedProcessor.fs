module Sagan.ChangefeedProcessor

open System
open System.Linq

open Microsoft.Azure.Documents
open Microsoft.Azure.Documents.Client

open FSharp.Control


type CosmosEndpoint = {
  uri : Uri
  authKey : string
  databaseName : string
  collectionName : string
}

type PartitionPosition = {
  PartitionId : string
  RangeMin : int64
  RangeMax : int64
  LastLSN : int64
}

type ChangefeedPosition = PartitionPosition list

/// ChangefeedProcessor configuration
type Config = {
  /// MaxItemCount fetched from a partition per batch
  BatchSize : int

  /// Interval between invocations of `progressHandler`
  ProgressInterval : TimeSpan

  /// Position in the Changefeed to begin processing from
  StartingPosition : StartingPosition

  /// Position in the Changefeed to stop processing at
  StoppingPosition : ChangefeedPosition option
}

/// ChangefeedProcessor starting position in the DocDB changefeed
and StartingPosition =
  | Beginning
  | ChangefeedPosition of ChangefeedPosition


type private State = {
  /// The client used to communicate with DocumentDB
  client : DocumentClient

  /// URI of the collection being processed
  collectionUri : Uri
}



[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PartitionPosition =
  /// Returns true if the range of the second argument, y, is fully contained within the first one.
  let inline fstCoversSnd (x:PartitionPosition) (y:PartitionPosition) =
    (x.RangeMin <= y.RangeMin) && (x.RangeMax >= y.RangeMax)

  /// Converts a cosmos range string from hex to int64
  let rangeToInt64 str =
    match Int64.TryParse(str, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture) with
    | true, 255L when str.ToLower() = "ff" -> Int64.MaxValue    // max for last partition
    | true, i -> i
    | false, _ when str = "" -> 0L
    | false, _ -> 0L  // NOTE: I am not sure if this should be the default or if we should return an option instead

  /// Converts a cosmos logical sequence number (LSN) from string to int64
  let lsnToInt64 str =
    match Int64.TryParse str with
    | true, i -> i
    | false, _ -> 0L



[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ChangeFeedPosition =
  let fstSucceedsSnd (cfp1:ChangefeedPosition) (cfp2:ChangefeedPosition) =
    let successionViolationExists =
      seq { // test range covers
        for x in cfp1 do
          yield cfp2 |> Seq.exists (fun y -> (PartitionPosition.fstCoversSnd x y) && (x.LastLSN > y.LastLSN))
      }
      |> Seq.exists id // find any succession violations
    not successionViolationExists

  let tryPickLatest (cfp1:ChangefeedPosition) (cfp2:ChangefeedPosition) =
    if fstSucceedsSnd cfp1 cfp2 then Some cfp1
    elif fstSucceedsSnd cfp2 cfp1 then Some cfp2
    else None

  let tryGetPartitionById id (cfp:ChangefeedPosition) =
    cfp |> List.tryFind (fun pp -> pp.PartitionId = id)






/// Returns the current set of partitions in docdb changefeed
let private getPartitions (st:State) = async {
  // TODO: set FeedOptions properly. Needed for resuming from a changefeed position
  let! response = st.client.ReadPartitionKeyRangeFeedAsync st.collectionUri |> Async.AwaitTaskCorrect
  return response.ToArray()
}


/// Reads a partition of the changefeed and returns an AsyncSeq<Document[] * LSN>
///   - Document[] is a batch of documents read from changefeed
///   - LSN is the last logical sequence number in the batch.
let rec private readPartition (config:Config) (st:State) (pkr:PartitionKeyRange) =
  let continuationToken : string =
    match config.StartingPosition with
    | Beginning -> null
    | ChangefeedPosition cfp ->
      cfp
      |> List.tryPick (function pp when pp.PartitionId = pkr.Id -> Some(string pp.LastLSN) | _ -> None)
      |> Option.getValueOr null   // docdb starts at the the beginning of a partition if null
  let cfo =
    ChangeFeedOptions(
      PartitionKeyRangeId = pkr.Id,
      MaxItemCount = Nullable config.BatchSize,
      StartFromBeginning = true,   // TODO: double check that this is ignored by docdb if RequestContinuation is set
      RequestContinuation = continuationToken)
  let query = st.client.CreateDocumentChangeFeedQuery(st.collectionUri, cfo)

  let rec readPartition (query:Linq.IDocumentQuery<Document>) (pkr:PartitionKeyRange) = asyncSeq {
    let! results = query.ExecuteNextAsync<Document>() |> Async.AwaitTask
    let pp : PartitionPosition = {
      PartitionId = pkr.Id
      RangeMin = pkr.GetPropertyValue "minInclusive" |> PartitionPosition.rangeToInt64
      RangeMax = pkr.GetPropertyValue "maxExclusive" |> PartitionPosition.rangeToInt64
      LastLSN = results.ResponseContinuation.Replace("\"", "") |> PartitionPosition.lsnToInt64
    }
    yield (results.ToArray(), pp)
    if query.HasMoreResults then
      match config.StoppingPosition with
      | None ->
        yield! readPartition query pkr
      | Some cfp ->
        let cont =
          cfp
          |> ChangeFeedPosition.tryGetPartitionById pkr.Id  // TODO: switch to finding partition by range rather than id (in case of splits)
          |> Option.map (fun stoppingPosition -> stoppingPosition.LastLSN >= pp.LastLSN)  // TODO: this can stop after the stop position, but this is ok for now. Fix later.
          |> Option.getValueOr true   // if this partition is not specified in the stopping position, then we will treat it as a no stopping position
        if cont then yield! readPartition query pkr
        else ()
    else ()

  }
  readPartition query pkr


/// Returns an async computation that runs a concurrent (per-docdb-partition) changefeed processor.
/// - `handle`: is an asynchronous function that takes a batch of documents and returns a result
/// - `progressHandler`: is an asynchronous function ('a list * ChangefeedPosition) -> Async<unit>
///    that is called periodically with a list of outputs that were produced by the handle function since the
///    last invocation and the current position of the changefeedprocessor.
let go (cosmos:CosmosEndpoint) (config:Config) handle progressHandler = async {
  use client = new DocumentClient(cosmos.uri, cosmos.authKey)
  let state = {
    client = client
    collectionUri = UriFactory.CreateDocumentCollectionUri(cosmos.databaseName, cosmos.collectionName)
  }

  // updates the given partition position in the given changefeed position
  let updateChangefeedPosition (cfp:ChangefeedPosition) (pp:PartitionPosition) =
    cfp
    |> List.choose (function | {PartitionId=pId} when pId=pp.PartitionId -> None | p -> Some p)
    |> List.cons pp

  // updates changefeed position and add the new element to the list of outputs
  let accumPartitionsPositions (outputs:'a list, cfp:ChangefeedPosition) (handlerOutput: 'a, pp:PartitionPosition) =
    (handlerOutput::outputs) , (updateChangefeedPosition cfp pp)

  // converts a buffered list of handler output to a flat list of outputs and an updated changefeed position
  let flatten (x: ('a list * ChangefeedPosition) []) : 'a list * ChangefeedPosition =
    let flattenOutputs os =
      Seq.collect id os
      |> Seq.toList
      |> List.rev
    let flattenPartitionPositions (cfps:ChangefeedPosition[]) = cfps |> Array.tryLast |> Option.getValueOr []
    x
    |> Array.unzip
    |> mapPair flattenOutputs flattenPartitionPositions


  // used to accumulate the output of all the user handle functions
  let progressReactor = Reactor.mk

  // wrap the document handler so that it takes and passes out partition position
  let handle (doc, pp) = async {
    let! ret = handle doc
    (ret, pp) |> Reactor.send progressReactor
  }
  let! progressTracker =
    progressReactor
    |> Reactor.recv
    |> AsyncSeq.scan accumPartitionsPositions ([],[])
    |> AsyncSeq.bufferByTime (int config.ProgressInterval.TotalMilliseconds)
    |> AsyncSeq.iterAsync (flatten >> progressHandler)
    |> Async.StartChild


  let! partitions = getPartitions state
  let workers =
    partitions
    |> Array.map ((fun pkr -> readPartition config state pkr) >> AsyncSeq.iterAsync handle)
    |> Async.Parallel
    |> Async.Ignore

  return! Async.choose progressTracker workers
}
