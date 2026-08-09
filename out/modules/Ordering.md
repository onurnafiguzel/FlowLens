<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Ordering

## Endpoint'ler

| Endpoint | Tablo | Tanım | Akış |
|---|---:|---|---|
| `GET /api/ordering/orders` | 1 | `src/Modules/Ordering/ModularCommerce.Ordering.Api/Endpoints/OrderEndpoints.cs:52` | [diyagram](../flows/get-api-ordering-orders.md) |
| `GET /api/ordering/orders/{id:guid}` | 1 | `src/Modules/Ordering/ModularCommerce.Ordering.Api/Endpoints/OrderEndpoints.cs:42` | [diyagram](../flows/get-api-ordering-orders-id-guid.md) |
| `POST /api/ordering/checkout` | 12 | `src/Modules/Ordering/ModularCommerce.Ordering.Api/Endpoints/OrderEndpoints.cs:22` | [diyagram](../flows/post-api-ordering-checkout.md) |
| `POST /api/ordering/orders/{id:guid}/cancel` | 7 | `src/Modules/Ordering/ModularCommerce.Ordering.Api/Endpoints/OrderEndpoints.cs:61` | [diyagram](../flows/post-api-ordering-orders-id-guid-cancel.md) |

## Tablolar

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `ordering.order_lines` | W | `ProductId`, `ProductName`, `Quantity`, `ReservationId`, `id`, `order_id` | `src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Persistence/Configurations/OrderConfiguration.cs:31` |
| `ordering.order_status_history` | W | `FromStatus`, `OccurredAtUtc`, `ToStatus`, `TriggeredBy`, `id`, `order_id` | `src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Persistence/Configurations/OrderConfiguration.cs:54` |
| `ordering.orders` | WR | `CreatedAtUtc`, `CustomerId`, `Id`, `IdempotencyKey`, `Status`, `UpdatedAtUtc` | `src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Persistence/Configurations/OrderConfiguration.cs:13` |
| `ordering.outbox_messages` | WR | `Error`, `ProcessedOnUtc`, `RetryCount` | `src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Outbox/OutboxMessageConfiguration.cs:10` |

`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.

## Event'ler

| Event | Yayınlanıyor | Tüketiciler | Tanım |
|---|---|---|---|
| `OrderCancelled` | evet | **yok** | `src/Modules/Ordering/ModularCommerce.Ordering.Contracts/IntegrationEvents/OrderCancelled.cs:2` |
| `OrderPaid` | evet | `OrderPaidNotificationConsumer.Consume` | `src/Modules/Ordering/ModularCommerce.Ordering.Contracts/IntegrationEvents/OrderPaid.cs:2` |

## Bağımlılıklar

**Bu modülün dokunduğu modüller:**

- `Cart` — sözleşme, 2 çağrı<br>  `CheckoutHandler.HandleAsync -> ICartService.ClearAsync (src/Modules/Cart/ModularCommerce.Cart.Contracts/ICartService.cs:10)`
- `Catalog` — sözleşme, 1 çağrı<br>  `CheckoutHandler.HandleAsync -> IProductReader.GetByIdsAsync (src/Modules/Catalog/ModularCommerce.Catalog.Contracts/IProductReader.cs:4)`
- `Inventory` — sözleşme, 4 çağrı<br>  `CancelOrderHandler.HandleAsync -> IStockReservationService.ReturnAsync (src/Modules/Inventory/ModularCommerce.Inventory.Contracts/IStockReservationService.cs:19)`
- `Notification` — event, 1 çağrı<br>  `OrderPaid -> OrderPaidNotificationConsumer.Consume (src/Modules/Notification/ModularCommerce.Notification.Api/Consumers/OrderPaidNotificationConsumer.cs:10)`
- `Payment` — sözleşme, 2 çağrı<br>  `CancelOrderHandler.HandleAsync -> IPaymentService.RefundAsync (src/Modules/Payment/ModularCommerce.Payment.Contracts/IPaymentService.cs:7)`

**Bu modüle dokunanlar:**

- `Inventory` — sözleşme, 1 çağrı<br>  `ReservationTtlSweeper.SweepBatchAsync -> IOrderReservationReconciler.ClassifyAsync (src/Modules/Ordering/ModularCommerce.Ordering.Contracts/IOrderReservationReconciler.cs:11)`

## Bilinen sınırlar

Bu sayfa için kaydedilmiş bir sınır yok.
