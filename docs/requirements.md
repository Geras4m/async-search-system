# Agentic Development Prompt: Async Search System (.NET 8)

## Source and Scope

This prompt is based on the original PDF task describing a web API, Search Service, and Notification Service implemented in .NET 8 and ASP.NET Core, using gRPC between the web API and Search Service and a message broker between the Search Service and Notification Service.

- **Original requirements**: Asynchronous hotel search, 6 result batches at 5-second intervals, completion flag, event-based notification, and Docker-based local startup.
- **This document adds**: A more detailed architecture (Clean Architecture style), testing recommendations, logging guidelines, and suggested tooling to achieve a production-quality implementation.

For clarity, the **web API** mentioned in the original PDF is referred to as the **API Gateway** in this document; they are the same component.

---

## Overview

The goal is to implement a production-quality distributed system in **.NET 8** consisting of three services:

1. API Gateway (web API)
2. Search Service
3. Notification Service

The system must implement an asynchronous hotel search workflow and demonstrate:

- HTTP/JSON API communication via the API Gateway
- gRPC communication between API Gateway and Search Service
- Asynchronous processing of search operations
- Event-driven architecture
- Message broker integration between Search Service and Notification Service
- CQRS using MediatR
- Dockerized deployment for local startup

---

# Business Requirements

The core business requirement is to implement an asynchronous hotel search workflow, where clients can start a search and poll for results until completion.

## Functional Search Flow

1. A client sends an HTTP POST request to the API Gateway to start a search.
2. The API Gateway forwards the request to the Search Service using gRPC.
3. The Search Service immediately returns a unique `SearchId` to the API Gateway, which is then returned to the client.
4. Search execution continues asynchronously in the Search Service.
5. Clients use the `SearchId` to retrieve search progress and results via the API Gateway until the search completes.

## Execution Parameters

1. The Search Service appends search results in **six batches**.
2. Each batch is added **every 5 seconds**.
3. After the sixth batch:
   - The search is marked as completed by setting the completion flag to `true`.
   - A completion event is published to a message broker.
4. The Notification Service consumes the completion event and logs the `SearchId` of the completed search.

## Error and Edge Case Behavior (High Level)

- If a client requests a search by an invalid GUID, the API Gateway should reject the request.
- If a search with the specified `SearchId` does not exist, the API Gateway should return an appropriate error response (e.g., HTTP 404).
- If the search exists but no results have been produced yet, the API Gateway should return the current state with an empty result list and `isCompleted = false`.

---

# Technical Constraints

## Mandatory Technologies

The following technologies are **mandatory** (derived directly from the original task plus minimal supporting components):

- .NET 8
- ASP.NET Core
- gRPC
- MediatR (for commands/queries separation)
- Docker
- Docker Compose

## Recommended but Optional Technologies

The following technologies are **recommended** to achieve a production-quality implementation but are not strictly required to satisfy the original task requirements:

- RabbitMQ (message broker implementation)
- Serilog (structured logging)
- FluentValidation (request validation)
- Minimal APIs (for concise HTTP endpoints)
- BackgroundService (for search execution engine)
- xUnit (unit testing)
- Testcontainers (integration testing with ephemeral infrastructure)

---

# High-Level Architecture

```text
+-------------------+
|      Client       |
+---------+---------+
          |
          | HTTP/JSON
          v
+-------------------+
|    API Gateway    |
+---------+---------+
          |
          | gRPC
          v
+-------------------+
|   Search Service  |
+---------+---------+
          |
          | RabbitMQ Event
          v
+-------------------+
| Notification Svc  |
+-------------------+
```

- **API Gateway (Web API)**: Accepts HTTP/JSON requests and exposes endpoints to start searches and retrieve results.
- **Search Service**: Handles search lifecycle, asynchronous result generation, and publishing completion events.
- **Notification Service**: Consumes completion events from the message broker and logs receipt.

---

# Architecture Principles

The implementation should follow these architectural principles to remain maintainable and extensible:

- SOLID principles
- Clean Architecture concepts (domain/application separated from infrastructure)
- CQRS with MediatR
- Dependency Injection
- Separation of concerns
- Repository pattern
- Event-driven communication
- Fully asynchronous implementation

---

# Solution Structure

```text
src
│
├── ApiGateway
│   ├── Endpoints
│   ├── Services
│   ├── GrpcClients
│   └── Program.cs
│
├── SearchService
│   ├── Application
│   │   ├── Commands
│   │   ├── Queries
│   │   ├── Handlers
│   │   └── Validators
│   │
│   ├── Domain
│   │   ├── Entities
│   │   └── Repositories
│   │
│   ├── Infrastructure
│   │   ├── Persistence
│   │   ├── Messaging
│   │   └── BackgroundJobs
│   │
│   ├── Grpc
│   └── Program.cs
│
├── NotificationService
│   ├── Messaging
│   ├── Consumers
│   └── Program.cs
│
├── Shared
│   ├── GrpcContracts
│   ├── EventContracts
│   └── Common
│
├── Tests
│   ├── UnitTests
│   └── IntegrationTests
│
└── docker-compose.yml
```

This structure follows a Clean Architecture style separation between API, Application, Domain, and Infrastructure layers for the Search Service.

---

# Domain Design

## Search Entity

```csharp
public class Search
{
    public Guid Id { get; set; }

    public bool IsCompleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public List<HotelResult> Results { get; set; } = new();
}
```

## HotelResult Entity

```csharp
public class HotelResult
{
    public string HotelId { get; set; }

    public string Name { get; set; }

    public decimal Price { get; set; }
}
```

These entities represent the core domain model and are part of the Domain layer.

---

# CQRS Design

## Commands

### StartSearchCommand

Responsible for:

- Creating a new search
- Generating `SearchId`
- Persisting initial state via a domain repository abstraction
- Scheduling asynchronous processing (delegated to a background execution component)

### AppendSearchBatchCommand

Responsible for:

- Adding one batch of hotels to an existing search
- Persisting the updated search state via the repository abstraction

### CompleteSearchCommand

Responsible for:

- Marking the search as completed
- Persisting the updated search state
- Triggering publication of a completion event via an application-level messaging abstraction (implemented in the Infrastructure layer)

---

## Queries

### GetSearchResultsQuery

Responsible for:

- Retrieving current search state from the repository
- Returning all accumulated results and the completion flag

---

# Repository Design

## Interface

```csharp
public interface ISearchRepository
{
    Task<Search?> GetAsync(Guid id);

    Task CreateAsync(Search search);

    Task UpdateAsync(Search search);
}
```

## Initial Storage

For this exercise, use an in-memory store:

```csharp
ConcurrentDictionary<Guid, Search>
```

Create an in-memory repository implementation. The repository must be thread-safe because background processing updates search results while clients are reading them.

> Note: In a real system, this in-memory implementation would be replaced by a persistent data store (e.g., database) without changing the application or domain layers.

---

# API Gateway Requirements

## HTTP Endpoint: Start Search

### Route

```http
POST /searches
```

### Request

```json
{
  "destination": "Paris"
}
```

### Processing

1. Validate request (destination present and within allowed length).
2. Build gRPC request.
3. Call Search Service via gRPC.
4. Return `SearchId` to the client.

### Response

```json
{
  "searchId": "cbaf4961-10d1-42a0-b24d-111111111111"
}
```

---

## HTTP Endpoint: Get Search Results

### Route

```http
GET /searches/{searchId}
```

### Processing

1. Validate `searchId` as a GUID.
2. Call Search Service via gRPC.
3. Map gRPC response to JSON.
4. Handle error cases such as missing search or gRPC unavailability appropriately.

### Response

```json
{
  "searchId": "cbaf4961-10d1-42a0-b24d-111111111111",
  "isCompleted": false,
  "results": []
}
```

---

# Search Service Requirements

## gRPC Service

Create protobuf contract with two operations:

### StartSearch

```proto
rpc StartSearch (
    StartSearchRequest
)
returns (
    StartSearchResponse
);
```

### GetSearchResults

```proto
rpc GetSearchResults (
    GetSearchResultsRequest
)
returns (
    GetSearchResultsResponse
);
```

---

# Search Execution Engine

Implement a dedicated `BackgroundService` responsible for orchestrating asynchronous search execution.

## Responsibilities

- Process searches independently.
- Append results every 5 seconds.
- Complete search after six iterations.
- Publish completion event when the search is marked as completed.

---

# Search Processing Algorithm

```csharp
for (var batch = 1; batch <= 6; batch++)
{
    await Task.Delay(TimeSpan.FromSeconds(5));

    await mediator Send(
        new AppendSearchBatchCommand(
            searchId,
            batch));
}

await mediator Send(
    new CompleteSearchCommand(searchId));
```

This algorithm runs inside the background execution component, using MediatR to append batches and complete the search.

---

# Hotel Data Generation

Generate fake hotel results for each batch.

Each batch should contain:

```text
Batch 1 -> Hotels 1-5
Batch 2 -> Hotels 6-10
Batch 3 -> Hotels 11-15
Batch 4 -> Hotels 16-20
Batch 5 -> Hotels 21-25
Batch 6 -> Hotels 26-30
```

Example:

```csharp
new HotelResult
{
    HotelId = Guid.NewGuid().ToString(),
    Name = $"Hotel {counter}",
    Price = Random.Shared.Next(80, 400)
}
```

---

# Messaging Architecture

## Broker

Use RabbitMQ as the message broker implementation.

## Exchange

```text
search.completed
```

## Queue

```text
notification.search.completed
```

The Search Service publishes completion events to the `search.completed` exchange, and the Notification Service consumes them from the `notification.search.completed` queue.

---

# Event Contract

```csharp
public sealed record SearchCompletedEvent(
    Guid SearchId,
    DateTime CompletedAtUtc);
```

---

# Event Publishing Logic

After:

```csharp
search.IsCompleted = true;
```

Publish:

```csharp
SearchCompletedEvent
```

The publisher should:

1. Serialize JSON payload.
2. Publish to RabbitMQ exchange.
3. Log successful publication.

The application layer should depend on an abstraction (e.g., `ISearchEventsPublisher`), with the concrete RabbitMQ implementation placed in the Infrastructure layer.

---

# Notification Service Requirements

## Consumer Responsibilities

1. Listen to the RabbitMQ queue `notification.search.completed`.
2. Deserialize the `SearchCompletedEvent`.
3. Log the incoming message, including the `SearchId`.

Example log:

```text
[Information]
Search completed event received.
SearchId: cbaf4961-10d1-42a0-b24d-111111111111
```

---

# Logging Requirements

Use structured logging (e.g., Serilog) to capture key events throughout the workflow.

Log the following events:

## Search Created

```text
Search created
SearchId={SearchId}
```

## Batch Added

```text
Batch added
SearchId={SearchId}
Batch={BatchNumber}
```

## Search Completed

```text
Search completed
SearchId={SearchId}
```

## Event Published

```text
Event published
SearchId={SearchId}
```

## Event Consumed

```text
Event received
SearchId={SearchId}
```

---

# Validation Requirements

Use FluentValidation (or similar) for request validation in the API Gateway.

## Start Search Request

Validate:

```text
- Destination is required
- Destination length > 2
- Destination length < 100
```

---

# Error Handling

Implement proper error handling across services.

## API Gateway

- Invalid GUID.
- Search not found.
- gRPC unavailable.
- Timeout.

## Search Service

- Missing search.
- Repository errors.
- RabbitMQ failures.

## Notification Service

- Deserialization errors.
- RabbitMQ connectivity issues.

---

# Dockerization

Create a Dockerfile for each service:

```text
ApiGateway
SearchService
NotificationService
```

Each Dockerfile should produce a container image capable of running the respective service in the composed environment.

---

# Docker Compose

The `docker-compose.yml` must contain:

```yaml
services:
  apigateway:
  searchservice:
  notificationservice:
  rabbitmq:
```

RabbitMQ ports:

```yaml
5672
15672
```

The entire environment must start with:

```bash
docker compose up --build
```

---

# Configuration

Use `appsettings.json` and environment variables for configuration.

## API Gateway

```json
{
  "Grpc": {
    "SearchService": "http://searchservice:8080"
  }
}
```

## Search Service

```json
{
  "RabbitMq": {
    "Host": "rabbitmq"
  }
}
```

## Notification Service

```json
{
  "RabbitMq": {
    "Host": "rabbitmq"
  }
}
```

---

# Testing Guidelines

The original task does not explicitly require automated tests, but to reach production-quality standards, the following tests are **strongly recommended**.

## Unit Tests

Create unit tests covering:

### StartSearchCommandHandler

- Creates a search.
- Returns a `SearchId`.

### AppendSearchBatchCommandHandler

- Adds a batch to results.

### CompleteSearchCommandHandler

- Sets `IsCompleted`.
- Triggers event publication through the messaging abstraction.

### Repository Tests

- Save.
- Update.
- Retrieve.

---

## Integration Tests

Create an end-to-end integration test covering:

### Scenario

1. Start search via API Gateway.
2. Obtain `SearchId`.
3. Poll results via API Gateway.
4. Verify batches are added over time.
5. Verify completion flag is set to true at the end.
6. Verify completion event is published.
7. Verify Notification Service consumes the event and logs the `SearchId`.

---

# Acceptance Criteria

The implementation is considered complete only if the following workflow succeeds:

## Step 1

Start environment:

```bash
docker compose up --build
```

## Step 2

Create search:

```http
POST /searches
```

Response:

```json
{
  "searchId": "<guid>"
}
```

## Step 3

Poll search:

```http
GET /searches/{guid}
```

## Step 4

Observe:

- Results increasing every 5 seconds.
- Six total updates.

## Step 5

Final response contains:

```json
{
  "isCompleted": true
}
```

## Step 6

Search Service publishes:

```json
{
  "searchId": "<guid>",
  "completedAtUtc": "<timestamp>"
}
```

## Step 7

Notification Service logs:

```text
Search completed event received.
SearchId: <guid>
```

## Step 8

Verify architecture constraints:

- API Gateway ↔ Search Service uses gRPC only.
- Search Service ↔ Notification Service uses RabbitMQ only.
- No direct HTTP communication between Search Service and Notification Service.
- No direct gRPC communication between Search Service and Notification Service.

---

# Deliverables

Group deliverables by project to clarify responsibilities.

## API Gateway

1. API Gateway project.
2. HTTP endpoints (`/searches` POST, `/searches/{searchId}` GET).
3. gRPC client for Search Service.
4. Request validation.
5. Configuration (`appsettings.json`, environment variables).

## Search Service

6. Search Service project.
7. Domain entities (`Search`, `HotelResult`).
8. MediatR commands and queries.
9. Command and query handlers.
10. Repository abstraction (`ISearchRepository`).
11. In-memory repository implementation.
12. Background search execution (`BackgroundService`).
13. RabbitMQ event publisher implementation.
14. gRPC service implementation and proto files.
15. Logging.

## Notification Service

16. Notification Service project.
17. RabbitMQ consumer.
18. Logging of received events.

## Shared

19. Shared contracts project (gRPC contracts, event contracts, shared common types).

## Infrastructure and Operations

20. Dockerfiles for each service.
21. `docker-compose.yml`.
22. Configuration files for all services.

## Testing and Documentation

23. Unit tests (as described above).
24. Integration tests (as described above).
25. `README.md` with instructions on building, running, and verifying the system.

The final solution must:

- Compile successfully.
- Run locally via Docker Compose.
- Follow Clean Architecture principles.
- Use asynchronous programming correctly.
- Be production-quality and maintainable.

