# KAONOMI.MongoEngine

A small server that speaks the **wire protocol of MongoDB** (a standalone `mongod` 7.0 for the clients), made for what
MARI and Orleans ask of MongoDB in KAONOMI: the membership table of the Orleans cluster (`UseMongoCluster` of the 7
hosts), and the repositories, definitions, grain states and reminders of MARI when they are on MongoDB. The clients keep
their driver (`MongoDB.Driver` 2.25) and their connection string `mongodb://localhost:27017`.

## Run

```bash
dotnet run --project KAONOMI.MongoEngine
```

Stop MongoDB first (`mongod` uses 27017 too), or give the engine another port (`--port 27018`). Stop the engine with
Ctrl+C, `SIGTERM` or the `shutdown` command of MongoDB (the chat sends it on `/bye`): the collections are written to disk before it exits. A second engine on the same `dbPath` stops at
once (`mongod.lock`), and the port is checked before the data is opened.

To move KAONOMI to the engine: stop the hosts, stop `mongod`, start the engine, start the hosts. The membership table
starts empty: the silos write it again when they start (nothing else of KAONOMI is in MongoDB).

## Configuration: `config/mongoengine.yml`

Keys in the style of `mongod.conf`, nested or dotted; `--set key=value` overrides one (`--port` and `--dbpath` too).
Relative paths are relative to the folder of the file.

| Key | Default | Role |
| --- | --- | --- |
| `net.port`, `net.bindIp` | `27017`, `127.0.0.1` | Where the engine listens (`0.0.0.0`: every interface) |
| `net.maxIncomingConnections` | `10000` | Connections beyond are closed |
| `storage.dbPath` | `../data` | One folder per database; per collection a snapshot (`.bson`), a journal (`.journal`) and its indexes (`.meta`) |
| `storage.journal.commitIntervalMs` | `100` | How often the journal is forced to disk |
| `storage.journal.compactAfter` | `10000` | Journal records before the collection is written again as a snapshot |
| `systemLog.path` | (console only) | Log file |
| `systemLog.verbosity` | `0` | `1`: every connection and command |

## What is supported

| Area | |
| --- | --- |
| Protocol | OP_MSG (body and document sequences, `moreToCome`), the OP_QUERY / OP_REPLY of the first handshake; no compression, no TLS, no authentication |
| Server | `hello`, `isMaster`, `ping`, `buildInfo`, `getParameter`, `hostInfo`, `serverStatus`, `connectionStatus`, `whatsmyuri`, `getLog`, `endSessions` (and the other session commands), `listCommands`, `currentOp`, `shutdown` |
| CRUD | `find` (filter, sort, skip, limit, projection, batchSize, singleBatch), `getMore`, `killCursors`, `insert`, `update` (operators, replacement, `upsert`, `multi`), `delete`, `findAndModify`, `count`, `distinct`, `aggregate`; the bulk writes of the driver |
| Filters | equality (arrays: one element equal), dotted paths, `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`, `$exists`, `$type`, `$regex`, `$mod`, `$size`, `$all`, `$elemMatch`, `$not`, `$and`, `$or`, `$nor` |
| Updates | `$set`, `$unset`, `$setOnInsert`, `$inc`, `$mul`, `$min`, `$max`, `$rename`, `$currentDate`, `$push` (`$each`, `$position`, `$sort`, `$slice`), `$addToSet`, `$pull`, `$pullAll`, `$pop` |
| Aggregation | `$match`, `$sort`, `$skip`, `$limit`, `$project`, `$addFields`/`$set`, `$unset`, `$replaceRoot`/`$replaceWith`, `$unwind`, `$group` (`$sum`, `$avg`, `$min`, `$max`, `$first`, `$last`, `$push`, `$addToSet`, `$count`), `$count`, `$sortByCount`, `$lookup` (equality), common expressions |
| Collections, indexes | `create`, `drop`, `listCollections`, `listDatabases`, `dropDatabase`, `createIndexes` (unique indexes enforced), `listIndexes`, `dropIndexes`, `collStats`, `dbStats` |
| Errors | the codes and messages of MongoDB: `E11000 duplicate key` (11000), `CommandNotFound` (59), `NamespaceNotFound` (26), `CursorNotFound` (43)… |

Not supported (an error says so): transactions (as a standalone `mongod`), change streams, `$expr`/`$where`/`$text`,
positional updates (`$`, `$[]`) and `arrayFilters`, pipeline updates, `$out`/`$merge`, collations, geo and text
indexes, replication.

## Guarantees

- **Atomic writes**: one lock per collection, the find of the documents to change and their change happen without any
  other write in between. The membership table of Orleans counts on it: its writes are upserts filtered on etags, and a
  stale etag makes the upsert fail with `E11000` on `_id`, which Orleans reads as a conflict (32 writers with the same
  etag at once: exactly one wins).
- **Durability**: each write is in the journal before it is answered; the journal is forced to disk every
  `commitIntervalMs` (a crash of the machine can lose that interval, as MongoDB with its journal), then written as a
  snapshot. An incomplete last record (a crash during a write) is left out at the next start.
- **Order and types**: the natural order of the documents is kept; the BSON types are kept as they are written (int,
  long, double, decimal, dates, binary, ObjectId…); the comparisons and sorts follow the order of MongoDB between types.

## Tests

```bash
dotnet test KAONOMI.MongoEngine.Tests
```

The tests start the engine in their process (free port, temporary folder): the official driver (handshake, filters,
updates, upserts and duplicate keys, findAndModify, bulk writes, indexes, cursors, the aggregation pipeline of MARI, a
restart), the commands of the membership table captured from KAONOMI with the profiler of MongoDB, and two real Orleans
8 silos with `Orleans.Providers.MongoDB` that join, see each other and leave.

## Code

| Folder | Role |
| --- | --- |
| `Configuration/` | `EngineSettings`: the YAML file and the overrides |
| `Wire/` | `WireProtocol` (messages, BSON), `WireServer` (TCP, one task per connection) |
| `Engine/` | `CommandHandler` (the commands, cursors), `Filter`, `Update`, `Paths`, `Sorting` (sorts, projections), `Aggregation`, `BsonOrder` (the order and equality of MongoDB), `MongoError` |
| `Storage/` | `StorageEngine` (databases, `mongod.lock`, journal sync), `Collection` (documents, indexes, journal, snapshot) |
