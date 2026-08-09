<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# PUT /api/cart/items/{productId:guid}

**Modül:** Cart · **Tanım:** `src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs:45`

```mermaid
flowchart TD
  n0["Cart · UpdateItemQuantityHandler.HandleAsync"]
  n1["Cart · CachingCartRepository.GetAsync (ambiguous)"]
  n2["Cart · CachingCartRepository.SaveAsync (ambiguous)"]
  n3["Cart · PostgresCartRepository.GetAsync (ambiguous)"]
  n4["Cart · PostgresCartRepository.IsDatabaseUnavailable"]
  n5["Cart · PostgresCartRepository.SaveAsync (ambiguous)"]
  n6[["Cart · PUT /api/cart/items/{productId:guid}"]]
  n7[("Cart · cart.carts")]

  n0 -->|"1"| n1
  n0 -->|"1"| n3
  n0 -->|"2"| n2
  n0 -->|"2"| n5
  n1 --> n3
  n2 --> n5
  n3 --> n4
  n3 ==>|"CartRecord"| n7
  n5 --> n4
  n5 ==> n7
  n6 --> n0

  classDef unseen stroke-dasharray: 4 4,stroke-width:2px
  class n3,n4,n5 unseen
```


> **Numaralar kaynak kodda yazılma sırasıdır**, çalışma sırası değil —
> koşullu dallar, döngüler ve erken `return`'ler ikisini ayırır.
> **`koşullu` işaretli adımlar hiç koşmayabilir**, ve bir `if`/ternary'nin iki
> dalındaki adımlar birbirini dışlar — ikisi birden koşmaz.
> Aynı numarayı taşıyan kutular **tek bir çağrıdan** gelir.

## Çağrı sırası

**UpdateItemQuantityHandler.HandleAsync** — `src/Modules/Cart/ModularCommerce.Cart.Application/Carts/UpdateItemQuantity/UpdateItemQuantityHandler.cs:12`

1. `UpdateItemQuantityHandler.cs:24` → `CachingCartRepository.GetAsync`, `PostgresCartRepository.GetAsync`
2. `UpdateItemQuantityHandler.cs:42` → `CachingCartRepository.SaveAsync`, `PostgresCartRepository.SaveAsync`

**PostgresCartRepository.GetAsync** — `src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:10`

1. `PostgresCartRepository.cs:30` *(koşullu)* → `PostgresCartRepository.IsDatabaseUnavailable`

- `cart.carts` — kaynakta bir çağrı ifadesi yok (veri kenarı ya da arayüzden implementasyona geçiş), çağrı yeri kaydedilmedi

**PostgresCartRepository.SaveAsync** — `src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:36`

1. `PostgresCartRepository.cs:65` *(koşullu)* → `PostgresCartRepository.IsDatabaseUnavailable`

- `cart.carts` — kaynakta bir çağrı ifadesi yok (veri kenarı ya da arayüzden implementasyona geçiş), çağrı yeri kaydedilmedi

## Veri katmanı

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `cart.carts` | WR | `CustomerId`, `Items`, `UpdatedAtUtc` | `src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/Configurations/CartConfiguration.cs:10` |

## Diyagram neyi göstermiyor

Gösterilen **8** node; ham yürüyüş **34** node'a ulaşıyor. Gizlenen: 16 ara çağrı, 8 utility, 2 arayüz bildirimi, 0 veriye ulaşmayan dal.

Tam liste: `flowlens trace "PUT /api/cart/items/{productId:guid}"`

## Bilinen sınırlar

- **unmapped-column** — Yazilan bir property'nin kolonu yok (Ignore edilmis, hesaplanan ya da JSON alani).<br>`property written but not mapped to a column: CartItemRecord.AddedAtUtc at src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:41 (no column)`, `property written but not mapped to a column: CartItemRecord.ProductId at src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:41 (no column)`, `property written but not mapped to a column: CartItemRecord.Quantity at src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:41 (no column)`
- **second-class-evidence** — Bazi yazma iddialari dolayli kanita dayaniyor: entity insasi ya da parametreli bir SaveChanges. Dogru olabilir, ama dogrudan okunmus degil.<br>`new CartItemRecord(...) at src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:41`, `new CartRecord(...) at src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:49`
- **ambiguous-implementation** — Bir interface cagrisi birden fazla implementasyona aciliyor. Graph HANGISININ kostugunu KAYDETMIYOR: dekorator zinciri ve koleksiyon enjeksiyonunda hepsi kosar (dogru cevap), config anahtariyla secilende yalniz biri kosar (asiri-yaklasim). Olculen bedel: veri katmaninda 0 tablo/0 kolon, ExternalCall'da 1/1 yanlis pozitif.<br>`src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/CachingCartRepository.cs:26`, `src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/CachingCartRepository.cs:9`, `src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:10`, `src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:36`

> Diyagram dar görünüyorsa tıklayarak büyütebilir veya
> [mermaid.live'da açabilirsiniz](https://mermaid.live/edit#pako:eAEBSgO1_HsiY29kZSI6ImZsb3djaGFydCBURFxuICBuMFtcIkNhcnQgwrcgVXBkYXRlSXRlbVF1YW50aXR5SGFuZGxlci5IYW5kbGVBc3luY1wiXVxuICBuMVtcIkNhcnQgwrcgQ2FjaGluZ0NhcnRSZXBvc2l0b3J5LkdldEFzeW5jIChhbWJpZ3VvdXMpXCJdXG4gIG4yW1wiQ2FydCDCtyBDYWNoaW5nQ2FydFJlcG9zaXRvcnkuU2F2ZUFzeW5jIChhbWJpZ3VvdXMpXCJdXG4gIG4zW1wiQ2FydCDCtyBQb3N0Z3Jlc0NhcnRSZXBvc2l0b3J5LkdldEFzeW5jIChhbWJpZ3VvdXMpXCJdXG4gIG40W1wiQ2FydCDCtyBQb3N0Z3Jlc0NhcnRSZXBvc2l0b3J5LklzRGF0YWJhc2VVbmF2YWlsYWJsZVwiXVxuICBuNVtcIkNhcnQgwrcgUG9zdGdyZXNDYXJ0UmVwb3NpdG9yeS5TYXZlQXN5bmMgKGFtYmlndW91cylcIl1cbiAgbjZbW1wiQ2FydCDCtyBQVVQgL2FwaS9jYXJ0L2l0ZW1zL3twcm9kdWN0SWQ6Z3VpZH1cIl1dXG4gIG43WyhcIkNhcnQgwrcgY2FydC5jYXJ0c1wiKV1cblxuICBuMCAtLT58XCIxXCJ8IG4xXG4gIG4wIC0tPnxcIjFcInwgbjNcbiAgbjAgLS0-fFwiMlwifCBuMlxuICBuMCAtLT58XCIyXCJ8IG41XG4gIG4xIC0tPiBuM1xuICBuMiAtLT4gbjVcbiAgbjMgLS0-IG40XG4gIG4zID09PnxcIkNhcnRSZWNvcmRcInwgbjdcbiAgbjUgLS0-IG40XG4gIG41ID09PiBuN1xuICBuNiAtLT4gbjBcblxuICBjbGFzc0RlZiB1bnNlZW4gc3Ryb2tlLWRhc2hhcnJheTogNCA0LHN0cm9rZS13aWR0aDoycHhcbiAgY2xhc3MgbjMsbjQsbjUgdW5zZWVuXG4iLCJtZXJtYWlkIjoie1xuICBcInRoZW1lXCI6IFwiZGVmYXVsdFwiXG59IiwiYXV0b1N5bmMiOnRydWUsInVwZGF0ZURpYWdyYW0iOnRydWV9VscdcA).
