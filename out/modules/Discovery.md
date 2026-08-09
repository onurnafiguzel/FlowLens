<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Discovery

## Endpoint'ler

| Endpoint | Tablo | Tanım | Akış |
|---|---:|---|---|
| `POST /api/discovery/search` | 0 | `src/Modules/Discovery/ModularCommerce.Discovery.Api/Endpoints/SearchEndpoints.cs:17` | [diyagram](../flows/post-api-discovery-search.md) |

## Tablolar

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `discovery.product_embeddings` | W | `ProductId`, `SourceTextHash`, `UpdatedAtUtc` | `src/Modules/Discovery/ModularCommerce.Discovery.Infrastructure/Persistence/Configurations/ProductEmbeddingConfiguration.cs:11` |

`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.

## Event'ler

Bu modül integration event tanımlamıyor.

## Bağımlılıklar

**Bu modülün dokunduğu modüller:**

yok.

**Bu modüle dokunanlar:**

- `Catalog` — event, 2 çağrı<br>  `ProductCreated -> ProductChangedConsumer.Consume (src/Modules/Discovery/ModularCommerce.Discovery.Api/Consumers/ProductChangedConsumer.cs:15)`

## Bilinen sınırlar

- `raw SQL reaches the database outside the model, so no table edge: dataSource.CreateCommand at src/Modules/Discovery/ModularCommerce.Discovery.Infrastructure/Persistence/ProductVectorRepository.cs:26`
- `raw SQL reaches the database outside the model, so no table edge: dataSource.CreateCommand at src/Modules/Discovery/ModularCommerce.Discovery.Infrastructure/Persistence/ProductVectorRepository.cs:40`
- `raw SQL reaches the database outside the model, so no table edge: dataSource.CreateCommand at src/Modules/Discovery/ModularCommerce.Discovery.Infrastructure/Persistence/ProductVectorRepository.cs:60`
