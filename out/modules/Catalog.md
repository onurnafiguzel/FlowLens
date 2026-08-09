<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Catalog

## Endpoint'ler

| Endpoint | Tablo | Tanım | Akış |
|---|---:|---|---|
| `GET /api/catalog/products` | 1 | `src/Modules/Catalog/ModularCommerce.Catalog.Api/Endpoints/ProductEndpoints.cs:24` | [diyagram](../flows/get-api-catalog-products.md) |
| `GET /api/catalog/products/{id:guid}` | 1 | `src/Modules/Catalog/ModularCommerce.Catalog.Api/Endpoints/ProductEndpoints.cs:33` | [diyagram](../flows/get-api-catalog-products-id-guid.md) |
| `POST /api/catalog/products` | 3 | `src/Modules/Catalog/ModularCommerce.Catalog.Api/Endpoints/ProductEndpoints.cs:43` | [diyagram](../flows/post-api-catalog-products.md) |
| `PUT /api/catalog/products/{id:guid}` | 3 | `src/Modules/Catalog/ModularCommerce.Catalog.Api/Endpoints/ProductEndpoints.cs:54` | [diyagram](../flows/put-api-catalog-products-id-guid.md) |

## Tablolar

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `catalog.outbox_messages` | WR | `Error`, `ProcessedOnUtc`, `RetryCount` | `src/Modules/Catalog/ModularCommerce.Catalog.Infrastructure/Outbox/OutboxMessageConfiguration.cs:10` |
| `catalog.products` | WR | `CreatedAtUtc`, `Description`, `Id`, `IsActive`, `Name`, `Sku`, `StockQuantity`, `UpdatedAtUtc`, `price_amount`, `price_currency` | `src/Modules/Catalog/ModularCommerce.Catalog.Infrastructure/Persistence/Configurations/ProductConfiguration.cs:11` |

`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.

## Event'ler

| Event | Yayınlanıyor | Tüketiciler | Tanım |
|---|---|---|---|
| `ProductCreated` | evet | `ProductChangedConsumer.Consume` | `src/Modules/Catalog/ModularCommerce.Catalog.Contracts/IntegrationEvents/ProductCreated.cs:7` |
| `ProductUpdated` | evet | `ProductChangedConsumer.Consume` | `src/Modules/Catalog/ModularCommerce.Catalog.Contracts/IntegrationEvents/ProductUpdated.cs:4` |

## Bağımlılıklar

**Bu modülün dokunduğu modüller:**

- `Discovery` — event, 2 çağrı<br>  `ProductCreated -> ProductChangedConsumer.Consume (src/Modules/Discovery/ModularCommerce.Discovery.Api/Consumers/ProductChangedConsumer.cs:15)`

**Bu modüle dokunanlar:**

- `Ordering` — sözleşme, 1 çağrı<br>  `CheckoutHandler.HandleAsync -> IProductReader.GetByIdsAsync (src/Modules/Catalog/ModularCommerce.Catalog.Contracts/IProductReader.cs:4)`
- `Shared` — **doğrudan referans (⚠ ihlal adayı)**, 1 çağrı<br>  `IDataSeeder.SeedAsync -> CatalogDataSeeder.SeedAsync (src/Modules/Catalog/ModularCommerce.Catalog.Infrastructure/Persistence/Seed/CatalogDataSeeder.cs:14)`

## Bilinen sınırlar

Bu sayfa için kaydedilmiş bir sınır yok.
