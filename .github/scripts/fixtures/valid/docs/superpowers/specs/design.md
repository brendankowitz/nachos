# Fixture design

## 1. Overview

See §1, §1.1, and §1.1.1.

### 1.1 Details

#### 1.1.1 Nested

[Guide](../../guide.md#hello-world)
[Repeated heading](../../guide.md#hello-world-1)
[Unicode](../../guide.md#caf%C3%A9)
[Self](#1-overview)
[Root relative](/docs/guide.md)
[Asset](../../asset%20name.txt?raw=true)
![Image](../../asset%20name.txt)
[Reference][guide]
[guide][]
[guide]

[guide]: ../../guide.md#hello-world "Guide"

| Name | Link |
| --- | --- |
| Guide | [Table link](../../guide.md) |

[External](https://unreachable.invalid/not-crawled)
[Mail](mailto:nobody@example.invalid)
[Protocol relative](//unreachable.invalid/ignored)

`[Example](missing.md)` and `§1` are code examples.

```markdown
[Example](missing.md)
[example]: missing.md
## 404. Not a heading
§1
```

    [Indented example](missing.md)
    §1

```mermaid
flowchart LR
  A["<b>Ready</b>"] --> B[Done]
```

~~~mermaid
sequenceDiagram
  Alice->>Bob: Hello
~~~
