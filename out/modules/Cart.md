<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Cart

## Endpoint'ler

| Endpoint | Tablo | Tanım | Akış |
|---|---:|---|---|
| `DELETE /api/cart/items/{productId:guid}` | 1 | `src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs:57` | [diyagram](../flows/delete-api-cart-items-productid-guid.md) |
| `GET /api/cart` | 1 | `src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs:25` | [diyagram](../flows/get-api-cart.md) |
| `POST /api/cart/items` | 1 | `src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs:34` | [diyagram](../flows/post-api-cart-items.md) |
| `PUT /api/cart/items/{productId:guid}` | 1 | `src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs:45` | [diyagram](../flows/put-api-cart-items-productid-guid.md) |

## Tablolar

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `cart.carts` | WR | `CustomerId`, `Items`, `UpdatedAtUtc` | `src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/Configurations/CartConfiguration.cs:10` |

`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.

## Event'ler

Bu modül integration event tanımlamıyor.

## Bağımlılıklar

**Bu modülün dokunduğu modüller:**

yok.

**Bu modüle dokunanlar:**

- `Ordering` — sözleşme, 2 çağrı<br>  `CheckoutHandler.HandleAsync -> ICartService.ClearAsync (src/Modules/Cart/ModularCommerce.Cart.Contracts/ICartService.cs:10)`

## Bilinen sınırlar

- `property written but not mapped to a column: CartItemRecord.AddedAtUtc at src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:41 (no column)`
- `property written but not mapped to a column: CartItemRecord.ProductId at src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:41 (no column)`
- `property written but not mapped to a column: CartItemRecord.Quantity at src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/PostgresCartRepository.cs:41 (no column)`
