# Faz 3 doğrulama — elle karşılaştırma

**Tarih:** 2026-08-08 · **Graph:** 400 node / 841 kenar / 16 tablo / 82 kolon
**Yöntem:** `flowlens trace` çıktısı ↔ ModularCommerce kaynağının elle okunması
(handler → repository → domain → EF konfigürasyonu → migration).

Gerçeklik kaynağı **graph.json değil**, hedef reponun kendisidir: her tablo ve kolon iddiası
`IEntityTypeConfiguration` ve `Migrations/*.cs` dosyalarına kadar takip edilmiştir.

**Kapsam — üç ölçüm:**

| § | Ne ölçüldü |
|---|---|
| 1–4 | **Forward** — dört endpoint, tablo/kolon recall ve precision |
| 5 | **Backward** — iki tablo + bir kolon, dönen kök listesinin doğruluğu |
| 6 | **Ambiguous politikası** — "tüm implementasyonlar" kararının precision maliyeti |

## Endpoint seçimi ve gerekçesi

Dördü de bilerek farklı **şekiller**; aynı şeklin dört örneği bir doğrulama değil, bir tekrardır.

| Endpoint | Neden seçildi |
|---|---|
| `PUT /api/cart/items/{productId:guid}` | `OwnsMany(...).ToJson()` (jsonb owned koleksiyon) + önünde bir cache dekoratörü + ilişkisel olmayan bir depo (Redis) |
| `POST /api/identity/signup` | En sade yazma yolu: tek tablo, saf INSERT, ara katman yok. Sadelik bir kontrol grubudur — burada çıkan fark her yerde vardır |
| `POST /api/ordering/orders/{id:guid}/cancel` | Üç modüle yayılan telafi akışı: owned koleksiyonlar, SaveChanges interceptor'ı, olay yayını, iki dış modül servisi |
| `POST /api/discovery/search` | **EF dışı.** İlk üçü EF akışıydı; o seçim roadmap'in dört hata kategorisinden ikisini ölçülemez kılıyordu. Discovery'nin tüm veri erişimi ham SQL |

> **İlk üçün seçimi eksikti ve bu bir ölçüm hatasıydı.** Üçü de EF üzerinden çalıştığı için
> "roadmap'in kategorilerine denk gelinmedi" sonucu, aracın değil **örneklemin** özelliğiydi.
> §4 bunu düzeltiyor; ders §10.3 kural 4 olarak `phase-3-notes.md`'ye geçti.

---

## 1. `PUT /api/cart/items/{productId:guid}`

### FlowLens

```
Data layer - 1 table(s), 2 column(s):

  WR  cart.carts   .../Configurations/CartConfiguration.cs:10
        CustomerId, UpdatedAtUtc
```

### Elle takip

`CartEndpoints.cs:45` → `UpdateItemQuantityHandler.HandleAsync`
→ `ICartRepository` = `CachingCartRepository` (dekoratör) → `PostgresCartRepository`

- `GetAsync` — `context.Carts.AsNoTracking().FirstOrDefaultAsync(...)` → **`cart.carts` R**
- `SaveAsync` — iki dal:
  - satır yoksa: `context.Carts.Add(new CartRecord { CustomerId, Items, UpdatedAtUtc })`
  - satır varsa: `record.Items = items; record.UpdatedAtUtc = DateTime.UtcNow`
- `CachingCartRepository` her iki yolda `ICartCache.SetAsync` → `RedisCartCache` → Redis `StringSetAsync`

Fiziksel şema (`20260717152814_InitialCartSchema.cs:22-24`):

```
carts(CustomerId uuid, UpdatedAtUtc timestamptz, Items jsonb)
```

**Gerçek:** 1 tablo (`cart.carts` WR), **3 kolon** — `CustomerId`, `UpdatedAtUtc`, `Items`.
Ayrıca Redis: ilişkisel olmayan, ikinci bir kalıcı depo.

### Fark

| # | Fark | Sınıf |
|---|---|---|
| **F1** | `cart.carts.Items` **hiç node değil** | Düzeltilebilir — model anlık görüntüsü eksiği |
| **F2** | Redis hiçbir node tipiyle temsil edilmiyor | Ontoloji sınırı — bilinçli, ama sessiz |

**F1'in mekanizması.** `EfProbe.CollectProperties` her entity için `typeBase.GetProperties()`
geziyor. `OwnsMany(...).ToJson()` ile eşlenen `CartItemRecord`'un property'leri (ProductId,
Quantity, AddedAtUtc) **kolon değil, JSON alanı**; kapsayıcı `Items` jsonb kolonu ise
`CartRecord`'un property'si değil — sahiplik ilişkisinin kendisinin taşıdığı bir kolon.
İkisi de `GetProperties()`'e düşmüyor, dolayısıyla `Items` için `Column` node'u üretilmiyor.
`record.Items = items` ataması FlowLens tarafından **görülüyor** (analiz yolu var), ama
bağlanacak bir kolon node'u olmadığı için kenar üretilemiyor.

FlowLens `CartItemRecord --MAPS_TO--> cart.carts` kenarını doğru kuruyor: entity düzeyinde
eşleme doğru, kayıp yalnız kolon düzeyinde.

> **Düzeltme yolu:** `EfProbe`, JSON'a eşlenen owned tipler için `entityType.IsMappedToJson()`
> zaten okuyor (`EfProbe.cs:215`) ama kullanmıyor. Kapsayıcı kolon adı EF modelinde mevcut;
> tek bir sentetik `EfProperty` (Name = navigasyon adı, ColumnName = kapsayıcı kolon) yeter.

**F2'nin durumu.** `ExternalCall` tespiti bugün yalnız `HttpClient` çağrılarına bakıyor
(`HttpClientInvocation`, graph'ta 1 adet). `StackExchange.Redis` bir node üretmiyor.
Kanıt kaybolmuş değil — `RedisCartCache.SetAsync` ve `KeyFor` traversal'da `Method` olarak
görünüyor — ama **tiplenmemiş**: "bu akış bir dış depoya yazıyor" sorusuna cevap vermiyor.
Faz 2'de `ExternalCall`'un ne olduğu HTTP üzerinden tanımlanmıştı; bu, o kararın faturasıdır.

**Metrik:** tablo 1/1 (%100) · kolon 2/3 (%67) · yanlış pozitif 0.

---

## 2. `POST /api/identity/signup`

### FlowLens

```
Data layer - 1 table(s), 3 column(s):

  WR  identity.users   .../Configurations/UserConfiguration.cs:11
        CreatedAtUtc, PasswordHash, email
```

### Elle takip

`AuthEndpoints.cs:20` → `SignupHandler.HandleAsync`

- `users.GetByEmailAsync` → `context.Users.FirstOrDefaultAsync(...)` → **`identity.users` R**
- `User.Create` → `new User(email, passwordHash)` → ctor: `Email`, `PasswordHash`, `CreatedAtUtc`
- `users.Add(user)` → `context.Users.Add(user)` → **`identity.users` W**
- `users.SaveChangesAsync()` → INSERT

`UserConfiguration`: `Email` value object **tek kolona** (`email`) `HasConversion` ile açılıyor;
`Id` `HasKey`; `DomainEvents` `Ignore`.

**Gerçek:** 1 tablo (WR), INSERT'in yazdığı kolonlar: `Id`, `email`, `PasswordHash`, `CreatedAtUtc`.

Doğru çıkan iki şey — ikisi de sessiz yanlış üretebilecekken:
- **Outbox yok.** `User.Create`, `UserRegistered` domain event'ini `Raise` ediyor; ama Identity
  modülünde `SaveChangesInterceptor` yok (repoda yalnız Catalog ve Ordering'de var) ve
  `builder.Ignore(u => u.DomainEvents)`. Olay hiçbir yere yazılmıyor. FlowLens outbox iddia etmedi.
- **`identity.refresh_tokens` yok.** Signup token üretmiyor (`SignupResponse` yalnız id + email).
  Aynı modülün ikinci tablosu, doğru şekilde dışarıda bırakıldı.

### Fark

| # | Fark | Sınıf |
|---|---|---|
| **F3** | `identity.users.Id` raporlanmıyor | Düzeltilebilir — INSERT satır düzeyi değil atama düzeyi sayılıyor |

**F3'ün mekanizması.** `Id`, `Shared.Kernel.Entity`'de bir **property initializer**:

```csharp
public Guid Id { get; protected init; } = Guid.NewGuid();   // Entity.cs:7
```

Bu bir constructor **gövdesi** ataması değil, ve `User`'ın değil **base tipin** üyesi.
`AddConstructorColumnWritesAsync` entity'nin kendi ctor gövdesini geziyor; miras alınan
initializer kapsam dışında.

Bu tekil bir kaçırma değil — **grafikte `.Id` ile biten tek bir kolon node'u yok** (82 kolonun
hiçbiri). Aynı aile: gölge (shadow) kolonlar da yok — `order_lines.id`, `order_lines.order_id`,
`payment_attempts.id`, `payment_attempts.payment_id` hiçbir C# ataması tarafından adlandırılmıyor
(`OrderConfiguration.cs:32-34`, `PaymentConfiguration.cs:57-59`), dolayısıyla hiç görünmüyorlar.

**Metrik:** tablo 1/1 (%100) · kolon 3/4 (%75) · yanlış pozitif 0.

---

## 3. `POST /api/ordering/orders/{id:guid}/cancel`

### FlowLens

```
Data layer - 7 table(s), 18 column(s):

  WR  inventory.reservations         Status
  WR  inventory.stock_items          OnHand, UpdatedAtUtc
  W   ordering.order_status_history  FromStatus, OccurredAtUtc, ToStatus, TriggeredBy
  WR  ordering.orders                Status, UpdatedAtUtc
  W   ordering.outbox_messages       (kolon yok)
  W   payment.payment_attempts       AttemptNumber, ErrorCode, LatencyMs, OccurredAtUtc,
                                     Outcome, PspTransactionId
  WR  payment.payments               RefundTransactionId, RefundedAtUtc, Status
```

### Elle takip

`OrderEndpoints.cs:61` → `CancelOrderHandler.HandleAsync` — telafi orkestrasyonu, üç adım:

**(0)** `orders.GetByIdAsync` → `context.Orders.FirstOrDefaultAsync` → **`ordering.orders` R**.
`Order.Lines` ve `StatusHistory` **owned koleksiyon** (`OrderConfiguration.cs:29,52`); EF owned
navigasyonları **auto-include** eder → aynı sorgu `ordering.order_lines` ve
`ordering.order_status_history` tablolarını da **okur**.

**(1)** `order.Cancel("cancel")` → `TransitionTo`:
```csharp
Status = next;  UpdatedAtUtc = DateTime.UtcNow;                     // orders: 2 kolon
_statusHistory.Add(new OrderStatusChange(previous, next, triggeredBy));  // history: yeni satır
Raise(new OrderStatusChanged(...));  ...  Raise(new OrderCancelled(...));
```

**(2)** `foreach (var line in order.Lines)` → `IStockReservationService.ReturnAsync` →
`StockReservationService.ExecuteWithRetryAsync`: `context.Reservations` R, `context.StockItems` R,
`stockItem.Return(reservation)` → `OnHand += ...; UpdatedAtUtc = ...; reservation.MarkReturned()`
(→ `Status`), `context.SaveChangesAsync()`.

**(3)** `IPaymentService.RefundAsync` → `context.Payments.FirstOrDefaultAsync` R,
`payment.Refund(...)` → `Status`, `RefundTransactionId`, `RefundedAtUtc` +
`_attempts.Add(new PaymentAttempt(...))` (6 kolon), `context.SaveChangesAsync()`.

**(4)** `orders.SaveChangesAsync()` → `DomainEventToOutboxInterceptor.SavingChangesAsync`
→ `context.Set<OutboxMessage>().Add(new OutboxMessage { Type, Content, OccurredOnUtc })`
→ **`ordering.outbox_messages` W**.

**Gerçek:** **8 tablo**, kolon dökümü aşağıda.

Doğru çıkan bir şey daha: `OrderCancelled` olayı `Event` node'una kadar gidiyor ve **orada
duruyor**. Repoda üç consumer var (`ProductChangedConsumer`, `OrderPaidNotificationConsumer`) ve
hiçbiri `OrderCancelled` almıyor — `Order.cs:150`'deki yorum bunu "W10'da gelecek" diye yazıyor.
Yani durma bir kesinti değil, doğru cevap. Consumes kenarının yönü `event → consumer` olduğu için
tüketici olsaydı forward traversal asenkron sınırı geçecekti (Catalog → Discovery yolunda geçiyor).

### Fark

| # | Fark | Sınıf |
|---|---|---|
| **F4** | `ordering.order_lines` **R** kaçırıldı | Düzeltilebilir — owned navigasyon okuması modellenmiyor |
| **F5** | `ordering.outbox_messages` kolon düzeyinde boş (4 kolon) | Düzeltilebilir — interceptor kuralı tablo düzeyinde |
| F3 | 4 gölge kolon (`id`, `order_id`, `id`, `payment_id`) | Aynı aile |

**F4'ün mekanizması.** `EntityAccessAnalyzer` READS kenarlarını `context.<DbSet>` erişiminden ve
sorgu zincirlerinden üretiyor. `order.Lines` bir **navigasyon okuması** — sözdiziminde hiçbir
`DbSet` görünmüyor, ama EF owned koleksiyonu auto-include ettiği için SQL'de `order_lines`
tablosu var. FlowLens'in görebileceği bir `context.OrderLines` ifadesi yok, çünkü öyle bir
`DbSet` de yok.

Not: `ordering.order_status_history` de aynı sebeple **okunuyor**; FlowLens onu listeliyor ama
**yalnız W olarak** — yazma yolundan geldiği için. Yani F4 tek tablo değil, bir kenar tipi kaybı.

> **Düzeltme yolu:** EF modeli sahiplik ilişkisini biliyor (`FindOwnership`). Bir entity'ye READS
> kenarı üretildiğinde, o entity'nin owned navigasyonlarının tablolarına da READS eklenebilir.
> Auto-include owned tipler için EF'in garantisi, sezgisel bir tahmin değil.

**F5'in mekanizması.** Interceptor kuralı (§5.8) `OutboxMessage` entity'sinden tabloya bir
**tablo düzeyi** W kenarı sentezliyor. Ama `CollectOutboxMessages` gövdesindeki
`new OutboxMessage { Type = ..., Content = ..., OccurredOnUtc = ... }` object initializer'ı hiçbir
çağrı yolundan erişilebilir değil — interceptor'ı **EF çağırır, kod değil**. Bu yüzden o üç atama
hiçbir endpoint'in ulaşılabilir kümesinde değil.

Grafikte `ordering.outbox_messages`'ın üç kolonu **var** — `Error`, `RetryCount`, `ProcessedOnUtc`
— ama bunlar `OutboxDispatcher` (BackgroundService kökü) tarafından yazılanlar. Yani tablo kolonlu,
sadece **yazan akış** kolonsuz. Bu, tablo listesine bakan birinin fark etmeyeceği türden bir boşluk.

### Kolon dökümü

| Tablo | Gerçek | FlowLens | Eksik |
|---|---|---|---|
| `ordering.orders` | 2 | 2 | — |
| `ordering.order_lines` | 0 (yalnız R) | R kaçırıldı | tablo düzeyi |
| `ordering.order_status_history` | 6 | 4 | `id`, `order_id` (gölge) |
| `ordering.outbox_messages` | 4 | 0 | `Id`, `Type`, `Content`, `OccurredOnUtc` |
| `payment.payments` | 3 | 3 | — |
| `payment.payment_attempts` | 8 | 6 | `id`, `payment_id` (gölge) |
| `inventory.stock_items` | 2 | 2 | — |
| `inventory.reservations` | 1 | 1 | — |
| **Toplam** | **26** | **18** | **8** |

**Metrik:** tablo 7/8 (%87,5) · kolon 18/26 (%69) · yanlış pozitif 0.

---

## 4. `POST /api/discovery/search` — sınırın en net kanıtı

İlk üç endpoint roadmap'in dört hata kategorisinden (reflection, dynamic dispatch, string tabanlı
SQL, ambiguous interface) **hiçbirine denk gelmedi.** Bu bir ölçüm değil, bir **seçim etkisiydi**:
üçü de EF üzerinden çalışan akışlardı. Discovery bunu düzeltiyor — modülün tüm veri erişimi
ham SQL.

### FlowLens

```
POST /api/discovery/search  (Endpoint, Discovery)
reaches 14 node(s):
  Handler (1) · Method (10) · Repository (2) · ExternalCall (1)
```

**Veri katmanı bloğu hiç basılmıyor: 0 tablo, 0 kolon.**

### Elle takip

`SearchEndpoints.cs:17` → `SearchProductsHandler.HandleAsync`
→ `IEmbeddingService.EmbedAsync` → `IProductVectorRepository.SearchAsync`
→ `ProductVectorRepository.SearchAsync` (`ProductVectorRepository.cs:47`):

```sql
SELECT "ProductId", 1 - ("Embedding" <=> @q) AS score
FROM discovery.product_embeddings
ORDER BY "Embedding" <=> @q
LIMIT @n;
```

`NpgsqlDataSource.CreateCommand` — EF yok, `DbSet` yok, `IModel` yok. Vektör tipi EF10'da
eşlenemediği için modül bilinçli olarak EF dışında.

**Gerçek:** 1 tablo (`discovery.product_embeddings` **R**), 2 kolon okunuyor.
**FlowLens:** 0 tablo. **Tablo recall: 0/1 (%0).**

### Bu kayıp neden diğerlerinden farklı

Tablo graph'ta **var** ve dolu: `discovery.product_embeddings`, 3 kolonu ve W kenarları ile.
Ama `Backward("table:discovery.product_embeddings")` şu iki endpoint'i döndürüyor:

```
POST /api/catalog/products          d9
PUT  /api/catalog/products/{id}     d9
```

— yani **yazanları**, Catalog → `ProductCreated`/`ProductUpdated` → `ProductChangedConsumer` →
`IndexProductHandler` zinciri üzerinden, 9 seviye derinlikten. Tablonun **tek okuyucusu**
(`POST /api/discovery/search`) listede yok.

Yazma tarafının yakalanması ise **kazadan ibaret**: `IndexProductHandler` bir `ProductEmbedding`
nesnesi *inşa ediyor* ve birileri o entity'yi ayrıca EF'te de konfigüre etmiş
(`ProductEmbeddingConfiguration.cs:11`). Kenarın mekanizması `EntityConstruction` — **ikinci sınıf**.
Gerçek I/O (`INSERT ... ON CONFLICT`, `ProductVectorRepository.cs:17-24`) hâlâ görünmez. Yani
FlowLens burada **doğru cevabı yanlış kanıtla** veriyor: yazma iddiası entity inşasından geliyor,
`UpsertAsync`'in SQL'inden değil. Faz 2 kararı C'nin ("hangi repository çağrısından, hangi entity
üzerinden — kanıt taşı") tam olarak uyardığı durum.

### Sessiz değil — dört ham SQL sitesi de diagnostics'te

```
raw SQL reaches the database outside the model, so no table edge:
  dataSource.CreateCommand              ProductVectorRepository.cs:26   (UpsertAsync)
  dataSource.CreateCommand              ProductVectorRepository.cs:40   (GetSourceTextHashAsync)
  dataSource.CreateCommand              ProductVectorRepository.cs:60   (SearchAsync)
  context.Database.ExecuteSqlAsync      NaiveReservationStrategy.cs:37  (UPDATE stock_items)
```

**Bu, L6'nın çalıştığının kanıtı.** Kayıp gerçek ama **sessiz değil**: tam olarak dört site,
`file:line` ile. `graph.json` "hiçbir tabloya dokunmuyor" demiyor, "burada bakamadım" diyor.
Denetimin bulduğu dört hatadan farkı bu — onlar hiçbir iz bırakmıyordu.

Aynı şey F1 için de geçerli (ilk üç endpoint'te fark etmemiştim):

```
property written but not mapped to a column: CartRecord.Items at PostgresCartRepository.cs:52, :58
```

`cart.carts.Items` **raporlanan** bir kayıp. Sınıflandırması "sessiz kayıp" değil, "bilinen sınır".

### Fark

| # | Fark | Sınıf |
|---|---|---|
| **F6** | `discovery.product_embeddings` **R** hiç görünmüyor (tek okuyucusu bu endpoint) | **Yapısal (L6)** — SQL string'i parse etmek roadmap'te yasak |
| **F7** | Yazma kenarı doğru tabloyu ikinci sınıf ve yanlış kanıtla gösteriyor | Yapısal sonuç |

**Metrik:** tablo 0/1 (%0) · kolon 0/2 (%0) · yanlış pozitif 0 (aşağıdaki ExternalCall hariç, §6).

Bu, doğrulanan dört endpoint içindeki **tek yapısal** sınır. F1–F5 düzeltilebilir; F6 için tek yol
SQL parse etmek ve bu roadmap'te açıkça yasaklı — isim tahmini ve SQL parse yerine `IModel`
şart koşulmuş. **Aracın kapsamı EF ile eşleşiyor; hedef reponun tamamıyla değil.**

---

## Forward toplu sonucu

| Endpoint | Tablo (gerçek/bulunan) | Kolon (gerçek/bulunan) |
|---|---|---|
| `PUT /api/cart/items/{productId}` | 1 / 1 | 3 / 2 |
| `POST /api/identity/signup` | 1 / 1 | 4 / 3 |
| `POST /api/ordering/orders/{id}/cancel` | 8 / 7 | 26 / 18 |
| `POST /api/discovery/search` | 1 / 0 | 2 / 0 |
| **Toplam** | **11 / 9** | **35 / 23** |

| | Recall | Precision |
|---|---|---|
| Tablo | **%82** | **%100** |
| Kolon | **%66** | **%100** |

**Dört endpoint, sıfır yanlış pozitif tablo/kolon.** FlowLens'in söylediği hiçbir tablo veya kolon
yanlış değil — hepsi kaynakta doğrulandı. Tüm sapma **eksiklik** yönünde. (Tek yanlış pozitif
tablo/kolon dışında: §6'daki `ExternalCall`.)

**EF kapsamı ile toplam kapsam ayrı raporlanmalı.** EF üzerinden çalışan üç endpoint'te tablo
recall'ı **9/10 (%90)**; EF dışı tek endpoint'te **0/1**. Tek bir %82, aracın nerede güvenilir
nerede kör olduğunu gizler. Faz 5 eval'i bu ikisini ayrı ölçmeli.

Yol yapısı da doğru: dekoratör zinciri (`Caching` → `Postgres`) her iki uçla, modüller arası
sözleşme çağrıları (`IPaymentService`, `IStockReservationService`) doğru implementasyonla,
olay yayını registry filtresiyle (`OrderCancelled` yayınlanıyor, `OrderStatusChanged`
yayınlanmıyor — çünkü registry'de yok) eşleşti.

### EF içi kaçırmaların tamamı tek bir kök nedene iniyor

F1–F5'in dördü aynı cümlenin farklı yüzleri (F6/F7 ayrı — orası EF'in dışı):

> **FlowLens kolon yazmalarını ATAMA'dan üretiyor; ama bir INSERT bir SATIR yazar.**

`UPDATE` yolunda atama düzeyi **doğru** ölçüdür: `record.UpdatedAtUtc = ...` gerçekten yalnız o
kolonu değiştirir. Beş UPDATE yolunda kolon recall'ı **9/10 (%90)** — orders 2/2, payments 3/3,
stock_items 2/2, reservations 1/1, `cart.carts` 1/2. Tek kaçık `Items`, ve sebebi atama düzeyinin
kendisi değil F1 (kolon node'u hiç yok).

`INSERT` yolunda ise EF, entity'nin **tüm eşlenmiş kolonlarını** yazar — atansın atanmasın:
üretilen `Id`, gölge anahtar `id`, gölge FK `order_id`, JSON kapsayıcı `Items`, atanmamış
nullable'lar. Bunların hiçbirinin işaret edilecek bir C# ataması yok. Beş INSERT yolunda kolon
recall'ı **15/25 (%60)**:

| Eklenen satır | Gerçek | FlowLens |
|---|---|---|
| `CartRecord` → `cart.carts` | 3 | 2 |
| `User` → `identity.users` | 4 | 3 |
| `OrderStatusChange` → `order_status_history` | 6 | 4 |
| `OutboxMessage` → `ordering.outbox_messages` | 4 | 0 |
| `PaymentAttempt` → `payment_attempts` | 8 | 6 |

(`cart.carts` hem INSERT hem UPDATE dalında yazılıyor; birleşik 33/23 sayımında bir kez sayıldı,
bu iki tabloda iki kez görünüyor.)

**Düzeltme:** mekanizma `DbSetProperty` / `SetOfT` / `EntityConstruction` + `Add` olduğunda —
yani bir satır ekleniyorsa — o entity'nin tablosundaki **tüm** kolonlara W kenarı üret; atamayla
adlandırılanların daha kesin kanıtı (`file:line`) ayrıca korunur. Bu tek değişiklik F3'ü ve
gölge kolonları kapatır. F1 ondan önce gelmeli: `EfProbe` kolon node'unu üretmezse satır düzeyi
kural da onu bulamaz. F4 (owned navigasyon okuması) ayrı ve bağımsızdır.

**F5 bu değişikliğin parçası değil, ayrı bir kapsam kararı.** Interceptor gövdesini analiz etmek
kök kümesini değiştirme riski taşıyor; karar ve gerekçesi `phase-3-notes.md` §5.9'da
(seçilen: gövde **analiz edilir ama yürünmez**, sonuç `SaveChanges` çağrı sitesine iliştirilir).

Bu değişiklik precision'ı düşürür mü? Teknik olarak hayır: EF o kolonları gerçekten INSERT
cümlesine koyar. Ama "etkilenen kolon" sorusunun cevabını genişletir — impact analizi için doğru
yönde bir genişleme (kaçırma, fazladan söylemekten tehlikelidir; roadmap 5b bunu açıkça yazıyor).

### Düzeltilebilir mi, yapısal sınır mı?

| # | Fark | Karar | Maliyet |
|---|---|---|---|
| F1 | jsonb kapsayıcı kolon | **Düzeltilebilir** — EF modeli kolon adını biliyor | Küçük (`EfProbe`, tek sentetik property) |
| F2 | Redis / ilişkisel olmayan depo | **Ontoloji kararı** — düzeltmek `ExternalCall`'un tanımını değiştirmek demek | Karar gerektirir, kod değil |
| F3 | `Id` + gölge kolonlar | **Düzeltilebilir** — satır düzeyi INSERT kuralı | Orta (`DataLayerOverlay`) |
| F4 | owned navigasyon okuması | **Düzeltilebilir** — `FindOwnership` üzerinden türetilir | Orta |
| F5 | interceptor kolon düzeyi | **Düzeltilebilir** — interceptor gövdesi ayrı bir kök olarak analiz edilir. **Bu bir kapsam kararı**, bkz. `phase-3-notes.md` §5.9 | Orta + karar |
| F6 | ham SQL tablosu (`product_embeddings` R) | **YAPISAL (L6)** — SQL parse roadmap'te yasak | Kapatılamaz |
| F7 | ham SQL yazması ikinci sınıf kanıtla yakalanıyor | **Yapısal sonuç** — F6'nın yan etkisi | Kapatılamaz |

**Bir yapısal sınır var: F6/F7.** Kalan beşin dördü kod değişikliğiyle kapanır; F2 bir kapsam
kararıdır ve kapsam kararlarının maliyeti kod değil, ontolojinin büyümesidir — roadmap §5 bunu
açıkça "genişletme isteği gelirse önce sor" diye bağlamış. F5'in düzeltmesi de kapsam kararı
gerektiriyor (kök kümesi değişiyor) ve ayrıca karara bağlandı.

**Bu doküman düzeltme yapmıyor.** Ölçüm ve teşhis; hangisinin yapılacağı Faz 4/5 önceliğidir.
Faz 5 eval set'inin bu yedi farkı **tekrar bulması** beklenir — bulmuyorsa eval set yanlıştır.

### Kaçırma kategorileri (Faz 5 için hazır girdi)

Roadmap 5b dört kategori öngörüyordu: reflection, dynamic dispatch, string tabanlı SQL, ambiguous
interface. İlk üç endpoint (hepsi EF akışı) **hiçbirine denk gelmedi** — bu bir ölçüm değil, bir
**seçim etkisiydi**. Discovery eklenince ikisi karşılığını buldu:

| Roadmap kategorisi | Karşılaşıldı mı |
|---|---|
| Reflection | Hayır — hedef repo kullanmıyor |
| Dynamic dispatch | Hayır — hedef repo `dynamic` kullanmıyor |
| **String tabanlı SQL** | **Evet** — F6/F7, 4 site, hepsi diagnostic'te (§4) |
| **Ambiguous interface** | **Evet** — §6'da ayrıca ölçüldü, tablo maliyeti 0, `ExternalCall` maliyeti 1 |

Ölçümden çıkan **yeni** kategoriler — roadmap'in listesinde yoktu:

1. **`ef-side-effect`** — EF'in çağırdığı kod (interceptor). Çağrı grafiğinde yolu yok.
2. **`ef-implicit-read`** — owned/auto-include navigasyon okuması. Sözdiziminde `DbSet` yok.
3. **`ef-unnamed-column`** — gölge kolon, üretilen anahtar, JSON kapsayıcı. Hiçbir C# ataması yok.
4. **`inherited-initializer`** — base tipteki property initializer. Türetilmiş tipin gövdesinde yok.
5. **`non-relational-store`** — Redis. Ontolojide karşılığı yok.

Beşi de **"kaynakta bir şey var ama sözdiziminde işaret edilecek bir yer yok"** ailesinden.
Roadmap'in dördü ise "sözdizimi var ama çözülemiyor" ailesindendi. İkisi ayrı problem; eval set
**ikisini de** sormalı — ve kategoriler ölçüldükçe genişlemeli.

---

## 5. Backward traversal doğrulaması

Faz 3 kabul kriterlerinden biri `Backward(nodeId, maxDepth)`. Yukarısı yalnız forward yönü
ölçüyordu; **Faz 5a triage bot'unun tamamı backward'a dayanıyor**, dolayısıyla ayrıca doğrulandı.
İki tablo ve bir kolon seçildi, dönen kök listesi elle kontrol edildi.

### 5.1 `Backward("table:ordering.orders")` — 33 node, 4 endpoint

```
Endpoint (4)
  d4  POST /api/ordering/checkout
  d5  GET  /api/ordering/orders
  d5  GET  /api/ordering/orders/{id:guid}
  d5  POST /api/ordering/orders/{id:guid}/cancel
```

Elle: `OrderRepository.AddAsync` (checkout W), `.GetByIdAsync` (GetOrder + cancel R),
`.GetByIdempotencyKeyAsync` (checkout R), `OrderQueries.GetMyOrdersAsync` (GET /orders R).
Ordering modülünün dört endpoint'i de gerçekten `orders`'a dokunuyor, beşinci endpoint yok.
**Eksik yok, fazla yok.**

**Ama cevap eksik sunuluyor.** Sonuç kümesinde `ReservationTtlSweeper.ExecuteAsync` de var (d6) —
Inventory'nin **BackgroundService**'i, `IOrderReservationReconciler.ClassifyAsync` üzerinden
`context.Orders.AsNoTracking().Where(o => o.Status == Paid)` okuyor
(`OrderReservationReconciler.cs:20`). Doğru bir cevap; ama `Method (11)` başlığının altında,
`Order.Create` ve `Order.Cancel` ile aynı listede duruyor.

> **F8 — Kök tipleri çıktıda ayrışmıyor.** Faz 2'de kök kümesi *Endpoint + Consumer +
> BackgroundService* olarak kararlaştırıldı, ama `NodeKind`'da `BackgroundService` ve `Consumer`
> yok — ikisi de `Method`. Dolayısıyla *"bu tabloya kim dokunuyor?"* sorusunun cevabı
> **"4 endpoint"** gibi görünüyor; doğrusu **"4 endpoint + 1 arka plan işi"**. Triage bot için
> bu materyal bir fark: bir tabloda bozulma varsa şüpheli listesine süpürücü de girmeli.
>
> **Sınıf:** ontoloji eksiği, düzeltilebilir. İki yol var — `NodeKind`'a iki değer eklemek
> (ontoloji büyür, roadmap §5 "önce sor" diyor) veya `Node`'a bir `IsRoot`/`RootKind` alanı
> eklemek (ontoloji sabit kalır, sunum düzelir). **İkincisi tercih edilmeli**: kök olmak bir
> düğüm *tipi* değil, düğümün graph'taki *rolü*.

### 5.2 `Backward("table:payment.payments")` — 32 node, 3 endpoint

```
Endpoint (3)
  d2  GET  /api/payment/dev/payments
  d5  POST /api/ordering/checkout
  d5  POST /api/ordering/orders/{id:guid}/cancel
```

Elle: hedefte `context.Payments` yalnız beş yerde geçiyor — `PaymentService.cs:53` (`.Add`),
`:88` (`.Remove`), `:131`, `:213` (sorgu) ve `PaymentDevEndpoints.cs:28`. `PaymentService`'in
çağıranları `ChargeAsync` (checkout) ve `RefundAsync` (cancel); dev endpoint kendi lambda'sında
okuyor. Arka plan işi yok. **Üçü tam, eksik yok, fazla yok.**

Dev endpoint'in **d2**'de olması ayrıca §5.7c düzeltmesinin çalıştığının kanıtı: lambda gövdeleri
analiz edilmeseydi bu endpoint listede hiç olmayacaktı.

### 5.3 `Backward("column:ordering.orders.Status")` — 10 node, 2 endpoint

```
Endpoint (2)
  d3  POST /api/ordering/checkout
  d4  POST /api/ordering/orders/{id:guid}/cancel
Method (6)
  d1  Order.Create · Order.TransitionTo
  d2  Order.Cancel · Order.MarkPaid · Order.MarkPaymentPending · Order.MarkStockReserved
```

Elle: `Status`'a yazan iki yer var — `Order.Create` (ilk değer) ve `TransitionTo` (`Order.cs:166`).
`TransitionTo`'yu çağıran altı metot: `MarkStockReserved`, `MarkPaymentPending`, `MarkPaid`,
`Cancel`, `MarkShipped`, `Expire`.

**Listede `MarkShipped` ve `Expire` yok — ve bu doğru.** İkisi de hedefte **hiç çağrılmıyor**
(repo genelinde tek eşleşme kendi bildirimleri). Ulaşılamayan kod yürüyüşe girmediği için
graph'ta da yok; doğrulandı: `Order.Expire` ve `Order.MarkShipped` için node üretilmemiş.
GET endpoint'lerinin listede olmaması da doğru — okuyorlar, yazmıyorlar.

**Eksik yok, fazla yok.** Ama iki sunum notu:

> **F9 — Kolon backward'ı yalnız "kim yazıyor"u cevaplar, "kim okuyor"u değil.** Column node'ları
> yalnız bir yazma onları adlandırdığında üretiliyor (tasarım kararı, §5.7b). `OrderReservationReconciler`
> `Where(o => o.Status == Paid)` ile `Status` kolonunu **okuyor** ama bu kolon düzeyinde temsil
> edilmiyor. Triage bot *"bu kolonu kim okuyor"* diye sorarsa cevap boş gelir. **Bilinçli sınır
> olarak kaydedilmeli** — bugün hiçbir yerde yazılı değil.
>
> **F10 — Backward çıktısındaki "Data layer" bloğu yanıltıcı.** `Backward("table:ordering.orders")`
> sonunda *"WR ordering.orders — 5 kolon"* basılıyor. Bu, ulaşan akışların yazdığı kolonlar değil,
> **hedefin kendi kolon kümesi** (Column→Table kenarları ters yönde geziliyor). Forward'da blok
> "bu akış neye dokunuyor" demek; backward'da "bu tablonun kolonları" demek. Aynı başlık, iki
> anlam. Kozmetik ama Faz 4'te LLM'e giden metin bu.

### Backward sonucu

| Sorgu | Endpoint (gerçek/bulunan) | Eksik | Fazla |
|---|---|---|---|
| `table:ordering.orders` | 4 / 4 | — | — |
| `table:payment.payments` | 3 / 3 | — | — |
| `column:ordering.orders.Status` | 2 / 2 | — | — |

**Backward recall %100, precision %100.** Kabul kriteri karşılanıyor; Faz 5a'nın dayanağı sağlam.
Bulunan üç sorun (F8, F9, F10) **doğrulukla değil sunumla** ilgili — ama F8 triage bot'un cevabını
eksik gösterir, dolayısıyla Faz 5a'dan **önce** ele alınmalı.

---

## 6. Ambiguous politikasının precision maliyeti

Faz 2 kararı: bir interface çağrısı **tüm** implementasyonlara açılır (`implementation-policy all`).
Bunun bedeli bugüne kadar ölçülmemişti. Ölçüldü.

### Yöntem

Hedefin çalışma zamanı konfigürasyonu `appsettings.json`'dan sabitlendi:

```
Inventory:ReservationStrategy = "OptimisticConcurrency"   → Naive ve RedisLock ASLA koşmaz
Embedding:Provider            = "Fake"                     → HttpEmbeddingService ASLA koşmaz
```

Sonra graph üzerinde iki BFS: (1) tüm implementasyonlarla, (2) yukarıdaki ölü implementasyonlar
hariç. Fark, "tek bir runtime konfigürasyonunda gerçekten erişilebilen" ile "graph'ın döndürdüğü"
arasındaki mesafedir.

### Sonuç

| Endpoint | Node | Tablo | Kolon | ExternalCall |
|---|---|---|---|---|
| `POST /api/ordering/checkout` | 180 → 176 | **12 → 12** | **50 → 50** | 0 → 0 |
| `POST /api/discovery/search` | 15 → 12 | 0 → 0 | 0 → 0 | **1 → 0** |

**Checkout'ta karar bedava.** 3 rezervasyon stratejisinden 2'si runtime'da ölü, ama **tablo ve
kolon düzeyinde tek bir fazlalık üretmiyorlar** — üçü de aynı iki tabloya dokunuyor
(`inventory.stock_items` R/W, `inventory.reservations` W). Fazlalık yalnız 4 metot node'u (%2,2).
Aynısı `ICartRepository` ve `IProductReader` için de geçerli ama farklı sebeple: onlar dekoratör,
yani **ikisi de gerçekten koşuyor** — orada "tüm implementasyonlar" zaten doğru cevap.
`INotificationChannel` de öyle: `IEnumerable<INotificationChannel>` enjekte ediliyor, üç kanalın
üçü de çalışıyor (`NotificationModule.cs:32,38`).

**Discovery'de bedava değil.** `Embedding:Provider = "Fake"` iken `HttpEmbeddingService` hiç
örneklenmiyor; ama graph'ın **tek `ExternalCall` node'u** tam olarak o.

> `POST /api/discovery/search` için *"bu akış bir dış HTTP servisine çıkıyor"* cevabı,
> **varsayılan konfigürasyonda yanlıştır.** Precision bu tek node için %0.

### Değerlendirme

Karar ölçülünce üç parçaya ayrılıyor:

| Ambiguous kaynağı | Runtime'da | "Tümü" politikası |
|---|---|---|
| Dekoratör zinciri (`ICartRepository`, `IProductReader`) | Hepsi koşar | **Doğru** |
| Koleksiyon enjeksiyonu (`INotificationChannel`) | Hepsi koşar | **Doğru** |
| Config anahtarı (`IReservationStrategy`, `IEmbeddingService`) | **Biri** koşar | **Aşırı-yaklaşım** |

Yani politika kendi başına yanlış değil; **yanlış olan, üç farklı DI şeklini tek etiketle
göstermek.** `[ambiguous]` etiketi "bunlardan biri" mi "hepsi birden" mi demek — çıktıdan
anlaşılmıyor.

**Veri katmanında maliyet bugün sıfır** (12→12 tablo, 50→50 kolon), çünkü ModularCommerce'te
config-seçimli implementasyonlar aynı tablolara dokunuyor. Bu bir **tesadüf**, garanti değil:
`Naive` stratejisi aynı tabloya *ham SQL ile* yazıyor (`NaiveReservationStrategy.cs:37`) ve
FlowLens onu zaten göremiyor — tablo listesine `StockItem.Reserve`'ün atamasından giriyor.
Yani `Naive` seçiliyken FlowLens'in `stock_items` W iddiası **doğru tablo, yanlış kanıt**:
o konfigürasyonda `AsNoTracking` yüzünden atama hiç persist edilmiyor, yazan ham SQL.
Karar C'nin (kanıt taşı) uyardığı durumun ikinci örneği.

**Sonuç:** karar veri katmanında bedava, `ExternalCall` katmanında 1/1 yanlış pozitif.
Faz 5 eval'i **ambiguous kaynağını kategori olarak** ayırmalı; "tüm implementasyonlar" tek bir
politika olarak ölçülürse maliyeti hem gizlenir hem abartılır.

---

## 7. Doğrulamanın toplu sonucu

| Ölçüm | Sonuç |
|---|---|
| Forward — tablo | recall %82 (9/11), precision %100 |
| Forward — tablo, **yalnız EF akışları** | recall %90 (9/10) |
| Forward — kolon | recall %66 (23/35), precision %100 |
| Forward — kolon, UPDATE / INSERT | %90 (9/10) / %60 (15/25) |
| **Backward — kök** | **recall %100, precision %100** (3 sorgu, 9 kök) |
| Ambiguous politikası — tablo maliyeti | **0** (12→12, 50→50) |
| Ambiguous politikası — `ExternalCall` maliyeti | **1/1 yanlış pozitif** |

**On fark bulundu.** F1–F5 EF içi eksikler (düzeltilebilir), F6–F7 yapısal ham SQL sınırı (L6),
F8–F10 backward sunumu (F8 düzeltilmeli, F9 kaydedilmeli, F10 kozmetik).

**İki cümlelik özet:**

> Aracın **doğruluğu** yüksek: dört forward, üç backward sorgusunda tek bir yanlış tablo/kolon/kök
> yok. Aracın **kapsamı** EF'in kapsamı: EF'in gördüğünü görüyor, ham SQL'i ve ilişkisel olmayan
> depoları görmüyor — ama görmediği her yeri `file:line` ile rapor ediyor.

**Faz 5a için tek engel F8:** triage bot *"bu tabloya kim dokunuyor"* sorusuna "4 endpoint" diyor,
doğrusu "4 endpoint + 1 arka plan işi". Cevap eksik değil, **sunumu** eksik — ama bot cevabı
kategoriye göre okuyacak.

---

## 8. Düzeltme sonrası — F1, F3 ve F8 kapatıldı

Faz 4'e geçmeden üç düzeltme yapıldı. Diğerleri (F2, F4, F5, F9, F10) bilerek ertelendi ve
`known-limitations.md`'de açık.

### Ne değişti

**F1 — `EfProbe.CollectJsonContainerColumns`.** `OwnsMany(...).ToJson()` koleksiyonunun kapsayıcı
kolonu artık snapshot'ta. Kolon iki tarafın da `GetProperties()`'inde yok — owned tipin üyeleri JSON
alanı, sahibin `Items`'ı navigasyon — dolayısıyla açıkça toplanması gerekiyordu. Sentetik property
**navigasyonun adını** taşıyor, çünkü C#'ın atadığı isim o.

**F3 — satır düzeyi kural.** `EntityAccess` yeni bir `WritesWholeRow` alanı taşıyor;
`Add`/`AddRange` (INSERT tüm kolonları listeler), `Update`/`UpdateRange` (EF her property'yi
Modified işaretler) ve mapped bir entity'nin inşası bunu tetikliyor. `DataLayerOverlay` o entity'nin
tablosundaki **kalan** kolonlara W kenarı üretiyor.

Üç tasarım kararı:

1. **Yeni mekanizma: `RowInsert`.** Atamayla adlandırılan kolon daha kesin kenarını korur; `RowInsert`
   tam olarak *"satır yazıldı, dolayısıyla bu kolon da yazıldı"* iddialarını işaretler. Ayrı sayılır,
   ayrı ölçülebilir — karar C'nin gereği.
2. **`IsRowVersion` hariç.** `xmin`'i INSERT değil Postgres yazar.
3. **Gölge kolonun konumu entity bildirimi.** C# üyesi yok, ama uydurma konum da yok: açacağın dosya
   o. Miras alınan üyeler için base tipler yukarı doğru geziliyor — `identity.users.Id` bu sayede
   `Shared.Kernel/Entity.cs:7`'yi buluyor, ki doğru cevap tam olarak orası.

**F8 — `Node.RootKind`.** `NodeKind`'a değer **eklenmedi**; ayrı bir alan
(`None | Endpoint | Consumer | BackgroundService`). Kök olmak bir düğüm tipi değil, graph'taki rolü.
Alan `None` değerinde de yazılıyor — §5.7a'nın dersi: varsayılanında kaybolan alan, yazılı olmayan
bir kural üretir. Backward çıktısı artık kökleri en başta ve kategoriye göre basıyor.

### Ölçüm

```
Entry points (5): 4 endpoints + 1 background job

  endpoint         d4  POST /api/ordering/checkout
  endpoint         d5  GET /api/ordering/orders
  endpoint         d5  GET /api/ordering/orders/{id:guid}
  endpoint         d5  POST /api/ordering/orders/{id:guid}/cancel
  background job   d6  ReservationTtlSweeper.ExecuteAsync
```

| Endpoint | Kolon: gerçek | önce | **sonra** |
|---|---|---|---|
| `PUT /api/cart/items/{productId}` | 3 | 2 | **3** |
| `POST /api/identity/signup` | 4 | 3 | **4** |
| `POST /api/ordering/orders/{id}/cancel` | 26 | 18 | **22** |
| `POST /api/discovery/search` | 2 | 0 | 0 |
| **Toplam** | **35** | 23 | **29** |

| Ölçüm | Önce | Sonra |
|---|---|---|
| Kolon recall | %66 (23/35) | **%83 (29/35)** |
| — INSERT yolları | %60 (15/25) | **%84 (21/25)** |
| — UPDATE yolları | %90 (9/10) | **%100 (10/10)** |
| Kolon precision | %100 | **%100** (15/15 migration'da doğrulandı) |
| **Tablo recall** | %82 (9/11) | **değişmedi — %82 (9/11)** |
| **Tablo precision** | %100 | **değişmedi — %100** |

Kalan 6 kolonun 4'ü outbox (**F5**, ertelendi), 2'si Discovery'nin ham SQL'i (**F6**, yapısal).

**Tablo sayıları neden değişmedi:** satır düzeyi kural yalnız *kolon* kenarı üretiyor. Zaten
erişilebilen bir tablonun kolonlarını ekliyor, yeni tablo eklemiyor — eksik iki tablo (F4'ün
`ordering.order_lines` **okuması** ve F6'nın `discovery.product_embeddings`'i) bu kuralın konusu
değil. Beklenen davranış ve doğrulandı: cancel hâlâ 7/8 tablo, discovery hâlâ 0/1.

Graph: 400 → **415 node**, 841 → **966 kenar**, 82 → **97 kolon**.
Yeni `RowInsert` mekanizması **109 kenar**. Tüm invariant'lar sağlam: `kind` 415/415 ve 966/966,
konumsuz node 0, dangling 0, kolon→tablo 97/97, `rootKind` 32/32 kök (25 endpoint · 3 consumer ·
4 background job).

### Eklenen 15 kolonun tamamı migration'da doğrulandı

Recall'ı yükseltmek precision'ı düşürerek de yapılabilir — var olmayan kolonlar uydurarak. O yüzden
**82 → 97'nin farkı olan 15 kolonun her biri** `Migrations/*.cs`'e karşı tek tek kontrol edildi.
Gerçeklik kaynağı yine graph.json değil, fiziksel şema.

14'ü yalnızca `RowInsert` kenarı taşıyor (yani tam olarak yeni kuralın ürettikleri); 15'incisi
`cart.carts.Items` — F1 kolon node'unu var ettiği için artık mevcut `PropertyAssignment` bağlanıyor.

| # | Kolon | Migration kanıtı |
|---|---|---|
| 1 | `cart.carts.Items` | `InitialCartSchema.cs:24` — `Items = table.Column<string>(type: "jsonb")` |
| 2 | `catalog.products.Id` | `InitialCatalogSchema.cs:22`, PK `:35` |
| 3 | `identity.users.Id` | `InitialIdentitySchema.cs:22`, PK `:29` |
| 4 | `identity.refresh_tokens.Id` | `InitialIdentitySchema.cs:37`, PK `:46` |
| 5 | `inventory.reservations.Id` | `InitialInventorySchema.cs:22`, PK `:32` |
| 6 | `inventory.stock_items.Id` | `InitialInventorySchema.cs:40`, PK `:50` |
| 7 | `notification.notification_logs.Id` | `InitialNotification.cs:22`, PK `:32` |
| 8 | `ordering.orders.Id` | `InitialOrderingSchema.cs:23`, PK `:32` |
| 9 | `ordering.order_lines.id` | `InitialOrderingSchema.cs:40`, PK `:52` |
| 10 | `ordering.order_lines.order_id` | `InitialOrderingSchema.cs:48`, FK `:53-59` |
| 11 | `ordering.order_status_history.id` | `InitialOrderingSchema.cs:67`, PK `:77` |
| 12 | `ordering.order_status_history.order_id` | `InitialOrderingSchema.cs:73`, FK `:78-83` |
| 13 | `payment.payments.Id` | `InitialPaymentSchema.cs:23`, PK `:40` |
| 14 | `payment.payment_attempts.id` | `InitialPaymentSchema.cs:48`, PK `:60` |
| 15 | `payment.payment_attempts.payment_id` | `InitialPaymentSchema.cs:56`, FK `:62` |

**15/15 gerçek. Uydurulmuş kolon yok. Precision %100 korundu.**

İki negatif kontrol, kuralın körü körüne "her tabloya Id ekle" yapmadığının kanıtı:

- **`notification.processed_messages`'ta `Id` YOK** ve FlowLens de üretmedi. O tablonun anahtarı
  bileşik: `PrimaryKey("pk_processed_messages", x => new { x.IdempotencyKey, x.ConsumerType })`
  (`InitialNotification.cs:47`). Kural EF modelini okuyor, isim kalıbı uydurmuyor.
- **`xmin` hiçbir yerde yok.** `inventory.stock_items` ve `payment.payments` migration'larında
  fiziksel olarak var (`xmin` concurrency token) ama INSERT onu yazmaz — Postgres yazar.
  `IsRowVersion` filtresi tuttu.

### Precision düşmedi — üç kanaryayla doğrulandı

Satır düzeyi kural, §5.3'te kaldırılan hatayı ters yönden geri getirebilirdi. Getirmedi:

| Kanarya | Beklenen | Ölçülen |
|---|---|---|
| `GET /api/catalog/products` | 0 kolon (saf okuma) | **0** |
| Checkout'ta `cart.carts` | kolon yok (`Remove`, tüm satırı yazmaz) | **kolon yok** |
| Cancel'da `ordering.orders` | yalnız `Status`, `UpdatedAtUtc` (UPDATE) | **2 kolon** |

Üçüncüsü en önemlisi: aynı akış `ordering.order_status_history`'ye satır **ekliyor** (6 kolon, gölge
`id`/`order_id` dahil) ama `ordering.orders`'ı **güncelliyor** (2 kolon). Kural iki şekli aynı
gövdede ayırt ediyor. `AnInsertClaimsEveryColumnButAnUpdateStillClaimsOnlyTheOnesItAssigns` testi
bunu sabitliyor.

### Dikkat: `payment.payments.RefundedAtUtc` checkout'ta geri geldi

§5.3'te bu tam olarak bir **hata** olarak kaldırılmıştı. Şimdi tekrar görünüyor — ama başka bir
yoldan ve bu kez doğru olarak:

- **Eskiden:** checkout `payment.payments`'ı *okuyordu* ve `Table → Column` kenarı yüzünden o tablonun
  tüm kolonlarına ulaşıyordu. Okuyan, yazan gibi görünüyordu. Hata.
- **Şimdi:** checkout `payment.payments`'a **satır ekliyor**. INSERT cümlesi `RefundedAtUtc` kolonunu
  gerçekten listeliyor (NULL olarak). Kolonun tipi değişse veya kolon düşse checkout kırılır — yani
  impact analizi için doğru cevap.

Ayrım çıktıdan denetlenebilir: kenar `mechanism: RowInsert` taşıyor, `PropertyAssignment` değil.
**Aynı isim, farklı iddia** — ve mekanizma alanı tam olarak bunun için var.

### Testler

142 test, 0 atlanan (önce 125). Yeni 17'nin sabitlediği:
`ResolvesTheContainerColumnOfAJsonMappedCollection` · `MarksOnlyTheStatementsThatWriteEveryColumnOfTheRow`
(8 vaka) · `AWholeRowWriteSurvivesAnEarlierPartialWriteOnTheSameDbSet` ·
`ColumnsNoAssignmentNamesAreStillReachedOnAnInsert` (4 vaka) ·
`AnInsertClaimsEveryColumnButAnUpdateStillClaimsOnlyTheOnesItAssigns` ·
`EveryRootNodeCarriesItsRootKind` · `BackwardFromOrdersFindsBothEndpointsAndABackgroundJob`.

Son ikisi §10.4'ün popülasyon invariant'ı biçiminde: *her* kök node'u `RootKind` taşır **ve**
başka hiçbir node taşımaz — örneklem değil, tüm 415 node üzerinde.
