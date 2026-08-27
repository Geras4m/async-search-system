# Async Search System

An asynchronous hotel search built as three .NET 8 services. A client `POST`s a destination to the
API Gateway and gets a `searchId` back immediately — nothing is searched on the request path. The
Search Service then accumulates results in the background: six batches of five hotels, one batch
every five seconds. The client polls `GET /searches/{searchId}` and watches the result list grow
from empty to thirty entries, at which point `isCompleted` flips to `true`. Completion also
publishes a `SearchCompletedEvent` to RabbitMQ, which the Notification Service consumes and logs.

A full run takes about 30 seconds from the `POST` to the completion event.

## Architecture

```text
        +-------------------+
        |      Client       |
        +---------+---------+
                  |
                  |  HTTP/JSON        POST /searches
                  |                   GET  /searches/{searchId}
                  v
        +-------------------+
        |    API Gateway    |   validation, error translation (RFC 7807)
        +---------+---------+
                  |
                  |  gRPC (HTTP/2 cleartext, h2c)
                  |  search.v1.SearchGrpcService
                  v
        +-------------------+
        |   Search Service  |   Domain / Application / Infrastructure / Api
        +---------+---------+   background execution engine
                  |
                  |  AMQP publish
                  v
        +-------------------+
        |     RabbitMQ      |   exchange  search.completed              (fanout, durable)
        +---------+---------+   queue     notification.search.completed (durable)
                  |
                  |  AMQP consume
                  v
        +-------------------+
        | Notification Svc  |   logs the SearchId of each completed search
        +-------------------+
```

Two transport constraints come straight from the specification and are enforced by the project
reference graph, not by convention:

- **The API Gateway reaches the Search Service only over gRPC.** `ApiGateway.csproj` references
  `Shared.GrpcContracts` and nothing else belonging to the Search Service; there is no HTTP client
  and no shared application code between them.
- **The Search Service reaches the Notification Service only through the broker.** Neither project
  references the other, neither is configured with the other's address, and all they share is
  `Shared.EventContracts` plus the topology constants in `Shared.Common`. There is no HTTP and no
  gRPC between them in either direction.

## Quick start

Requires Docker with the Compose plugin. Nothing else — no .NET SDK, no local RabbitMQ.

```bash
git clone <this repository>
cd da-task
docker compose up --build
```

The first `up` builds three images and waits for the broker's healthcheck (`rabbitmq-diagnostics
ping`, with a 30 second start period) before starting the Search Service and Notification Service,
so allow the stack roughly a minute to settle on a cold start. When it is ready:

| Endpoint | URL |
| --- | --- |
| API Gateway | `http://localhost:8080` |
| Gateway health probe | `http://localhost:8080/health` |
| RabbitMQ management UI | `http://localhost:15672` (`guest` / `guest`) |
| Search Service gRPC (h2c) | `http://localhost:5001` — optional, for `grpcurl` |

### Start a search

```bash
curl -s -X POST http://localhost:8080/searches \
  -H 'Content-Type: application/json' \
  -d '{"destination":"Paris"}'
```

```json
{ "searchId": "cbaf4961-10d1-42a0-b24d-111111111111" }
```

### Poll it

```bash
curl -s http://localhost:8080/searches/cbaf4961-10d1-42a0-b24d-111111111111
```

Immediately after starting, before the first batch lands:

```json
{
  "searchId": "cbaf4961-10d1-42a0-b24d-111111111111",
  "isCompleted": false,
  "results": []
}
```

Around the twelve second mark, after two batches (abbreviated to three of the ten entries):

```json
{
  "searchId": "cbaf4961-10d1-42a0-b24d-111111111111",
  "isCompleted": false,
  "results": [
    { "hotelId": "0d2f7c19-6a54-4f0b-9a3e-1c8b5d2e7f40", "name": "Hotel 1", "price": 212 },
    { "hotelId": "9c41e8b7-2d13-4a6c-8e57-3f90a1b4c6d2", "name": "Hotel 2", "price": 145 },
    { "hotelId": "5b78d3a0-4e21-49cf-b6d8-7a0c2e91f5b3", "name": "Hotel 10", "price": 388 }
  ]
}
```

After roughly thirty seconds the sixth batch has been appended and the search is final. The list
holds all thirty hotels; it is abbreviated to three entries here:

```json
{
  "searchId": "cbaf4961-10d1-42a0-b24d-111111111111",
  "isCompleted": true,
  "results": [
    { "hotelId": "0d2f7c19-6a54-4f0b-9a3e-1c8b5d2e7f40", "name": "Hotel 1", "price": 212 },
    { "hotelId": "9c41e8b7-2d13-4a6c-8e57-3f90a1b4c6d2", "name": "Hotel 2", "price": 145 },
    { "hotelId": "c6a90f31-58b2-4d7e-9013-6e4f8a2b5c17", "name": "Hotel 30", "price": 96 }
  ]
}
```

Hotel names are deterministic (`Hotel 1` through `Hotel 30`), identifiers are fresh GUIDs, and
prices are drawn from `[MinHotelPrice, MaxHotelPrice)` — 80 to 399 by default.

### Copy-pasteable polling loop

Bash, using `jq`:

```bash
SEARCH_ID=$(curl -s -X POST http://localhost:8080/searches \
  -H 'Content-Type: application/json' \
  -d '{"destination":"Paris"}' | jq -r .searchId)

echo "searchId=$SEARCH_ID"

for i in $(seq 1 8); do
  curl -s "http://localhost:8080/searches/$SEARCH_ID" \
    | jq -c '{isCompleted, results: (.results | length)}'
  sleep 5
done
```

PowerShell, no extra tooling (on Windows, plain `curl` is not curl — use this or `curl.exe`):

```powershell
$searchId = (Invoke-RestMethod -Method Post -Uri 'http://localhost:8080/searches' `
    -ContentType 'application/json' -Body '{"destination":"Paris"}').searchId

"searchId=$searchId"

do {
    $state = Invoke-RestMethod -Uri "http://localhost:8080/searches/$searchId"
    "isCompleted={0} results={1}" -f $state.isCompleted, $state.results.Count
    if (-not $state.isCompleted) { Start-Sleep -Seconds 5 }
} until ($state.isCompleted)
```

Expected output: `results` climbs 0, 5, 10, 15, 20, 25, 30 and `isCompleted` is `true` on the final
line.

## Verifying the acceptance criteria

Run the stack, then follow the logs in a second terminal while the loop above is running.

| # | Criterion | How to verify |
| --- | --- | --- |
| 1 | Environment starts with one command | `docker compose up --build`, then `docker compose ps` lists `asyncsearch-rabbitmq`, `asyncsearch-searchservice`, `asyncsearch-apigateway`, `asyncsearch-notificationservice` |
| 2 | `POST /searches` returns a `searchId` | the `curl` above returns `200` with `{ "searchId": "<guid>" }`; `docker compose logs searchservice` contains `Search created. SearchId=<guid>` |
| 3 | `GET /searches/{guid}` returns the current state | the poll returns `200` with `isCompleted` and `results` |
| 4 | Results grow every five seconds, six updates | `docker compose logs -f searchservice` shows six lines, `Batch added. SearchId=<guid> Batch=1 ResultCount=5` through `Batch=6 ResultCount=30` |
| 5 | Final response has `isCompleted: true` | last line of the polling loop, plus the log line `Search completed. SearchId=<guid>` |
| 6 | Completion event is published | `Event published. SearchId=<guid>` in the Search Service log; message activity is visible in the management UI under exchange `search.completed` |
| 7 | Notification Service logs the event | `docker compose logs -f notificationservice` shows `Search completed event received. SearchId=<guid> CompletedAtUtc=<timestamp>` |
| 8 | Transport constraints hold | see the reference-graph argument under [Architecture](#architecture): no project reference and no configured address links the Search Service to the Notification Service |

Log commands worth keeping open:

```bash
docker compose logs -f notificationservice     # the completion event, criterion 7
docker compose logs -f searchservice           # batches, completion, publish
docker compose logs -f apigateway              # one structured line per HTTP request
docker compose ps                              # container status and published ports
```

At start-up the Notification Service also logs `Consuming search completion events.
Queue=notification.search.completed Exchange=search.completed PrefetchCount=10`, which confirms the
queue and its binding exist before any search runs.

## Repository layout

```text
.
├── AsyncSearchSystem.sln
├── Directory.Build.props            # net8.0, nullable, analyzers, warnings as errors
├── Directory.Packages.props         # Central Package Management: every version lives here
├── docker-compose.yml
├── .env.example                     # optional host-port and credential overrides
├── docs/
│   └── requirements.md              # the functional specification
├── src/
│   ├── ApiGateway/                  # Endpoints, GrpcClients, Contracts, Validators, Middleware
│   ├── NotificationService/         # Consumers, Messaging (worker; no HTTP surface)
│   ├── SearchService/
│   │   ├── SearchService.Domain/            # Entities, Repositories, Exceptions
│   │   ├── SearchService.Application/       # Commands, Queries, Handlers, Validators,
│   │   │                                    #   Behaviors, Abstractions, Models, Options
│   │   ├── SearchService.Infrastructure/    # Persistence, Messaging, BackgroundJobs, Generation
│   │   └── SearchService.Api/               # Grpc, Program.cs, Dockerfile, appsettings
│   └── Shared/
│       ├── Shared.Common/           # MessagingConstants, EventSerialization
│       ├── Shared.EventContracts/   # SearchCompletedEvent
│       └── Shared.GrpcContracts/    # Protos/search.proto, DecimalValue extensions
└── tests/
    ├── UnitTests/
    └── IntegrationTests/
```

Each of the three services owns its `Dockerfile`, but all three are built with the **repository
root as the build context**, because `Directory.Build.props`, `Directory.Packages.props` and the
`src/Shared` projects live above the individual project directories and Docker cannot read outside
its context. `docker-compose.yml` already does this; to build one by hand, run from the repository
root:

```bash
docker build -f src/ApiGateway/Dockerfile -t asyncsearch/apigateway .
```

### Four projects, not four folders

The specification sketches the Search Service as four *folders* inside one project. This repository
uses four *projects* instead. The reason is enforcement: a folder is a naming convention that a
single `using` directive can quietly violate, whereas a project reference is checked by the
compiler. Clean Architecture's dependency rule — dependencies point inward, toward the domain —
becomes a build error rather than a code-review comment.

| Project | References | Consequence |
| --- | --- | --- |
| `SearchService.Domain` | nothing: no project references, no NuGet packages | the domain model cannot accidentally learn about MediatR, RabbitMQ, gRPC or ASP.NET Core |
| `SearchService.Application` | `SearchService.Domain`, `Shared.Common`, `Shared.EventContracts` | use cases depend on the domain and on abstractions they declare themselves (`ISearchRepository`, `ISearchEventsPublisher`, `IHotelResultGenerator`, `ISearchExecutionScheduler`, `IClock`) |
| `SearchService.Infrastructure` | `SearchService.Application`, `SearchService.Domain` | implementations depend on the abstractions, never the reverse; the broker client lives here and only here |
| `SearchService.Api` | `SearchService.Application`, `SearchService.Infrastructure`, `Shared.GrpcContracts` | the host is the only project allowed to know both the use cases and their implementations, because it is the composition root |

Adding a reference from `SearchService.Application` back to `SearchService.Infrastructure` produces
a circular reference the build rejects outright. That is the whole point.

The mapping onto the folder layout in the specification is one to one:

| Specification | This repository |
| --- | --- |
| `SearchService/Domain/{Entities,Repositories}` | `src/SearchService/SearchService.Domain/{Entities,Repositories}` |
| `SearchService/Application/{Commands,Queries,Handlers,Validators}` | `src/SearchService/SearchService.Application/{Commands,Queries,Handlers,Validators}` |
| `SearchService/Infrastructure/{Persistence,Messaging,BackgroundJobs}` | `src/SearchService/SearchService.Infrastructure/{Persistence,Messaging,BackgroundJobs}` |
| `SearchService/Grpc` and `SearchService/Program.cs` | `src/SearchService/SearchService.Api/Grpc` and `src/SearchService/SearchService.Api/Program.cs` |
| `ApiGateway/{Endpoints,Services,GrpcClients}` | `src/ApiGateway/{Endpoints,GrpcClients,Contracts,Validators,Middleware}` |
| `NotificationService/{Messaging,Consumers}` | `src/NotificationService/{Messaging,Consumers}` |
| `Shared/{GrpcContracts,EventContracts,Common}` | `src/Shared/{Shared.GrpcContracts,Shared.EventContracts,Shared.Common}` |
| `Tests/{UnitTests,IntegrationTests}` | `tests/{UnitTests,IntegrationTests}` |

## Design decisions

### CQRS with MediatR, and two pipeline behaviours

Every state change and every read goes through a MediatR request: `StartSearchCommand`,
`AppendSearchBatchCommand`, `CompleteSearchCommand`, `GetSearchResultsQuery`. The gRPC service is
then a pure transport adapter — it maps protobuf to a request, dispatches it, and maps the outcome
back to protobuf or to a status code. The same handlers are reachable from the background engine
without duplicating a line of workflow logic, which is exactly what the specification's processing
algorithm asks for.

Two behaviours wrap the pipeline, and registration order matters because MediatR nests them
outermost first:

1. `ValidationBehavior` runs every registered FluentValidation validator and throws before the
   handler is reached. Handlers therefore never open with a block of argument checks, and a request
   with no registered validator passes straight through, so adding one later needs no wiring change.
2. `LoggingBehavior` sits inside it and records the request name and elapsed milliseconds at
   `Debug`, gated on `IsEnabled` so neither the log records nor the timing cost anything when debug
   logging is off. Because it is inside validation, the duration it reports is the handler's, not
   validation's.

### Repository abstraction, in-memory implementation, snapshot on read

`ISearchRepository` lives in the Domain layer; `InMemorySearchRepository` — a
`ConcurrentDictionary<Guid, Search>` — lives in Infrastructure and is registered as a singleton
(a scoped registration would hand every request its own empty dictionary, and searches would appear
to vanish between the `POST` and the first `GET`). Replacing it with a database implementation
touches that one registration and nothing else.

The subtle part is concurrency. The background engine appends a batch while gRPC callers poll the
same search. `Search` is deliberately not thread-safe: it wraps a plain `List<HotelResult>`. If the
dictionary handed out the stored instance, a reader could enumerate that list mid-append — a torn
read that surfaces either as an `InvalidOperationException` from the enumerator or as a result set
containing half a batch. So the repository stores a snapshot on write and returns a snapshot on
read. Each stored value is unreachable except through the dictionary and is never mutated after
publication; each returned value belongs solely to its caller. Writers replace a reference, which is
atomic, instead of mutating shared state, so a reader sees the state either before a batch or after
it, never in between — and no lock is involved. `HotelResult` is an immutable record, so copying the
list is enough to make the copy independent.

`UpdateAsync` uses a `TryUpdate` retry loop rather than an indexer assignment, so an update racing a
removal fails with `SearchNotFoundException` instead of resurrecting a deleted search.

### Money as a protobuf `DecimalValue`

Protobuf has no `decimal` scalar, and `double` silently corrupts monetary values. `search.proto`
therefore carries prices as a `DecimalValue` message of `units` (int64) plus `nanos` (sfixed32),
where `value = units + nanos / 1e9` and both fields carry the same sign. `Shared.GrpcContracts`
supplies `decimal.ToDecimalValue()` and `DecimalValue?.ToDecimal()`, so `decimal` survives end to
end: the domain holds `decimal`, the wire holds an exact integer pair, and the gateway's JSON holds
`decimal` again. Prices in this demo happen to be whole numbers, which is precisely the situation in
which a lossy representation goes unnoticed until it matters.

### A durable fanout exchange and a durable queue, declared by both sides

The Search Service publishes persistent messages to the `search.completed` fanout exchange; the
Notification Service consumes from the durable `notification.search.completed` queue bound to it.
Fanout rather than direct, because a completion is a broadcast fact — a second subscriber can bind
its own queue without the publisher changing a line.

Both services declare the full topology (exchange, queue, binding) themselves. AMQP declarations are
idempotent and the two declarations are identical, so whichever service wins the race creates it and
the other agrees. That is what makes container start-up order irrelevant: the Search Service can
publish before the Notification Service has ever run, and the Notification Service can bind before
the first event exists. Durable plus persistent means an event the broker has accepted survives a
broker restart. Consumption is manual-ack with `prefetchCount: 10`; malformed payloads are nacked
*without* requeue, because a poison message never becomes valid on a retry, while a transient
handling failure is requeued.

Completion is persisted *before* the event is published, so a broker outage can never leave a search
stuck in the running state — the flag clients poll for is the source of truth, and the event only
announces it.

### Source-generated logging

Every log statement is a `[LoggerMessage]` partial method, so the template, event id and level are
compiled into a strongly typed call: no boxing, no formatting work on a disabled level, and no
interpolated string smuggled into a message template. Templates stay stable and structured, which is
what makes the acceptance criteria greppable — `Search created. SearchId={SearchId}`,
`Batch added. SearchId={SearchId} Batch={BatchNumber} ResultCount={ResultCount}`,
`Search completed. SearchId={SearchId}`, `Event published. SearchId={SearchId}` and
`Search completed event received. SearchId={SearchId} CompletedAtUtc={CompletedAtUtc}`. Serilog
writes them to the console in all three services.

### Configurable `BatchCount` and `BatchInterval`

`SearchExecutionOptions` binds the `Search` section and defaults to exactly what the specification
mandates: `BatchCount = 6`, `HotelsPerBatch = 5`, `BatchInterval = 00:00:05`. They are options
rather than constants so that automated tests can compress a 30 second workflow into a fraction of a
second — set `Search__BatchInterval=00:00:00.05` and the same six-batch behaviour is observable in
under a second, without changing production behaviour or the code under test. The same knob makes a
live demo as fast or as slow as you want it. Both option types are validated with data annotations
and `ValidateOnStart`, so a mistyped interval or an out-of-range port stops the host immediately
with a precise message instead of failing halfway through a search.

## Configuration

Each service reads `appsettings.json` (container defaults), then `appsettings.{Environment}.json`
(`Development` points everything at `localhost`), then environment variables. Later sources win.

**The `__` convention.** .NET's environment variable configuration provider maps a double underscore
onto the `:` section separator, because `:` is not usable in an environment variable name on every
platform. `RabbitMq__Host` therefore sets the configuration key `RabbitMq:Host`, which is the `Host`
property of the `RabbitMq` section in `appsettings.json`. Deeper nesting works the same way.

| Variable | Service | Default | Purpose |
| --- | --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | API Gateway, Search Service | `Production` | selects `appsettings.{Environment}.json`; `Development` also enables Swagger UI on the gateway and detailed gRPC errors |
| `DOTNET_ENVIRONMENT` | Notification Service | `Production` | the same role — a worker runs on the generic host, which reads this name rather than the ASP.NET Core one |
| `ASPNETCORE_HTTP_PORTS` | API Gateway, Search Service | `8080` | in-container Kestrel listen port; set in both Dockerfiles and again in Compose |
| `Grpc__SearchService` | API Gateway | `http://searchservice:8080` (`http://localhost:5001` under `Development`) | address of the gRPC backend; the gateway fails fast at start-up if it is missing |
| `RabbitMq__Host` | Search Service, Notification Service | `rabbitmq` (`localhost` under `Development`) | broker host name |
| `RabbitMq__Port` | Search Service, Notification Service | `5672` | broker AMQP port |
| `RabbitMq__UserName` | Search Service, Notification Service | `guest` | broker user; Compose passes `RABBITMQ_DEFAULT_USER` |
| `RabbitMq__Password` | Search Service, Notification Service | `guest` | broker password; Compose passes `RABBITMQ_DEFAULT_PASS` |
| `RabbitMq__VirtualHost` | Search Service, Notification Service | `/` | broker virtual host |
| `RabbitMq__MaxConnectRetries` | Search Service, Notification Service | `10` | connection attempts before the connection is reported as failed |
| `RabbitMq__RetryDelay` | Search Service, Notification Service | `00:00:02` | delay between connection attempts |
| `Search__BatchCount` | Search Service | `6` | batches appended before a search is marked complete |
| `Search__HotelsPerBatch` | Search Service | `5` | hotels generated per batch |
| `Search__BatchInterval` | Search Service | `00:00:05` | delay before each batch, as a `TimeSpan` |
| `Search__MaxConcurrentSearches` | Search Service | `64` | searches the engine executes concurrently |
| `Search__MinHotelPrice` | Search Service | `80` | lowest generated price, inclusive |
| `Search__MaxHotelPrice` | Search Service | `400` | highest generated price, exclusive |

Compose reads a further set from an optional `.env` file next to `docker-compose.yml`. Copying
`.env.example` is optional — every reference in the Compose file carries a default — and these
change only what is published on the host, never how containers address each other.

| Variable | Default | Effect |
| --- | --- | --- |
| `RABBITMQ_DEFAULT_USER` | `guest` | seeds the broker's user *and* is handed to both services as `RabbitMq__UserName` |
| `RABBITMQ_DEFAULT_PASS` | `guest` | the matching password, handed over as `RabbitMq__Password` |
| `APIGATEWAY_HTTP_PORT` | `8080` | host port for the gateway |
| `SEARCHSERVICE_HTTP_PORT` | `5001` | host port for the Search Service's gRPC endpoint (convenience only) |
| `RABBITMQ_AMQP_PORT` | `5672` | host port for AMQP |
| `RABBITMQ_MANAGEMENT_PORT` | `15672` | host port for the management UI |

The broker reads its credentials only when it initialises an empty data directory. If you change
them after the stack has run once, reset the volume as well with `docker compose down -v`.

## Building and testing without Docker

### Prerequisites, including one that bites

The solution targets **net8.0**. Building it needs only a reasonably current .NET SDK — a .NET 9 or
.NET 10 SDK compiles `net8.0` correctly. *Running* anything that touches an ASP.NET Core host
additionally needs the **ASP.NET Core 8 runtime**, and a newer runtime is not substituted
automatically: the launch fails with `The framework 'Microsoft.AspNetCore.App', version '8.0.0' was
not found`.

That covers more than the two web hosts. An ASP.NET Core project propagates its
`Microsoft.AspNetCore.App` framework reference to every project that references it, so **both** test
projects inherit it — `IntegrationTests` from the two hosts it drives, and `UnitTests` from the API
Gateway project it references. Their generated `*.runtimeconfig.json` files ask for
`Microsoft.NETCore.App` 8.0 *and* `Microsoft.AspNetCore.App` 8.0.

If only a newer runtime is installed, opt into roll-forward:

```powershell
# PowerShell
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
dotnet test tests/UnitTests/UnitTests.csproj
```

```bash
# bash
DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/UnitTests/UnitTests.csproj
```

The Docker images ship the correct runtime — `mcr.microsoft.com/dotnet/aspnet:8.0` for the two web
hosts, `runtime:8.0` for the worker — so nothing needs rolling forward inside a container. That is
why Compose is the primary supported path.

### Build and test

```bash
dotnet build AsyncSearchSystem.sln
dotnet test tests/UnitTests/UnitTests.csproj
dotnet test tests/IntegrationTests/IntegrationTests.csproj
```

`UnitTests` is fast, in-memory and needs neither Docker nor a broker: it covers the handlers, the
repository, the result generator and the validators. Prefix it with `DOTNET_ROLL_FORWARD` per the
note above if the ASP.NET Core 8 runtime is missing.

`IntegrationTests` is a different animal. It hosts the real services and starts a real RabbitMQ
through Testcontainers, so it **requires a running Docker daemon** — without one, the broker-backed
tests are skipped rather than failed:

```bash
DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/IntegrationTests/IntegrationTests.csproj
```

### Coverage

`tests/coverlet.runsettings` excludes everything carrying `GeneratedCodeAttribute` — the ~1,500
lines of protobuf and gRPC stubs generated from `search.proto`, and the bodies the
`[LoggerMessage]` source generator emits. Counting machine-written code would make the headline
number meaningless in both directions.

```bash
dotnet test tests/UnitTests/UnitTests.csproj \
  --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings
```

Combining both suites gives **77% line coverage** of hand-written code. It is not uniform, and the
shape is deliberate: the application layer, the messaging infrastructure and the Notification
Service consumer are at or near 100%, while the API Gateway sits lower because its Swagger wiring
and several `Program.cs` branches are only exercised under `Development`.

### Running the services on the host

`Development` configuration points every service at `localhost`, so a broker has to be listening on
`localhost:5672`. The simplest one is the Compose broker on its own:

```bash
docker compose up -d rabbitmq
```

Then, in three terminals:

```bash
dotnet run --project src/SearchService/SearchService.Api      # http://localhost:5001 (h2c)
dotnet run --project src/ApiGateway                           # http://localhost:5000
dotnet run --project src/NotificationService
```

The gateway's `Development` configuration already points `Grpc:SearchService` at
`http://localhost:5001`, and Swagger UI is served at `http://localhost:5000/swagger`.

### The Search Service will not open in a browser, and that is correct

Kestrel in the Search Service pins every endpoint to `HttpProtocols.Http2`, because gRPC without TLS
requires HTTP/2 cleartext (h2c). Browsers do not speak h2c: they negotiate HTTP/2 only over TLS and
fall back to HTTP/1.1 on a plain `http://` URL, which this host does not accept. Pointing a browser
at `http://localhost:5001` therefore fails, and nothing is wrong. Use a gRPC client instead. Server
reflection is not enabled, so hand the client the contract explicitly:

```bash
grpcurl -plaintext \
  -import-path src/Shared/Shared.GrpcContracts/Protos -proto search.proto \
  -d '{"destination":"Paris"}' \
  localhost:5001 search.v1.SearchGrpcService/StartSearch
```

The API Gateway, by contrast, is an ordinary HTTP/1.1 endpoint and works in any browser or `curl`.

## Troubleshooting

**Port already in use.** `docker compose up` fails with a bind error on 8080, 5672, 15672 or 5001.
Copy `.env.example` to `.env` and change the offending host port — only the left-hand side of the
mapping changes, and containers keep addressing each other on their internal ports.

```bash
cp .env.example .env    # then edit APIGATEWAY_HTTP_PORT, RABBITMQ_MANAGEMENT_PORT, ...
docker compose up --build
```

**Docker daemon not running.** `docker compose` reports that it cannot connect to the Docker daemon,
and `dotnet test` on `IntegrationTests` skips its broker-backed tests. Start Docker Desktop (or the
`docker` service), confirm with `docker info`, and retry.

**RabbitMQ is still starting.** The Search Service and the Notification Service both wait on
`condition: service_healthy`, and the broker's healthcheck has a 30 second start period, so on a
cold start those two containers legitimately sit in the `Created` state for about half a minute
before they are started. A service that does reach a not-yet-ready broker retries the connection up
to `RabbitMq__MaxConnectRetries` times, logging `RabbitMQ connection attempt failed, retrying.` each
time, and converges on its own. Check progress with:

```bash
docker compose ps
docker compose logs rabbitmq | tail -n 20
```

**A search completes but no notification arrives.** Look for `Failed to publish the search completed
event.` in `docker compose logs searchservice`. Completion is persisted before publication, so
`isCompleted` can legitimately be `true` while the announcement failed; in that case the broker is
the thing to inspect, in the management UI at `http://localhost:15672`.

**Reset broker state.** The `async-search-rabbitmq-data` volume outlives `docker compose down`, so
queued messages, the declared topology and the seeded credentials all survive a restart. To start
from an empty broker:

```bash
docker compose down -v
docker compose up --build
```
