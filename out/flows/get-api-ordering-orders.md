<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# GET /api/ordering/orders

**Modül:** Ordering · **Tanım:** `src/Modules/Ordering/ModularCommerce.Ordering.Api/Endpoints/OrderEndpoints.cs:52`

```mermaid
flowchart TD
  n0["Ordering · GetMyOrdersHandler.HandleAsync"]
  n1["Ordering · OrderQueries.GetMyOrdersAsync"]
  n2[["Ordering · GET /api/ordering/orders"]]
  n3[("Ordering · ordering.orders")]

  n0 --> n1
  n1 ==>|"Order"| n3
  n2 --> n0
```


## Veri katmanı

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `ordering.orders` | R | — | `src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Persistence/Configurations/OrderConfiguration.cs:13` |

## Diyagram neyi göstermiyor

Gösterilen **4** node; ham yürüyüş **9** node'a ulaşıyor. Gizlenen: 1 ara çağrı, 3 utility, 1 arayüz bildirimi, 0 veriye ulaşmayan dal.

Tam liste: `flowlens trace "GET /api/ordering/orders"`

## Bilinen sınırlar

Bu sayfa için kaydedilmiş bir sınır yok.

> Diyagram dar görünüyorsa tıklayarak büyütebilir veya
> [mermaid.live'da açabilirsiniz](https://mermaid.live/edit#pako:eAEBYwGc_nsiY29kZSI6ImZsb3djaGFydCBURFxuICBuMFtcIk9yZGVyaW5nIMK3IEdldE15T3JkZXJzSGFuZGxlci5IYW5kbGVBc3luY1wiXVxuICBuMVtcIk9yZGVyaW5nIMK3IE9yZGVyUXVlcmllcy5HZXRNeU9yZGVyc0FzeW5jXCJdXG4gIG4yW1tcIk9yZGVyaW5nIMK3IEdFVCAvYXBpL29yZGVyaW5nL29yZGVyc1wiXV1cbiAgbjNbKFwiT3JkZXJpbmcgwrcgb3JkZXJpbmcub3JkZXJzXCIpXVxuXG4gIG4wIC0tPiBuMVxuICBuMSA9PT58XCJPcmRlclwifCBuM1xuICBuMiAtLT4gbjBcbiIsIm1lcm1haWQiOiJ7XG4gIFwidGhlbWVcIjogXCJkZWZhdWx0XCJcbn0iLCJhdXRvU3luYyI6dHJ1ZSwidXBkYXRlRGlhZ3JhbSI6dHJ1ZX2xkXoa).
