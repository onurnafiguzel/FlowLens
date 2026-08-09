<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Inventory

## Endpoint'ler

| Endpoint | Tablo | Tanım | Akış |
|---|---:|---|---|
| `GET /api/inventory/reservations/{id:guid}` | 1 | `src/Modules/Inventory/ModularCommerce.Inventory.Api/Endpoints/ReservationEndpoints.cs:26` | [diyagram](../flows/get-api-inventory-reservations-id-guid.md) |
| `GET /api/inventory/stock/{productId:guid}` | 1 | `src/Modules/Inventory/ModularCommerce.Inventory.Api/Endpoints/StockEndpoints.cs:17` | [diyagram](../flows/get-api-inventory-stock-productid-guid.md) |
| `POST /api/inventory/dev/reservations/{id:guid}/expire-now` | 1 | `src/Modules/Inventory/ModularCommerce.Inventory.Api/Endpoints/StockEndpoints.cs:46` | [diyagram](../flows/post-api-inventory-dev-reservations-id-guid-expire-now.md) |
| `POST /api/inventory/reservations` | 2 | `src/Modules/Inventory/ModularCommerce.Inventory.Api/Endpoints/ReservationEndpoints.cs:14` | [diyagram](../flows/post-api-inventory-reservations.md) |
| `PUT /api/inventory/dev/stock/{productId:guid}` | 2 | `src/Modules/Inventory/ModularCommerce.Inventory.Api/Endpoints/StockEndpoints.cs:32` | [diyagram](../flows/put-api-inventory-dev-stock-productid-guid.md) |

## Tablolar

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `inventory.reservations` | WR | `CreatedAtUtc`, `ExpiresAtUtc`, `Id`, `ProductId`, `Quantity`, `Status`, `StockItemId` | `src/Modules/Inventory/ModularCommerce.Inventory.Infrastructure/Persistence/Configurations/ReservationConfiguration.cs:11` |
| `inventory.stock_items` | WR | `CreatedAtUtc`, `Id`, `OnHand`, `ProductId`, `Reserved`, `UpdatedAtUtc` | `src/Modules/Inventory/ModularCommerce.Inventory.Infrastructure/Persistence/Configurations/StockItemConfiguration.cs:11` |

`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.

## Event'ler

Bu modül integration event tanımlamıyor.

## Bağımlılıklar

**Bu modülün dokunduğu modüller:**

- `Ordering` — sözleşme, 1 çağrı<br>  `ReservationTtlSweeper.SweepBatchAsync -> IOrderReservationReconciler.ClassifyAsync (src/Modules/Ordering/ModularCommerce.Ordering.Contracts/IOrderReservationReconciler.cs:11)`

**Bu modüle dokunanlar:**

- `Ordering` — sözleşme, 4 çağrı<br>  `CancelOrderHandler.HandleAsync -> IStockReservationService.ReturnAsync (src/Modules/Inventory/ModularCommerce.Inventory.Contracts/IStockReservationService.cs:19)`
- `Shared` — **doğrudan referans (⚠ ihlal adayı)**, 1 çağrı<br>  `IDataSeeder.SeedAsync -> InventoryDataSeeder.SeedAsync (src/Modules/Inventory/ModularCommerce.Inventory.Infrastructure/Persistence/Seed/InventoryDataSeeder.cs:18)`

## Bilinen sınırlar

- `raw SQL reaches the database outside the model, so no table edge: context.Database.ExecuteSqlAsync at src/Modules/Inventory/ModularCommerce.Inventory.Infrastructure/Persistence/Strategies/NaiveReservationStrategy.cs:37`
