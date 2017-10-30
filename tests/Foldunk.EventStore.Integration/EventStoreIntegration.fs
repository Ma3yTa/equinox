﻿module Foldunk.EventStore.Integration.EventStoreIntegration

open Foldunk.EventStore
open Swensen.Unquote
open System.Threading

/// Needs an ES instance with default settings
/// TL;DR: At an elevated command prompt: choco install eventstore-oss; \ProgramData\chocolatey\bin\EventStore.ClusterNode.exe
let connectToLocalEventStoreNode () = async {
    let localhost = System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1113)
    let conn = EventStore.ClientAPI.EventStoreConnection.Create(localhost)
    do! conn.ConnectAsync() |> Async.AwaitTask
    return conn }

let defaultBatchSize = 500

let createCartServiceWithoutOptimization eventStoreConnection batchSize =
    Backend.Cart.Service(fun _ignoreCompactionEventTypeOption -> GesStreamBuilder(eventStoreConnection, batchSize).Create)

let createCartService eventStoreConnection batchSize =
    Backend.Cart.Service(fun compactionEventType -> GesStreamBuilder(eventStoreConnection, batchSize, ?compaction = Option.map CompactionStrategy.EventType compactionEventType).Create)

let createContactPreferencesServiceWithoutOptimization eventStoreConnection =
    Backend.ContactPreferences.Service(fun _ignoreWindowSize _ignoreCompactionPredicate -> GesStreamBuilder(eventStoreConnection, defaultBatchSize).Create)

let createContactPreferencesService eventStoreConnection =
    Backend.ContactPreferences.Service(fun batchSize compactionPredicate -> GesStreamBuilder(eventStoreConnection, batchSize, CompactionStrategy.Predicate compactionPredicate).Create)

#nowarn "1182" // From hereon in, we may have some 'unused' privates (the tests)

type Tests() =
    let addAndThenRemoveItems exceptTheLastOne context cartId skuId log (service: Backend.Cart.Service) count =
        service.FlowAsync(log, cartId, fun _ctx execute ->
            for i in 1..count do
                execute <| Domain.Cart.AddItem (context, skuId, i)
                if not exceptTheLastOne || i <> count then
                    execute <| Domain.Cart.RemoveItem (context, skuId) )
    let addAndThenRemoveItemsManyTimes context cartId skuId log service count =
        addAndThenRemoveItems false context cartId skuId log service count
    let addAndThenRemoveItemsManyTimesExceptTheLastOne context cartId skuId log service count =
        addAndThenRemoveItems true context cartId skuId log service count

    let createLoggerWithCapture () =
        let capture = LogCaptureBuffer()
        let subscribeLogListeners observable =
            capture.Subscribe observable |> ignore
        createLogger subscribeLogListeners, capture

    let singleSliceForward = "ReadStreamEventsForwardAsync"
    let singleBatchForward = [singleSliceForward; "LoadF"]
    let batchForwardAndAppend = singleBatchForward @ ["AppendToStreamAsync"]

    [<AutoData>]
    let ``Can roundtrip against EventStore, correctly batching the reads [without any optimizations]`` context cartId skuId = Async.RunSynchronously <| async {
        let log, capture = createLoggerWithCapture ()
        let! conn = connectToLocalEventStoreNode ()
        let batchSize = 3
        let service = createCartServiceWithoutOptimization conn batchSize

        // The command processing should trigger only a single read and a single write call
        let addRemoveCount = 6
        do! addAndThenRemoveItemsManyTimesExceptTheLastOne context cartId skuId log service addRemoveCount
        test <@ batchForwardAndAppend = capture.ExternalCalls @>

        // Restart the counting
        capture.Clear()

        // Validate basic operation; Key side effect: Log entries will be emitted to `capture`
        let! state = service.Read log cartId
        let expectedEventCount = 2 * addRemoveCount - 1
        test <@ addRemoveCount = match state with { items = [{ quantity = quantity }] } -> quantity | _ -> failwith "nope" @>

        // Need to read 4 batches to read 11 events in batches of 3
        let expectedBatches = ceil(float expectedEventCount/float batchSize) |> int
        test <@ List.replicate (expectedBatches-1) singleSliceForward @ singleBatchForward = capture.ExternalCalls @>
    }

    [<AutoData(MaxTest = 2)>]
    let ``Can roundtrip against EventStore, managing sync conflicts by retrying [without any optimizations]`` ctx initialState = Async.RunSynchronously <| async {
        let log1, capture1 = createLoggerWithCapture ()
        let! conn = connectToLocalEventStoreNode ()
        // Ensure batching is included at some point in the proceedings
        let batchSize = 3

        let context, cartId, (sku11, sku12, sku21, sku22) = ctx

        // establish base stream state
        let service1 = createCartServiceWithoutOptimization conn batchSize
        let! maybeInitialSku =
            let (streamEmpty, skuId) = initialState
            async {
                if streamEmpty then return None
                else
                    let addRemoveCount = 2
                    do! addAndThenRemoveItemsManyTimesExceptTheLastOne context cartId skuId log1 service1 addRemoveCount
                    return Some (skuId, addRemoveCount) }

        let act prepare (service : Backend.Cart.Service) log skuId count =
            service.FlowAsync(log, cartId, prepare = prepare, flow = fun _ctx execute ->
                execute <| Domain.Cart.AddItem (context, skuId, count))

        let eventWaitSet () = let e = new ManualResetEvent(false) in (Async.AwaitWaitHandle e |> Async.Ignore), async { e.Set() |> ignore }
        let w0, s0 = eventWaitSet ()
        let w1, s1 = eventWaitSet ()
        let w2, s2 = eventWaitSet ()
        let w3, s3 = eventWaitSet ()
        let w4, s4 = eventWaitSet ()
        let t1 = async {
            // Wait for other to have state, signal we have it, await conflict and handle
            let prepare = async {
                do! w0
                do! s1
                do! w2 }
            do! act prepare service1 log1 sku11 11
            // Wait for other side to load; generate conflict
            let prepare = async { do! w3 }
            do! act prepare service1 log1 sku12 12
            // Signal conflict generated
            do! s4 }
        let log2, capture2 = createLoggerWithCapture ()
        let service2 = createCartServiceWithoutOptimization conn batchSize
        let t2 = async {
            // Signal we have state, wait for other to do same, engineer conflict
            let prepare = async {
                do! s0
                do! w1 }
            do! act prepare service2 log2 sku21 21
            // Signal conflict is in place
            do! s2
            // Await our conflict
            let prepare = async {
                do! s3
                do! w4 }
            do! act prepare service2 log2 sku22 22 }
        // Act: Engineer the conflicts and applications, with logging into capture1 and capture2
        do! Async.Parallel [t1; t2] |> Async.Ignore

        // Load state
        let! result = service1.Read log1 cartId

        // Ensure correct values got persisted
        let has sku qty = result.items |> List.exists (fun { skuId = s; quantity = q } -> (sku, qty) = (s, q))
        test <@ maybeInitialSku |> Option.forall (fun (skuId, quantity) -> has skuId quantity)
                && has sku11 11 && has sku12 12
                && has sku21 21 && has sku22 22 @>
       // Intended conflicts pertained
        let hadConflict= function HasProp "conflict" (SerilogBool true) -> Some () | _ -> None
        test <@ [1; 1] = [for c in [capture1; capture2] -> c.ChooseCalls hadConflict |> List.length] @>
    }

    let singleBatchBackwards = ["ReadStreamEventsBackwardAsync"; "LoadB"]
    let batchBackwardsAndAppend = singleBatchBackwards @ ["AppendToStreamAsync"]

    [<AutoData>]
    let ``Can roundtrip against EventStore, correctly compacting to avoid redundant reads`` context skuId cartId = Async.RunSynchronously <| async {
        let log, capture = createLoggerWithCapture ()
        let! conn = connectToLocalEventStoreNode ()
        let batchSize = 10
        let service = createCartService conn batchSize

        // Trigger 10 events, then reload
        do! addAndThenRemoveItemsManyTimes context cartId skuId log service 5
        let! _ = service.Read log cartId

        // ... should see a single read as we are inside the batch threshold
        test <@ batchBackwardsAndAppend @ singleBatchBackwards = capture.ExternalCalls @>

        // Add two more, which should push it over the threshold and hence trigger inclusion of a snapshot event (but not incurr extra roundtrips)
        capture.Clear()
        do! addAndThenRemoveItemsManyTimes context cartId skuId log service 1
        test <@ batchBackwardsAndAppend = capture.ExternalCalls @>

        // While we now have 13 events, we should be able to read them with a single call
        capture.Clear()
        let! _ = service.Read log cartId
        test <@ singleBatchBackwards = capture.ExternalCalls @>

        // Add 8 more; total of 21 should not trigger snapshotting as Event Number 12 (the 13th one) is a shapshot
        capture.Clear()
        do! addAndThenRemoveItemsManyTimes context cartId skuId log service 4
        test <@ batchBackwardsAndAppend = capture.ExternalCalls @>

        // While we now have 21 events, we should be able to read them with a single call
        capture.Clear()
        let! _ = service.Read log cartId
        // ... and trigger a second snapshotting (inducing a single additional read + write)
        do! addAndThenRemoveItemsManyTimes context cartId skuId log service 1
        // and reload the 24 events with a single read
        let! _ = service.Read log cartId
        test <@ singleBatchBackwards @ batchBackwardsAndAppend @ singleBatchBackwards = capture.ExternalCalls @>
    }

    [<AutoData>]
    let ``Can correctly read and update values with window size of 1 using tautological Compaction predicate`` id value = Async.RunSynchronously <| async {
        let! eventStoreConnection = connectToLocalEventStoreNode ()
        let log, capture = createLoggerWithCapture ()
        let service = createContactPreferencesService eventStoreConnection

        let (Domain.ContactPreferences.Id email) = id
        // Feed some junk into the stream
        for i in 0..11 do
            let quickSurveysValue = i % 2 = 0
            do! service.Update log email { value with quickSurveys = quickSurveysValue }
        // Ensure there will be something to be changed by the Update below
        do! service.Update log email { value with quickSurveys = not value.quickSurveys }

        capture.Clear()
        do! service.Update log email value

        let! result = service.Read log email
        test <@ value = result @>

        test <@ batchBackwardsAndAppend @ singleBatchBackwards = capture.ExternalCalls @>
    }