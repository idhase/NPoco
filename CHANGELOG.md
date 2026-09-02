# Changelog

## [1.0.0] - 2026-09-03

First release of the fork. Two features: an OPENJSON rewrite for large `IN` lists, and a
`LegacyDateTime` marker so a `DateTime` property can declare a `datetime` column.

Both are opt-in through `SqlServer2016DatabaseType`. Nothing changes for any other
database type.

### OPENJSON for large IN lists

A long `IN` list produces a new query plan for every distinct value count, which fills the
plan cache. OPENJSON passes the whole list as a single JSON parameter, so one plan covers
every list length.

The rewrite applies at 32 or more values. Below that, normal parameters are used. The
constant is `SqlServer2016Expression<T>.OpenJsonThreshold`.

Requires database compatibility level 130 or higher. Pass `false` to the constructor on a
lower level.

`UseOpenJsonForInClauses` turns the rewrite off. It is set through the constructor and is
get-only. During development it was a settable property reached through a shared
`DatabaseType` accessor, so one caller could change the behaviour of every `Database` in
the process. Both the accessor and the setter are gone.

The flag controls the OPENJSON rewrite only. Power-of-two parameter padding is a separate
plan-cache optimisation and always runs.

#### Supported types

`int`, `long`, `short`, `byte`, `string`, `Guid`, `DateTime`, and enums over any supported
integer type. Columns marked `AnsiString` or `LegacyDateTime` are handled too.

These types are excluded, each for a reason that was reproduced:

| Type | Problem |
|---|---|
| `decimal` | `decimal(38,18)` overflows above roughly 1e20 and truncates past 18 decimal places |
| `double`, `float` | lossy string round-trip, and `NaN`/`Infinity` have no JSON number form |
| `bool` | needs 32 or more entries to qualify, which cannot happen in practice |
| `DateTimeOffset` | untested, not known to be impossible; see the note in `SqlServer2016Expression` |

Excluded types fall back to the parameterised path. Tests assert that they never take the
OPENJSON route.

#### JSON serialisation

`InListJson` writes the array. It is hand-written rather than delegated to a JSON library,
because the output has to be a flat array of scalars in the exact formats OPENJSON accepts,
and a general purpose serializer is configured for round-tripping objects instead: type
name handling wraps the array in an envelope, date settings change the format, escaping
choices differ. Any of those silently produce SQL that OPENJSON cannot parse.

The previous implementation used the fastJSON copy vendored in NPoco, which turned out to
truncate `DateTime` to whole seconds and to write negative `DateTimeOffset` values with a
double sign (`--05:00`). Both were reproduced. It is no longer used here.

The writer is not pluggable. What it emits is fixed by the `WITH ([value] <type>)` clause on
the other side, and that clause comes from a type map the caller cannot reach, so only one
rendering per type is ever correct. To serialise differently you have to change the SQL types
with it: subclass `SqlServer2016Expression<T>` and override `ExpressionVisitor` on the
database type.

### LegacyDateTime

NPoco maps a `DateTime` property to `DbType.DateTime`, which rounds to 1/300 second. A
`datetime2` column stores the full value, so the parameter and the column disagree and an
equality filter finds nothing. Over 300 random timestamps, a `datetime` column compared
against a `datetime2` value matched 96 times, and the reverse 110 times.

`SqlServer2016DatabaseType` now maps `DateTime` to `DbType.DateTime2` by default, which is
correct for `datetime2` columns of any scale: a scale mismatch between parameter and column
is harmless, so one `datetime2(7)` OPENJSON `WITH` clause serves them all.

For the columns that really are `datetime`, mark the property:

```csharp
[ColumnType(typeof(LegacyDateTime))]
public DateTime CreatedAt { get; set; }
```

or `WithDbType<LegacyDateTime>()` in a fluent mapping. The property stays `DateTime`; only
the mapping changes. Marked columns send `DbType.DateTime` and serialise with three
fractional digits, since `datetime` rejects more than three (Msg 241). After marking, all
200 of 200 round-trip comparisons matched, in both directions.

`LegacyDateTime` is a marker in the shape of `AnsiString`, and is handled on all three
paths: the OPENJSON `WITH` clause, the LINQ provider's `==` and `IN`, and FluentSql.

### Fixes

`IN` lists against an `AnsiString` column no longer send nvarchar. Base NPoco wraps
`AnsiString` values only in `VisitBinary`, so `==` comparisons were correct but `IN` clauses
were not, costing an index seek on every varchar column. `FormatParameters` is now
overridden to wrap them. Checked against a `varchar(50)` indexed column on SQL Server 2022:
the plan goes from Index Scan with `CONVERT_IMPLICIT` on the column to a clean Index Seek.
The OPENJSON path emits `varchar(max)` for the same reason.

Parameter padding is now capped at 1024 instead of 2000. SQL Server allows at most 2100
parameters per request, and a 1500 value list was padded up to 2000, leaving almost no room
for the rest of the query. Lists above the cap now pass through unpadded.

`VisitStaticArrayMethodCall` no longer delegates to the base class in its fallback branch.
The base re-runs `VisitExpressionList` over the same arguments, visiting them twice.

`TypeSupportedAsJson` and `GetSqlServerType` were replaced by a single
`TryResolveJsonColumnType`. They read the type map independently, so the check that granted
eligibility and the type that got emitted could disagree and put a null into the generated
SQL.

### Changes to upstream code

Two upstream files are changed: 20 added lines and three widened method signatures.
Nothing upstream is removed or rewritten.

`src/NPoco/Expressions/SqlExpression.cs`: `FlattenList` widens from private to protected,
`FormatParameters` and `BuildInStatement` widen to protected virtual, and a protected
`GetColumnType` helper is added. None of that changes behaviour; it opens the extension
points the SQL Server 2016 expression needs. One branch is added to `VisitBinary` to wrap
`LegacyDateTime`, mirroring the `AnsiString` branch above it.

`src/NPoco.FluentSql/SqlExpressionTranslator.cs`: the same `LegacyDateTime` wrap, next to
the existing `AnsiString` wrap.

Both new branches are reachable only for a column declared as `LegacyDateTime`, which does
not exist upstream.

An earlier version of this work put OPENJSON directly in core, where it affected every
database type. That has been removed. SqlServer2012, PostgreSQL, MySQL and the rest are
back to plain parameterised `IN`.

### Tests

`SqlServer2016ExpressionTests` (36) and `FluentSql/FluentSqlMarkerColumnTests` (7).

### Packaging

Package ids are `Idha.NPoco*`. The assembly is still named `NPoco.dll`, which keeps it a
drop-in replacement but means it cannot be installed alongside the official NPoco package.

Target frameworks are narrowed to `net8.0` and `net10.0`.

Fork identity (authors, URLs, copyright, description) lives in `Directory.Build.targets`
rather than the `.csproj` files. Those exist upstream, so editing them causes a conflict on
every sync. Merge `ecf6e8b` is the example: it left duplicated `VersionPrefix` and
`TargetFrameworks` lines behind.

`NOTICE` records the upstream copyright and the modified files, as Apache-2.0 section 4(b)
requires.

Every `Idha.*` package now ships `LICENSE.txt`. Upstream sets `PackageLicenseFile` on NPoco
alone, so the SqlServer, Abstractions and JsonNet packages were published without a licence
file at all. That is an Apache-2.0 section 4(a) gap inherited from upstream.
