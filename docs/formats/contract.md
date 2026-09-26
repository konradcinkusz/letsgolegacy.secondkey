# contract.yaml — writing a contract

A contract says what the system **must** do, what it must **never** do, and which
differences between two versions do not matter. It is YAML, validated by
[`schemas/contract.schema.json`](../../schemas/contract.schema.json) and then by the
rules in `ContractRules` (below). A complete example:
[`schemas/samples/contract.yaml`](../../schemas/samples/contract.yaml).

```sh
sk validate contract.yaml
```

## Shape

```yaml
apiVersion: secondkey/v1
kind: Contract
metadata: { name: sample-shop, revision: 1 }   # revision: bump it whenever a clause changes
compare:   { headers: [content-type, location], html: extracts }
extract:   [ ... ]      # named values pulled out of responses
normalize: { ... }      # differences that do not matter
clauses:   [ ... ]      # what must happen, what must never happen
```

## Clauses

```yaml
- id: CHECKOUT-NEVER-5XX            # UPPER-KEBAB, unique
  kind: never                        # must | never
  title: Checkout never answers with a server error
  why: A 5xx at checkout loses the sale and says nothing the customer can act on.
  when: { method: POST, path: "^/checkout$" }     # optional scope; default: every request
  assert:
    - { select: response.status, op: gte, value: 500 }
  provenance: { origin: incident, status: accepted, author: risk, date: 2026-09-26 }
```

- **`must`** — every assertion holds for every request in scope.
- **`never`** — the assertions never *all* hold together for any request in scope. A
  `never` clause describes the forbidden situation; it passes when that situation does
  not occur.
- **`why`** is required: what breaks for the business if the clause stops holding.
- Only **`status: accepted`** clauses decide a verdict. `proposed` clauses (drafted by a
  person, mined from recordings, or drafted by a model) are evaluated and reported, and
  decide nothing until a person accepts them in a reviewed change to this file.
- A contract must contain **at least one accepted `never` clause**. A contract that
  states no absence has not said what must not happen, which is half of its job.

## Selecting values

Every assertion selects values from an **observation** of one request on one side:

| Root | Contents |
|---|---|
| `request` | `method`, `path`, `query.<name>` (list), `headers.<name>` (list), `body.json…`, `body.text`, `body.form.<field>` (list) |
| `response` | `status`, `headers.<name>` (list), `body.json…`, `body.text` |
| `extract` | `extract.<name>` — the values your extractors pulled out |
| `db` | `db["dbo.Orders"].inserted / deleted / updated` — row changes, when a state probe is configured |
| `outbound` | calls the system made to other systems (phase 02) |

Path syntax: `.name`, `[3]`, `[*]` (every element), `.**` (any depth), and
`["name.with.dots"]`. Header names are lower case and every header is a list:
`response.headers.location[0]`.

> **Quote paths with brackets inside `{ … }`.** In a YAML flow mapping `[` and `]` are
> syntax, so write `{ select: "response.headers.location[0]", … }`. In block style
> (`select: response.headers.location[0]` on its own line) no quotes are needed.

## Operators

| Operator | Needs | Holds when |
|---|---|---|
| `exists` / `absent` | — | a non-null value is / is not selected |
| `equals` / `notEquals` | `value` or `ref` | JSON equality (numbers compare numerically) |
| `lt` `lte` `gt` `gte` | `value` or `ref` | numeric comparison |
| `between` | `min`, `max` | `min ≤ x ≤ max` |
| `approx` | `value` or `ref`, and `absolute` or `relative` | within the tolerance |
| `matches` / `notMatches` | `value` (regex) | the string matches (culture-invariant) |
| `contains` / `notContains` | `value` or `ref` | substring, or array membership |
| `in` / `notIn` | `value` (list) | the value is one of the list |
| `countEquals` `countAtLeast` `countAtMost` | `value` (integer) | on the number of selected values — or, when a path without `[*]`/`**` lands on one array (an `all: true` extractor), on that array's elements |

`ref` compares against another selected value instead of a literal
(`{ select: extract.cartTotal, op: equals, ref: response.body.json.total }`).

When a path selects several values, `quantifier` decides: **`all`** (default) — at least
one value is selected and every one satisfies; **`any`** — at least one satisfies;
**`none`** — no value satisfies (an empty selection qualifies).

**No assertion passes vacuously.** A clause whose `when` matched no request in a run is
reported as **unexercised**, never as passed.

## Extractors — values from HTML, JSON, headers and text

Clauses never read prose. A value that lives in an HTML page is extracted first:

```yaml
extract:
  - name: cartTotal
    when: { method: GET, path: "^/cart$" }
    from: html                    # html | json | header | text
    selector: ".order-total"      # a CSS selector for html; a regex with one group for text
    as: decimal                   # string | decimal | integer | boolean
    culture: pl-PL                # how numbers in the page are written; default invariant
```

With `compare.html: extracts` (the default) an HTML page is compared between the two
versions **only through its extracted values** — markup is not behaviour. Use
`compare.html: text` to compare pages as text.

## Normalization — differences that do not matter

```yaml
normalize:
  ignore:     [response.body.json.generatedAt]
  tolerances: [{ path: response.body.json.total, absolute: 0.01 }]
  sets:       [{ path: response.body.json.items, key: sku }]   # order does not matter
  masks:
    timestamps: true      # ISO-8601 date-times become <timestamp>
    guids: true           # GUIDs become <guid>
    patterns:
      - { name: csrf, regex: 'value="[^"]{20,}"', replacement: 'value="<token>"' }
```

A difference explained by one of these rules makes an exchange **equal under
contract**; a difference nothing explains is a **regression** until a person adds a rule
or a clause (see [ADR 0003](../adr/0003-verdict-classes.md)).
