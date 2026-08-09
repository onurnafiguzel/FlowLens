<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Modül bağımlılık grafiği

Bir modülün diğerine bağımlı sayılma kuralı, dokunduğu **katmana** dayanır —
modül adına değil. Katman, node id'sindeki `ModularCommerce.<Modül>.<Katman>`
segmentinden okunur.

| Kategori | Kural | Ok |
|---|---|---|
| **Sözleşme çağrısı** — meşru | hedef katman `Contracts`, kenar `CALLS` | düz `-->` |
| **Event** — meşru, en gevşek bağ | `PUBLISHES` / `CONSUMES` | kesikli `-.->` |
| **Doğrudan referans** — ⚠ ihlal adayı | hedef katman `Application` / `Infrastructure` / `Domain` | kalın `==>` |

```mermaid
flowchart LR
  n0["Cart"]
  n1["Catalog"]
  n2["Discovery"]
  n3["Inventory"]
  n4["Notification"]
  n5["Ordering"]
  n6["Payment"]
  n7["Shared"]

  n1 -.->|"event x2"| n2
  n3 -->|"contract x1"| n5
  n5 -->|"contract x2"| n0
  n5 -->|"contract x1"| n1
  n5 -->|"contract x4"| n3
  n5 -.->|"event x1"| n4
  n5 -->|"contract x2"| n6
  n7 ==>|"direct x1"| n1
  n7 ==>|"direct x1"| n3

  classDef flagged stroke-width:3px
  class n1,n3 flagged
```


## İhlal adayları

`Contracts` dışından doğrudan referanslar. **Bu bir hüküm değil, bir işaret** —
kasıtlı bir tercih olabilir; kararı okuyan verir.

### `Shared` → `Catalog`

- `IDataSeeder.SeedAsync -> CatalogDataSeeder.SeedAsync (src/Modules/Catalog/ModularCommerce.Catalog.Infrastructure/Persistence/Seed/CatalogDataSeeder.cs:14)`

### `Shared` → `Inventory`

- `IDataSeeder.SeedAsync -> InventoryDataSeeder.SeedAsync (src/Modules/Inventory/ModularCommerce.Inventory.Infrastructure/Persistence/Seed/InventoryDataSeeder.cs:18)`

## Shared

`Shared`'a giden **204** kenar diyagrama çizilmedi: 8 modülün tamamı `Shared.Kernel`'e bağlı ve bu tasarım gereği. Hepsini çizmek `Shared`'ı her şeye bağlı bir merkez yapar ve hiçbir şey söylemez.

**Tersi çizilir:** `Shared` bir modülün içine çağrı yapıyorsa bağımlılık ters yöndedir
ve ihlal adayı olarak işaretlenir.
