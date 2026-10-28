# location-ingest Specification

## ADDED Requirements

### Requirement: Location point ingestion
The system SHALL accept `POST /location` with `{lat, lng, timestamp, accuracyM?}` authenticated by a per-user API key, and SHALL buffer the point in Redis with a 24-hour TTL.

#### Scenario: Valid point accepted
- **WHEN** a request with valid coordinates and a timestamp within ±5 min of server time arrives
- **THEN** the point is appended to the user's Redis buffer and `202 Accepted` is returned

#### Scenario: Stale or invalid point rejected
- **WHEN** the timestamp is older than 24h or coordinates are out of range
- **THEN** `400 Bad Request` is returned and nothing is stored

#### Scenario: Coarse fix rejected (amended 28 Oct 2026, from real use)
- **WHEN** a point reports `accuracyM` worse than 150 m (IP-geolocated browser fixes report km-scale accuracy)
- **THEN** it is rejected and nothing is stored; a point without `accuracyM` is still accepted

#### Scenario: Implausible jump rejected (amended 28 Oct 2026, from real use)
- **WHEN** a point lands more than 1 km from the previous buffered point at an implied speed over 250 km/h, within 3 hours of it
- **THEN** it is rejected (a Mac browser tab once posted an IP-geolocated fix from another continent, which became a plan's leave reference); after a gap of 3 hours or more the new fix re-anchors wherever it lands

### Requirement: Owntracks ingestion
The system SHALL accept `POST /location/owntracks` with Owntracks HTTP-mode JSON payloads authenticated by the same per-user API key, and SHALL map `_type: "location"` messages (`lat`, `lon`, `tst`, `acc`) to the standard point; other `_type`s are acknowledged and ignored.

#### Scenario: Owntracks location
- **WHEN** Owntracks posts `{"_type":"location","lat":52.09,"lon":5.23,"tst":1761500000,"acc":12}`
- **THEN** the point is buffered as if posted to `/location` and an empty JSON array is returned (Owntracks expects `[]`)

#### Scenario: Non-location message
- **WHEN** Owntracks posts a `_type: "lwt"` or `"transition"` message
- **THEN** `200` with `[]` is returned and nothing is stored

### Requirement: Pluggable location source
The system SHALL define `ILocationSource` with at least a `GpxReplaySource` implementation that replays a trail at configurable speed for demos and tests.

#### Scenario: GPX replay
- **WHEN** `GpxReplaySource` is started with a trail and a 60× speed factor
- **THEN** points are posted to `/location` in order with scaled timestamps

### Requirement: Visit collapsing
The system SHALL run a job every 10 minutes that converts buffered points into `Visit` records: a stay of at least 5 minutes within a 75 m radius becomes one Visit.

#### Scenario: Stay detected
- **WHEN** points show the user within 75 m of a centroid for 20 minutes
- **THEN** one `Visit` is created with `arrivedAt`, `departedAt` and a `placeId` resolved from the centroid

#### Scenario: Transit not recorded
- **WHEN** points move continuously with no 5-minute stay
- **THEN** no `Visit` is created

#### Scenario: Ongoing stay
- **WHEN** the stay has not ended when the job runs
- **THEN** a `Visit` is created with `departedAt = null` and updated on a later run

### Requirement: Raw points are not persisted
The system SHALL NOT write raw location points to PostgreSQL.

#### Scenario: Buffer expiry
- **WHEN** 24 hours pass after a point was ingested
- **THEN** the point is gone from Redis and only derived `Visit` records remain

### Requirement: Current location
The system SHALL expose `location.current()` returning the latest buffered point and its age.

#### Scenario: No recent point
- **WHEN** the latest point is older than 30 minutes
- **THEN** `location.current()` returns the point flagged `isStale = true` and the planner falls back to the home place
