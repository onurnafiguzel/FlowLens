<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# ModularCommerce — akış haritası

`flowlens docs` ile `graph.json`'dan üretildi. Elle düzenlenmez; her üretim
aynı girdiden aynı baytları verir.

> **Kapsam uyarısı.** FlowLens'in gördüğü, EF Core'un gördüğüdür. Ham SQL ile
> erişilen tablolar ve ilişkisel olmayan depolar burada **yok** — ama nerede
> bakılamadığı ilgili sayfada `file:line` ile yazılı.

## Modüller

| Modül | Endpoint | Tablo | Event |
|---|---:|---:|---:|
| [Cart](modules/Cart.md) | 4 | 1 | 0 |
| [Catalog](modules/Catalog.md) | 4 | 2 | 2 |
| [Discovery](modules/Discovery.md) | 1 | 1 | 0 |
| [Host](modules/Host.md) | 1 | 0 | 0 |
| [Identity](modules/Identity.md) | 4 | 2 | 0 |
| [Inventory](modules/Inventory.md) | 5 | 2 | 0 |
| [Notification](modules/Notification.md) | 1 | 2 | 0 |
| [Ordering](modules/Ordering.md) | 4 | 4 | 2 |
| [Payment](modules/Payment.md) | 1 | 2 | 0 |
| [Shared](modules/Shared.md) | 0 | 0 | 0 |

[Modül bağımlılık grafiği](modules/dependencies.md) — 9 kenar, 2 ihlal adayı.

## Akışlar

| Endpoint | Modül | Tablo | Node |
|---|---|---:|---:|
| [`DELETE /api/cart/items/{productId:guid}`](flows/delete-api-cart-items-productid-guid.md) | Cart | 1 | 10 |
| [`GET /api/cart`](flows/get-api-cart.md) | Cart | 1 | 6 |
| [`GET /api/catalog/products`](flows/get-api-catalog-products.md) | Catalog | 1 | 5 |
| [`GET /api/catalog/products/{id:guid}`](flows/get-api-catalog-products-id-guid.md) | Catalog | 1 | 5 |
| [`GET /api/inventory/reservations/{id:guid}`](flows/get-api-inventory-reservations-id-guid.md) | Inventory | 1 | 4 |
| [`GET /api/inventory/stock/{productId:guid}`](flows/get-api-inventory-stock-productid-guid.md) | Inventory | 1 | 4 |
| [`GET /api/notification/dev/logs/{orderId:guid}`](flows/get-api-notification-dev-logs-orderid-guid.md) | Notification | 1 | 2 |
| [`GET /api/ordering/orders`](flows/get-api-ordering-orders.md) | Ordering | 1 | 4 |
| [`GET /api/ordering/orders/{id:guid}`](flows/get-api-ordering-orders-id-guid.md) | Ordering | 1 | 4 |
| [`GET /api/payment/dev/payments`](flows/get-api-payment-dev-payments.md) | Payment | 1 | 2 |
| [`POST /api/cart/items`](flows/post-api-cart-items.md) | Cart | 1 | 8 |
| [`POST /api/catalog/products`](flows/post-api-catalog-products.md) | Catalog | 3 | 11 |
| [`POST /api/identity/login`](flows/post-api-identity-login.md) | Identity | 2 | 6 |
| [`POST /api/identity/logout`](flows/post-api-identity-logout.md) | Identity | 1 | 4 |
| [`POST /api/identity/refresh`](flows/post-api-identity-refresh.md) | Identity | 2 | 7 |
| [`POST /api/identity/signup`](flows/post-api-identity-signup.md) | Identity | 1 | 5 |
| [`POST /api/inventory/dev/reservations/{id:guid}/expire-now`](flows/post-api-inventory-dev-reservations-id-guid-expire-now.md) | Inventory | 1 | 2 |
| [`POST /api/inventory/reservations`](flows/post-api-inventory-reservations.md) | Inventory | 2 | 4 |
| [`POST /api/ordering/checkout`](flows/post-api-ordering-checkout.md) | Ordering | 12 | 24 |
| [`POST /api/ordering/orders/{id:guid}/cancel`](flows/post-api-ordering-orders-id-guid-cancel.md) | Ordering | 7 | 12 |
| [`PUT /api/cart/items/{productId:guid}`](flows/put-api-cart-items-productid-guid.md) | Cart | 1 | 8 |
| [`PUT /api/catalog/products/{id:guid}`](flows/put-api-catalog-products-id-guid.md) | Catalog | 3 | 12 |
| [`PUT /api/inventory/dev/stock/{productId:guid}`](flows/put-api-inventory-dev-stock-productid-guid.md) | Inventory | 2 | 6 |

### Veri katmanına dokunmayan (2)

Bunlar eksik değil — ölçüldü ve hiçbir tabloya ulaşmıyorlar.

- [`GET /`](flows/get.md) — `src/Bootstrapper/ModularCommerce.Host/Program.cs:79`
- [`POST /api/discovery/search`](flows/post-api-discovery-search.md) — `src/Modules/Discovery/ModularCommerce.Discovery.Api/Endpoints/SearchEndpoints.cs:17`
