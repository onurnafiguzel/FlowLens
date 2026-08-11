# FlowLens — Faz 7 eval raporu

Bu rapor testlerin cevabını vermez. Testler kodun **çalıştığını** doğrular; buradaki sayılar cevabın **doğru ve tam olduğunu** ölçer. Beklenen değerler ModularCommerce kaynağından elle çıkarıldı ve `evals/questions.json` runner yazılmadan ÖNCE commit'lendi.

## 1. Kaynak

| | |
|---|---|
| Graph | `C:\Users\USER\source\repos\FlowLens\graph.json` |
| Düğüm / kenar | 415 / 966 |
| Soru | 22 |
| Çözülemeyen selector | 0 |
| EF dışı tablo | 1 — `discovery.product_embeddings` |

> **EF içi / EF dışı nasıl ayrıldı:** bir tabloya EF'in kendisinin SQL ürettiği bir mekanizmayla (`DbSetProperty`, `SetOfT`, `FluentChainHead`, `ExecuteUpdateSetProperty`, `SaveChangesInterceptor`, `OwnedCollectionAdd`, `RowInsert`) ulaşılıyorsa **EF içi**. Yalnız inşadan ya da change-tracker çıkarımından ulaşılıyorsa **EF dışı**. Elle liste değil, graph'tan türetiliyor.

> **Oracle'ın kolon kuralı:** INSERT satirin tum eslenmis kolonlarini yazar, DEGERI VERITABANININ URETTIGI kolonlar haric (IsRowVersion ve IdentityByDefault). Ayirt edici test: EF onu RETURNING ile geri okuyorsa yazmiyordur. UPDATE yalniz atananlari yazar. DELETE hicbir kolon yazmaz.

## 2. Metrikler

Recall önceliklidir: eksik bir kolon, fazladan bir kolondan tehlikelidir.

| Seviye | Kapsam | Beklenen | Bulunan | Recall | Dönen | Fazladan | Precision |
|---|---|---:|---:|---:|---:|---:|---:|
| tablo | EF içi | 35 | 34 | %97.1 | 34 | 0 | %100.0 |
| tablo | EF dışı | 4 | 3 | %75.0 | 3 | 0 | %100.0 |
| tablo (erisim) | — | 37 | 31 | %83.8 | 37 | 0 | — |
| kolon-yazma | EF içi | 163 | 133 | %81.6 | 138 | 5 | %96.4 |
| kolon-yazma | EF dışı | 12 | 9 | %75.0 | 9 | 0 | %100.0 |
| kolon-okuma | EF dışı | 2 | 0 | %0.0 | 0 | 0 | — |
| kok | — | 34 | 26 | %76.5 | 26 | 0 | %100.0 |
| event | — | 5 | 3 | %60.0 | 3 | 0 | %100.0 |
| dis depo | — | 5 | 0 | %0.0 | 1 | 1 | %0.0 |
| dugum | — | 0 | 0 | — | 0 | 0 | — |
| sinir kodu | — | 12 | 11 | %91.7 | 19 | 0 | — |

> **`kolon-yazma` ve `kolon-okuma` toplanmaz.** `AnswerBuilder.ColumnsByTable` yalnız `Writes` kenarlarına bakıyor, dolayısıyla okunan bir kolonun recall'ı YAPISAL olarak 0. İkisini tek sayıya indirmek, yazma recall'ını ilgisiz bir sebeple aşağı çeker ve F9'un gerçek boyutunu gizlerdi.

> `sınır kodu` satırı bir **varlık** iddiasıdır, küme eşitliği değil: sorular bulunması ZORUNLU kodları sayar, cevabın taşıyabileceği kodların tamamını değil. Bu yüzden precision hesaplanmaz.

> `tablo (erisim R/W)` satırının popülasyonu **bulunan tablolardır**, uyuşmazlıklar değil. `Bulunan` sütunu erişimi doğru raporlanan tablo sayısıdır; aradaki fark uyuşmazlık sayısıdır ve her biri §6'da adıyla yazılıdır. Bir tablo bulunamadıysa erişimi hiç kontrol edilmez — aynı kayıp iki kez sayılmaz.

## 3. Kanıt skoru — üç sonuç

Doğru cevap ile doğru sebep aynı şey değil. İkiye indirgenirse F7 sınıfı ya kayıp görünür (yanlış) ya kaybolur (yanlış).

| Sonuç | Anlamı | Adet | Pay |
|---|---|---:|---:|
| `beklenen-mekanizmayla` | doğru cevap, doğru kanıt (`Direct` / `RowLevel`) | 142 | %81.1 |
| `farklı-ama-geçerli` | doğru cevap, ikinci sınıf kanıt (`Inferred` / `SecondClass`) | 0 | %0.0 |
| `bulunamadı` | recall kaybı | 33 | %18.9 |

> Yalnız **yazma** kolonları üzerinden hesaplanır. Okunan kolon hiçbir mekanizma taşımaz, dolayısıyla hepsi `bulunamadı` olur ve recall satırını tekrarlamaktan başka bir şey söylemezdi.

## 4. Kategori kırılımı — popülasyonla birlikte

| Sınıf | Soru | Popülasyon | Temsilci mi | Beklenen | Bulunan | Kaçırılan |
|---|---:|---:|---|---:|---:|---:|
| P1 | 8 | 25 | evet | 193 | 162 | 31 |
| P2 | 3 | 4 | **HAYIR** — tek örnek, kategori değil o örnek ölçüldü | 27 | 25 | 2 |
| P3 | 2 | 5 | evet | 33 | 20 | 13 |
| P5 | 2 | 2 | evet | 11 | 9 | 2 |
| P6 | 1 | 3 | evet | 5 | 1 | 4 |
| P8 | 4 | 16 | evet | 25 | 21 | 4 |
| P9 | 1 | 97 | evet | 5 | 2 | 3 |
| P10 | 1 | 14 | evet | 10 | 10 | 0 |

## 5. Öngörü kutuları — 3×2

Birim **soru**, öngörü değil: bir öngörüyü belirli bir kaçırmaya bağlamak graph'ın taşımadığı bir eşleme gerektirirdi ve yedi öngörüye tek kayıp için kredi vermek olurdu. Tek tek atıf §6'daki tablodan elle yapılabilir.

| | gerçekleşti | gerçekleşmedi |
|---|---|---|
| **ongoruldu, sinir ACIK** | **13** · Q01, Q02, Q03, Q06, Q10, Q12, Q13, Q15, Q16, Q17, Q18, Q20, Q22<br>beklenen acik sinir - teyit | — (ONGORU BASTAN YANLISTI - bulgu) |
| **ongoruldu, sinir KAPANMISTI** | — (REGRESYON - kapanmis sinir geri acildi) | — (kapanis korunuyor) |
| **ongorulmedi** | — (BU FAZIN ASIL BULGUSU) | **9** · Q04, Q05, Q07, Q08, Q09, Q11, Q14, Q19, Q21<br>normal |

> Sol-alt kutu (öngörülmedi + gerçekleşti) doluysa eval işini yapmıştır: çıktıyı kopyalayarak bir kaçırma öngörüsü üretilemez.

## 6. Soru soru

### Q01 — Checkout akisi hangi tablolara ve kolonlara dokunuyor?

`POST /api/ordering/checkout` · forward · kategori `ef-forward` · popülasyon `P1` = 25

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 12 | 12 | 0 |
| tablo — erisim (R/W) | — | 12 | 10 | — |
| kolon-yazma — kolon | EF içi | 68 | 59 | 3 |
| dis depo — dis depo | — | 1 | 0 | 0 |

**Öngörülen kaçırmalar:** `F2` · `F4` · `F5` · `L16-4` · `L17` · `L19` · `L21`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- dis depo: RedisCartCache
- kolon-yazma: ordering.order_lines.Currency
- kolon-yazma: ordering.order_lines.UnitPrice
- kolon-yazma: ordering.outbox_messages.Content
- kolon-yazma: ordering.outbox_messages.Error
- kolon-yazma: ordering.outbox_messages.Id
- kolon-yazma: ordering.outbox_messages.OccurredOnUtc
- kolon-yazma: ordering.outbox_messages.ProcessedOnUtc
- kolon-yazma: ordering.outbox_messages.RetryCount
- kolon-yazma: ordering.outbox_messages.Type
- tablo: ordering.order_lines bekleniyor RW, gelen W
- tablo: ordering.order_status_history bekleniyor RW, gelen W

Fazladan gelenler (precision kaybı):

- kolon-yazma: ordering.order_lines.id
- kolon-yazma: ordering.order_status_history.id
- kolon-yazma: payment.payment_attempts.id

### Q02 — Siparis iptali akisi hangi tablolara ve kolonlara dokunuyor?

`POST /api/ordering/orders/{id:guid}/cancel` · forward · kategori `ef-forward` · popülasyon `P1` = 25

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 8 | 7 | 0 |
| tablo — erisim (R/W) | — | 7 | 6 | — |
| kolon-yazma — kolon | EF içi | 27 | 20 | 2 |

**Öngörülen kaçırmalar:** `F4` · `F5` · `L16-4` · `L19` · `L21`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- kolon-yazma: ordering.outbox_messages.Content
- kolon-yazma: ordering.outbox_messages.Error
- kolon-yazma: ordering.outbox_messages.Id
- kolon-yazma: ordering.outbox_messages.OccurredOnUtc
- kolon-yazma: ordering.outbox_messages.ProcessedOnUtc
- kolon-yazma: ordering.outbox_messages.RetryCount
- kolon-yazma: ordering.outbox_messages.Type
- tablo: ordering.order_lines
- tablo: ordering.order_status_history bekleniyor RW, gelen W

Fazladan gelenler (precision kaybı):

- kolon-yazma: ordering.order_status_history.id
- kolon-yazma: payment.payment_attempts.id

### Q03 — Yeni urun olusturma akisi hangi tablolara ve kolonlara dokunuyor?

`POST /api/catalog/products` · forward · kategori `ef-forward` · popülasyon `P1` = 25

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 2 | 2 | 0 |
| tablo — tablo | EF dışı | 1 | 1 | 0 |
| tablo — erisim (R/W) | — | 3 | 2 | — |
| kolon-yazma — kolon | EF içi | 17 | 10 | 0 |
| kolon-yazma — kolon | EF dışı | 4 | 3 | 0 |

**Öngörülen kaçırmalar:** `F5` · `F7` · `L16-4` · `L5` · `L6`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- kolon-yazma: catalog.outbox_messages.Content
- kolon-yazma: catalog.outbox_messages.Error
- kolon-yazma: catalog.outbox_messages.Id
- kolon-yazma: catalog.outbox_messages.OccurredOnUtc
- kolon-yazma: catalog.outbox_messages.ProcessedOnUtc
- kolon-yazma: catalog.outbox_messages.RetryCount
- kolon-yazma: catalog.outbox_messages.Type
- kolon-yazma: discovery.product_embeddings.Embedding
- tablo: discovery.product_embeddings bekleniyor RW, gelen W

### Q04 — Stok rezervasyonu akisi hangi tablolara ve kolonlara dokunuyor?

`POST /api/inventory/reservations` · forward · kategori `ef-forward` · popülasyon `P1` = 25

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 2 | 2 | 0 |
| tablo — erisim (R/W) | — | 2 | 2 | — |
| kolon-yazma — kolon | EF içi | 9 | 9 | 0 |

Kaçırma **yok**.

### Q05 — Login akisi hangi tablolara ve kolonlara dokunuyor?

`POST /api/identity/login` · forward · kategori `ef-forward` · popülasyon `P1` = 25

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 2 | 2 | 0 |
| tablo — erisim (R/W) | — | 2 | 2 | — |
| kolon-yazma — kolon | EF içi | 6 | 6 | 0 |

Kaçırma **yok**.

### Q06 — Sepette urun miktari guncelleme akisi hangi tablolara ve kolonlara dokunuyor?

`PUT /api/cart/items/{productId:guid}` · forward · kategori `ef-forward` · popülasyon `P1` = 25

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 1 | 1 | 0 |
| tablo — erisim (R/W) | — | 1 | 1 | — |
| kolon-yazma — kolon | EF içi | 3 | 3 | 0 |
| dis depo — dis depo | — | 1 | 0 | 0 |

**Öngörülen kaçırmalar:** `F2` · `L17`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- dis depo: RedisCartCache

### Q07 — Urun listeleme akisi hangi tablolara dokunuyor?

`GET /api/catalog/products` · forward · kategori `ef-forward` · popülasyon `P1` = 25

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 1 | 1 | 0 |
| tablo — erisim (R/W) | — | 1 | 1 | — |

Kaçırma **yok**.

### Q08 — Kok endpoint hangi tablolara dokunuyor?

`GET /` · forward · kategori `ef-forward` · popülasyon `P1` = 25

Kaçırma **yok**.

### Q09 — OrderPaid event'i tuketildikten sonra hangi tablolara ve kolonlara dokunuluyor?

`event:ModularCommerce.Ordering.Contracts.IntegrationEvents.OrderPaid` · forward · kategori `event-bridge` · popülasyon `P2` = 4

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 2 | 2 | 0 |
| tablo — erisim (R/W) | — | 2 | 2 | — |
| kolon-yazma — kolon | EF içi | 11 | 11 | 0 |
| event — tuketen | — | 1 | 1 | 0 |
| dis depo — dis depo | — | 0 | 0 | 0 |
| sinir — sinir kodu | — | 1 | 1 | — |

Kaçırma **yok**.

### Q10 — ProductCreated event'i tuketildikten sonra hangi tablolara ve kolonlara dokunuluyor?

`event:ModularCommerce.Catalog.Contracts.IntegrationEvents.ProductCreated` · forward · kategori `event-bridge` · popülasyon `P2` = 4

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF dışı | 1 | 1 | 0 |
| tablo — erisim (R/W) | — | 1 | 0 | — |
| kolon-yazma — kolon | EF dışı | 4 | 3 | 0 |
| event — tuketen | — | 1 | 1 | 0 |
| sinir — sinir kodu | — | 3 | 3 | — |

**Öngörülen kaçırmalar:** `F7` · `L5` · `L6`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- kolon-yazma: discovery.product_embeddings.Embedding
- tablo: discovery.product_embeddings bekleniyor RW, gelen W

### Q11 — OrderCancelled event'ini kim tuketiyor ve tuketici hangi tablolara dokunuyor?

`event:ModularCommerce.Ordering.Contracts.IntegrationEvents.OrderCancelled` · forward · kategori `event-bridge` · popülasyon `P2` = 1 (**temsilci değil**)

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| event — tuketen | — | 0 | 0 | 0 |
| dugum — dugum | — | 0 | 0 | 0 |

Kaçırma **yok**.

### Q12 — Arama akisi hangi tablolari ve kolonlari OKUYOR?

`POST /api/discovery/search` · forward · kategori `raw-sql` · popülasyon `P3` = 5

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF dışı | 1 | 0 | 0 |
| kolon-okuma — kolon | EF dışı | 2 | 0 | 0 |
| sinir — sinir kodu | — | 1 | 1 | — |

**Öngörülen kaçırmalar:** `F6` · `F9` · `L18-2` · `L6`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- kolon-okuma: discovery.product_embeddings.Embedding
- kolon-okuma: discovery.product_embeddings.ProductId
- tablo: discovery.product_embeddings

### Q13 — Urun guncelleme akisi hangi tablolara ve kolonlara dokunuyor?

`PUT /api/catalog/products/{id:guid}` · forward · kategori `raw-sql` · popülasyon `P3` = 5

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 2 | 2 | 0 |
| tablo — tablo | EF dışı | 1 | 1 | 0 |
| tablo — erisim (R/W) | — | 3 | 2 | — |
| kolon-yazma — kolon | EF içi | 13 | 6 | 0 |
| kolon-yazma — kolon | EF dışı | 4 | 3 | 0 |
| event — tuketen | — | 1 | 1 | 0 |
| dis depo — dis depo | — | 1 | 0 | 1 |
| sinir — sinir kodu | — | 4 | 4 | — |

**Öngörülen kaçırmalar:** `F2` · `F5` · `F7` · `L16-4` · `L17` · `L5` · `L6`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- dis depo: RedisProductCache
- kolon-yazma: catalog.outbox_messages.Content
- kolon-yazma: catalog.outbox_messages.Error
- kolon-yazma: catalog.outbox_messages.Id
- kolon-yazma: catalog.outbox_messages.OccurredOnUtc
- kolon-yazma: catalog.outbox_messages.ProcessedOnUtc
- kolon-yazma: catalog.outbox_messages.RetryCount
- kolon-yazma: catalog.outbox_messages.Type
- kolon-yazma: discovery.product_embeddings.Embedding
- tablo: discovery.product_embeddings bekleniyor RW, gelen W

Fazladan gelenler (precision kaybı):

- dis depo: HTTP -> HttpEmbeddingService

### Q14 — Dev stok reset akisi hangi tablolara ve kolonlara dokunuyor?

`PUT /api/inventory/dev/stock/{productId:guid}` · forward · kategori `row-level-regression` · popülasyon `P10` = 14

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 2 | 2 | 0 |
| tablo — erisim (R/W) | — | 2 | 2 | — |
| kolon-yazma — kolon | EF içi | 6 | 6 | 0 |

Kaçırma **yok**.

### Q15 — Outbox dispatcher hangi tabloya, hangi kolonlari yaziyor ve hangi event'leri yayinliyor?

`ModularCommerce.Ordering.Infrastructure.Outbox.OutboxDispatcher.ExecuteAsync(System.Threading.CancellationToken)` · forward · kategori `outbox` · popülasyon `P5` = 2

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| tablo — tablo | EF içi | 1 | 1 | 0 |
| tablo — erisim (R/W) | — | 1 | 1 | — |
| kolon-yazma — kolon | EF içi | 3 | 3 | 0 |
| event — yayinlanan | — | 2 | 0 | 0 |

**Öngörülen kaçırmalar:** `L22`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- event: OrderCancelled
- event: OrderPaid

### Q16 — discovery.product_embeddings tablosuna hangi giris noktalarindan ulasiliyor?

`table:discovery.product_embeddings` · backward · kategori `backward-roots` · popülasyon `P8` = 16

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| kok — endpoint | — | 3 | 2 | 0 |
| kok — consumer | — | 2 | 2 | 0 |
| kok — arka plan isi | — | 1 | 1 | 0 |
| sinir — sinir kodu | — | 1 | 0 | — |

**Öngörülen kaçırmalar:** `F6` · `L6`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- kok: POST /api/discovery/search
- sinir: raw-sql

### Q17 — inventory.stock_items tablosuna hangi giris noktalarindan ulasiliyor?

`table:inventory.stock_items` · backward · kategori `backward-roots` · popülasyon `P8` = 16

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| kok — endpoint | — | 5 | 5 | 0 |
| kok — consumer | — | 0 | 0 | 0 |
| kok — arka plan isi | — | 2 | 2 | 0 |
| dis depo — dis depo | — | 1 | 0 | 0 |
| sinir — sinir kodu | — | 1 | 1 | — |

**Öngörülen kaçırmalar:** `F2` · `L17`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- dis depo: RedisDistributedLock

### Q18 — cart.carts tablosuna hangi giris noktalarindan ulasiliyor?

`table:cart.carts` · backward · kategori `backward-roots` · popülasyon `P8` = 16

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| kok — endpoint | — | 5 | 5 | 0 |
| kok — consumer | — | 0 | 0 | 0 |
| kok — arka plan isi | — | 0 | 0 | 0 |
| dis depo — dis depo | — | 1 | 0 | 0 |
| sinir — sinir kodu | — | 1 | 1 | — |

**Öngörülen kaçırmalar:** `F2` · `L17`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- dis depo: RedisCartCache

### Q19 — notification.processed_messages tablosuna hangi giris noktalarindan ulasiliyor?

`table:notification.processed_messages` · backward · kategori `backward-roots` · popülasyon `P8` = 3

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| kok — endpoint | — | 1 | 1 | 0 |
| kok — consumer | — | 1 | 1 | 0 |
| kok — arka plan isi | — | 0 | 0 | 0 |

Kaçırma **yok**.

### Q20 — ordering.order_lines tablosuna hangi giris noktalarindan ulasiliyor - okuyanlar dahil?

`table:ordering.order_lines` · backward · kategori `owned-navigation-read` · popülasyon `P6` = 3

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| kok — endpoint | — | 4 | 1 | 0 |
| kok — consumer | — | 0 | 0 | 0 |
| kok — arka plan isi | — | 1 | 0 | 0 |

**Öngörülen kaçırmalar:** `F4` · `L19`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- kok: GET /api/ordering/orders
- kok: GET /api/ordering/orders/{id:guid}
- kok: POST /api/ordering/orders/{id:guid}/cancel
- kok: ReservationTtlSweeper.ExecuteAsync

### Q21 — catalog.outbox_messages tablosuna hangi giris noktalarindan ulasiliyor?

`table:catalog.outbox_messages` · backward · kategori `outbox` · popülasyon `P5` = 2

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| kok — endpoint | — | 2 | 2 | 0 |
| kok — consumer | — | 0 | 0 | 0 |
| kok — arka plan isi | — | 2 | 2 | 0 |

Kaçırma **yok**.

### Q22 — ordering.orders.Status kolonuna hangi giris noktalari dokunuyor - okuyanlar dahil?

`column:ordering.orders.Status` · backward · kategori `column-backward` · popülasyon `P9` = 97

| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |
|---|---|---:|---:|---:|
| kok — endpoint | — | 4 | 2 | 0 |
| kok — consumer | — | 0 | 0 | 0 |
| kok — arka plan isi | — | 1 | 0 | 0 |

**Öngörülen kaçırmalar:** `F9` · `L18-2`

**Oracle:** `beklemede`

Gerçekleşen kaçırmalar:

- kok: GET /api/ordering/orders
- kok: GET /api/ordering/orders/{id:guid}
- kok: ReservationTtlSweeper.ExecuteAsync

## 7. Meta-test — F1..F10 ve L1..L22

Boş satır, gerekçesi yazılmadıkça eval set'in eksik olduğu anlamına gelir.

| Sınıf | Görünür kılan soru | Gerekçeli boşluk |
|---|---|---|
| F1 | Q06 | — |
| F2 | Q01, Q06, Q13, Q17, Q18 | — |
| F3 | Q01, Q02, Q14 | — |
| F4 | Q01, Q02, Q20 | — |
| F5 | Q01, Q02, Q03, Q13 | — |
| F6 | Q12, Q16 | — |
| F7 | Q03, Q10, Q13 | — |
| F8 | Q16, Q17, Q18, Q19, Q20, Q21, Q22 | — |
| F9 | Q12, Q22 | — |
| F10 | — | Yapisal: backward cevabi DataLayer TASIMIYOR (tip seviyesinde null). EvalTests.ABackwardAnswerCarriesNoDataLayer bunu popülasyon uzerinde sabitliyor. |
| L1 | Q01, Q02, Q03, Q04, Q05, Q06, Q07, Q08 | — |
| L2 | — | Faz 3'te konusuz kaldi: DbSet erisimi ifadenin TIPINDEN cozuluyor, accessor'in seklinden degil. |
| L3 | Q09, Q10, Q13, Q17, Q18 | — |
| L4 | Q09, Q10, Q11, Q13, Q15 | — |
| L5 | Q03, Q10, Q13 | — |
| L6 | Q03, Q10, Q12, Q13, Q16 | — |
| L7 | Q08 | — |
| L8 | — | Yapisal garanti, cevap dogrulugu degil. Mevcut suite'te ThinningUtilityNodesNeverChangesWhatIsReachable 41 sorguda sabitliyor. |
| L9 | Q03, Q14 | — |
| L10 | — | Olculdu: 4 site (CheckoutHandler.cs:60,175 - CardPaymentStrategy.cs:45,50). Hicbiri bir tabloyu, kolonu, koku ya da event'i degistirmiyor - cevap duzeyinde olculebilir etkisi YOK, dolayisiyla onu gorunur kilan bir soru YAZILAMAZ. |
| L11 | Q09, Q18 | — |
| L12 | — | Tek ornek (CardPaymentStrategy). Popülasyon 1: kategori degil yalniz o ornek olculebilirdi. |
| L13 | Q01, Q06 | — |
| L14 | — | Ortam kosulu (EF surum kapisi). EfPreflight build'i durdurur; cevap dogrulugu sorusu degil. |
| L15 | Q01, Q13, Q21 | — |
| L16 | Q01, Q02, Q03, Q13, Q14 | — |
| L17 | Q01, Q06, Q13, Q17, Q18 | — |
| L18 | Q12, Q22 | — |
| L19 | Q01, Q02, Q20 | — |
| L20 | — | Calisma zamani olgusu (JIT inlining). Statik eval yapisal olarak goremez. |
| L21 | Q01, Q02 | — |
| L22 | Q15 | — |

## 8. Ölçülemeyen sınıflar

Popülasyonu 0 olan ya da statik bir eval'in yapısal olarak göremeyeceği sınıflar. Sessizce atlanmaz — atlanan bir satır "kapsandı" diye okunur.

| Sınıf | Ad | Neden ölçülemedi |
|---|---|---|
| P15 | reflection | Hedef repo Activator.CreateInstance / Type.GetMethod().Invoke KULLANMIYOR. Popülasyon 0. |
| P16 | dynamic-dispatch | Hedef repo 'dynamic' KULLANMIYOR. Popülasyon 0. |
| P17 | inlining (L20) | Calisma zamani olgusu; statik eval goremez. Faz 6 adim 0b'de olculdu: 97/255 senkron dugum risk altinda. |
| P14 | delegate / Polly (L12) | Tek site (CardPaymentStrategy). Tek ornek bir kategori olusturmaz. |

## 9. Oracle çapraz kontrolü

Eval "kaçırma" dediğinde iki hipotez var: tool kaçırdı, ya da elle çıkarılan beklenen değer yanlıştı. İkincisi eval set'in KENDİ kusurudur ve ayrı sayılır.

| Sonuç | Adet |
|---|---:|
| `oracle-dogrulandi` — beklenen değer kaynakta var, kaçırma tool'a ait | 0 |
| `oracle-duzeltildi` — beklenen değer yanlıştı, **bulgu** | 0 |
| `beklemede` — çapraz kontrol henüz yapılmadı | 13 |

> Bir düzeltme `evals/oracle-verdicts.json`'a yazılır ve ModularCommerce `file:line` kanıtı taşımak ZORUNDADIR — çıktı bir gerekçe değildir. Düzeltme `questions.json`'a AYRI bir commit'te girer, runner commit'ine karışmaz; böylece "beklenen değer çıktıya uydurulmuş mu?" sorusu tek bir `git log` ile cevaplanır.
