# DataOil.LINQPad

`DataOil.LINQPad` 0.1.5 is a LINQPad 9 driver, not a library an application references, so there is
no solution here to build: its samples ship inside the package, and LINQPad lists them under
**Samples** once the driver is installed.

| Sample | What it shows |
| --- | --- |
| *Data Oil - typed LINQ over your own schema* | The driver's typed sets: a count, a filter, a `GroupBy`, ordering and paging, each translated by the provider and visible in the SQL tab |
| *Data Oil - the engine's own statements* | The schema as the engine describes it, and a statement in the engine's own dialect through `this.DataOil()`; it runs against any database unchanged |

## Installing it

The driver is on [nuget.org](https://www.nuget.org/packages/DataOil.LINQPad/0.1.5), tagged
`linqpaddriver`, so LINQPad finds it there:

1. In LINQPad 9: **Add Connection**, **View more drivers...**.
2. Choose **Show all drivers**, search for **DataOil.LINQPad**, and install it. LINQPad fetches it
   from nuget.org with what it depends on - `DataOil.EntityFrameworkCore` 0.1.4 and
   `DataOil.Client` 0.2.2. The package carries the Data Oil Software Licence, and LINQPad asks you
   to accept it.

It needs LINQPad 9, because the provider is .NET 10. Close LINQPad before copying a driver into its
driver folder by hand: a copy over a running LINQPad can leave a driver it cannot load, and nothing
says so.

## Connecting to the public sandbox

The quickest way to see the driver work: **Add Connection**, **Data Oil**, and

| Field | Value |
| --- | --- |
| Endpoint | `https://sandbox.dataoil.tech` |
| Database | `Sandbox` |
| Account | `suser` |
| Password | `hFn3dh#2%hn8A` (typed as it is; LINQPad stores it encrypted) |

The account is public and read only: it can read `Sandbox` and nothing else, and every write is
refused with 403 `Security`. The driver builds typed sets from what the account may read -
`WorldBank_rows`, `WorldBank_chunks`, `WorldBank`, `Glossary` and the rest - so a query such as

```csharp
WorldBank_rows
    .Where(r => r.Year == 2015)
    .OrderByDescending(r => r.GDP_USD)
    .Take(5)
    .Select(r => new { r.Country, r.GDP_USD })
```

runs as soon as the connection is open, and the SQL tab shows the backtick-quoted statement the
provider sent. (The driver names a property after its column, joining the words with `_` and
dropping the characters C# does not allow - `GDP (USD)` is `GDP_USD`, `Life Expectancy` is
`Life_Expectancy`; the schema tree on the left shows the names it chose.) The sandbox has fair-use limits -
about 60 requests a minute from one address - so page through large results rather than refreshing
in a loop.

## Connecting to your own tenant

**Add Connection**, **Data Oil**: your endpoint (such as `https://acme.dataoil.tech`), the database,
and an account or an API key (`dot_...`), which LINQPad stores encrypted. Every query is billed to
your tenant like any other work on your data.

For the same steps in code - the sandbox read through LINQ, and in your own tenant connect, create,
write, query, search and clean up - see the [Entity Framework Core sample](../efcore/README.md),
which uses the provider this driver is built on.
